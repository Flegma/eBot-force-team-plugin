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
    public override string ModuleVersion => "1.0.1";

    private const int MaxRetries = 3;
    private const float RetryIntervalSeconds = 0.5f;

    public override void Load(bool hotReload)
    {
        AddCommand("css_ebot_force_team", "Force a player to a team by SteamID64",
            CommandForceTeam);
        Logger.LogInformation("[EbotForceTeam] Plugin loaded (v{Version})", ModuleVersion);
    }

    private void CommandForceTeam(CCSPlayerController? caller, CommandInfo info)
    {
        if (info.ArgCount < 3)
        {
            Logger.LogWarning("[EbotForceTeam] Not enough arguments (got {Count}, need 3)", info.ArgCount);
            return;
        }

        var steamId64Str = info.ArgByIndex(1);
        var teamStr = info.ArgByIndex(2).ToLower();

        Logger.LogInformation("[EbotForceTeam] Received force_team command: steamid64={SteamId64} team={Team}",
            steamId64Str, teamStr);

        if (!ulong.TryParse(steamId64Str, out var steamId64))
        {
            Logger.LogWarning("[EbotForceTeam] Failed to parse steamid64: '{Raw}'", steamId64Str);
            return;
        }

        var targetTeam = teamStr switch
        {
            "ct" => CsTeam.CounterTerrorist,
            "t"  => CsTeam.Terrorist,
            _    => (CsTeam?)null
        };

        if (targetTeam == null)
        {
            Logger.LogWarning("[EbotForceTeam] Invalid team string: '{Team}'", teamStr);
            return;
        }

        AttemptForceTeam(steamId64, targetTeam.Value, 0);
    }

    private void AttemptForceTeam(ulong steamId64, CsTeam targetTeam, int attempt)
    {
        var player = Utilities.GetPlayerFromSteamId64(steamId64);

        if (player == null || !player.IsValid)
        {
            if (attempt < MaxRetries)
            {
                Logger.LogInformation(
                    "[EbotForceTeam] Player {SteamId64} not found (attempt {Attempt}/{Max}), retrying in {Interval}s",
                    steamId64, attempt + 1, MaxRetries, RetryIntervalSeconds);
                AddTimer(RetryIntervalSeconds, () => AttemptForceTeam(steamId64, targetTeam, attempt + 1));
                return;
            }

            Logger.LogWarning("[EbotForceTeam] Player {SteamId64} not found after {Max} retries, giving up",
                steamId64, MaxRetries);
            return;
        }

        if (player.Team == targetTeam)
        {
            Logger.LogInformation("[EbotForceTeam] Player {Name} ({SteamId64}) already on {Team}, no action needed",
                player.PlayerName, steamId64, targetTeam);
            return;
        }

        Logger.LogInformation("[EbotForceTeam] Switching player {Name} ({SteamId64}) from {Current} to {Target}",
            player.PlayerName, steamId64, player.Team, targetTeam);

        Server.NextFrame(() =>
        {
            player.ChangeTeam(targetTeam);

            Server.NextFrame(() =>
            {
                if (player.IsValid && player.Team == targetTeam)
                {
                    Logger.LogInformation("[EbotForceTeam] Verified: {Name} ({SteamId64}) is now on {Team}",
                        player.PlayerName, steamId64, targetTeam);
                }
                else if (player.IsValid)
                {
                    Logger.LogWarning("[EbotForceTeam] Verification failed: {Name} ({SteamId64}) is on {Actual}, expected {Expected}",
                        player.PlayerName, steamId64, player.Team, targetTeam);
                }
                else
                {
                    Logger.LogWarning("[EbotForceTeam] Player {SteamId64} became invalid after team change", steamId64);
                }
            });
        });
    }
}
