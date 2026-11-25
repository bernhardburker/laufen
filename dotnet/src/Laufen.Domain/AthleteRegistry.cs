using System.Text.Json.Serialization;

namespace Laufen.Domain;

/// <summary>
/// Represents the multi-tenant athlete registry stored in <c>athletes/registry.json</c>.
/// </summary>
public sealed record AthleteRegistry
{
    [JsonPropertyName("default_athlete")]
    public required string DefaultAthlete { get; init; }

    [JsonPropertyName("athletes")]
    public required IReadOnlyDictionary<string, AthleteRegistryEntry> Athletes { get; init; }
}

/// <summary>
/// Represents a single tenant entry in <see cref="AthleteRegistry"/>.
/// </summary>
public sealed record AthleteRegistryEntry
{
    [JsonPropertyName("intervals_id")]
    public required string IntervalsId { get; init; }

    [JsonPropertyName("display_name")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("active")]
    public required bool Active { get; init; }

    [JsonPropertyName("dashboard_slug")]
    public required string DashboardSlug { get; init; }

    [JsonPropertyName("telegram_chat_id")]
    public string? TelegramChatId { get; init; }

    [JsonPropertyName("briefing_enabled")]
    public bool? BriefingEnabled { get; init; }

    [JsonPropertyName("briefing_time")]
    public string? BriefingTime { get; init; }

    [JsonPropertyName("morning_briefing_enabled")]
    public bool? MorningBriefingEnabled { get; init; }

    [JsonPropertyName("evening_briefing_time")]
    public string? EveningBriefingTime { get; init; }

    [JsonPropertyName("evening_briefing_enabled")]
    public bool? EveningBriefingEnabled { get; init; }

    [JsonPropertyName("post_run_summary_enabled")]
    public bool? PostRunSummaryEnabled { get; init; }
}
