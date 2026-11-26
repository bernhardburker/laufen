using System.Text.Json.Serialization;

namespace Laufen.Domain;

/// <summary>
/// Authoritative root planning decision emitted by the agentic coach.
/// </summary>
public sealed record CoachPlanDecision
{
    [JsonPropertyName("action")]
    public required string Action { get; init; }

    [JsonPropertyName("target_date")]
    public required DateOnly TargetDate { get; init; }

    [JsonPropertyName("primary_workout")]
    public WorkoutDefinition? PrimaryWorkout { get; init; }

    [JsonPropertyName("secondary_workout")]
    public WorkoutDefinition? SecondaryWorkout { get; init; }

    [JsonPropertyName("rest_day_reason")]
    public string? RestDayReason { get; init; }

    [JsonPropertyName("keep_existing_reason")]
    public string? KeepExistingReason { get; init; }

    [JsonPropertyName("rationale")]
    public required CoachDecisionRationale Rationale { get; init; }

    [JsonPropertyName("updated_compass")]
    public required BlockCompassSnapshot UpdatedCompass { get; init; }
}

/// <summary>
/// Structured reasoning record captured for decision tracing and auditability.
/// </summary>
public sealed record CoachDecisionRationale
{
    [JsonPropertyName("observations")]
    public required IReadOnlyList<string> Observations { get; init; }

    [JsonPropertyName("tradeoff_weighed")]
    public required string TradeoffWeighed { get; init; }

    [JsonPropertyName("progression_intent")]
    public required string ProgressionIntent { get; init; }

    [JsonPropertyName("rejected_alternative")]
    public required string RejectedAlternative { get; init; }
}

/// <summary>
/// Compact rolling coaching memory tracking block focus, milestones, and athlete patterns.
/// </summary>
public sealed record BlockCompassSnapshot
{
    [JsonPropertyName("updated_at")]
    public required DateOnly UpdatedAt { get; init; }

    [JsonPropertyName("current_block_focus")]
    public required string CurrentBlockFocus { get; init; }

    [JsonPropertyName("base_milestone")]
    public required string BaseMilestone { get; init; }

    [JsonPropertyName("quality_milestone")]
    public required string QualityMilestone { get; init; }

    [JsonPropertyName("observed_athlete_patterns")]
    public required IReadOnlyList<string> ObservedAthletePatterns { get; init; }

    [JsonPropertyName("last_decision_summary")]
    public required string LastDecisionSummary { get; init; }

    [JsonPropertyName("health_state")]
    public string? HealthState { get; init; }

    [JsonPropertyName("auto_event_ids")]
    public IReadOnlyList<long>? AutoEventIds { get; init; }

    [JsonPropertyName("active_milestones")]
    public IReadOnlyList<StageMilestone>? ActiveMilestones { get; init; }
}

/// <summary>
/// Structured workout AST independent of Intervals.icu text rendering.
/// </summary>
public sealed record WorkoutDefinition
{
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("session_type")]
    public required string SessionType { get; init; }

    [JsonPropertyName("steps")]
    public required IReadOnlyList<WorkoutStepNode> Steps { get; init; }

    [JsonPropertyName("coaching_notes")]
    public string? CoachingNotes { get; init; }
}

/// <summary>
/// Flat discriminated step node supporting warmup, steady, repeat, and cooldown steps.
/// </summary>
public sealed record WorkoutStepNode
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("duration_seconds")]
    public int? DurationSeconds { get; init; }

    [JsonPropertyName("target")]
    public WorkoutTargetSpec? Target { get; init; }

    [JsonPropertyName("cue")]
    public string? Cue { get; init; }

    [JsonPropertyName("reps")]
    public int? Reps { get; init; }

    [JsonPropertyName("work")]
    public WorkoutIntervalLeg? Work { get; init; }

    [JsonPropertyName("recovery")]
    public WorkoutIntervalLeg? Recovery { get; init; }
}

/// <summary>
/// Work or recovery interval leg inside a <see cref="WorkoutStepNode"/> repeat block.
/// </summary>
public sealed record WorkoutIntervalLeg
{
    [JsonPropertyName("duration_seconds")]
    public required int DurationSeconds { get; init; }

    [JsonPropertyName("target")]
    public required WorkoutTargetSpec Target { get; init; }

    [JsonPropertyName("cue")]
    public string? Cue { get; init; }
}

/// <summary>
/// Mutually exclusive control target specification (HR zone, pace corridor, or RPE).
/// </summary>
public sealed record WorkoutTargetSpec
{
    [JsonPropertyName("modality")]
    public required string Modality { get; init; }

    [JsonPropertyName("hr_zone_start")]
    public int? HrZoneStart { get; init; }

    [JsonPropertyName("hr_zone_end")]
    public int? HrZoneEnd { get; init; }

    [JsonPropertyName("pace_fast_sec_per_km")]
    public int? PaceFastSecPerKm { get; init; }

    [JsonPropertyName("pace_slow_sec_per_km")]
    public int? PaceSlowSecPerKm { get; init; }

    [JsonPropertyName("rpe")]
    public int? Rpe { get; init; }
}
