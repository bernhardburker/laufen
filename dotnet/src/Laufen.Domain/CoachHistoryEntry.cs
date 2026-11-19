using System.Text.Json.Serialization;

namespace Laufen.Domain;

/// <summary>
/// Represents a single coaching cycle record in <c>athletes/&lt;slug&gt;/coach_history.json</c>.
/// </summary>
public sealed record CoachHistoryEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("date")]
    public required DateOnly Date { get; init; }

    [JsonPropertyName("recommendation")]
    public required string Recommendation { get; init; }

    [JsonPropertyName("target_type")]
    public required string TargetType { get; init; }

    [JsonPropertyName("target_hr_max")]
    public int? TargetHrMax { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("actual")]
    public required string Actual { get; init; }

    [JsonPropertyName("effect")]
    public required string Effect { get; init; }

    [JsonPropertyName("evaluated_run_ids")]
    public IReadOnlyList<string>? EvaluatedRunIds { get; init; }
}
