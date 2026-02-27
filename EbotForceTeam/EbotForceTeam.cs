using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace EbotForceTeam;

public class EbotForceTeam : BasePlugin
{
    public override string ModuleName => "eBot Force Team";
    public override string ModuleVersion => "1.0.0";

    public override void Load(bool hotReload)
    {
        AddCommand("css_ebot_force_team", "Force a player to a team by SteamID64",
            CommandForceTeam);
    }

    #nullable disable
    private void CommandForceTeam(CCSPlayerController caller, CommandInfo info)
    #nullable restore
    {
        if (info.ArgCount < 3) return;

        var steamId64Str = info.ArgByIndex(1);
        var teamStr = info.ArgByIndex(2).ToLower();

        if (!ulong.TryParse(steamId64Str, out var steamId64)) return;

        var targetTeam = teamStr switch
        {
            "ct" => CsTeam.CounterTerrorist,
            "t"  => CsTeam.Terrorist,
            _    => (CsTeam?)null
        };
        if (targetTeam == null) return;

        var player = Utilities.GetPlayerFromSteamId64(steamId64);
        if (player == null || !player.IsValid) return;

        if (player.Team != targetTeam.Value)
        {
            player.SwitchTeam(targetTeam.Value);
        }
    }
}
