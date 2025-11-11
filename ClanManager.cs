using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Oxide.Core.Libraries.Covalence;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Rust;

namespace Oxide.Plugins
{
    [Info("ClanManager", "RogueAssassin", "1.0.32")]
    [Description("Full-featured clan system with offline TC auth, automatic TC registration, alliances, chat, group limit enforcement, flexible config, logging, and friendly-fire prevention")]
    public class ClanManager : RustPlugin
    {
        #region --- Dictionary | Variables ---
        private HashSet<ulong> playersWithWelcomeMessage = new HashSet<ulong>();
        private DateTime lastWarActionTime = DateTime.MinValue;
        private bool warCooldownEnabled = true;
        private TimeSpan warCooldownDuration = TimeSpan.FromMinutes(5);
        #endregion

        #region --- Config ---

        private class ConfigData
        {
            [JsonProperty("Config Version")]
            public string Version { get; set; } = "1.0.32"; // plugin version

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

            [JsonProperty("Custom Server Name")]
            public string ServerName { get; set; } = "My Server";  // Default server name

            [JsonProperty("Welcome Message Text")]
            public string WelcomeMessageText { get; set; } = "Welcome to {serverName}! Use /cmhelp to see clan commands.";  // Default message with placeholder

            [JsonProperty("FirstLogin Cleanup Interval in minutes")]
            public int FirstLoginCleanupMinutes { get; set; } = 1440;

            [JsonProperty("Enable Clan Event Logging (true=on, false=off)")]
            public bool EventLogging { get; set; } = true;

            [JsonProperty("Enable persistent file logging (true=on, false=off)")]
            public bool PersistentLogging { get; set; } = true;

            [JsonProperty("Persistent log file name")]
            public string LogFileName { get; set; } = "ClanManagerEvents.txt";

            [JsonProperty("Enable Debug Logging (true=on, false=off)")]
            public bool DebugLogging { get; set; } = false;

            [JsonProperty("Default Clan Permission Level")]
            public string DefaultPermissionLevel { get; set; } = "Member";

            [JsonProperty("Enable Emergency TC Cleanup (true=on, false=off)")]
            public bool EmergencyCleanup { get; set; } = false;

            [JsonProperty("Enable Auto TC Registration for New Clans")]
            public bool AutoRegisterTC { get; set; } = true;

            [JsonProperty("Enable War Cooldown (true=on, false=off)")]
            public bool WarCooldownEnabled { get; set; } = false;

            [JsonProperty("War Cooldown Duration (minutes)")]
            public double WarCooldownDuration { get; set; } = 5;


            /* Placeholder for any additional/new config options from updated plugin
            Example: [JsonProperty("NewSettingName")] public bool NewSettingName { get; set; } = true;*/

        }

        private ConfigData configData;

        protected override void LoadDefaultConfig()
        {
            configData = new ConfigData();
            SaveConfig();
        }

        private void LoadWarCooldownSettings()
        {
            warCooldownEnabled = configData.WarCooldownEnabled;
            warCooldownDuration = TimeSpan.FromMinutes(configData.WarCooldownDuration);

            Puts($"[ClanManager] War cooldowns: {(warCooldownEnabled ? "ENABLED" : "DISABLED")} ({warCooldownDuration.TotalMinutes} minutes)");
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                configData = Config.ReadObject<ConfigData>();
                LogEvent("Configuration loaded successfully.");
            }
            catch
            {
                PrintError("Failed to load config, creating default.");
                LoadDefaultConfig();
                LogEvent("Configuration file was invalid and has been regenerated.");
            }
        }

        protected override void SaveConfig() => Config.WriteObject(configData, true);

        private void ProcessConfig()
        {
            bool changed = false;

            // Upgrade logic if config version is missing or outdated
            if (string.IsNullOrEmpty(configData.Version) || configData.Version != this.Version.ToString())
            {
                PrintWarning($"Config version {configData.Version} is outdated; upgrading to {this.Version}");

                // Ensure defaults for any newly added settings
                if (string.IsNullOrEmpty(configData.ServerName)) { configData.ServerName = "My Server"; changed = true; }
                if (string.IsNullOrEmpty(configData.WelcomeMessageText)) { configData.WelcomeMessageText = "Welcome to {serverName}! Use /cmhelp to see clan commands."; changed = true; }
                if (configData.WarCooldownEnabled == null) { configData.WarCooldownEnabled = false; changed = true; }
                if (configData.WarCooldownDuration == 0) { configData.WarCooldownDuration = 5; changed = true; }

                configData.Version = this.Version.ToString();
                changed = true;
            }

            if (changed)
            {
                SaveConfig();
            }
        }

        #endregion

        #region --- Data Classes ---

        public class Clan
        {
            public string Name;
            public HashSet<ulong> Members;
            public HashSet<ulong> TCs;

            public Clan()
            {
                Members = new HashSet<ulong>();
                TCs = new HashSet<ulong>();
            }
        }

        private List<Clan> Clans = new List<Clan>();

        public class ClanData
        {
            public string Name;
            public ulong Owner;
            public List<ulong> Members;
            public List<ulong> CoLeaders;
            public List<ulong> TCs;
            public DateTime CreatedAt; // Add this field
            // War-related data
            public List<string> Enemies; // List of clan names this clan is at war with
            public DateTime? WarStartTime; // When the war started (optional)
        }

        public class InviteData
        {
            public string ClanName { get; set; }
            public DateTime InvitedAt { get; set; }
        }

        private class StorageData
        {
            public Dictionary<string, ClanData> Clans = new Dictionary<string, ClanData>();
            public Dictionary<ulong, List<InviteData>> PendingInvites = new Dictionary<ulong, List<InviteData>>();
        }

        private StorageData dataStorage = new StorageData();

        #endregion

        #region --- Initialization ---

        private void Init()
        {
            LoadConfig();
            ProcessConfig();
            LoadWarCooldownSettings();
            LoadStorage();
            RebuildRuntimeClans();
            permission.RegisterPermission("clanmanager.admin", this);
            permission.RegisterPermission("clanmanager.setwelcome", this);
        }


        private void LoadStorage()
        {
            dataStorage = Interface.Oxide.DataFileSystem.ReadObject<StorageData>(Name) ?? new StorageData();
        }

        private void SaveStorage() => Interface.Oxide.DataFileSystem.WriteObject(Name, dataStorage);

        private void RebuildRuntimeClans()
        {
            Clans.Clear();
            foreach (var kv in dataStorage.Clans)
            {
                var clanData = kv.Value;
                var clan = new Clan
                {
                    Name = clanData.Name ?? kv.Key,
                    Members = new HashSet<ulong>(clanData.Members),
                    TCs = new HashSet<ulong>(clanData.TCs)
                };
                Clans.Add(clan);
            }
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            var clan = FindPlayerClan(player);
            if (clan == null)
                return;

            if (clan.Enemies.Count > 0)
            {
                foreach (var enemyClanName in clan.Enemies)
                {
                    var enemyClan = dataStorage.Clans[enemyClanName];
                    player.ChatMessage($"You are at war with clan '{enemyClan.Name}'! War started on {clan.WarStartTime?.ToString("yyyy-MM-dd HH:mm")}");
                }
            }
        }

        void OnServerInitialized()
        {
            LoadWelcomeState();
            LogEvent("Plugin has been loaded and initialized.");
        }

        void OnServerSave()
        {
            SaveStorage();
            LogEvent("Server save triggered. Clan data saved successfully.");
        }

        void Unload()
        {
            SaveStorage();
            LogEvent("Plugin is unloading. All data saved and memory cleared.");
        }

        void OnPlayerInit(BasePlayer player)
        {
            IPlayer covalencePlayer = player.IPlayer;  // Access IPlayer interface from BasePlayer
            
            // Send the welcome message only if it's enabled, the player hasn't received it yet, and if the message toggle is on
            if (configData.WelcomeMessageEnabled && !playersWithWelcomeMessage.Contains(player.userID))
            {
                SendWelcomeMessage(covalencePlayer);  // Send the custom welcome message (now passing IPlayer)
            }
        }

        /* Can be applied in future udpates
        private void OnPlayerTakeDamage(BasePlayer player, HitInfo info)
        {
            var clan = FindPlayerClan(player);
            if (clan != null && clan.Enemies.Count > 0)
            {
                // Increase damage by 10% if at war
                info.damage *= 1.1f;
            }
        }
        */

        #endregion

        #region --- Helpers ---

        private ClanData FindPlayerClan(BasePlayer player)
        {
            foreach (var clan in dataStorage.Clans.Values)
            {
                if (clan.Members.Contains(player.userID) || clan.CoLeaders.Contains(player.userID) || clan.Owner == player.userID)
                    return clan;
            }
            return null;
        }

        private BasePlayer FindPlayerByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player != null && player.displayName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return player;
            }

            foreach (var clan in dataStorage.Clans.Values)
            {
                foreach (var memberId in clan.Members)
                {
                    if (GetPlayerName(memberId).Equals(name, StringComparison.OrdinalIgnoreCase))
                        return BasePlayer.FindByID(memberId);
                }
            }

            return null;
        }

        private BasePlayer FindPlayerById(ulong playerId)
        {
            if (playerId == 0) return null;

            // Check active players first
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player != null && player.userID == playerId)
                    return player;
            }

            // If player is not active, check through clans
            foreach (var clan in dataStorage.Clans.Values)
            {
                foreach (var memberId in clan.Members)
                {
                    if (memberId == playerId)
                        return BasePlayer.FindByID(memberId);
                }
            }

            return null;
        }

        private int GetMaxGroupLimit()
        {
            return configData.GroupToggle ? configData.CustomGroupLimit : configData.MaxGroupLimit;
        }

        private void LogEvent(string message)
        {
            if (!configData.EventLogging || string.IsNullOrEmpty(message)) return;

            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            string formatted = $"[{timestamp}] [ClanManager v{Version}] {message}";

            Puts(formatted);

            if (configData.PersistentLogging)
            {
                try
                {
                    string filePath = $"{Interface.Oxide.DataDirectory}/{configData.LogFileName}";
                    File.AppendAllText(filePath, formatted + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    PrintError($"[ClanManager] Failed to write log: {ex.Message}");
                }
            }
        }

        private void CleanUpExpiredInvites()
        {
            DateTime now = DateTime.UtcNow;
            var expiredInvites = new List<(ulong, InviteData)>();

            foreach (var kv in dataStorage.PendingInvites)
            {
                foreach (var invite in kv.Value)
                {
                    if (now - invite.InvitedAt > TimeSpan.FromDays(2)) // 2-day expiry
                    {
                        expiredInvites.Add((kv.Key, invite));
                    }
                }
            }

            // Remove expired entries
            foreach (var (playerId, invite) in expiredInvites)
            {
                dataStorage.PendingInvites[playerId].Remove(invite);
                if (dataStorage.PendingInvites[playerId].Count == 0)
                    dataStorage.PendingInvites.Remove(playerId);
            }

            if (expiredInvites.Count > 0)
                SaveStorage();
        }

        #endregion

        #region --- WelcomeMessages ---        
        private void SendWelcomeMessage(IPlayer player)
        {
            ulong playerId;
            
            // Convert player.Id (which is a string) to ulong
            if (ulong.TryParse(player.Id, out playerId))
            {
                // Check if the welcome message is enabled and the player hasn't already received it
                if (configData.WelcomeMessageEnabled && !playersWithWelcomeMessage.Contains(playerId))
                {
                    // Replace the {serverName} placeholder with the actual server name from the config
                    string welcomeMessage = configData.WelcomeMessageText.Replace("{serverName}", configData.ServerName);

                    player.Message(welcomeMessage);  // Send the updated welcome message
                    playersWithWelcomeMessage.Add(playerId);  // Mark the player as having received the message
                    SaveWelcomeState();  // Save the player's state
                }
            }
            else
            {
                Console.WriteLine("Invalid player ID format.");
            }
        }

        private string GetPlayerName(ulong id)
        {
            BasePlayer player = BasePlayer.FindByID(id);
            return player != null ? player.displayName : $"Unknown-{id}";
        }

        // Load the welcome state at server startup
        private void LoadWelcomeState()
        {
            if (File.Exists("playersWithWelcomeMessage.txt"))
            {
                var lines = File.ReadAllLines("playersWithWelcomeMessage.txt");
                playersWithWelcomeMessage = new HashSet<ulong>(lines.Select(line => ulong.Parse(line)));
            }
        }

        // Save the state of players who have received the welcome message
        private void SaveWelcomeState()
        {
            File.WriteAllLines("playersWithWelcomeMessage.txt", playersWithWelcomeMessage.Select(id => id.ToString()));
        }

        #endregion

        #region --- Friendly Fire Prevention ---

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitInfo)
        {
            if (configData.FriendlyFire) return null;

            if (entity is BasePlayer victim && hitInfo?.Initiator is BasePlayer attacker)
            {
                var victimClan = FindPlayerClan(victim);
                var attackerClan = FindPlayerClan(attacker);

                if (victimClan != null && attackerClan != null && victimClan == attackerClan)
                {
                    hitInfo.damageTypes = new DamageTypeList();
                    attacker.ChatMessage($"You cannot harm your clan member: {victim.displayName}");
                    return true;
                }
            }
            return null;
        }

        #endregion

        #region --- TC Authorization & Registration ---

        private void OnEntitySpawned(BuildingPrivlidge tc)
        {
            if (tc == null) return;

            ulong tcId = tc.net.ID.Value;

            // Find clan in storage first
            var clanDataEntry = dataStorage.Clans.Values.FirstOrDefault(cd => cd.TCs.Contains(tcId));
            if (clanDataEntry == null) return;

            // Find or rebuild runtime clan
            var clan = Clans.FirstOrDefault(c => c.TCs.Contains(tcId));
            if (clan == null)
            {
                clan = new Clan
                {
                    Name = clanDataEntry.Name,
                    Members = new HashSet<ulong>(clanDataEntry.Members),
                    TCs = new HashSet<ulong>(clanDataEntry.TCs)
                };
                Clans.Add(clan);
            }

            foreach (ulong memberId in clan.Members)
            {
                if (!tc.authorizedPlayers.Contains(memberId))
                    tc.authorizedPlayers.Add(memberId);
            }
        }

        private void RegisterTCForClan(Clan clan, BuildingPrivlidge tc)
        {
            if (clan == null || tc == null) return;

            ulong tcId = tc.net.ID.Value;

            if (!clan.TCs.Contains(tcId))
                clan.TCs.Add(tcId);

            foreach (ulong memberId in clan.Members)
            {
                if (!tc.authorizedPlayers.Contains(memberId))
                    tc.authorizedPlayers.Add(memberId);
            }
        }

        #endregion

        #region --- TC Removal Cleanup ---

        private void OnEntityDeath(BaseNetworkable entity, HitInfo info)
        {
            if (entity is BuildingPrivlidge tc)
            {
                foreach (var clan in dataStorage.Clans.Values)
                {
                    if (clan.TCs.Contains(tc.net.ID.Value))
                    {
                        clan.TCs.Remove(tc.net.ID.Value);
                        SaveStorage();
                        LogEvent($"TC {tc.net.ID.Value} removed from clan '{clan.Name}' due to destruction.");
                        break;
                    }
                }
            }
        }

        #endregion
        
        #region --- Clan Commands ---

        [Command("cmhelp")]
        private void CmdHelp(BasePlayer player, string command, string[] args)
        {
            player.ChatMessage("=== ClanManager Commands ===");
            player.ChatMessage("/cmcreate <clan_name> - Create a new clan.");
            player.ChatMessage("/cmdisband - Disband your clan (Owner only).");
            player.ChatMessage("/cminvite <player> - Invite a player to your clan (Owner only).");
            player.ChatMessage("/cmaccept <clan_name> - Accept a pending clan invite.");
            player.ChatMessage("/cmdecline <clan_name> - Decline a pending clan invite.");
            player.ChatMessage("/cmpromote <player> - Promote a member to co-leader (Owner only).");
            player.ChatMessage("/cmdemote <player> - Demote a co-leader to member (Owner only).");
            player.ChatMessage("/cmkick <player> - Remove a member from your clan (Owner only).");
            player.ChatMessage("/cmleave - Leave your current clan.");
            player.ChatMessage("/cminfo [clan_name] - View your clan or another clan's info.");
            player.ChatMessage("/cmlist - View all clans on the server.");
            player.ChatMessage("/cmdeclarewar <enemy_clan> - Declare war on another clan.");
            player.ChatMessage("/cmendwar <enemy_clan> - End a war with another clan.");

            if (player.IPlayer.HasPermission("clanmanager.admin") || player.IPlayer.HasPermission("clanmanager.setwelcome"))
            {
                player.ChatMessage("/cmsetwelcome <message> - Set the server’s welcome message (Admins only).");
            }
        }

        [Command("cmcreate")]
        private void CmdCreate(IPlayer player, string command, string[] args)
        {
            if (args.Length != 1)
            {
                player.Message("Usage: /cmcreate <clan_name>");
                return;
            }

            string clanName = args[0].ToLower();  // Normalize the clan name
            if (dataStorage.Clans.ContainsKey(clanName))
            {
                player.Message($"Clan '{clanName}' already exists!");
                return;
            }

            var newClan = new ClanData
            {
                Name = clanName,
                Owner = ulong.Parse(player.Id),
                Members = new List<ulong> { ulong.Parse(player.Id) },
                CoLeaders = new List<ulong>(),
                TCs = new List<ulong>(),
                CreatedAt = DateTime.UtcNow  // Set creation date
            };

            dataStorage.Clans[clanName] = newClan;
            SaveStorage();
            player.Message($"Clan '{clanName}' successfully created!");
            LogEvent($"{player.Name} created clan '{clanName}'.");
        }

        [Command("cmdisband")]
        private void CmdDisband(IPlayer player, string command, string[] args)
        {
            var clan = FindPlayerClan(player.Object as BasePlayer);
            if (clan == null)
            {
                player.Message("You are not in a clan.");
                return;
            }

            if (clan.Owner != ulong.Parse(player.Id))
            {
                player.Message("Only the clan owner can disband the clan.");
                return;
            }

            dataStorage.Clans.Remove(clan.Name);
            SaveStorage();
            player.Message($"Clan '{clan.Name}' has been disbanded.");
            LogEvent($"{player.Name} disbanded clan '{clan.Name}'.");
        }

        [Command("cminvite")]
        private void CmdInvite(IPlayer player, string command, string[] args)
        {
            if (args.Length != 1)
            {
                player.Message("Usage: /cminvite <player>");
                return;
            }

            var clan = FindPlayerClan(player.Object as BasePlayer);
            if (clan == null)
            {
                player.Message("You are not in a clan.");
                return;
            }

            if (clan.Owner != ulong.Parse(player.Id))
            {
                player.Message("Only the clan owner can invite.");
                return;
            }

            var target = FindPlayerByName(args[0]);
            if (target == null)
            {
                player.Message("Player not found.");
                return;
            }

            // Remove expired invites before adding new ones
            CleanUpExpiredInvites();

            // Check if the player already has a valid invite
            if (dataStorage.PendingInvites.ContainsKey(target.userID) &&
                dataStorage.PendingInvites[target.userID].Any(inv => inv.ClanName == clan.Name))
            {
                player.Message($"{target.displayName} already has a pending invite to '{clan.Name}'.");
                return;
            }

            // Add invite entry
            if (!dataStorage.PendingInvites.ContainsKey(target.userID))
                dataStorage.PendingInvites[target.userID] = new List<InviteData>();

            dataStorage.PendingInvites[target.userID].Add(new InviteData
            {
                ClanName = clan.Name,
                InvitedAt = DateTime.UtcNow
            });

            SaveStorage();

            target.ChatMessage($"You have been invited to join clan '{clan.Name}'. Use /cmaccept {clan.Name} to join.");
            player.Message($"Invite sent to {target.displayName}.");
            LogEvent($"{player.Name} invited {target.displayName} to clan '{clan.Name}'.");
        }

        [Command("cmaccept")]
        private void CmdAccept(IPlayer player, string command, string[] args)
        {
            if (args.Length != 1)
            {
                player.Message("Usage: /cmaccept <clan_name>");
                return;
            }

            string clanName = args[0].ToLower();
            ulong playerId = ulong.Parse(player.Id);

            if (!dataStorage.Clans.ContainsKey(clanName))
            {
                player.Message($"Clan '{clanName}' does not exist.");
                return;
            }

            // Check for expired invites first
            CleanUpExpiredInvites();

            // Check if invite exists
            if (!dataStorage.PendingInvites.ContainsKey(playerId) ||
                !dataStorage.PendingInvites[playerId].Any(inv => inv.ClanName == clanName))
            {
                player.Message($"You do not have a pending invite for '{clanName}'.");
                return;
            }

            var clan = dataStorage.Clans[clanName];
            var playerClan = FindPlayerClan(player.Object as BasePlayer);
            if (playerClan != null)
            {
                player.Message("You are already in a clan.");
                return;
            }

            // Add player to clan
            clan.Members.Add(playerId);
            dataStorage.PendingInvites[playerId].RemoveAll(inv => inv.ClanName == clanName);
            if (dataStorage.PendingInvites[playerId].Count == 0)
                dataStorage.PendingInvites.Remove(playerId);

            SaveStorage();

            player.Message($"You have joined the clan '{clanName}'.");
            clan.Members.ForEach(m => FindPlayerById(m).ChatMessage($"{player.Name} has joined your clan!"));
            LogEvent($"{player.Name} joined clan '{clanName}'.");
        }

        [Command("cmdecline")]
        private void CmdDecline(IPlayer player, string command, string[] args)
        {
            if (args.Length != 1)
            {
                player.Message("Usage: /cmdecline <clan_name>");
                return;
            }

            string clanName = args[0].ToLower();
            ulong playerId = ulong.Parse(player.Id);

            // Check for expired invites
            CleanUpExpiredInvites();

            // Check if invite exists
            if (!dataStorage.PendingInvites.ContainsKey(playerId) ||
                !dataStorage.PendingInvites[playerId].Any(inv => inv.ClanName == clanName))
            {
                player.Message($"You do not have a pending invite for '{clanName}'.");
                return;
            }

            // Remove invite
            dataStorage.PendingInvites[playerId].RemoveAll(inv => inv.ClanName == clanName);
            if (dataStorage.PendingInvites[playerId].Count == 0)
                dataStorage.PendingInvites.Remove(playerId);

            SaveStorage();

            player.Message($"You have declined the invite from '{clanName}'.");
            LogEvent($"{player.Name} declined clan invite from '{clanName}'.");
        }

        [Command("cmpromote")]
        private void CmdPromote(IPlayer player, string command, string[] args)
        {
            if (args.Length != 1)
            {
                player.Message("Usage: /cmpromote <player>");
                return;
            }

            var clan = FindPlayerClan(player.Object as BasePlayer);
            if (clan == null || clan.Owner != ulong.Parse(player.Id))
            {
                player.Message("You must be the clan owner to promote.");
                return;
            }

            var target = FindPlayerByName(args[0]);
            if (target == null || !clan.Members.Contains(target.userID))
            {
                player.Message("Player is not a member of your clan.");
                return;
            }

            if (!clan.CoLeaders.Contains(target.userID))
            {
                clan.CoLeaders.Add(target.userID);
                SaveStorage();
                player.Message($"{target.displayName} has been promoted to co-leader.");
                target.ChatMessage($"You have been promoted to co-leader of '{clan.Name}'.");
            }
            else
            {
                player.Message($"{target.displayName} is already a co-leader.");
            }
        }

        [Command("cmdemote")]
        private void CmdDemote(IPlayer player, string command, string[] args)
        {
            if (args.Length != 1)
            {
                player.Message("Usage: /cmdemote <player>");
                return;
            }

            var clan = FindPlayerClan(player.Object as BasePlayer);
            if (clan == null || clan.Owner != ulong.Parse(player.Id))
            {
                player.Message("You must be the clan owner to demote.");
                return;
            }

            var target = FindPlayerByName(args[0]);
            if (target == null || !clan.CoLeaders.Contains(target.userID))
            {
                player.Message("Player is not a co-leader.");
                return;
            }

            clan.CoLeaders.Remove(target.userID);
            SaveStorage();
            player.Message($"{target.displayName} has been demoted to member.");
            target.ChatMessage($"You have been demoted in clan '{clan.Name}'.");
        }

        [Command("cmkick")]
        private void CmdKick(IPlayer player, string command, string[] args)
        {
            if (args.Length != 1)
            {
                player.Message("Usage: /cmkick <player>");
                return;
            }

            var clan = FindPlayerClan(player.Object as BasePlayer);
            if (clan == null || clan.Owner != ulong.Parse(player.Id))
            {
                player.Message("You must be the clan owner to kick.");
                return;
            }

            var target = FindPlayerByName(args[0]);
            if (target == null || (!clan.Members.Contains(target.userID) && !clan.CoLeaders.Contains(target.userID)))
            {
                player.Message("Player is not in your clan.");
                return;
            }

            clan.Members.Remove(target.userID);
            clan.CoLeaders.Remove(target.userID);
            SaveStorage();
            player.Message($"{target.displayName} has been kicked from the clan.");
            target.ChatMessage($"You have been kicked from clan '{clan.Name}'.");
        }

        [Command("cmleave")]
        private void CmdLeave(IPlayer player, string command, string[] args)
        {
            var clan = FindPlayerClan(player.Object as BasePlayer);
            if (clan == null)
            {
                player.Message("You are not in a clan.");
                return;
            }

            ulong playerId = ulong.Parse(player.Id);

            if (clan.Owner == playerId)
            {
                player.Message("Owner cannot leave the clan. Use /cmdisband to disband.");
                return;
            }

            clan.Members.Remove(playerId);
            clan.CoLeaders.Remove(playerId);
            SaveStorage();
            player.Message($"You have left clan '{clan.Name}'.");
            LogEvent($"{player.Name} left clan '{clan.Name}'.");
        }

        [Command("cminfo")]
        private void CmdInfo(IPlayer player, string command, string[] args)
        {
            ClanData clan = null;

            // If no clan name is specified, fetch the player's current clan
            if (args.Length == 0)
            {
                clan = FindPlayerClan(player.Object as BasePlayer);
                if (clan == null)
                {
                    player.Message("You are not in a clan and no clan name was specified.");
                    return;
                }
            }
            else
            {
                string clanName = args[0].ToLower();  // Normalize clan name to lower case for consistency
                if (!dataStorage.Clans.ContainsKey(clanName))
                {
                    player.Message($"Clan '{clanName}' does not exist.");
                    return;
                }
                clan = dataStorage.Clans[clanName];
            }

            // Display clan info
            var coLeaders = string.Join(", ", clan.CoLeaders.ConvertAll(GetPlayerName));
            var members = string.Join(", ", clan.Members.ConvertAll(GetPlayerName));
            var tcs = string.Join(", ", clan.TCs.ConvertAll(tc => tc.ToString()));

            player.Message($"=== Clan Info: {clan.Name} ===");
            player.Message($"Owner: {GetPlayerName(clan.Owner)}");
            player.Message($"Co-Leaders: {(string.IsNullOrEmpty(coLeaders) ? "None" : coLeaders)}");
            player.Message($"Members: {(string.IsNullOrEmpty(members) ? "None" : members)}");
            player.Message($"TCs: {(string.IsNullOrEmpty(tcs) ? "None" : tcs)}");
            player.Message($"Total Members: {clan.Members.Count}");
            player.Message($"Clan Created: {clan.CreatedAt}");
        }

        [Command("cmlist")]
        private void CmdList(IPlayer player, string command, string[] args)
        {
            if (dataStorage.Clans.Count == 0)
            {
                player.Message("No clans exist.");
                return;
            }

            // Sort clans by member count or creation date
            var sortedClans = dataStorage.Clans.Values
                .OrderByDescending(clan => clan.Members.Count)
                .ToList();

            player.Message("=== Clans on Server ===");
            foreach (var clan in sortedClans)
            {
                player.Message($"{clan.Name} - Owner: {GetPlayerName(clan.Owner)}, Members: {clan.Members.Count}, Created: {clan.CreatedAt}");
            }
        }

        [Command("cmdeclarewar")]
        private void CmdDeclareWar(IPlayer player, string command, string[] args)
        {
            if (args.Length != 1)
            {
                player.Message("Usage: /cmdeclarewar <enemy_clan_name>");
                return;
            }

            string enemyClanName = args[0].ToLower();
            var clan = FindPlayerClan(player.Object as BasePlayer);
            if (clan == null || clan.Owner != ulong.Parse(player.Id))
            {
                player.Message("You must be the clan owner to declare war.");
                return;
            }

            if (!dataStorage.Clans.ContainsKey(enemyClanName))
            {
                player.Message($"Clan '{enemyClanName}' does not exist.");
                return;
            }

            var enemyClan = dataStorage.Clans[enemyClanName];
            if (enemyClan.Owner == clan.Owner)
            {
                player.Message("You cannot declare war on your own clan.");
                return;
            }

            if (clan.Enemies.Contains(enemyClanName))
            {
                player.Message($"You are already at war with '{enemyClanName}'.");
                return;
            }

            // Cooldown Check
            if (warCooldownEnabled && DateTime.UtcNow - lastWarActionTime < warCooldownDuration)
            {
                player.Message($"You must wait {warCooldownDuration.TotalMinutes} minutes before declaring war again.");
                return;
            }

            // Declare war
            clan.Enemies.Add(enemyClanName);
            enemyClan.Enemies.Add(clan.Name);  // Add reverse war declaration
            lastWarActionTime = DateTime.UtcNow;  // Update cooldown timestamp
            SaveStorage();  // Save the changes

            player.Message($"You have declared war on '{enemyClanName}'.");
            enemyClan.Members.ForEach(m => FindPlayerById(m).ChatMessage($"Your clan '{enemyClanName}' is now at war with '{clan.Name}'!"));
            LogEvent($"{player.Name} declared war on '{enemyClanName}' at {DateTime.UtcNow}");
        }

        [Command("cmendwar")]
        private void CmdEndWar(IPlayer player, string command, string[] args)
        {
            if (args.Length != 1)
            {
                player.Message("Usage: /cmendwar <enemy_clan_name>");
                return;
            }

            string enemyClanName = args[0].ToLower();
            var clan = FindPlayerClan(player.Object as BasePlayer);
            if (clan == null || clan.Owner != ulong.Parse(player.Id))
            {
                player.Message("You must be the clan owner to end a war.");
                return;
            }

            if (!dataStorage.Clans.ContainsKey(enemyClanName))
            {
                player.Message($"Clan '{enemyClanName}' does not exist.");
                return;
            }

            var enemyClan = dataStorage.Clans[enemyClanName];
            if (!clan.Enemies.Contains(enemyClanName))
            {
                player.Message($"You are not at war with '{enemyClanName}'.");
                return;
            }

            // Cooldown Check
            if (warCooldownEnabled && DateTime.UtcNow - lastWarActionTime < warCooldownDuration)
            {
                player.Message($"You must wait {warCooldownDuration.TotalMinutes} minutes before ending a war.");
                return;
            }

            // End the war
            clan.Enemies.Remove(enemyClanName);
            enemyClan.Enemies.Remove(clan.Name);
            lastWarActionTime = DateTime.UtcNow;  // Update cooldown timestamp
            SaveStorage();

            player.Message($"You have ended the war with '{enemyClanName}'.");
            enemyClan.Members.ForEach(m => FindPlayerById(m).ChatMessage($"The war with '{clan.Name}' has ended."));
            LogEvent($"{player.Name} ended the war with '{enemyClanName}' at {DateTime.UtcNow}");
        }

        [Command("cmsetwelcome")]
        private void CmdSetWelcomeMessage(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("clanmanager.setwelcome"))  // Ensure the player has permission to change the message
            {
                player.Message("You do not have permission to set the welcome message.");
                return;
            }

            if (args.Length == 0)
            {
                player.Message("Usage: /cmsetwelcome <message>");
                return;
            }

            string newMessage = string.Join(" ", args);  // Combine all args into a single string
            configData.WelcomeMessageText = newMessage;  // Update the config
            SaveConfig();  // Save the new welcome message in the config

            player.Message($"Welcome message has been updated to: {newMessage}");
            LogEvent($"{player.Name} updated the welcome message to: \"{newMessage}\"");
        }
        #endregion
    }
}
