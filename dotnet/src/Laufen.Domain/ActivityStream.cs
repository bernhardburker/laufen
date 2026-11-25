using System.Text.Json.Serialization;

namespace Laufen.Domain;

/// <summary>
/// Per-second activity sensor streams cached under <c>athletes/&lt;slug&gt;/streams/&lt;id&gt;.json</c>.
/// </summary>
public sealed record ActivityStreamData
{
    [JsonPropertyName("time")]
    public IReadOnlyList<int?>? Time { get; init; }

    [JsonPropertyName("heartrate")]
    public IReadOnlyList<double?>? Heartrate { get; init; }

    [JsonPropertyName("velocity_smooth")]
    public IReadOnlyList<double?>? VelocitySmooth { get; init; }

    [JsonPropertyName("cadence")]
    public IReadOnlyList<double?>? Cadence { get; init; }

    [JsonPropertyName("fixed_altitude")]
    public IReadOnlyList<double?>? FixedAltitude { get; init; }

    [JsonPropertyName("altitude")]
    public IReadOnlyList<double?>? Altitude { get; init; }
}
