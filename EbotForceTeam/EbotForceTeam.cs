using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

#nullable enable

namespace EbotForceTeam;

public class EbotForceTeam : BasePlugin
{
    public override string ModuleName => "eBot Force Team";
    public override string ModuleVersion => "1.4.0";

    private readonly Dictionary<ulong, CsTeam> _roster = new();
    private readonly HashSet<ulong> _knownPlayers = new();

    public override void Load(bool hotReload)
    {
        AddCommand("css_ebot_force_team", "Force a player to a team immediately",
            CommandForceTeam);
        AddCommand("css_ebot_set_roster", "Register a player's team assignment",
            CommandSetRoster);
        AddCommand("css_ebot_clear_rosters", "Clear all roster assignments",
            CommandClearRosters);
        AddCommand("css_ebot_apply_rosters", "Enforce roster assignments on all connected players",
            CommandApplyRosters);

        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        AddCommandListener("jointeam", OnJoinTeamCommand);

        Logger.LogInformation("[EbotForceTeam] Plugin loaded (v{Version})", ModuleVersion);
    }

    // --- RCON Commands ---

    private void CommandSetRoster(CCSPlayerController? caller, CommandInfo info)
    {
        if (info.ArgCount < 3)
        {
            Logger.LogWarning("[EbotForceTeam] set_roster: not enough args (got {Count}, need 3)", info.ArgCount);
            return;
        }

        var steamId64Str = info.ArgByIndex(1);
        var teamStr = info.ArgByIndex(2).ToLower();

        if (!ulong.TryParse(steamId64Str, out var steamId64))
        {
            Logger.LogWarning("[EbotForceTeam] set_roster: failed to parse steamid64: '{Raw}'", steamId64Str);
            return;
        }

        var targetTeam = ParseTeam(teamStr);
        if (targetTeam == null)
        {
            Logger.LogWarning("[EbotForceTeam] set_roster: invalid team: '{Team}'", teamStr);
            return;
        }

        _roster[steamId64] = targetTeam.Value;
        Logger.LogInformation("[EbotForceTeam] Roster set: {SteamId64} -> {Team}", steamId64, targetTeam.Value);

        // If player is already on the server, move them now
        var player = Utilities.GetPlayerFromSteamId64(steamId64);
        if (player != null && player.IsValid && player.Team != targetTeam.Value
            && player.Team != CsTeam.None && player.Team != CsTeam.Spectator)
        {
            ForcePlayerToTeam(player, targetTeam.Value, false);
        }
    }

    private void CommandClearRosters(CCSPlayerController? caller, CommandInfo info)
    {
        var count = _roster.Count;
        _roster.Clear();
        _knownPlayers.Clear();
        Logger.LogInformation("[EbotForceTeam] Rosters cleared ({Count} entries removed, known players reset)", count);
    }

    private void CommandApplyRosters(CCSPlayerController? caller, CommandInfo info)
    {
        if (_roster.Count == 0)
        {
            Logger.LogInformation("[EbotForceTeam] apply_rosters: no roster entries, nothing to do");
            return;
        }

        Logger.LogInformation("[EbotForceTeam] apply_rosters: enforcing {Count} roster entries on connected players",
            _roster.Count);

        EnforceRosters("apply_rosters");
    }

    private void CommandForceTeam(CCSPlayerController? caller, CommandInfo info)
    {
        if (info.ArgCount < 3)
        {
            Logger.LogWarning("[EbotForceTeam] force_team: not enough args (got {Count}, need 3)", info.ArgCount);
            return;
        }

        var steamId64Str = info.ArgByIndex(1);
        var teamStr = info.ArgByIndex(2).ToLower();

        Logger.LogInformation("[EbotForceTeam] force_team received: steamid64={SteamId64} team={Team}",
            steamId64Str, teamStr);

        if (!ulong.TryParse(steamId64Str, out var steamId64))
        {
            Logger.LogWarning("[EbotForceTeam] force_team: failed to parse steamid64: '{Raw}'", steamId64Str);
            return;
        }

        var targetTeam = ParseTeam(teamStr);
        if (targetTeam == null)
        {
            Logger.LogWarning("[EbotForceTeam] force_team: invalid team: '{Team}'", teamStr);
            return;
        }

        // Also store in roster so future jointeam commands are blocked
        _roster[steamId64] = targetTeam.Value;

        var player = Utilities.GetPlayerFromSteamId64(steamId64);
        if (player == null || !player.IsValid)
        {
            Logger.LogWarning("[EbotForceTeam] force_team: player {SteamId64} not on server, saved to roster",
                steamId64);
            return;
        }

        if (player.Team == targetTeam.Value)
        {
            Logger.LogInformation("[EbotForceTeam] force_team: {Name} ({SteamId64}) already on {Team}",
                player.PlayerName, steamId64, targetTeam.Value);
            return;
        }

        ForcePlayerToTeam(player, targetTeam.Value, false);
    }

    // --- Event Handlers ---

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid)
            return HookResult.Continue;

        var steamId64 = player.SteamID;

        if (!_roster.TryGetValue(steamId64, out var targetTeam))
        {
            Logger.LogInformation("[EbotForceTeam] Connect: {Name} ({SteamId64}) not in roster, no action",
                player.PlayerName, steamId64);
            return HookResult.Continue;
        }

        var isReconnect = _knownPlayers.Contains(steamId64);
        _knownPlayers.Add(steamId64);

        Logger.LogInformation("[EbotForceTeam] Connect: {Name} ({SteamId64}) rostered for {Team} (reconnect={Reconnect})",
            player.PlayerName, steamId64, targetTeam, isReconnect);

        // Small delay to let the client fully initialize before forcing team
        AddTimer(0.5f, () =>
        {
            if (!player.IsValid) return;

            if (player.Team == targetTeam)
            {
                Logger.LogInformation("[EbotForceTeam] Connect: {Name} already on correct team {Team}",
                    player.PlayerName, targetTeam);

                // Reconnect during freeze time: respawn even if already on correct team
                if (isReconnect && !player.PawnIsAlive && CanRespawnForReconnect())
                {
                    player.Respawn();
                    Logger.LogInformation("[EbotForceTeam] Respawned reconnecting {Name} (freeze/warmup/paused)",
                        player.PlayerName);
                }
                return;
            }

            ForcePlayerToTeam(player, targetTeam, isReconnect);
        });

        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (_roster.Count == 0)
            return HookResult.Continue;

        // Delay to let the game finish its own team swaps (half-time, overtime)
        AddTimer(1.0f, () => EnforceRosters("round_start"));

        return HookResult.Continue;
    }

    private HookResult OnJoinTeamCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid)
            return HookResult.Continue;

        var steamId64 = player.SteamID;

        if (!_roster.TryGetValue(steamId64, out var targetTeam))
            return HookResult.Continue;

        // Parse which team they're trying to join
        var requestedTeamStr = info.ArgCount >= 2 ? info.ArgByIndex(1) : "";
        var requestedTeam = requestedTeamStr switch
        {
            "1" => CsTeam.Spectator,
            "2" => CsTeam.Terrorist,
            "3" => CsTeam.CounterTerrorist,
            _   => (CsTeam?)null
        };

        if (requestedTeam == targetTeam)
        {
            Logger.LogInformation("[EbotForceTeam] jointeam: {Name} choosing correct team {Team}, allowing",
                player.PlayerName, targetTeam);
            return HookResult.Continue;
        }

        Logger.LogInformation("[EbotForceTeam] jointeam: {Name} tried to join {Requested}, blocking and forcing {Target}",
            player.PlayerName, requestedTeam?.ToString() ?? requestedTeamStr, targetTeam);

        // Block the manual team choice and force correct team
        Server.NextFrame(() =>
        {
            if (!player.IsValid) return;

            if (player.Team != targetTeam)
            {
                player.SwitchTeam(targetTeam);
            }

            Server.NextFrame(() =>
            {
                if (player.IsValid && !player.PawnIsAlive && CanRespawnGeneral())
                {
                    player.Respawn();
                    Logger.LogInformation("[EbotForceTeam] jointeam: Respawned {Name} (warmup/paused)",
                        player.PlayerName);
                }
            });
        });

        return HookResult.Handled;
    }

    // --- Helpers ---

    private void EnforceRosters(string source)
    {
        var moved = 0;
        var correct = 0;

        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || player.IsBot || player.IsHLTV)
                continue;

            if (player.Team == CsTeam.None || player.Team == CsTeam.Spectator)
                continue;

            if (!_roster.TryGetValue(player.SteamID, out var targetTeam))
                continue;

            if (player.Team == targetTeam)
            {
                correct++;
                continue;
            }

            Logger.LogInformation("[EbotForceTeam] {Source}: {Name} ({SteamId64}) is on {Current}, forcing to {Target}",
                source, player.PlayerName, player.SteamID, player.Team, targetTeam);
            ForcePlayerToTeam(player, targetTeam, false);
            moved++;
        }

        if (moved > 0 || correct > 0)
        {
            Logger.LogInformation("[EbotForceTeam] {Source}: {Moved} moved, {Correct} already correct",
                source, moved, correct);
        }
    }

    private void ForcePlayerToTeam(CCSPlayerController player, CsTeam targetTeam, bool isReconnect)
    {
        Logger.LogInformation("[EbotForceTeam] Forcing {Name} ({SteamId64}) from {Current} to {Target} (reconnect={Reconnect})",
            player.PlayerName, player.SteamID, player.Team, targetTeam, isReconnect);

        Server.NextFrame(() =>
        {
            if (!player.IsValid) return;

            player.SwitchTeam(targetTeam);

            // Verify on next frame
            Server.NextFrame(() =>
            {
                if (!player.IsValid) return;

                if (player.Team == targetTeam)
                {
                    Logger.LogInformation("[EbotForceTeam] Verified: {Name} is now on {Team}",
                        player.PlayerName, targetTeam);

                    if (!player.PawnIsAlive)
                    {
                        // Reconnect: respawn in warmup, paused, OR freeze time
                        // First connect / enforcement: respawn only in warmup or paused
                        var shouldRespawn = isReconnect ? CanRespawnForReconnect() : CanRespawnGeneral();

                        if (shouldRespawn)
                        {
                            player.Respawn();
                            Logger.LogInformation("[EbotForceTeam] Respawned {Name} (reconnect={Reconnect})",
                                player.PlayerName, isReconnect);
                        }
                    }
                }
                else
                {
                    Logger.LogWarning("[EbotForceTeam] Verification failed: {Name} is on {Actual}, expected {Expected}",
                        player.PlayerName, player.Team, targetTeam);
                }
            });
        });
    }

    /// <summary>
    /// Reconnecting player: respawn in warmup, freeze time, or paused.
    /// </summary>
    private bool CanRespawnForReconnect()
    {
        var gameRules = GetGameRules();
        if (gameRules == null) return false;
        return gameRules.WarmupPeriod || gameRules.FreezePeriod || gameRules.GamePaused;
    }

    /// <summary>
    /// General case (first connect, enforcement, jointeam): respawn only in warmup or paused.
    /// </summary>
    private bool CanRespawnGeneral()
    {
        var gameRules = GetGameRules();
        if (gameRules == null) return false;
        return gameRules.WarmupPeriod || gameRules.GamePaused;
    }

    private CCSGameRules? GetGameRules()
    {
        return Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules;
    }

    private static CsTeam? ParseTeam(string teamStr)
    {
        return teamStr switch
        {
            "ct" => CsTeam.CounterTerrorist,
            "t"  => CsTeam.Terrorist,
            _    => null
        };
    }
}
