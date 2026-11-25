using System.Text.Json.Serialization;

namespace Laufen.Domain;

/// <summary>
/// Root document wrapper for <c>athletes/&lt;slug&gt;/athlete.json</c>.
/// </summary>
public sealed record AthleteProfileDocument
{
    [JsonPropertyName("athlete")]
    public required AthleteProfile Athlete { get; init; }
}

/// <summary>
/// Represents an athlete's physiological profile, HR zones, and optional periodization slots.
/// </summary>
public sealed record AthleteProfile
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("classification")]
    public string? Classification { get; init; }

    [JsonPropertyName("training_mode")]
    public string? TrainingMode { get; init; }

    [JsonPropertyName("training_model_name")]
    public string? TrainingModelName { get; init; }

    [JsonPropertyName("target_runs_per_week")]
    public int? TargetRunsPerWeek { get; init; }

    [JsonPropertyName("allow_consecutive_run_days")]
    public bool? AllowConsecutiveRunDays { get; init; }

    [JsonPropertyName("weekly_slots")]
    public IReadOnlyList<WeeklySlot>? WeeklySlots { get; init; }

    [JsonPropertyName("heart_rate")]
    public required HeartRateProfile HeartRate { get; init; }

    [JsonPropertyName("pace_targets_min_per_km")]
    public IReadOnlyDictionary<string, string>? PaceTargetsMinPerKm { get; init; }

    [JsonPropertyName("goals")]
    public AthleteGoalsProfile? Goals { get; init; }

    [JsonPropertyName("time_budgets")]
    public AthleteTimeBudgets? TimeBudgets { get; init; }

    [JsonPropertyName("preferred_run_days")]
    public IReadOnlyList<string>? PreferredRunDays { get; init; }
}

/// <summary>
/// Optional athlete training goals and block focus used by the agentic coach.
/// </summary>
public sealed record AthleteGoalsProfile
{
    [JsonPropertyName("primary_goal")]
    public string? PrimaryGoal { get; init; }

    [JsonPropertyName("target_horizon")]
    public string? TargetHorizon { get; init; }

    [JsonPropertyName("current_focus")]
    public string? CurrentFocus { get; init; }
}

/// <summary>
/// Optional weekday and weekend session duration budgets in minutes.
/// </summary>
public sealed record AthleteTimeBudgets
{
    [JsonPropertyName("typical_weekday_min_minutes")]
    public int? TypicalWeekdayMinMinutes { get; init; }

    [JsonPropertyName("typical_weekday_max_minutes")]
    public int? TypicalWeekdayMaxMinutes { get; init; }

    [JsonPropertyName("typical_weekend_min_minutes")]
    public int? TypicalWeekendMinMinutes { get; init; }

    [JsonPropertyName("typical_weekend_max_minutes")]
    public int? TypicalWeekendMaxMinutes { get; init; }
}

/// <summary>
/// Represents a configurable weekly training slot in <see cref="AthleteProfile"/>.
/// </summary>
public sealed record WeeklySlot
{
    [JsonPropertyName("slot")]
    public required int Slot { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("name_display")]
    public string? NameDisplay { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("allowed_days")]
    public required IReadOnlyList<string> AllowedDays { get; init; }

    [JsonPropertyName("allowed_types")]
    public IReadOnlyList<string>? AllowedTypes { get; init; }

    [JsonPropertyName("preferred_type")]
    public string? PreferredType { get; init; }

    [JsonPropertyName("default_type")]
    public string? DefaultType { get; init; }

    [JsonPropertyName("workout_type")]
    public string? WorkoutType { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("target")]
    public string? Target { get; init; }

    [JsonPropertyName("target_hr_range")]
    public IReadOnlyList<int>? TargetHrRange { get; init; }

    [JsonPropertyName("target_hr_max")]
    public int? TargetHrMax { get; init; }

    [JsonPropertyName("duration_min")]
    public int? DurationMin { get; init; }

    [JsonPropertyName("requires_base_anchor")]
    public bool? RequiresBaseAnchor { get; init; }

    [JsonPropertyName("is_mandatory_base")]
    public bool? IsMandatoryBase { get; init; }
}

/// <summary>
/// Represents calibrated heart-rate thresholds and zones for an athlete.
/// </summary>
public sealed record HeartRateProfile
{
    [JsonPropertyName("resting_hr")]
    public required int RestingHr { get; init; }

    [JsonPropertyName("max_hr")]
    public required int MaxHr { get; init; }

    [JsonPropertyName("lthr")]
    public required int Lthr { get; init; }

    [JsonPropertyName("zones")]
    public required IReadOnlyDictionary<string, HeartRateZone> Zones { get; init; }

    [JsonPropertyName("raw_hr_zones")]
    public IReadOnlyList<int>? RawHrZones { get; init; }

    [JsonPropertyName("raw_zone_names")]
    public IReadOnlyList<string>? RawZoneNames { get; init; }
}

/// <summary>
/// Represents a single heart-rate zone boundary.
/// </summary>
public sealed record HeartRateZone
{
    [JsonPropertyName("zone")]
    public required int Zone { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("min")]
    public required int Min { get; init; }

    [JsonPropertyName("max")]
    public required int Max { get; init; }
}
