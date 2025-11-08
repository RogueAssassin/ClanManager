using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries.Covalence;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("ClanManager", "RogueAssassin", "1.0.24")]
    [Description("Full-featured clan system with offline TC auth, alliances, chat, group limit enforcement, flexible config, and logging")]
    public class ClanManager : RustPlugin
    {
        #region Config
        private PluginConfig config;
        public class PluginConfig
        {
            [JsonProperty("Enable AutoAuth (true=on, false=off)")]
            public bool AutoAuth { get; set; } = true;

            [JsonProperty("Enable offline TC auto-auth (true=on, false=off)")]
            public bool OfflineTCAutoAuth { get; set; } = true;

            [JsonProperty("Default Max Group Limit per Clan (Rust default)")]
            public int MaxGroupLimit { get; set; } = 10;

            [JsonProperty("Enable Custom Group Limit Toggle")]
            public bool GroupToggle { get; set; } = false;

            [JsonProperty("Custom Max Group Limit per Clan (if enabled)")]
            public int CustomGroupLimit { get; set; } = 10;

            [JsonProperty("Enable Clan Group Limit Enforcement")]
            public bool EnforceGroupLimit { get; set; } = true;

            [JsonProperty("Enable Friendly Fire between clan members")]
            public bool FriendlyFire { get; set; } = false;

            [JsonProperty("Enable Scheduled Welcome Message (true=on, false=off)")]
            public bool WelcomeMessageEnabled { get; set; } = true;

            [JsonProperty("Welcome Message Text")]
            public string WelcomeMessageText { get; set; } = "Welcome to the server! Use /cmhelp to see clan commands.";

            [JsonProperty("FirstLogin Cleanup Interval in minutes")]
            public int FirstLoginCleanupMinutes { get; set; } = 1440; // 1 day

            [JsonProperty("Enable Clan Event Logging (true=on, false=off)")]
            public bool EventLogging { get; set; } = true;

            [JsonProperty("Enable persistent file logging (true=on, false=off)")]
            public bool PersistentLogging { get; set; } = true;

            [JsonProperty("Persistent log file name")]
            public string LogFileName { get; set; } = "ClanManagerEvents.txt";
        }

        protected override void LoadDefaultConfig() { config = new PluginConfig(); SaveConfig(); }
        protected override void LoadConfig() { base.LoadConfig(); config = Config.ReadObject<PluginConfig>(); if (config == null) { config = new PluginConfig(); SaveConfig(); } }
        protected override void SaveConfig() => Config.WriteObject(config, true);
        #endregion

        #region Data Storage
        private ClanDataStorage dataStorage;
        public class ClanDataStorage
        {
            public Dictionary<string, Clan> Clans = new Dictionary<string, Clan>();
            public Dictionary<ulong, List<string>> PlayerInvites = new Dictionary<ulong, List<string>>();
            public Dictionary<string, List<string>> Alliances = new Dictionary<string, List<string>>();
            public Dictionary<ulong, double> FirstLogin = new Dictionary<ulong, double>();
        }

        public class Clan
        {
            public string Name;
            public List<ulong> Members = new List<ulong>();
            public List<ulong> Moderators = new List<ulong>();
            // store numeric ID value (ulong) so it JSON-serializes cleanly
            public List<ulong> TCs = new List<ulong>();
            public int MaxGroup => 10;
        }

        private void InitStorage()
        {
            dataStorage = Interface.Oxide.DataFileSystem.ReadObject<ClanDataStorage>("ClanManagerData") ?? new ClanDataStorage();
        }

        private void SaveStorage()
        {
            Interface.Oxide.DataFileSystem.WriteObject("ClanManagerData", dataStorage);
        }
        #endregion

        #region Hooks
        private void OnServerInitialized()
        {
            InitStorage();
            Puts("ClanManager v1.0.24 compiled and initialized");
            timer.Every(3600f, CleanOldFirstLoginEntries);
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            AutoAuthPlayer(player);
            NotifyPendingInvites(player);

            if (config.WelcomeMessageEnabled && (!dataStorage.FirstLogin.ContainsKey(player.userID)))
            {
                timer.Once(2f, () => player.ChatMessage(config.WelcomeMessageText));
            }

            dataStorage.FirstLogin[player.userID] = Time.realtimeSinceStartup;
            SaveStorage();
        }

        private void OnEntityBuilt(Planner planner, GameObject go)
        {
            var tc = go.GetComponent<BuildingPrivlidge>();
            if (tc == null) return;

            var owner = planner.GetOwnerPlayer();
            if (owner == null) return;

            var ownerClan = FindPlayerClan(owner);
            if (ownerClan == null) return;

            // get numeric id value for storage
            ulong idValue = GetNetworkableIdValueSafe(tc.net.ID);
            if (idValue == 0) return;

            if (!ownerClan.TCs.Contains(idValue)) ownerClan.TCs.Add(idValue);

            // Authorize clan members on this TC
            foreach (var memberID in ownerClan.Members)
            {
                if (!config.AutoAuth) continue;
                if (!AuthorizedPlayersContains(tc, memberID))
                    AddAuthorizedPlayer(tc, memberID);
            }

            SaveStorage();
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (!config.FriendlyFire) return null;
            var attacker = info?.Initiator as BasePlayer;
            var victim = entity?.GetComponent<BasePlayer>();
            if (attacker == null || victim == null) return null;
            var clanA = FindPlayerClan(attacker);
            var clanB = FindPlayerClan(victim);
            if (clanA != null && clanA == clanB) return false;
            return null;
        }
        #endregion

        #region Chat Commands
        [ChatCommand("cmhelp")]
        private void CmdHelp(BasePlayer player, string command, string[] args)
        {
            player.ChatMessage("ClanManager Commands:");
            player.ChatMessage("/clancreate <name>");
            player.ChatMessage("/claninvite <clan> <player>");
            player.ChatMessage("/clanjoin <clan>");
            player.ChatMessage("/clanleave");
            player.ChatMessage("/clankick <player>");
            player.ChatMessage("/clanpromote <player>");
            player.ChatMessage("/clandemote <player>");
            player.ChatMessage("/clandisband");
            player.ChatMessage("/clanally <clan>");
            player.ChatMessage("/clanunally <clan>");
            player.ChatMessage("/clanchat <msg>");
            player.ChatMessage("/allychat <msg>");
        }

        [ChatCommand("clancreate")]
        private void CmdCreateClan(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0) { player.ChatMessage("Usage: /clancreate <name>"); return; }
            string name = SanitizeClanName(args[0]);
            if (dataStorage.Clans.ContainsKey(name)) { player.ChatMessage("Clan exists!"); return; }

            var clan = new Clan { Name = name };
            clan.Members.Add(player.userID); clan.Moderators.Add(player.userID);
            dataStorage.Clans[name] = clan; SaveStorage();
            player.ChatMessage($"Clan {name} created!");
            LogEvent($"Clan created: {name} by {player.displayName} ({player.userID})");
        }

        [ChatCommand("claninvite")]
        private void CmdInvite(BasePlayer player, string command, string[] args)
        {
            if (args.Length < 2) { player.ChatMessage("Usage: /claninvite <clan> <player>"); return; }
            string cname = SanitizeClanName(args[0]); string targetName = args[1];

            if (!dataStorage.Clans.TryGetValue(cname, out var clan)) { player.ChatMessage("Clan not found"); return; }
            if (!clan.Members.Contains(player.userID)) { player.ChatMessage("Not a clan member"); return; }

            if (config.EnforceGroupLimit && clan.Members.Count >= (config.GroupToggle ? config.CustomGroupLimit : config.MaxGroupLimit))
            {
                player.ChatMessage($"Clan {cname} has reached its member limit!");
                return;
            }

            var target = BasePlayer.Find(targetName);
            if (target == null) { player.ChatMessage("Player not found"); return; }

            if (!dataStorage.PlayerInvites.ContainsKey(target.userID))
                dataStorage.PlayerInvites[target.userID] = new List<string>();

            if (!dataStorage.PlayerInvites[target.userID].Contains(cname))
                dataStorage.PlayerInvites[target.userID].Add(cname);

            SaveStorage();
            player.ChatMessage($"{target.displayName} invited to {cname}");
            LogEvent($"{player.displayName} invited {target.displayName} ({target.userID}) to clan {cname}");
        }

        [ChatCommand("clanjoin")]
        private void CmdJoinClan(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0) { player.ChatMessage("Usage: /clanjoin <name>"); return; }
            string cname = SanitizeClanName(args[0]);
            if (!dataStorage.Clans.TryGetValue(cname, out var clan)) { player.ChatMessage("Clan not found"); return; }
            if (!dataStorage.PlayerInvites.TryGetValue(player.userID, out var invites) || !invites.Contains(cname))
            { player.ChatMessage("No invite found"); return; }

            if (config.EnforceGroupLimit && clan.Members.Count >= (config.GroupToggle ? config.CustomGroupLimit : config.MaxGroupLimit))
            {
                player.ChatMessage($"Clan {cname} has reached its member limit!");
                return;
            }

            clan.Members.Add(player.userID); invites.Remove(cname); SaveStorage();
            player.ChatMessage($"Joined clan {cname}");
            LogEvent($"{player.displayName} ({player.userID}) joined clan {cname}");
        }

        [ChatCommand("clanleave")]
        private void CmdLeaveClan(BasePlayer player, string command, string[] args)
        {
            var clan = FindPlayerClan(player);
            if (clan == null) { player.ChatMessage("You are not in a clan."); return; }

            clan.Members.Remove(player.userID);
            clan.Moderators.Remove(player.userID);

            if (clan.Members.Count == 0)
            {
                dataStorage.Clans.Remove(clan.Name);
                SaveStorage();
                player.ChatMessage($"Clan {clan.Name} disbanded because no members left.");
                LogEvent($"Clan {clan.Name} disbanded (all members left).");
                return;
            }

            SaveStorage();
            player.ChatMessage($"You have left clan {clan.Name}");
            LogEvent($"{player.displayName} ({player.userID}) left clan {clan.Name}");
        }

        [ChatCommand("clankick")]
        private void CmdKick(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0) { player.ChatMessage("Usage: /clankick <player>"); return; }
            var clan = FindPlayerClan(player);
            if (clan == null) { player.ChatMessage("You are not in a clan."); return; }
            if (!clan.Moderators.Contains(player.userID)) { player.ChatMessage("Only clan moderators can kick."); return; }

            var target = BasePlayer.Find(args[0]);
            if (target == null || !clan.Members.Contains(target.userID)) { player.ChatMessage("Player not found in your clan."); return; }

            clan.Members.Remove(target.userID);
            clan.Moderators.Remove(target.userID);
            SaveStorage();
            player.ChatMessage($"You kicked {target.displayName} from the clan.");
            target.ChatMessage($"You were kicked from clan {clan.Name}");
            LogEvent($"{player.displayName} ({player.userID}) kicked {target.displayName} ({target.userID}) from clan {clan.Name}");
        }

        [ChatCommand("clanpromote")]
        private void CmdPromote(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0) { player.ChatMessage("Usage: /clanpromote <player>"); return; }
            var clan = FindPlayerClan(player);
            if (clan == null) { player.ChatMessage("You are not in a clan."); return; }
            if (!clan.Moderators.Contains(player.userID)) { player.ChatMessage("Only clan moderators can promote."); return; }

            var target = BasePlayer.Find(args[0]);
            if (target == null || !clan.Members.Contains(target.userID)) { player.ChatMessage("Player not found in your clan."); return; }

            if (!clan.Moderators.Contains(target.userID))
            {
                clan.Moderators.Add(target.userID);
                SaveStorage();
                player.ChatMessage($"{target.displayName} has been promoted to moderator.");
                target.ChatMessage("You have been promoted to clan moderator.");
                LogEvent($"{player.displayName} ({player.userID}) promoted {target.displayName} ({target.userID}) in clan {clan.Name}");
            }
            else player.ChatMessage("Player is already a moderator.");
        }

        [ChatCommand("clandemote")]
        private void CmdDemote(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0) { player.ChatMessage("Usage: /clandemote <player>"); return; }
            var clan = FindPlayerClan(player);
            if (clan == null) { player.ChatMessage("You are not in a clan."); return; }
            if (!clan.Moderators.Contains(player.userID)) { player.ChatMessage("Only clan moderators can demote."); return; }

            var target = BasePlayer.Find(args[0]);
            if (target == null || !clan.Moderators.Contains(target.userID)) { player.ChatMessage("Player not a moderator."); return; }

            clan.Moderators.Remove(target.userID);
            SaveStorage();
            player.ChatMessage($"{target.displayName} has been demoted from moderator.");
            target.ChatMessage("You have been demoted from clan moderator.");
            LogEvent($"{player.displayName} ({player.userID}) demoted {target.displayName} ({target.userID}) in clan {clan.Name}");
        }

        [ChatCommand("clandisband")]
        private void CmdDisband(BasePlayer player, string command, string[] args)
        {
            var clan = FindPlayerClan(player);
            if (clan == null) { player.ChatMessage("You are not in a clan."); return; }
            if (!clan.Moderators.Contains(player.userID)) { player.ChatMessage("Only moderators can disband the clan."); return; }

            foreach (var memberID in clan.Members)
            {
                var member = BasePlayer.FindByID(memberID);
                member?.ChatMessage($"Clan {clan.Name} has been disbanded.");
            }

            dataStorage.Clans.Remove(clan.Name);
            SaveStorage();
            LogEvent($"Clan {clan.Name} disbanded by {player.displayName} ({player.userID})");
        }

        [ChatCommand("clanally")]
        private void CmdAlly(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0) { player.ChatMessage("Usage: /clanally <clan>"); return; }
            var clan = FindPlayerClan(player);
            if (clan == null) { player.ChatMessage("You are not in a clan."); return; }

            string allyClan = SanitizeClanName(args[0]);
            if (!dataStorage.Clans.ContainsKey(allyClan)) { player.ChatMessage("Clan not found."); return; }
            if (allyClan == clan.Name) { player.ChatMessage("Cannot ally with your own clan."); return; }

            if (!dataStorage.Alliances.ContainsKey(clan.Name)) dataStorage.Alliances[clan.Name] = new List<string>();
            if (!dataStorage.Alliances[clan.Name].Contains(allyClan))
            {
                dataStorage.Alliances[clan.Name].Add(allyClan);
                SaveStorage();
                player.ChatMessage($"Your clan is now allied with {allyClan}");
                LogEvent($"Clan {clan.Name} formed alliance with {allyClan}");
            }
            else player.ChatMessage("Already allied.");
        }

        [ChatCommand("clanunally")]
        private void CmdUnAlly(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0) { player.ChatMessage("Usage: /clanunally <clan>"); return; }
            var clan = FindPlayerClan(player);
            if (clan == null) { player.ChatMessage("You are not in a clan."); return; }

            string allyClan = SanitizeClanName(args[0]);
            if (dataStorage.Alliances.TryGetValue(clan.Name, out var allies) && allies.Contains(allyClan))
            {
                allies.Remove(allyClan);
                SaveStorage();
                player.ChatMessage($"Alliance with {allyClan} removed.");
                LogEvent($"Clan {clan.Name} removed alliance with {allyClan}");
            }
            else player.ChatMessage("Not allied with that clan.");
        }

        [ChatCommand("clanchat")]
        private void CmdClanChat(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0) { player.ChatMessage("Usage: /clanchat <msg>"); return; }
            var clan = FindPlayerClan(player);
            if (clan == null) { player.ChatMessage("You are not in a clan."); return; }

            string message = string.Join(" ", args);
            foreach (var memberID in clan.Members)
            {
                var member = BasePlayer.FindByID(memberID);
                member?.ChatMessage($"[Clan] {player.displayName}: {message}");
            }
            LogEvent($"[ClanChat] {clan.Name} | {player.displayName} ({player.userID}): {message}");
        }

        [ChatCommand("allychat")]
        private void CmdAllyChat(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0) { player.ChatMessage("Usage: /allychat <msg>"); return; }
            var clan = FindPlayerClan(player);
            if (clan == null) { player.ChatMessage("You are not in a clan."); return; }

            string message = string.Join(" ", args);
            if (dataStorage.Alliances.TryGetValue(clan.Name, out var allies))
            {
                foreach (var allyClan in allies)
                {
                    if (dataStorage.Clans.TryGetValue(allyClan, out var ally))
                    {
                        foreach (var memberID in ally.Members)
                        {
                            var member = BasePlayer.FindByID(memberID);
                            member?.ChatMessage($"[AllyChat] {player.displayName}: {message}");
                        }
                    }
                }
            }

            foreach (var memberID in clan.Members)
            {
                var member = BasePlayer.FindByID(memberID);
                member?.ChatMessage($"[AllyChat] {player.displayName}: {message}");
            }

            LogEvent($"[AllyChat] {clan.Name} | {player.displayName} ({player.userID}): {message}");
        }
        #endregion

        #region Utilities
        private string SanitizeClanName(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            var s = input.Trim().ToLowerInvariant();
            if (s.Length > 24) s = s.Substring(0, 24);
            return s;
        }

        private Clan FindPlayerClan(BasePlayer player)
        {
            if (player == null) return null;
            return dataStorage.Clans.Values.FirstOrDefault(c => c.Members.Contains(player.userID));
        }

        private ulong GetNetworkableIdValueSafe(object netIdObj)
        {
            if (netIdObj == null) return 0;
            try
            {
                var type = netIdObj.GetType();
                var prop = type.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var val = prop.GetValue(netIdObj);
                    if (val is ulong ul) return ul;
                    if (val is long l && l >= 0) return (ulong)l;
                    if (val is uint ui) return ui;
                    if (ulong.TryParse(val?.ToString(), out var parsed)) return parsed;
                }
                var s = netIdObj.ToString();
                if (ulong.TryParse(s, out var parsed2)) return parsed2;
            }
            catch { }
            return 0;
        }

        private bool AuthorizedPlayersContains(object tcObj, ulong userId)
        {
            if (tcObj == null) return false;
            try
            {
                var tcType = tcObj.GetType();
                var authField = tcType.GetField("authorizedPlayers", BindingFlags.Public | BindingFlags.Instance);
                object authObj = null;
                if (authField != null) authObj = authField.GetValue(tcObj);
                else
                {
                    var authProp = tcType.GetProperty("authorizedPlayers", BindingFlags.Public | BindingFlags.Instance);
                    if (authProp != null) authObj = authProp.GetValue(tcObj);
                }

                if (!(authObj is IList list)) return false;

                Type elemType = null;
                var listType = list.GetType();
                if (listType.IsGenericType) elemType = listType.GetGenericArguments()[0];
                else if (list.Count > 0 && list[0] != null) elemType = list[0].GetType();

                if (elemType == typeof(ulong))
                {
                    foreach (var o in list) if (o is ulong u && u == userId) return true;
                }
                else if (elemType == typeof(uint))
                {
                    foreach (var o in list) if (o is uint ui && ui == (uint)userId) return true;
                }
                else if (elemType == typeof(ProtoBuf.PlayerNameID))
                {
                    foreach (var o in list)
                    {
                        if (o is ProtoBuf.PlayerNameID p && p.userid == userId) return true;
                    }
                }
                else
                {
                    foreach (var o in list)
                    {
                        if (o == null) continue;
                        var t = o.GetType();
                        var userProp = t.GetProperty("userid", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (userProp != null)
                        {
                            var val = userProp.GetValue(o);
                            if (val != null && ulong.TryParse(val.ToString(), out var found) && found == userId) return true;
                        }
                        var userField = t.GetField("userid", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (userField != null)
                        {
                            var val = userField.GetValue(o);
                            if (val != null && ulong.TryParse(val.ToString(), out var found2) && found2 == userId) return true;
                        }
                        if (o is IConvertible)
                        {
                            try
                            {
                                var conv = Convert.ToUInt64(o);
                                if (conv == userId) return true;
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private void AddAuthorizedPlayer(object tcObj, ulong userId)
        {
            if (tcObj == null) return;
            try
            {
                var tcType = tcObj.GetType();
                var authField = tcType.GetField("authorizedPlayers", BindingFlags.Public | BindingFlags.Instance);
                object authObj = null;
                if (authField != null) authObj = authField.GetValue(tcObj);
                else
                {
                    var authProp = tcType.GetProperty("authorizedPlayers", BindingFlags.Public | BindingFlags.Instance);
                    if (authProp != null) authObj = authProp.GetValue(tcObj);
                }

                if (!(authObj is IList list)) return;

                Type elemType = null;
                var listType = list.GetType();
                if (listType.IsGenericType) elemType = listType.GetGenericArguments()[0];
                else if (list.Count > 0 && list[0] != null) elemType = list[0].GetType();

                if (elemType == typeof(ulong))
                {
                    list.Add((ulong)userId);
                }
                else if (elemType == typeof(uint))
                {
                    list.Add((uint)userId);
                }
                else if (elemType == typeof(ProtoBuf.PlayerNameID))
                {
                    list.Add(new ProtoBuf.PlayerNameID { userid = userId });
                }
                else
                {
                    if (elemType != null)
                    {
                        try
                        {
                            var ctor = elemType.GetConstructor(Type.EmptyTypes);
                            if (ctor != null)
                            {
                                var inst = ctor.Invoke(null);
                                var prop = elemType.GetProperty("userid", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                                if (prop != null && prop.CanWrite)
                                {
                                    if (prop.PropertyType == typeof(ulong)) prop.SetValue(inst, userId);
                                    else if (prop.PropertyType == typeof(uint)) prop.SetValue(inst, (uint)userId);
                                    else
                                    {
                                        var conv = Convert.ChangeType(userId, prop.PropertyType);
                                        prop.SetValue(inst, conv);
                                    }
                                    list.Add(inst);
                                    return;
                                }
                                var field = elemType.GetField("userid", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                                if (field != null)
                                {
                                    if (field.FieldType == typeof(ulong)) field.SetValue(inst, userId);
                                    else if (field.FieldType == typeof(uint)) field.SetValue(inst, (uint)userId);
                                    else
                                    {
                                        var conv = Convert.ChangeType(userId, field.FieldType);
                                        field.SetValue(inst, conv);
                                    }
                                    list.Add(inst);
                                    return;
                                }
                            }
                        }
                        catch { }
                    }

                    try { list.Add(userId); } catch { try { list.Add((uint)userId); } catch { } }
                }
            }
            catch { }
        }

        private object FindEntityById(ulong id)
        {
            var serverEntities = BaseNetworkable.serverEntities;
            if (serverEntities == null) return null;
            var seType = serverEntities.GetType();

            // Try to find Find(NetworkableId) or Find(uint) or Find(ulong)
            MethodInfo findMethod = null;
            findMethod = seType.GetMethod("Find", new Type[] { Type.GetType("NetworkableId") }) ??
                         seType.GetMethod("Find", new Type[] { typeof(uint) }) ??
                         seType.GetMethod("Find", new Type[] { typeof(ulong) }) ??
                         seType.GetMethod("Find", new Type[] { typeof(object) });

            if (findMethod != null)
            {
                var param = findMethod.GetParameters()[0].ParameterType;
                object arg = null;
                try
                {
                    if (param.FullName == "NetworkableId" || param.Name == "NetworkableId")
                    {
                        // construct NetworkableId via reflection (NetworkableId may be a struct with a ctor taking ulong)
                        var netIdType = param;
                        try
                        {
                            var ctor = netIdType.GetConstructor(new Type[] { typeof(ulong) });
                            if (ctor != null) arg = ctor.Invoke(new object[] { id });
                            else
                            {
                                // fallback: try static method Parse or similar
                                arg = Activator.CreateInstance(netIdType);
                                var valProp = netIdType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                                if (valProp != null && valProp.CanWrite) valProp.SetValue(arg, id);
                            }
                        }
                        catch { arg = null; }
                    }
                    else if (param == typeof(uint)) arg = (uint)id;
                    else if (param == typeof(ulong)) arg = id;
                    else arg = Convert.ChangeType(id, param);
                }
                catch { arg = null; }

                if (arg != null)
                {
                    try
                    {
                        return findMethod.Invoke(serverEntities, new object[] { arg });
                    }
                    catch { /* ignore and try enumerable fallback below */ }
                }
            }

            // Fallback: try to enumerate serverEntities (some builds expose a ToArray method or enumerator)
            try
            {
                var toArray = seType.GetMethod("ToArray", BindingFlags.Public | BindingFlags.Instance);
                if (toArray != null)
                {
                    var arr = toArray.Invoke(serverEntities, null) as IEnumerable;
                    if (arr != null)
                    {
                        foreach (var ent in arr)
                        {
                            try
                            {
                                var netField = ent.GetType().GetField("net", BindingFlags.Public | BindingFlags.Instance);
                                if (netField == null) continue;
                                var net = netField.GetValue(ent);
                                var idObj = net?.GetType().GetProperty("ID", BindingFlags.Public | BindingFlags.Instance)?.GetValue(net);
                                if (idObj == null) continue;
                                if (GetNetworkableIdValueSafe(idObj) == id) return ent;
                            }
                            catch { continue; }
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private void AutoAuthPlayer(BasePlayer player)
        {
            var clan = FindPlayerClan(player);
            if (clan == null) return;

            foreach (var tcID in clan.TCs)
            {
                var tc = FindEntityById(tcID);
                if (tc == null)
                {
                    if (config.OfflineTCAutoAuth) continue;
                    else continue;
                }

                if (!AuthorizedPlayersContains(tc, player.userID))
                    AddAuthorizedPlayer(tc, player.userID);
            }
        }

        private void NotifyPendingInvites(BasePlayer player)
        {
            if (dataStorage.PlayerInvites.TryGetValue(player.userID, out var invites) && invites.Count > 0)
                player.ChatMessage($"Pending clan invites: {string.Join(", ", invites)}");
        }

        private void CleanOldFirstLoginEntries()
        {
            double threshold = UnityEngine.Time.realtimeSinceStartup - (config.FirstLoginCleanupMinutes * 60);
            var oldKeys = dataStorage.FirstLogin.Where(kvp => kvp.Value < threshold).Select(kvp => kvp.Key).ToList();
            foreach (var key in oldKeys) dataStorage.FirstLogin.Remove(key);
            if (oldKeys.Count > 0) SaveStorage();
        }

        private void LogEvent(string message)
        {
            if (!config.EventLogging) return;
            Puts($"[ClanManager Event] {message}");

            if (config.PersistentLogging)
            {
                string logPath = Path.Combine(Interface.Oxide.DataDirectory, config.LogFileName);
                try
                {
                    File.AppendAllText(logPath, $"[{DateTime.Now}] {message}\n");
                }
                catch { } // fail silently so logging doesn't crash server
            }
        }

        private void RemoveAllPluginData()
        {
            foreach (var clan in dataStorage.Clans.Values) clan.TCs.Clear();
            dataStorage.Clans.Clear();
            dataStorage.PlayerInvites.Clear();
            dataStorage.Alliances.Clear();
            dataStorage.FirstLogin.Clear();
            SaveStorage();
        }

        private void Unload()
        {
            RemoveAllPluginData();
        }
        #endregion
    }
}
