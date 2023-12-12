using System.Text;
using System.Text.Json;
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
    public bool DontChangeRtv { get; set; } = true;
    public float VoteDuration { get; set; } = 15f;
    public bool RunOfFVote { get; set; } = true;
    public float VotePercent { get; set; } = 0.6f;
    public bool IgnoreSpec { get; set; } = true;
    public bool AllowRtv { get; set; } = true;
    public float RtvPercent { get; set; } = 0.6f;
    public float RtvDelay { get; set; } = 3.0f;
}

[MinimumApiVersion(61)]
public class MapChooser : BasePlugin
{
    public override string ModuleName { get; } = "Map Chooser";
    public override string ModuleVersion { get; } = "1.2.3";
    public override string ModuleDescription { get; } = "Handles map voting and map changing";
    public override string ModuleAuthor { get; } = "Retro - https://insanitygaming.net/";

    private double _startTime = 0f;
    private ConVar? _timeLimitConVar = null;

    private string _mapsPath = "";
    
    private string _configPath = "";
    private Config _config = new Config();

    private List<string> _mapHistory = new List<string>();
    private Dictionary<ulong, string> _nominations = new Dictionary<ulong, string>();
    private List<string> _maps = new List<string>();
    private Dictionary<ulong, string> _playerVotes = new Dictionary<ulong, string>();

    private Dictionary<string, int> _votes = new Dictionary<string, int>();

    private int _totalVotes = 0;
    private bool _voteActive = false;
    private int _extends = 0;

    private List<ulong> _rtvCount = new List<ulong>();
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
        RegisterListener<Listeners.OnServerHibernationUpdate>((hiberNating) =>
            Console.WriteLine($"[Mapchooser] The server hibernation is now: {hiberNating}"));

        RegisterEventHandler<EventRoundStart>(EventOnRoundStart);
        RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEndEvent);

        if (hotReload)
        {
            OnMapStart(Server.MapName);
        }
    }

    // private void Log(string message)
    // {
    //     using var writer = new StreamWriter(Path.Combine(ModuleDirectory, "map_log.log"), append: true);
    //     writer.WriteLine($"{DateTime.Now:yyyy/MM/dd HH:mm:ss} {message}");
    // }

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

    // [ConsoleCommand("css_timeleft", "Prints the timeleft to chat")]
    // public void OnTimeLeftCommand(CCSPlayerController? player, CommandInfo cmd)
    // {
    //     if (_startTime == 0 || _timeLimitConVar == null) return;
    //     var timeLeft = _timeLimitConVar.GetPrimitiveValue<float>() * 60f;
    //     var elapsed = Server.CurrentTime - _startTime;
    //     var endTime = timeLeft - elapsed;
    //     cmd.ReplyToCommand($" {ChatColors.LightRed}[IG] {ChatColors.Default}The map will end in {ChatColors.LightRed}{FormatTime((float)endTime)}");
    // }

    [ConsoleCommand("css_rtv", "Rocks the vote")]
    [CommandHelper(whoCanExecute:CommandUsage.CLIENT_ONLY)]
    public void OnRtVCommand(CCSPlayerController? player, CommandInfo cmd)
    {
        if (!_config.AllowRtv)
            return;
        if (!_canRtv)
        {
            cmd.ReplyToCommand(
                $" {ChatColors.LightRed}[IG] {ChatColors.LightRed}RTV{ChatColors.Default} is not currently avaliable.");
            return;
        }
        if (_rtvCount.Contains(player!.SteamID) || _voteActive) return;
        _rtvCount.Add(player.SteamID);
        Server.PrintToConsole($"{_rtvCount.Count} - {Utilities.GetPlayers().Count(rtver => rtver.TeamNum > 1) * _config.RtvPercent}");
        
        if (_rtvCount.Count < Math.Floor(Utilities.GetPlayers().Count(rtver => rtver.TeamNum > 1) * _config.RtvPercent)) return;
        _wasRtv = true;
        Server.PrintToChatAll($" {ChatColors.LightRed}[IG] {ChatColors.LightRed}RTV{ChatColors.Default} vote is starting.");
        _mapVoteTimer?.Kill();
        StartMapVote();
    }
    
    [ConsoleCommand("css_unrtv", "No Rocks the vote")]
    [CommandHelper(whoCanExecute:CommandUsage.CLIENT_ONLY)]
    public void OnUnRtVCommand(CCSPlayerController? player, CommandInfo cmd)
    {
        if (!_rtvCount.Contains(player.SteamID)) return;
        _rtvCount.Remove(player.SteamID);
        Server.PrintToChatAll($" {ChatColors.LightRed}[IG]{ChatColors.Default} {ChatColors.Green}{player.PlayerName}{ChatColors.Default} has removed their vote to {ChatColors.Green}rtv{ChatColors.Default}[{_rtvCount.Count}/{Utilities.GetPlayers().Count(player => player.TeamNum > 1)}].");
    }
    
    [ConsoleCommand("css_nominate", "Puts up a map to be in the next vote")]
    [CommandHelper(whoCanExecute:CommandUsage.CLIENT_ONLY)]
    public void OnNominateCommand(CCSPlayerController? player, CommandInfo cmd)
    {
        if (_voteActive) return;
        var menu = new ChatMenu("Nominations Menu - Type !# to nominate");
        foreach (var tmp in _maps.Select(map => map.Replace("ws:", "").Trim()))
        {
            if (tmp == Server.MapName)
                menu.AddMenuOption($"{tmp} - Current Map", (_, _) => { }, true);
            else if(_mapHistory.Contains(tmp))
                menu.AddMenuOption($"{tmp} - Recently Played", (_, _) => { }, true);
            else if(_nominations.Values.Contains(tmp))
                menu.AddMenuOption($"{tmp} - Already Nominated", (_, _) => { }, true);
            else
                menu.AddMenuOption($"{tmp}", (player, option) =>
                {
                    _nominations[player.SteamID] = tmp;
                    Server.PrintToChatAll($" {ChatColors.LightRed}[IG]{ChatColors.Default} {ChatColors.Green}{player.PlayerName}{ChatColors.Default} has nominated {ChatColors.Green}{option.Text}{ChatColors.Default}.");
                });
        }
        ChatMenus.OpenMenu(player, menu);
    }

    private HookResult EventOnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (_mapVoteTimer == null && !_voteActive && _nextMap == "")
        {
            Console.WriteLine("Detected new map starting timer");
            SetupTimeLimitCountDown();
        }

        return HookResult.Continue;
    }

    private void SetupTimeLimitCountDown()
    {
        _timeLimitConVar = ConVar.Find("mp_timelimit");
        if (_timeLimitConVar == null)
        {
            Console.WriteLine("[MapChooser] Unable to find \"mp_timelimit\" convar failing");
            return;
        }
        
        Console.WriteLine("[MapChooser] Setting up time limit countdown!");

        AddTimer(_config.RtvDelay * 60f, () => _canRtv = true, TimerFlags.STOP_ON_MAPCHANGE);

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
        var menu = new ChatMenu("Map Vote - Type !# to vote");
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

        var message = new StringBuilder("Map Vote\n");

        var number = 1;
        if (_wasRtv)
        {
            message.Append($"{number}. Don't Change\n");
            number++;
            menu.AddMenuOption("Don't Change", (controller, option) =>
            {
                if (!_voteActive) return;
                if (_votes.TryGetValue(option.Text, out var count))
                    _votes[option.Text] = count + 1;
                else
                    _votes[option.Text] = 1;

                _playerVotes[controller.SteamID] = option.Text;
                _totalVotes++;
                Server.PrintToChatAll($" {ChatColors.LightRed}[IG]{ChatColors.Default} {ChatColors.Green}{controller.PlayerName}{ChatColors.Default} has voted for {ChatColors.Green}{option.Text}{ChatColors.Default}.");
            });
        }
        
        for (var i = 0; i < _config.IncludeMaps; i++)
        {
            if (nextMap.Count == 0) break;
            var index = random.Next(0, nextMap.Count - 1);
            var map = nextMap.ElementAt(index).Replace("ws:", "").Trim();
            nextMap.RemoveAt(index);
            message.Append($"{number}. {map}\n");
            number++;
            menu.AddMenuOption(map, (controller, option) =>
            {
                if (!_voteActive) return;
                if (_votes.TryGetValue(option.Text, out var count))
                    _votes[option.Text] = count + 1;
                else
                    _votes[option.Text] = 1;

                _playerVotes[controller.SteamID] = option.Text;
                _totalVotes++;
                Server.PrintToChatAll($" {ChatColors.LightRed}[IG]{ChatColors.Default} {ChatColors.Green}{controller.PlayerName}{ChatColors.Default} has voted for {ChatColors.Green}{option.Text}{ChatColors.Default}.");
            });
        }

        if (!_wasRtv && _config.AllowExtend && _extends < _config.ExtendLimit)
        {
            message.Append($"{number}. Extend\n");
            menu.AddMenuOption("Extend", (controller, option) =>
            {
                if (!_voteActive) return;
                if (_votes.TryGetValue(option.Text, out var count))
                    _votes[option.Text] = count + 1;
                else
                    _votes[option.Text] = 1;

                _playerVotes[controller.SteamID] = option.Text;
                _totalVotes++;
                Server.PrintToChatAll($" {ChatColors.LightRed}[IG]{ChatColors.Default} {ChatColors.Green}{controller.PlayerName}{ChatColors.Default} has voted for {ChatColors.Green}{option.Text}{ChatColors.Default}.");
            });
        }

        AddTimer(Math.Min(_config.VoteDuration, 60f), OnVoteFinished, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private static CCSGameRules GetGameRules()
    {
        return Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").First().GameRules!;
    }

    private void OnVoteFinished()
    {
        Console.WriteLine("Map vote finished");
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

        Server.PrintToChatAll($" {ChatColors.LightRed}[IG]{ChatColors.Default} {ChatColors.Green}{winner}{ChatColors.Default} has been chosen as the next map.");

        if (_wasRtv)
        {
            _wasRtv = false;
            if (winner != "Dont Change")
            {
                // Log($"RTV successful changing map to {winner}");
                if (_maps.Any(map => map.Trim() == "ws:" + winner))
                    Server.ExecuteCommand($"ds_workshop_changelevel {winner}");
                else
                    Server.ExecuteCommand($"changelevel {winner}");
            }
            else
            {
                _canRtv = false;
                AddTimer(_config.RtvDelay * 60f, () => _canRtv = true, TimerFlags.STOP_ON_MAPCHANGE);
            }
        }
        else
        {
            if (winner == "Extend")
            {
                _timeLimitConVar.SetValue(_timeLimitConVar.GetPrimitiveValue<float>() + _config.ExtendTimeStep);
                _extends++;
                Console.WriteLine($"Extend won setting next vote time for {_config.ExtendTimeStep + (60 - Math.Min(_config.VoteDuration, 60f)) - (_config.VoteStartTime * 60f)}");
                _mapVoteTimer = AddTimer(_config.ExtendTimeStep + (60 - Math.Min(_config.VoteDuration, 60f)) - (_config.VoteStartTime * 60f), StartMapVote,
                    TimerFlags.STOP_ON_MAPCHANGE);
            }
            else
            {
                _nextMap = winner;
                _mapVoteTimer = AddTimer((_config.VoteStartTime * 60f) - Math.Min(_config.VoteDuration, 60f), () =>
                {
                    // Log($"Map vote successful changing map to {winner}");
                    GetGameRules().TerminateRound(5.0f, RoundEndReason.RoundDraw);
                }, TimerFlags.STOP_ON_MAPCHANGE);
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
            AddTimer(_config.RtvDelay * 60f, () => _canRtv = true, TimerFlags.STOP_ON_MAPCHANGE);
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
        Server.PrintToChatAll($" {ChatColors.LightRed}[IG]{ChatColors.Default} {ChatColors.Green}{_nextMap}{ChatColors.Default} has been chosen as the next map.");
        AddTimer((_config.VoteStartTime * 60f) - _config.VoteDuration, () =>
        {
            GetGameRules().TerminateRound(5.0f, RoundEndReason.RoundDraw);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void OnMapStart(string mapName)
    {
        var json = File.ReadAllText(_configPath);
        _config = JsonSerializer.Deserialize<Config>(json);

        _maps = new List<string>(File.ReadLines(_mapsPath));
        Console.WriteLine(string.Join(", ", _maps.ToArray()));
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
            AddTimer((float) Math.Min(convar.GetPrimitiveValue<int>(), convar.GetPrimitiveValue<int>() - 1.0), () =>
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
