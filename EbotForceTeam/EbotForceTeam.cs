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
    public override string ModuleVersion => "1.1.0";

    private readonly Dictionary<ulong, CsTeam> _roster = new();

    public override void Load(bool hotReload)
    {
        AddCommand("css_ebot_force_team", "Force a player to a team immediately",
            CommandForceTeam);
        AddCommand("css_ebot_set_roster", "Register a player's team assignment",
            CommandSetRoster);
        AddCommand("css_ebot_clear_rosters", "Clear all roster assignments",
            CommandClearRosters);

        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
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
            ForcePlayerToTeam(player, targetTeam.Value);
        }
    }

    private void CommandClearRosters(CCSPlayerController? caller, CommandInfo info)
    {
        var count = _roster.Count;
        _roster.Clear();
        Logger.LogInformation("[EbotForceTeam] Rosters cleared ({Count} entries removed)", count);
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

        ForcePlayerToTeam(player, targetTeam.Value);
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

        Logger.LogInformation("[EbotForceTeam] Connect: {Name} ({SteamId64}) is rostered for {Team}, forcing after delay",
            player.PlayerName, steamId64, targetTeam);

        // Small delay to let the client fully initialize before forcing team
        AddTimer(0.5f, () =>
        {
            if (!player.IsValid) return;

            if (player.Team == targetTeam)
            {
                Logger.LogInformation("[EbotForceTeam] Connect: {Name} already on correct team {Team}",
                    player.PlayerName, targetTeam);
                return;
            }

            ForcePlayerToTeam(player, targetTeam);
        });

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
            if (player.IsValid && player.Team != targetTeam)
            {
                player.SwitchTeam(targetTeam);
            }
        });

        return HookResult.Handled;
    }

    // --- Helpers ---

    private void ForcePlayerToTeam(CCSPlayerController player, CsTeam targetTeam)
    {
        Logger.LogInformation("[EbotForceTeam] Forcing {Name} ({SteamId64}) from {Current} to {Target}",
            player.PlayerName, player.SteamID, player.Team, targetTeam);

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
                }
                else
                {
                    Logger.LogWarning("[EbotForceTeam] Verification failed: {Name} is on {Actual}, expected {Expected}",
                        player.PlayerName, player.Team, targetTeam);
                }
            });
        });
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
