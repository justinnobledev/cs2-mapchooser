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
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace MapChooser;

[MinimumApiVersion(198)]
public partial class MapChooser : BasePlugin
{
    public override string ModuleName { get; } = "Map Chooser";
    public override string ModuleVersion { get; } = "1.3.1";
    public override string ModuleDescription { get; } = "Handles map voting and map changing";
    public override string ModuleAuthor { get; } = "Retro - https://insanitygaming.net/";

    private float _startTime = 0f;
    // private ConVar? _timeLimitConVar = null;

    private string _mapsPath = "";
    
    private string _configPath = "";
    private Config _config = new();

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
    private Timer? _rtvDelayTimer;

    private string _nextMap = "";

    private bool _firstRoundStarted = false;
    private Timer? _fixSwitchMapTimer;

    public override void Load(bool hotReload)
    {
        _configPath = Path.Combine(ModuleDirectory, "config.json");
        _mapsPath = Path.Combine(ModuleDirectory, "maps.txt");
        
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);

        RegisterEventHandler<EventRoundStart>(OnEventRoundStart);
        RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEndEvent);

        RegisterEventHandler<EventCsIntermission>((@event, info) =>
        {
            Logger.LogInformation("EventCsIntermission triggered");
            return HookResult.Continue;
        });
        
        AddCommandListener("say", OnSayCommand);
        AddCommandListener("say_team", OnSayCommand);

        if (hotReload)
        {
            OnMapStart(Server.MapName);
            AddTimer(1f, ()=>
            {
                hotReload = false;
                SetupTimeLimitCountDown();
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }
    }

    private HookResult OnEventRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (!_firstRoundStarted)
        {
            _rtvDelayTimer?.Kill();
            _mapVoteTimer?.Kill();
            SetupTimeLimitCountDown();
        }
        _firstRoundStarted = true;

        return HookResult.Continue;
    }

    private HookResult OnMatchEndEvent(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        if (_fixSwitchMapTimer is not null)
        {
            _fixSwitchMapTimer.Kill();
            _fixSwitchMapTimer = null;
        }
        Logger.LogInformation("OnMatchEndEvent triggered");
        var convar = ConVar.Find("mp_match_restart_delay");
        if (convar is null)
        {
            SwitchMap();
        }
        else
        {
            var delay = convar.GetPrimitiveValue<int>();
            AddTimer(Math.Max(0f, delay - 0.5f), () =>
            {
                SwitchMap();
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }
        return HookResult.Continue;
    }

    #region Listeners
    private void OnMapStart(string mapName)
    {
        Logger.LogInformation($"OnMapStart {mapName}");
        try
        {
            var json = File.ReadAllText(_configPath);
            _config = JsonSerializer.Deserialize<Config>(json);
        }
        catch (Exception e)
        {
            Logger.LogError($"Error occurred while reading map list: {e.Message}");
            Logger.LogError(e.StackTrace);
        }
        

        _maps = new List<string>(File.ReadLines(_mapsPath));
        if (_mapHistory.Count > _config.ExcludeMaps)
            _mapHistory.RemoveAt(0);
        
        SetupTimeLimitCountDown();
    }

    private void OnMapEnd()
    {
        _firstRoundStarted = false;
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
        // _timeLimitConVar = null;

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
        _rtvDelayTimer?.Kill();
        _rtvDelayTimer = null;
    }
    #endregion

    private void SetupTimeLimitCountDown()
    {
        var timeLimitConVar = ConVar.Find("mp_timelimit");
        if (timeLimitConVar is null)
        {
            Logger.LogError("[MapChooser] Unable to find \"mp_timelimit\" convar failing");
            return;
        }

        if (timeLimitConVar.GetPrimitiveValue<float>() <= 0.1) return;
        
        Logger.LogInformation($"Setting rtv delay to {_config.RtvDelay * 60f} {_config.RtvDelay}");
        _rtvDelayTimer?.Kill();
        if (_config.RtvDelay < 0.1f)
        {
            _canRtv = true;
        }
        else
            _rtvDelayTimer = AddTimer(_config.RtvDelay * 60f, () =>
            {
                Server.PrintToChatAll($"{Localizer["mapchooser.prefix"]} {Localizer["mapchooser.rtv_enabled"]}");
                _canRtv = true;
            }, TimerFlags.STOP_ON_MAPCHANGE);

        _startTime = Server.CurrentTime;
        _mapVoteTimer= AddTimer((timeLimitConVar.GetPrimitiveValue<float>() * 60f) - (_config.VoteStartTime * 60f),  StartMapVote, TimerFlags.STOP_ON_MAPCHANGE);
        Logger.LogInformation("Setting map vote timer to " + ((timeLimitConVar.GetPrimitiveValue<float>() * 60f) - (_config.VoteStartTime * 60f)));
    }
    
    #region Vote
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
        

        _mapVoteTimer = AddTimer(Math.Min(_config.VoteDuration, 60f), OnVoteFinished, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void OnVoteFinished()
    {
        _voteActive = false;
        _mapVoteTimer?.Kill();
        if (_totalVotes == 0)
        {
            if (_wasRtv)
            {
                _wasRtv = false;
                _rtvDelayTimer?.Kill();
                _rtvDelayTimer = AddTimer(_config.RtvDelay * 60f, () =>
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
                    () => { Logger.LogInformation("Ending round, no votes"); GetGameRules().TerminateRound(5.0f, RoundEndReason.RoundDraw); }, TimerFlags.STOP_ON_MAPCHANGE);
            }
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
                }, TimerFlags.STOP_ON_MAPCHANGE);
            }
            else
            {
                _canRtv = false;
                _rtvDelayTimer?.Kill();
                _rtvDelayTimer = AddTimer(_config.RtvDelay * 60f, () =>
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
                var timeLimitConVar = ConVar.Find("mp_timelimit");
                if (timeLimitConVar is null)
                {
                    Logger.LogCritical("Unable to find \"mp_timelimit\"");
                }else
                {
                    Logger.LogInformation($"Setting mp_timelimit to {timeLimitConVar.GetPrimitiveValue<float>() + _config.ExtendTimeStep}");
                    timeLimitConVar.SetValue(timeLimitConVar.GetPrimitiveValue<float>() + _config.ExtendTimeStep);
                    if(timeLimitConVar.GetPrimitiveValue<float>() > 0f)
                        _mapVoteTimer = AddTimer((_config.ExtendTimeStep * 60f) + (60 - Math.Min(_config.VoteDuration, 60f)), StartMapVote,
                            TimerFlags.STOP_ON_MAPCHANGE);
                    
                    Logger.LogInformation(
                        $"Setting next map vote time to {(_config.ExtendTimeStep * 60f) + (60 - Math.Min(_config.VoteDuration, 60f))}");
                }

                _extends++;
                
                _canRtv = false;
                _rtvDelayTimer?.Kill();
                _rtvDelayTimer = AddTimer(_config.RtvDelay * 60f, () =>
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
                    Logger.LogInformation($"Creating timer for timelimit enforcer {_config.VoteStartTime * 60f - Math.Min(_config.VoteDuration, 60f)} seconds");
                    _mapVoteTimer = AddTimer((_config.VoteStartTime * 60f) - Math.Min(_config.VoteDuration, 60f), () =>
                    {
                        Logger.LogInformation("Ending map due to time limit being reached.");
                        var restartDelay = ConVar.Find("mp_match_restart_delay")?.GetPrimitiveValue<int>() ?? 5;
                        Server.ExecuteCommand("mp_timelimit 1");
                        _fixSwitchMapTimer = AddTimer(7.0f, SwitchMap, TimerFlags.STOP_ON_MAPCHANGE);
                        Server.NextFrame(()=> { Logger.LogInformation("Ending round, next map vote over");
                            GetGameRules().TerminateRound(restartDelay, RoundEndReason.RoundDraw);
                        });
                    }, TimerFlags.STOP_ON_MAPCHANGE);
                }
            }
        }

        _playerVotes.Clear();
        _votes.Clear();
        _nominations.Clear();
        _totalVotes = 0;
    }
    #endregion
    
    
    #region Helpers
    private static CCSGameRules GetGameRules()
    {
        return Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules!;
    }
    
    private void SwitchMap()
    {
        Logger.LogInformation($"Switching map to {_nextMap}");
        if (_maps.Any(map => map.Trim() == "ws:" + _nextMap))
            Server.ExecuteCommand($"ds_workshop_changelevel {_nextMap}");
        else
            Server.ExecuteCommand($"changelevel {_nextMap}");
    }
    
    private int GetOnlinePlayerCount(bool countSpec = false)
    {
        var players = Utilities.GetPlayers().Where((player) => player is {IsValid: true, Connected: PlayerConnectedState.PlayerConnected, IsBot: false, IsHLTV: false});
        if (!countSpec) players = players.Where((player) => player.TeamNum > 1);
        return players.Count();
    }
    #endregion
}
