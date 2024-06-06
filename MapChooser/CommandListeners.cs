using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace MapChooser;

public partial class MapChooser
{
    public HookResult OnSayCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null) return HookResult.Continue;
        if (info.GetArg(1).ToLower().Equals("rtv"))
        {
            DoRtv(player);
        }
        else if (info.GetArg(1).ToLower().Equals("nextmap"))
        {
            if (string.IsNullOrEmpty(_nextMap))
            {
                player.PrintToChat($"{Localizer["mapchooser.prefix"]} The next map has yet to be chosen");
            }else
                player.PrintToChat($"{Localizer["mapchooser.prefix"]} The next map is {ChatColors.Green}{_nextMap}");
        }
        else if (info.GetArg(1).ToLower().Equals("timeleft"))
        {
            var timeLimitConVar = ConVar.Find("mp_timelimit");
            if (timeLimitConVar is null)
            {
                Logger.LogError("[MapChooser] Unable to find \"mp_timelimit\" convar failing");
                return HookResult.Continue;
            }

            var timeLimit = timeLimitConVar.GetPrimitiveValue<float>() * 60f;
            var timeElapsed = Server.CurrentTime - _startTime;

            var timeString = FormatTime(timeLimit - timeElapsed);
            player.PrintToChat($"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.timeleft", timeString]}");
        }
        return HookResult.Continue;
    }
}