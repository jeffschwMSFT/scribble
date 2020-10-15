using Microsoft.AspNetCore.SignalR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Claims;
using System.Text;
using System.Linq.Expressions;
using Microsoft.Graph;
using System.Threading;

namespace scribble.Server.Hubs
{
    public class UpdateHub : Hub
    {
        public override Task OnConnectedAsync()
        {
            // Context.User;
            return base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            // clean up
            var group = GroupDetails.Purge(Context.ConnectionId);

            // todo what if the owner drops?
            if (GroupDetails.TryGetOwner(group, out string ownerconnectionid))
            {
                if (string.Equals(Context.ConnectionId, ownerconnectionid, StringComparison.OrdinalIgnoreCase))
                {
                    // ugh
                    await SendMessage(group, "sorry I left the game and no more rounds can be played");
                }
            }

            // broadcast the updated group
            if (!string.IsNullOrWhiteSpace(group))
            {
                await Clients.Group(group).SendAsync("ReceiveJoin", string.Join(",", GroupDetails.GetUsers(group).Select(u => $"{u.Username}:{u.Score}")));
            }

            await base.OnDisconnectedAsync(exception);
        }

        public async Task SendStartGame(string group, string timeoutseconds, string seperator, string words)
        {
            // add words
            GroupDetails.AddRoundDetails(group, words.Split(seperator), Convert.ToInt32(timeoutseconds));

            // mark that this group has already started
            GroupDetails.SetGroupStarted(group, isstarted: true);

            // notify everyone else to start game
            await Clients.OthersInGroup(group).SendAsync("ReceiveStartGame");
        }

        public async Task SendJoin(string group, string username)
        {
            // associate connection with group
            await Groups.AddToGroupAsync(Context.ConnectionId, group);

            // associate username with group
            if (!GroupDetails.AddUser(group: group, connectionId: Context.ConnectionId, username))
            {
                await Clients.Group(group).SendAsync("ReceiveMessage", $"failed to add {username}");
                return;
            }

            // broadcast current players to everyone in the group
            await Clients.Group(group).SendAsync("ReceiveJoin", string.Join(",", GroupDetails.GetUsers(group).Select(u => $"{u.Username}:{u.Score}")));

            if (GroupDetails.IsInRound(group))
            {
                // todo the timeout will be wrong
                var round = GroupDetails.GetNextRound(group);
                if (round != null)
                {
                    await Clients.Client(Context.ConnectionId).SendAsync("ReceiveNextRound", round.Username, round.Timeout.ToString(), round.ObfuscatedWord, false /* candraw */);
                    return;
                }
            }
            if (GroupDetails.IsGroupStarted(group))
            {
                // send the game start inidcator
                await Clients.Client(Context.ConnectionId).SendAsync("ReceiveStartGame");
            }
        }

        public async Task SendMessage(string group, string message)
        {
            // get username
            var username = GroupDetails.GetUsername(group, Context.ConnectionId);

            // check if we are in a round, and if so then check if this is a valid guess
            if (GroupDetails.TryGetInRound(group, out RoundDetails round))
            {
                // cannot guess for your own question or if you have already gotten it right
                if (!GroupDetails.TryGetHasAnswered(group, Context.ConnectionId, out bool answered) || answered) return;

                // check if the round is still valid
                if (DateTime.UtcNow > round.Start && DateTime.UtcNow < round.End)
                {
                    // check if this guess is currect
                    if (string.Equals(message, round.Word, StringComparison.OrdinalIgnoreCase))
                    {
                        // give credit
                        GroupDetails.AddToScore(group, Context.ConnectionId, (float)(round.End - round.Start).TotalSeconds);
                        // mark that you answered
                        GroupDetails.SetHasAnswered(group, Context.ConnectionId);
                        // return message indicating success
                        message = "correct :)";
                    }
                }
            }

            // send the message to everyone in this group
            await Clients.Group(group).SendAsync("ReceiveMessage", $"{username}: {message}");

            // finish early if done
            if (GroupDetails.TryGetEndOfRound(group, out bool done) && done)
            {
                await SendRoundComplete(group);
            }
        }

        public async Task SendLine(string group, string x1, string y1, string x2, string y2, string color, string diameter)
        {
            // send the point to everyone (except the sender) in this group
            await Clients.OthersInGroup(group).SendAsync("ReceiveLine", x1, y1, x2, y2, color, diameter);
        }

        public async Task SendClear(string group)
        {
            // send clear to everyone in this group
            await Clients.Group(group).SendAsync("ReceiveClear");
        }

        public async Task SendNextRound(string group)
        {
            // choose details about the next round
            if (!GroupDetails.SetupNextRound(group, out RoundDetails round)) return;

            // give credit for the drawer
            GroupDetails.AddToScore(group, round.ConnectionId, round.Timeout);
            GroupDetails.SetHasAnswered(group, round.ConnectionId);

            // notify everyone except the drawer
            await Clients.GroupExcept(group, round.ConnectionId).SendAsync("ReceiveNextRound", round.Username, round.Timeout.ToString(), round.ObfuscatedWord, false /* candraw */);

            // notify the drawer
            await Clients.Client(round.ConnectionId).SendAsync("ReceiveNextRound", round.Username, round.Timeout.ToString(), round.Word, true /* candraw */);
        }

        public async Task SendRoundComplete(string group)
        {
            // stop round
            GroupDetails.SetInRound(group, inround: false);

            // send a round done notification
            await Clients.Group(group).SendAsync("ReceiveRoundComplete");

            // refresh the user list (with scores)
            await Clients.Group(group).SendAsync("ReceiveJoin", string.Join(",", GroupDetails.GetUsers(group).Select(u => $"{u.Username}:{u.Score}")));
        }

        #region private
        class RoundDetails
        {
            public int Timeout;
            public DateTime Start;
            public DateTime End;
            public string Username;
            public string ConnectionId;
            public string Word;
            public string ObfuscatedWord;
        }
        class UserDetails
        {
            public string Username;
            public double Score;
        }
        class GroupDetails
        {
            // container to keep lists of users in each group (static?)
            public static Dictionary<string /*group*/, GroupDetails> UserMap = new Dictionary<string, GroupDetails>();

            public GroupDetails(string owner)
            {
                IsStarted = false;
                TimeoutSeconds = 30;
                Words = new List<string>();
                Connections = new Dictionary<string, UserDetails>();
                Current = null;
                OwnerConnectionId = owner;
                HasAnswered = new HashSet<string>();
                NextWordIndex = 0;
                HasDrawn = new HashSet<string>();
            }

            public static string Purge(string connectionId)
            {
                var connectionsGroup = "";
                try
                {
                    Guard.EnterWriteLock();
                    // gather dead groups and remove connection ids
                    var deadgroups = new HashSet<string>();
                    foreach (var kvp in UserMap)
                    {
                        if (kvp.Value != null)
                        {
                            if (kvp.Value.Connections.Remove(connectionId))
                            {
                                // capture the group
                                connectionsGroup = kvp.Key;
                            }
                        }
                        if (kvp.Value.Connections.Count == 0) deadgroups.Add(kvp.Key);
                    }

                    // remove deadgroups
                    foreach (var group in deadgroups) UserMap.Remove(group);
                }
                finally
                {
                    Guard.ExitWriteLock();
                }

                return connectionsGroup;
            }

            public static bool TryGetEndOfRound(string group, out bool done)
            {
                done = false;
                if (string.IsNullOrWhiteSpace(group)) return false;

                try
                {
                    Guard.EnterReadLock();
                    GroupDetails details = null;
                    if (!UserMap.TryGetValue(group, out details)) return false;
                    if (details.Connections.Count != details.HasAnswered.Count)
                    {
                        done = false;
                        return true;
                    }
                    // check that all the connections are present in answered
                    foreach (var conn in details.Connections.Keys)
                    {
                        if (!details.HasAnswered.Contains(conn))
                        {
                            done = false;
                            return true;
                        }
                    }
                    // they are all present
                    done = true;
                    return true;
                }
                finally
                {
                    Guard.ExitReadLock();
                }
            }

            public static bool AddToScore(string group, string connectionId, float score)
            {
                if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(connectionId)) return false;

                try
                {
                    Guard.EnterWriteLock();
                    GroupDetails details = null;
                    if (!UserMap.TryGetValue(group, out details)) return false;
                    UserDetails user = null;
                    if (!details.Connections.TryGetValue(connectionId, out user)) return false;
                    user.Score += score;
                    return true;
                }
                finally
                {
                    Guard.ExitWriteLock();
                }
            }

            public static bool SetHasAnswered(string group, string connectionid)
            {
                if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(connectionid)) return false;

                try
                {
                    Guard.EnterWriteLock();
                    GroupDetails details = null;
                    if (!UserMap.TryGetValue(group, out details)) return false;
                    details.HasAnswered.Add(connectionid);
                    return true;
                }
                finally
                {
                    Guard.ExitWriteLock();
                }
            }

            public static bool TryGetHasAnswered(string group, string connectionid, out bool hasanswered)
            {
                hasanswered = false;
                if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(connectionid)) return false;

                try
                {
                    Guard.EnterReadLock();
                    GroupDetails details = null;
                    if (!UserMap.TryGetValue(group, out details)) return false;
                    hasanswered = details.HasAnswered.Contains(connectionid);
                    return true;
                }
                finally
                {
                    Guard.ExitReadLock();
                }
            }

            public static bool SetGroupStarted(string group, bool isstarted)
            {
                if (string.IsNullOrWhiteSpace(group)) return false;

                try
                {
                    Guard.EnterWriteLock();
                    GroupDetails details = null;
                    if (!UserMap.TryGetValue(group, out details)) return false;
                    details.IsStarted = isstarted;
                    return true;
                }
                finally
                {
                    Guard.ExitWriteLock();
                }
            }

            public static bool IsGroupStarted(string group)
            {
                if (string.IsNullOrWhiteSpace(group)) return false;

                try
                {
                    Guard.EnterReadLock();
                    GroupDetails details = null;
                    if (!UserMap.TryGetValue(group, out details)) return false;
                    return details.IsStarted;
                }
                finally
                {
                    Guard.ExitReadLock();
                }
            }

            public static bool SetInRound(string group, bool inround)
            {
                if (string.IsNullOrWhiteSpace(group)) return false;

                try
                {
                    Guard.EnterWriteLock();
                    GroupDetails details = null;
                    if (!UserMap.TryGetValue(group, out details)) return false;
                    details.InRound = inround;
                    return true;
                }
                finally
                {
                    Guard.ExitWriteLock();
                }
            }

            public static bool IsInRound(string group)
            {
                if (string.IsNullOrWhiteSpace(group)) return false;

                try
                {
                    Guard.EnterWriteLock();
                    GroupDetails details = null;
                    if (!UserMap.TryGetValue(group, out details)) return false;
                    return details.InRound;
                }
                finally
                {
                    Guard.ExitWriteLock();
                }
            }

            public static bool TryGetInRound(string group, out RoundDetails round)
            {
                round = null;
                if (string.IsNullOrWhiteSpace(group)) return false;

                try
                {
                    Guard.EnterReadLock();
                    GroupDetails details = null;
                    if (!UserMap.TryGetValue(group, out details)) return false;
                    if (details.InRound)
                    {
                        round = details.Current;
                        return true;
                    }
                }
                finally
                {
                    Guard.ExitReadLock();
                }

                return false;
            }

            public static bool SetupNextRound(string group, out RoundDetails round)
            {
                var rand = new Random();
                round = null;

                if (string.IsNullOrWhiteSpace(group)) return false;

                try
                {
                    Guard.EnterWriteLock();
                    GroupDetails details = null;
                    if (!UserMap.TryGetValue(group, out details)) return false;
                    if (details.Connections.Count == 0) return false;
                    if (details.Words.Count == 0) return false;

                    // fill in details for this round
                    details.Current = new RoundDetails() { Timeout = details.TimeoutSeconds };

                    // choose who will be drawing
                    var available = new HashSet<string>();
                    foreach (var kvp in details.Connections)
                    {
                        if (!details.HasDrawn.Contains(kvp.Key)) available.Add(kvp.Key);
                    }
                    // choose from either the set of people that have not drawn or from the whole set
                    if (available.Count >= 1)
                    {
                        // get a random drawer who has not drawn
                        var index = rand.Next() % available.Count;
                        details.Current.ConnectionId = available.ToArray()[index];
                    }
                    else
                    {
                        // clear hasdrawn
                        details.HasDrawn.Clear();
                        // get a random drawer
                        var index = rand.Next() % details.Connections.Keys.Count;
                        details.Current.ConnectionId = details.Connections.Keys.ToArray()[index];
                    }
                    // set the details
                    if (!details.Connections.TryGetValue(details.Current.ConnectionId, out UserDetails user)) throw new Exception("Failed to get something that clearly is present");
                    details.Current.Username = user.Username;

                    // choose word
                    if (details.NextWordIndex >= details.Words.Count) details.NextWordIndex = 0;
                    details.Current.Word = details.Words[details.NextWordIndex++];

                    // obfuscate the word
                    details.Current.ObfuscatedWord = Obfuscate(details.Current.Word);

                    // clear the folks that have already answered
                    details.HasAnswered.Clear();

                    // set valid time zones
                    details.Current.Start = DateTime.UtcNow;
                    details.Current.End = details.Current.Start.AddSeconds(details.Current.Timeout + 2);

                    // mark that we are in a round
                    details.InRound = true;

                    // return the round
                    round = details.Current;
                }
                finally
                {
                    Guard.ExitWriteLock();
                }

                return true;
            }

            public static RoundDetails GetNextRound(string group)
            {
                if (string.IsNullOrWhiteSpace(group)) return null;

                try
                {
                    Guard.EnterReadLock();
                    GroupDetails details = null;
                    if (!UserMap.TryGetValue(group, out details)) return null;
                    if (!details.InRound) return null;
                    return details.Current;
                }
                finally
                {
                    Guard.ExitReadLock();
                }
            }

            public static bool AddRoundDetails(string group, string[] words, int timeoutseconds)
            {
                if (string.IsNullOrWhiteSpace(group) || words == null || words.Length == 0) return false;
                
                try
                {
                    Guard.EnterWriteLock();
                    GroupDetails details = null;
                    if (!UserMap.TryGetValue(group, out details)) return false;
                    if (details.Words.Count > 0) details.Words.Clear();
                    details.Words.AddRange(words);
                    details.TimeoutSeconds = timeoutseconds;
                    details.NextWordIndex = 0;
                }
                finally
                {
                    Guard.ExitWriteLock();
                }

                return true;
            }

            public static bool AddUser(string group, string connectionId, string username)
            {
                if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(username)) return false;

                try
                {
                    Guard.EnterWriteLock();
                    GroupDetails details = null;
                    if (!UserMap.TryGetValue(group, out details))
                    {
                        details = new GroupDetails(owner: connectionId);
                        UserMap.Add(group, details);
                    }
                    if (!details.Connections.ContainsKey(connectionId)) details.Connections.Add(connectionId, new UserDetails() { Username = username });
                    else details.Connections[connectionId].Username = username;
                }
                finally
                {
                    Guard.ExitWriteLock();
                }

                return true;
            }

            public static string GetUsername(string group, string connectionId)
            {
                if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(group)) return "";

                try
                {
                    Guard.EnterReadLock();
                    GroupDetails details = null;
                    if (!UserMap.TryGetValue(group, out details)) return "";
                    if (details.Connections.TryGetValue(connectionId, out UserDetails user)) return user.Username;
                    return "";
                }
                finally
                {
                    Guard.ExitReadLock();
                }
            }

            public static List<UserDetails> GetUsers(string group)
            {
                var users = new List<UserDetails>();
                if (string.IsNullOrWhiteSpace(group)) return users;

                try
                {
                    Guard.EnterReadLock();
                    if (UserMap.TryGetValue(group, out GroupDetails details))
                    {
                        foreach (var user in details.Connections.Values) users.Add(user);
                    }
                }
                finally
                {
                    Guard.ExitReadLock();
                }

                return users;
            }

            public static bool TryGetOwner(string group, out string ownerconnectionid)
            {
                ownerconnectionid = "";
                if (string.IsNullOrWhiteSpace(group)) return false;

                try
                {
                    Guard.EnterReadLock();
                    if (!UserMap.TryGetValue(group, out GroupDetails details)) return false;
                    ownerconnectionid = details.OwnerConnectionId;
                    return true;
                }
                finally
                {
                    Guard.ExitReadLock();
                }
            }

            #region private
            private static ReaderWriterLockSlim Guard = new ReaderWriterLockSlim();
            private List<string> Words;
            private int NextWordIndex;
            private int TimeoutSeconds;
            private Dictionary<string /*connectionid*/, UserDetails> Connections;
            private bool IsStarted;
            private bool InRound;
            private RoundDetails Current;
            private string OwnerConnectionId;
            private HashSet<string> HasAnswered;
            private HashSet<string> HasDrawn;

            private static string Obfuscate(string word)
            {
                var sb = new StringBuilder(word.Length);
                foreach (var c in word.ToCharArray())
                {
                    if (char.IsLetterOrDigit(c)) sb.Append("_ ");
                    else if (c == '\n' || c == '\r') continue;
                    else if (c == '\t') sb.Append(' ');
                    else sb.Append(c);
                }
                return sb.ToString();
            }
            #endregion
        }


        #endregion
    }
}
