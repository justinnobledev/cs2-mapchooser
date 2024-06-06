using System.Text.Json.Serialization;

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