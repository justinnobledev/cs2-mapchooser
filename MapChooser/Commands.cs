using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace MapChooser;

public partial class  MapChooser
{
    [ConsoleCommand("css_timeleft", "Prints the timeleft")]
    public void OnTimeLeftCommand(CCSPlayerController? player, CommandInfo cmd)
    {
        var timeLimitConVar = ConVar.Find("mp_timelimit");
        if (timeLimitConVar is null)
        {
            Logger.LogError("[MapChooser] Unable to find \"mp_timelimit\" convar failing");
            return;
        }

        var timeLimit = timeLimitConVar.GetPrimitiveValue<float>() * 60f;
        var timeElapsed = Server.CurrentTime - _startTime;

        var timeString = FormatTime(timeLimit - timeElapsed);
        cmd.ReplyToCommand($"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.timeleft", timeString]}");
    }
    
    [ConsoleCommand("css_rtv", "Rocks the vote")]
    [CommandHelper(whoCanExecute:CommandUsage.CLIENT_ONLY)]
    public void OnRtVCommand(CCSPlayerController? player, CommandInfo cmd)
    {
        if (!_config.AllowRtv)
            return;
        if (!_canRtv)
        {
            cmd.ReplyToCommand(
                $"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.rtv_not_available"]}");
            return;
        }
        if (_rtvCount.Contains(player!.SteamID) || _voteActive) return;
        _rtvCount.Add(player.SteamID);
        var required = (int)Math.Floor(GetOnlinePlayerCount() * _config.RtvPercent);

        Server.PrintToChatAll($"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.rtv", player.PlayerName, _rtvCount.Count, required]}");
        
        if (_rtvCount.Count < required) return;
        _wasRtv = true;
        Server.PrintToChatAll($"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.rtv_vote_starting"]}");
        _mapVoteTimer?.Kill();
        if (_nextMap != "")
        {
            AddTimer(5f, () =>
            {
                if (_maps.Any(map => map.Trim() == "ws:" + _nextMap))
                    Server.ExecuteCommand($"ds_workshop_changelevel {_nextMap}");
                else
                    Server.ExecuteCommand($"changelevel {_nextMap}");
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }else
            StartMapVote();
    }
    
    void DoRtv(CCSPlayerController player)
    {
        if (!_config.AllowRtv)
            return;
        if (!_canRtv)
        {
            player.PrintToChat(
                $"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.rtv_not_available"]}");
            return;
        }
        if (_rtvCount.Contains(player!.SteamID) || _voteActive) return;
        _rtvCount.Add(player.SteamID);
        var required = (int)Math.Ceiling(GetOnlinePlayerCount() * _config.RtvPercent);

        Server.PrintToChatAll($"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.rtv", player.PlayerName, _rtvCount.Count, required]}");
        
        if (_rtvCount.Count < required) return;
        _wasRtv = true;
        Server.PrintToChatAll($"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.rtv_vote_starting"]}");
        _mapVoteTimer?.Kill();
        if (_nextMap != "")
        {
            AddTimer(5f, () =>
            {
                if (_maps.Any(map => map.Trim() == "ws:" + _nextMap))
                    Server.ExecuteCommand($"ds_workshop_changelevel {_nextMap}");
                else
                    Server.ExecuteCommand($"changelevel {_nextMap}");
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }else
            StartMapVote();
    }
    
    [ConsoleCommand("css_unrtv", "No Rocks the vote")]
    [CommandHelper(whoCanExecute:CommandUsage.CLIENT_ONLY)]
    public void OnUnRtVCommand(CCSPlayerController? player, CommandInfo cmd)
    {
        if (!_rtvCount.Contains(player.SteamID)) return;
        _rtvCount.Remove(player.SteamID);
        Server.PrintToChatAll(
            $"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.unrtv", player.PlayerName, _rtvCount.Count, (int)Math.Ceiling(GetOnlinePlayerCount() * _config.RtvPercent)]}");
    }
    
    [ConsoleCommand("css_nominate", "Puts up a map to be in the next vote")]
    [CommandHelper(whoCanExecute:CommandUsage.CLIENT_ONLY)]
    public void OnNominateCommand(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_voteActive) return;
        if (cmd.ArgCount > 2 )
        {
            var arg = cmd.GetArg(2);
            var mapSelect = _maps.Where(m => m.Contains(arg)).ToList();
            if (mapSelect.Count() > 1)
            {
                cmd.ReplyToCommand(Localizer["mapchooser.nominate_ambiguous", arg]);
                return;
            }

            var map = mapSelect.First().Replace("ws:", "").Trim();
            if (_nominations.Values.Contains(map) || _mapHistory.Contains(map) || map.Equals(Server.MapName))
                return;
            
            _nominations[player.SteamID] = map;
            Server.PrintToChatAll(
                $"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.nominate", player.PlayerName, map]}");
            return;
        }
        var menu = new ChatMenu(Localizer["mapchooser.nominate_header"]);
        foreach (var tmp in _maps.Select(map => map.Replace("ws:", "").Trim()))
        {
            if (tmp == Server.MapName)
                menu.AddMenuOption(Localizer["mapchooser.nominate_current_map", tmp], (_, _) => { }, true);
            else if(_mapHistory.Contains(tmp))
                menu.AddMenuOption(Localizer["mapchooser.nominate_recent", tmp], (_, _) => { }, true);
            else if(_nominations.Values.Contains(tmp))
                menu.AddMenuOption(Localizer["mapchooser.nominate_nominated", tmp], (_, _) => { }, true);
            else
                menu.AddMenuOption($"{tmp}", (player, option) =>
                {
                    _nominations[player.SteamID] = tmp;
                    Server.PrintToChatAll(
                        $"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.nominate", player.PlayerName, option.Text]}");
                });
        }
        MenuManager.OpenChatMenu(player, menu);
    }
    
    
    
    
    
    
    private static string FormatTime(float timeF)
    {
        if (timeF <= 0.0f)
        {
            return "N/A";
        }

        var time = new StringBuilder();

        var hours = (int)(timeF / 3600);
        timeF %= 3600;

        var mins = (int)(timeF / 60);
        timeF %= 60;

        var seconds = (int) timeF;

        if (hours > 0)
            time.Append($"{hours:00}:");
        time.Append($"{mins:00}:");
        time.Append($"{seconds:00}.");

        return time.ToString();
    }
}