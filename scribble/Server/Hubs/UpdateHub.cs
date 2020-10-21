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
            GroupDetails.TryGetUsername(Context.ConnectionId, out string group, out string username);

            // what if the owner drops?
            if (!string.IsNullOrWhiteSpace(group) && GroupDetails.TryGetOwner(group, out string ownerconnectionid))
            {
                if (string.Equals(Context.ConnectionId, ownerconnectionid, StringComparison.OrdinalIgnoreCase))
                {
                    // ugh
                    await SendMessage(group, "I left the game and no more rounds can be played (sorry)");
                }
                else
                {
                    // notify that you left
                    await SendMessage(group, $"I left the game");
                }
            }

            // clean up
            GroupDetails.Purge(Context.ConnectionId);

            // broadcast the updated group
            if (!string.IsNullOrWhiteSpace(group))
            {
                await Clients.Group(group).SendAsync("ReceiveJoin", string.Join(",", GroupDetails.GetUsers(group).Select(u => $"{u.Username}:{u.Score:f1}")));
            }

            await base.OnDisconnectedAsync(exception);
        }

        public async Task SendStartGame(string group, string timeoutseconds, string seperator, string words)
        {
            // set the owner
            GroupDetails.SetOwner(group, Context.ConnectionId);

            // add words
            if (!GroupDetails.AddRoundDetails(group, words.Split(seperator), Convert.ToInt32(timeoutseconds)))
            {
                await Clients.Group(group).SendAsync("ReceiveMessage", "failed to set game details");
                return;
            }

            // mark that this group has already started
            if (!GroupDetails.SetGroupStarted(group, isstarted: true))
            {
                await Clients.Group(group).SendAsync("ReceiveMessage", $"failed to start game");
                return;
            }

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
            await Clients.Group(group).SendAsync("ReceiveJoin", string.Join(",", GroupDetails.GetUsers(group).Select(u => $"{u.Username}:{u.Score:f1}")));

            // check if a round is in flight
            GroupDetails.TryGetInRound(group, out bool inround, out RoundDetails round);
            if (inround && round != null)
            {
                await Clients.Client(Context.ConnectionId).SendAsync("ReceiveNextRound", round.Username, round.Timeout.ToString(), round.ObfuscatedWord, false /* candraw */);
                return;
            }

            // make sure the player get the game start notification
            if (GroupDetails.IsGroupStarted(group))
            {
                // send the game start inidcator
                await Clients.Client(Context.ConnectionId).SendAsync("ReceiveStartGame");
            }
        }

        public async Task SendMessage(string group, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            // get username
            GroupDetails.TryGetUsername(group, Context.ConnectionId, out string username);

            // check if we are in a round, and if so then check if this is a valid guess
            var completeroundearly = false;
            GroupDetails.TryGetInRound(group, out bool inround, out RoundDetails round);
            if (inround && round != null)
            {
                // todo check if the word is contained in the reply

                // check if this guess is currect
                if (string.Equals(message, round.Word, StringComparison.OrdinalIgnoreCase))
                {
                    // cannot guess for your own question or if you have already gotten it right
                    if (GroupDetails.TryGetHasAnswered(group, Context.ConnectionId, out bool answered) && !answered)
                    {
                        // give credit
                        GroupDetails.AddToScore(group, Context.ConnectionId, (float)(round.End - DateTime.UtcNow).TotalSeconds);
                        // mark that you answered
                        GroupDetails.SetHasAnswered(group, Context.ConnectionId);
                    }

                    // return message indicating success
                    message = "correct :)";

                    // check again
                    GroupDetails.TryGetInRound(group, out inround, out round);

                    // check if the round should end early
                    // eg. it flipped from inround to !inround
                    completeroundearly = !inround;
                }
            }

            // send the message to everyone in this group
            await Clients.Group(group).SendAsync("ReceiveMessage", $"{username}: {message}");

            // finish early if done
            if (completeroundearly)
            {
                await SendRoundComplete(group);
            }
        }

        public async Task SendLine(string group, string x1, string y1, string x2, string y2, string color, string diameter)
        {
            // ensure this person is the drawer
            GroupDetails.TryGetInRound(group, out bool inround, out RoundDetails round);

            // exit if this client is not allowed to draw across all the screens
            if (!inround || !string.Equals(Context.ConnectionId, round.ConnectionId, StringComparison.OrdinalIgnoreCase)) return;

            // send the point to everyone (except the sender) in this group
            await Clients.OthersInGroup(group).SendAsync("ReceiveLine", x1, y1, x2, y2, color, diameter);
        }

        public async Task SendClear(string group)
        {
            // ensure this person is the drawer
            GroupDetails.TryGetInRound(group, out bool inround, out RoundDetails round);

            // exit if this client is not allowed to draw across all the screens
            if (!inround || !string.Equals(Context.ConnectionId, round.ConnectionId, StringComparison.OrdinalIgnoreCase)) return;

            // send clear to everyone in this group
            await Clients.OthersInGroup(group).SendAsync("ReceiveClear");
        }

        public async Task SendNextRound(string group)
        {
            // choose details about the next round
            if (!GroupDetails.StartNextRound(group, out RoundDetails round))
            {
                await Clients.Group(group).SendAsync("ReceiveMessage", "failed to start round");
                return;
            }

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
            await Clients.Group(group).SendAsync("ReceiveJoin", string.Join(",", GroupDetails.GetUsers(group).Select(u => $"{u.Username}:{u.Score:f1}")));
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
            // container to keep lists of users in each group
            public static Dictionary<string /*group*/, GroupDetails> UserMap = new Dictionary<string, GroupDetails>();

            public GroupDetails()
            {
                IsStarted = false;
                TimeoutSeconds = 30;
                Words = new List<string>();
                Connections = new Dictionary<string, UserDetails>();
                Current = null;
                OwnerConnectionId = "";
                HasAnswered = new HashSet<string>();
                NextWordIndex = 0;
                HasDrawn = new HashSet<string>();
                InstanceGuard = new ReaderWriterLockSlim();
            }

            public static string Purge(string connectionId)
            {
                var connectionsGroup = "";
                try
                {
                    Guard.EnterUpgradeableReadLock();
                    // gather dead groups and remove connection ids
                    var deadgroups = new HashSet<string>();
                    foreach (var kvp in UserMap)
                    {
                        try
                        {
                            kvp.Value.InstanceGuard.EnterUpgradeableReadLock();
                            if (kvp.Value.Connections.ContainsKey(connectionId))
                            {
                                try
                                {
                                    kvp.Value.InstanceGuard.EnterWriteLock();
                                    kvp.Value.Connections.Remove(connectionId);
                                }
                                finally
                                {
                                    kvp.Value.InstanceGuard.ExitWriteLock();
                                }
                                // capture the group
                                connectionsGroup = kvp.Key;
                            }
                            if (kvp.Value.Connections.Count == 0) deadgroups.Add(kvp.Key);
                        }
                        finally
                        {
                            kvp.Value.InstanceGuard.ExitUpgradeableReadLock();
                        }
                    }

                    // remove deadgroups
                    if (deadgroups.Count > 0)
                    {
                        try
                        {
                            Guard.EnterWriteLock();
                            foreach (var group in deadgroups) UserMap.Remove(group);
                        }
                        finally
                        {
                            Guard.ExitWriteLock();
                        }
                    }
                }
                finally
                {
                    Guard.ExitUpgradeableReadLock();
                }

                return connectionsGroup;
            }

            public static bool AddToScore(string group, string connectionId, float score)
            {
                if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(connectionId)) return false;

                GroupDetails details = null;
                try
                {
                    Guard.EnterReadLock();
                    if (!UserMap.TryGetValue(group, out details)) return false;
                }
                finally
                {
                    Guard.ExitReadLock();
                }
                try
                {
                    details.InstanceGuard.EnterWriteLock();
                    UserDetails user = null;
                    if (!details.Connections.TryGetValue(connectionId, out user)) return false;
                    user.Score += score;
                    return true;
                }
                finally
                {
                    details.InstanceGuard.ExitWriteLock();
                }
            }

            public static bool SetHasAnswered(string group, string connectionid)
            {
                if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(connectionid)) return false;

                GroupDetails details = null;
                try
                {
                    Guard.EnterReadLock();
                    if (!UserMap.TryGetValue(group, out details)) return false;
                }
                finally
                {
                    Guard.ExitReadLock();
                }
                try
                {
                    details.InstanceGuard.EnterWriteLock();
                    details.HasAnswered.Add(connectionid);
                    return true;
                }
                finally
                {
                    details.InstanceGuard.ExitWriteLock();
                }
            }

            public static bool TryGetHasAnswered(string group, string connectionid, out bool hasanswered)
            {
                hasanswered = false;
                if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(connectionid)) return false;

                GroupDetails details = null;
                try
                {
                    Guard.EnterReadLock();
                    if (!UserMap.TryGetValue(group, out details)) return false;
                }
                finally
                {
                    Guard.ExitReadLock();
                }
                try
                {
                    details.InstanceGuard.EnterReadLock();
                    hasanswered = details.HasAnswered.Contains(connectionid);
                    return true;
                }
                finally
                {
                    details.InstanceGuard.ExitReadLock();
                }
            }

            public static bool SetGroupStarted(string group, bool isstarted)
            {
                if (string.IsNullOrWhiteSpace(group)) return false;

                GroupDetails details = null;
                try
                {
                    Guard.EnterReadLock();
                    if (!UserMap.TryGetValue(group, out details)) return false;
                }
                finally
                {
                    Guard.ExitReadLock();
                }

                // non-guarded access
                details.IsStarted = isstarted;
                return true;
            }

            public static bool IsGroupStarted(string group)
            {
                if (string.IsNullOrWhiteSpace(group)) return false;

                GroupDetails details = null;
                try
                {
                    Guard.EnterReadLock();
                    if (!UserMap.TryGetValue(group, out details)) return false;
                }
                finally
                {
                    Guard.ExitReadLock();
                }

                // non-guarded access
                return details.IsStarted;
            }

            public static bool SetInRound(string group, bool inround)
            {
                if (string.IsNullOrWhiteSpace(group)) return false;

                GroupDetails details = null;
                try
                {
                    Guard.EnterReadLock();
                    if (!UserMap.TryGetValue(group, out details)) return false;
                }
                finally
                {
                    Guard.ExitReadLock();
                }

                // non-guarded access
                details.InRound = inround;
                return true;
            }

            public static bool TryGetInRound(string group, out bool inround, out RoundDetails round)
            {
                round = null;
                inround = false;
                if (string.IsNullOrWhiteSpace(group)) return false;

                GroupDetails details = null;
                try
                {
                    Guard.EnterReadLock();
                    if (!UserMap.TryGetValue(group, out details)) return false;
                }
                finally
                {
                    Guard.ExitReadLock();
                }

                // first exit early if not in a round
                if (!details.InRound)
                {
                    inround = false;
                    return true;
                }

                try
                {
                    details.InstanceGuard.EnterReadLock();

                    // second check if we are within the time limit of this round
                    if (DateTime.UtcNow < details.Current.Start || DateTime.UtcNow > details.Current.End)
                    {
                        inround = false;
                        return true;
                    }

                    // third do a quick check if the number of players is not the same as answered
                    if (details.Connections.Count != details.HasAnswered.Count)
                    {
                        round = details.Current;
                        inround = true;
                        return true;
                    }

                    // fourth check that all the connections are present in answered
                    foreach (var conn in details.Connections.Keys)
                    {
                        if (!details.HasAnswered.Contains(conn))
                        {
                            // this player has not answered yet
                            round = details.Current;
                            inround = true;
                            return true;
                        }
                    }

                    // everyone has answered
                    inround = false;
                    return true;
                }
                finally
                {
                    details.InstanceGuard.ExitReadLock();
                }
            }

            public static bool StartNextRound(string group, out RoundDetails round)
            {
                var rand = new Random();
                round = null;

                if (string.IsNullOrWhiteSpace(group)) return false;

                GroupDetails details = null;
                try
                {
                    Guard.EnterReadLock();
                    if (!UserMap.TryGetValue(group, out details)) return false;
                }
                finally
                {
                    Guard.ExitReadLock();
                }
                try
                {
                    details.InstanceGuard.EnterWriteLock();
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
                    details.Current.End = details.Current.Start.AddSeconds(details.Current.Timeout);

                    // mark that we are in a round
                    details.InRound = true;

                    // return the round
                    round = details.Current;
                }
                finally
                {
                    details.InstanceGuard.ExitWriteLock();
                }

                return true;
            }

            public static bool AddRoundDetails(string group, string[] words, int timeoutseconds)
            {
                if (string.IsNullOrWhiteSpace(group) || words == null || words.Length == 0) return false;

                GroupDetails details = null;
                try
                {
                    Guard.EnterReadLock();
                    if (!UserMap.TryGetValue(group, out details)) return false;
                }
                finally
                {
                    Guard.ExitReadLock();
                }
                try
                {
                    details.InstanceGuard.EnterWriteLock();
                    if (details.Words.Count > 0) details.Words.Clear();
                    details.Words.AddRange(words);
                    details.TimeoutSeconds = timeoutseconds;
                    details.NextWordIndex = 0;
                }
                finally
                {
                    details.InstanceGuard.ExitWriteLock();
                }

                return true;
            }

            public static bool AddUser(string group, string connectionId, string username)
            {
                if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(username)) return false;

                GroupDetails details = null;
                try
                {
                    Guard.EnterUpgradeableReadLock();
                    if (!UserMap.TryGetValue(group, out details))
                    {
                        details = new GroupDetails();
                        try
                        {
                            Guard.EnterWriteLock();
                            UserMap.Add(group, details);
                        }
                        finally
                        {
                            Guard.ExitWriteLock();
                        }
                    }
                }
                finally
                {
                    Guard.ExitUpgradeableReadLock();
                }
                try
                {
                    details.InstanceGuard.EnterWriteLock();
                    if (!details.Connections.ContainsKey(connectionId)) details.Connections.Add(connectionId, new UserDetails() { Username = username });
                    else details.Connections[connectionId].Username = username;
                }
                finally
                {
                    details.InstanceGuard.ExitWriteLock();
                }

                return true;
            }

            public static bool TryGetUsername(string connectionId, out string group, out string username)
            {
                username = "";
                group = "";
                if (string.IsNullOrWhiteSpace(connectionId)) return false;

                try
                {
                    Guard.EnterReadLock();
                    // deeper search is necessary
                    foreach (var kvp in UserMap)
                    {
                        try
                        {
                            kvp.Value.InstanceGuard.EnterReadLock();
                            if (kvp.Value.Connections.TryGetValue(connectionId, out UserDetails user))
                            {
                                group = kvp.Key;
                                username = user.Username;
                                return true;
                            }
                        }
                        finally
                        {
                            kvp.Value.InstanceGuard.ExitReadLock();
                        }
                    }
                }
                finally
                {
                    Guard.ExitReadLock();
                }

                return false;
            }

            public static bool TryGetUsername(string group, string connectionId, out string username)
            {
                username = "";
                if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(group)) return false;

                GroupDetails details = null;
                try
                {
                    Guard.EnterReadLock();
                    if (!UserMap.TryGetValue(group, out details)) return false;
                }
                finally
                {
                    Guard.ExitReadLock();
                }
                try
                {
                    details.InstanceGuard.EnterReadLock();
                    if (details.Connections.TryGetValue(connectionId, out UserDetails user))
                    {
                        username = user.Username;
                        return true;
                    }
                }
                finally
                {
                    details.InstanceGuard.ExitReadLock();
                }
                return false;
            }

            public static List<UserDetails> GetUsers(string group)
            {
                var users = new List<UserDetails>();
                if (string.IsNullOrWhiteSpace(group)) return users;

                GroupDetails details = null;
                try
                {
                    Guard.EnterReadLock();
                    if (!UserMap.TryGetValue(group, out details)) return users;
                }
                finally
                {
                    Guard.ExitReadLock();
                }
                try
                {
                    details.InstanceGuard.EnterReadLock();
                    foreach (var user in details.Connections.Values) users.Add(user);
                }
                finally
                {
                    details.InstanceGuard.ExitReadLock();
                }

                return users;
            }

            public static bool TryGetOwner(string group, out string ownerconnectionid)
            {
                ownerconnectionid = "";
                if (string.IsNullOrWhiteSpace(group)) return false;

                GroupDetails details = null;
                try
                {
                    Guard.EnterReadLock();
                    if (!UserMap.TryGetValue(group, out details)) return false;
                }
                finally
                {
                    Guard.ExitReadLock();
                }

                // non-guared access
                ownerconnectionid = details.OwnerConnectionId;
                return true;
            }

            public static bool SetOwner(string group, string ownerconnectionid)
            {
                if (string.IsNullOrWhiteSpace(group)) return false;

                GroupDetails details = null;
                try
                {
                    Guard.EnterReadLock();
                    if (!UserMap.TryGetValue(group, out details)) return false;
                }
                finally
                {
                    Guard.ExitReadLock();
                }

                // non-guared access
                details.OwnerConnectionId = ownerconnectionid;
                return true;
            }

            #region private
            // static
            private static ReaderWriterLockSlim Guard = new ReaderWriterLockSlim();

            // instance
            private ReaderWriterLockSlim InstanceGuard;
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
