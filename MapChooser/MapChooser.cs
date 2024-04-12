using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace MapChooser;

public class Config
{
    public float VoteStartTime { get; set; } = 3.0f;
    public bool AllowExtend { get; set; } = true;
    public float ExtendTimeStep { get; set; } = 10f;
    public int ExtendLimit { get; set; } = 3;
    public int ExcludeMaps { get; set; } = 0;
    public int IncludeMaps { get; set; } = 5;
    public bool IncludeCurrent { get; set; } = false;
    [JsonPropertyName("DontChangeRTV")]
    public bool DontChangeRtv { get; set; } = true;
    public float VoteDuration { get; set; } = 15f;
    // TODO: Add in run off voting
    // public bool RunOfFVote { get; set; } = true;
    // public float VotePercent { get; set; } = 0.6f;
    public bool IgnoreSpec { get; set; } = true;
    public bool AllowRtv { get; set; } = true;
    [JsonPropertyName("RTVPercent")]
    public float RtvPercent { get; set; } = 0.6f;
    [JsonPropertyName("RTVDelay")]
    public float RtvDelay { get; set; } = 3.0f;
    public bool EnforceTimeLimit { get; set; } = true;
}

[MinimumApiVersion(198)]
public class MapChooser : BasePlugin
{
    public override string ModuleName { get; } = "Map Chooser";
    public override string ModuleVersion { get; } = "1.3";
    public override string ModuleDescription { get; } = "Handles map voting and map changing";
    public override string ModuleAuthor { get; } = "Retro - https://insanitygaming.net/";

    private double _startTime = 0f;
    private ConVar? _timeLimitConVar = null;

    private string _mapsPath = "";
    
    private string _configPath = "";
    private Config _config = new Config();

    private List<string> _mapHistory = new();
    private Dictionary<ulong, string> _nominations = new();
    private List<string> _maps = new List<string>();
    private Dictionary<ulong, string> _playerVotes = new();

    private Dictionary<string, int> _votes = new();

    private int _totalVotes = 0;
    private bool _voteActive = false;
    private int _extends = 0;

    private List<ulong> _rtvCount = new();
    private bool _wasRtv = false;
    private bool _canRtv = false;

    private Timer? _mapVoteTimer;

    private string _nextMap = "";

    public override void Load(bool hotReload)
    {
        _configPath = Path.Combine(ModuleDirectory, "config.json");
        _mapsPath = Path.Combine(ModuleDirectory, "maps.txt");
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);

        RegisterEventHandler<EventRoundStart>(EventOnRoundStart);
        RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEndEvent);
 
        AddCommandListener("say", (player, info) =>
        {
            if (player == null) return HookResult.Continue;
            if (info.GetArg(1).Equals("rtv"))
            {
                DoRtv(player);
            }
            return HookResult.Continue;
        });
        AddCommandListener("say_team", (player, info) =>
        {
            if (player == null) return HookResult.Continue;
            if (info.GetArg(1).Equals("rtv"))
            {
                DoRtv(player);
            }
            return HookResult.Continue;
        });
        

        if (hotReload)
        {
            OnMapStart(Server.MapName);
            AddTimer(0.5f, SetupTimeLimitCountDown);
        }
    }
    
    private int GetOnlinePlayerCount(bool countSpec = false)
    {
        var players = Utilities.GetPlayers().Where((player) => player is {IsValid: true, Connected: PlayerConnectedState.PlayerConnected, IsBot: false, IsHLTV: false});
        if (!countSpec) players = players.Where((player) => player.TeamNum > 1);
        return players.Count();
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
            if (_maps.Any(map => map.Trim() == "ws:" + _nextMap))
                Server.ExecuteCommand($"ds_workshop_changelevel {_nextMap}");
            else
                Server.ExecuteCommand($"changelevel {_nextMap}");
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
        var required = (int)Math.Floor(GetOnlinePlayerCount() * _config.RtvPercent);

        Server.PrintToChatAll($"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.rtv", player.PlayerName, _rtvCount.Count, required]}");
        
        if (_rtvCount.Count < required) return;
        _wasRtv = true;
        Server.PrintToChatAll($"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.rtv_vote_starting"]}");
        _mapVoteTimer?.Kill();
        if (_nextMap != "")
        {
            if (_maps.Any(map => map.Trim() == "ws:" + _nextMap))
                Server.ExecuteCommand($"ds_workshop_changelevel {_nextMap}");
            else
                Server.ExecuteCommand($"changelevel {_nextMap}");
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
            $"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.unrtv", player.PlayerName, _rtvCount.Count, GetOnlinePlayerCount()]}");
    }
    
    [ConsoleCommand("css_nominate", "Puts up a map to be in the next vote")]
    [CommandHelper(whoCanExecute:CommandUsage.CLIENT_ONLY)]
    public void OnNominateCommand(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_voteActive) return;
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
        ChatMenus.OpenMenu(player, menu);
    }

    private HookResult EventOnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (_mapVoteTimer == null && !_voteActive && _nextMap == "")
        {
            SetupTimeLimitCountDown();
        }

        return HookResult.Continue;
    }

    private void SetupTimeLimitCountDown()
    {
        _timeLimitConVar = ConVar.Find("mp_timelimit");
        if (_timeLimitConVar == null)
        {
            Logger.LogError("[MapChooser] Unable to find \"mp_timelimit\" convar failing");
            return;
        }
        Console.WriteLine($"Setting rtv delay to {_config.RtvDelay * 60f} {_config.RtvDelay}");
        if (_config.RtvDelay < 0.1f)
        {
            _canRtv = true;
        }
        else
            AddTimer(_config.RtvDelay * 60f, () =>
            {
                Server.PrintToChatAll($"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.rtv_enabled"]}");
                _canRtv = true;
            }, TimerFlags.STOP_ON_MAPCHANGE);

        _startTime = Server.EngineTime;
        _mapVoteTimer= AddTimer((_timeLimitConVar.GetPrimitiveValue<float>() * 60f) - (_config.VoteStartTime * 60f),  StartMapVote, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void StartMapVote()
    {
        _voteActive = true;
        _totalVotes = 0;
        _votes.Clear();
        _rtvCount.Clear();
        _mapVoteTimer = null;
        var menu = new ChatMenu(Localizer["mapchooser.vote_header"]);
        var voteMaps = new List<string>(_maps);
        var nextMap = new List<string>(_nominations.Values);
        if (!_config.IncludeCurrent) voteMaps.Remove("ws:"+Server.MapName);
        var random = new Random();
        if (_config.ExcludeMaps > 0)
        {
            voteMaps = voteMaps.Where(map => !_mapHistory.Contains(map.Replace("ws:", "").Trim()) && !_nominations.Values.Contains(map.Replace("ws:", "").Trim())).ToList();
        }
        
        if (nextMap.Count < _config.IncludeMaps)
        {
            while (nextMap.Count < _config.IncludeMaps)
            {
                if (voteMaps.Count == 0) break;
                var index = random.Next(0, voteMaps.Count - 1);
                nextMap.Add(voteMaps[index]);
                voteMaps.RemoveAt(index);
            }
        }
        
        var number = 1;
        if (_wasRtv)
        {
            number++;
            menu.AddMenuOption(Localizer["mapchooser.option_dont_change"], (controller, option) =>
            {
                if (!_voteActive) return;
                if (_playerVotes.TryGetValue(controller.SteamID, out var vote))
                    _votes[vote]--;
                if (_votes.TryGetValue(option.Text, out var count))
                    _votes[option.Text] = count + 1;
                else
                    _votes[option.Text] = 1;

                if(!_playerVotes.ContainsKey(controller.SteamID))
                    _totalVotes++;
                _playerVotes[controller.SteamID] = option.Text;
                Server.PrintToChatAll(
                    $"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.voted_for", controller.PlayerName, option.Text]}");
            });
        }
        
        for (var i = 0; i < _config.IncludeMaps; i++)
        {
            if (nextMap.Count == 0) break;
            var index = random.Next(0, nextMap.Count - 1);
            var map = nextMap.ElementAt(index).Replace("ws:", "").Trim();
            nextMap.RemoveAt(index);
            number++;
            menu.AddMenuOption(map, (controller, option) =>
            {
                if (!_voteActive) return;
                if (_playerVotes.TryGetValue(controller.SteamID, out var vote))
                    _votes[vote]--;
                if (_votes.TryGetValue(option.Text, out var count))
                    _votes[option.Text] = count + 1;
                else
                    _votes[option.Text] = 1;

                if(!_playerVotes.ContainsKey(controller.SteamID))
                    _totalVotes++;
                _playerVotes[controller.SteamID] = option.Text;
                
                Server.PrintToChatAll(
                    $"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.voted_for", controller.PlayerName, option.Text]}");
            });
        }

        if (!_wasRtv && _config.AllowExtend && _extends < _config.ExtendLimit)
        {
            menu.AddMenuOption("Extend", (controller, option) =>
            {
                if (!_voteActive) return;
                if (_playerVotes.TryGetValue(controller.SteamID, out var vote))
                    _votes[vote]--;
                if (_votes.TryGetValue(option.Text, out var count))
                    _votes[option.Text] = count + 1;
                else
                    _votes[option.Text] = 1;

                if(!_playerVotes.ContainsKey(controller.SteamID))
                    _totalVotes++;
                
                _playerVotes[controller.SteamID] = option.Text;
                Server.PrintToChatAll(
                    $"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.voted_for", controller.PlayerName, option.Text]}");
            });
        }

        foreach (var player in Utilities.GetPlayers())
        {
            MenuManager.OpenChatMenu(player, menu);
        }
        

        AddTimer(Math.Min(_config.VoteDuration, 60f), OnVoteFinished, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private static CCSGameRules GetGameRules()
    {
        return Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules!;
    }

    private void OnVoteFinished()
    {
        _voteActive = false;
        if (_totalVotes == 0)
        {
            ChooseRandomNextMap();
            return;
        }
        var winner = "";
        var winnerVotes = 0;
        foreach (var (map, count) in _votes)
        {
            if (winner == "")
            {
                winner = map;
                winnerVotes = count;
            }
            else if (count > winnerVotes)
            {
                winner = map;
                winnerVotes = count;
            }
        }

        
        Server.PrintToChatAll($"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.map_won", winner]}");

        if (_wasRtv)
        {
            _wasRtv = false;
            if (!winner.Equals(Localizer["mapchooser.option_dont_change"]))
            {
                AddTimer(5f, () =>
                {
                    if (_maps.Any(map => map.Trim() == "ws:" + winner))
                        Server.ExecuteCommand($"ds_workshop_changelevel {winner}");
                    else
                        Server.ExecuteCommand($"changelevel {winner}");
                });
            }
            else
            {
                _canRtv = false;
                AddTimer(_config.RtvDelay * 60f, () =>
                {
                    Server.PrintToChatAll($"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.rtv_enabled"]}");
                    _canRtv = true;
                }, TimerFlags.STOP_ON_MAPCHANGE);
            }
        }
        else
        {
            if (winner.Equals(Localizer["mapchooser.option_extend"]))
            {
                _timeLimitConVar.SetValue(_timeLimitConVar.GetPrimitiveValue<float>() + _config.ExtendTimeStep);
                _extends++;
                _mapVoteTimer = AddTimer(_config.ExtendTimeStep + (60 - Math.Min(_config.VoteDuration, 60f)), StartMapVote,
                    TimerFlags.STOP_ON_MAPCHANGE);
                _canRtv = false;
                AddTimer(_config.RtvDelay * 60f, () =>
                {
                    Server.PrintToChatAll($"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.rtv_enabled"]}");
                    _canRtv = true;
                }, TimerFlags.STOP_ON_MAPCHANGE);
            }
            else
            {
                _nextMap = winner;
                if(_config.EnforceTimeLimit)
                {
                    _mapVoteTimer = AddTimer((_config.VoteStartTime * 60f) - Math.Min(_config.VoteDuration, 60f), () =>
                    {
                        var restartDelay =ConVar.Find("mp_round_restart_delay")?.GetPrimitiveValue<float>() ?? 5f;
                        GetGameRules().TerminateRound(restartDelay, RoundEndReason.RoundDraw);
                    }, TimerFlags.STOP_ON_MAPCHANGE);
                }
            }
        }

        _playerVotes.Clear();
        _votes.Clear();
        _nominations.Clear();
        _totalVotes = 0;
    }

    private void ChooseRandomNextMap()
    {
        if (_wasRtv)
        {
            _wasRtv = false;
            AddTimer(_config.RtvDelay * 60f, () =>
            {
                Server.PrintToChatAll($"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.rtv_enabled"]}");
                _canRtv = true;
            }, TimerFlags.STOP_ON_MAPCHANGE);
            return;
        }
        var nextMap = new List<string>(_maps);
        if (!_config.IncludeCurrent) nextMap.Remove("ws:"+Server.MapName);
        var random = new Random();
        if (_config.ExcludeMaps > 0)
        {
            nextMap = nextMap.Where(map => !_mapHistory.Contains(map.Replace("ws:", "").Trim()) && !_nominations.Values.Contains(map.Replace("ws:", "").Trim())).ToList();
        }
        _nextMap = nextMap.ElementAt(random.Next(0, nextMap.Count - 1)).Replace("ws:", "");
        Server.PrintToChatAll($"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.map_won", _nextMap]}");
        if(_config.EnforceTimeLimit)
        {
            _mapVoteTimer = AddTimer((_config.VoteStartTime * 60f) - _config.VoteDuration,
                () => { GetGameRules().TerminateRound(5.0f, RoundEndReason.RoundDraw); }, TimerFlags.STOP_ON_MAPCHANGE);
        }
    }

    private void OnMapStart(string mapName)
    {
        var json = File.ReadAllText(_configPath);
        _config = JsonSerializer.Deserialize<Config>(json);

        _maps = new List<string>(File.ReadLines(_mapsPath));
        if (_mapHistory.Count > _config.ExcludeMaps)
            _mapHistory.RemoveAt(0);
    }

    public HookResult OnMatchEndEvent(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        var convar = ConVar.Find("mp_match_restart_delay");
        if (convar == null)
        {
            if (_maps.Any(map => map.Trim() == "ws:" + _nextMap))
                Server.ExecuteCommand($"ds_workshop_changelevel {_nextMap}");
            else
                Server.ExecuteCommand($"changelevel {_nextMap}");
        }
        else
        {
            AddTimer((float) Math.Min(0f, convar.GetPrimitiveValue<int>() - 1.0), () =>
            {
                if (_maps.Any(map => map.Trim() == "ws:" + _nextMap))
                    Server.ExecuteCommand($"ds_workshop_changelevel {_nextMap}");
                else
                    Server.ExecuteCommand($"changelevel {_nextMap}");
            });
        }
        return HookResult.Continue;
    }

    private void OnMapEnd()
    {
        _nextMap = "";
        //Add the current map to map history
        _mapHistory.Add(Server.MapName);

        //Clear the various lists/dictionaries
        _nominations.Clear();
        _maps.Clear();
        _playerVotes.Clear();
        _votes.Clear();
        _rtvCount.Clear();

        //Set mp_timelimit convar handler to null
        _timeLimitConVar = null;

        //Reinitialize values to 0
        _startTime = 0;
        _totalVotes = 0;
        _extends = 0;

        //Reinitialize values to false
        _voteActive = false;
        _wasRtv = false;
        _canRtv = false;
        _mapVoteTimer?.Kill();
        _mapVoteTimer = null;
    }
}
