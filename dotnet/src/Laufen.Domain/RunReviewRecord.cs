using System.Text.Json.Serialization;

namespace Laufen.Domain;

/// <summary>
/// Represents a cached 4-stage run review record in <c>athletes/&lt;slug&gt;/run_reviews.json</c>.
/// </summary>
public sealed record RunReviewRecord
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("date")]
    public required DateTime Date { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("distance_km")]
    public required double DistanceKm { get; init; }

    [JsonPropertyName("moving_time_s")]
    public required int MovingTimeSeconds { get; init; }

    [JsonPropertyName("moving_time_str")]
    public required string MovingTimeText { get; init; }

    [JsonPropertyName("pace_str")]
    public required string PaceText { get; init; }

    [JsonPropertyName("avg_hr")]
    public required double AverageHeartRate { get; init; }

    [JsonPropertyName("max_hr")]
    public required int MaxHeartRate { get; init; }

    [JsonPropertyName("cadence_spm")]
    public required double CadenceSpm { get; init; }

    [JsonPropertyName("elevation_gain_m")]
    public required double ElevationGainMeters { get; init; }

    [JsonPropertyName("training_load")]
    public required double TrainingLoad { get; init; }

    [JsonPropertyName("icu_ctl")]
    public double? IcuCtl { get; init; }

    [JsonPropertyName("icu_atl")]
    public double? IcuAtl { get; init; }

    [JsonPropertyName("tsb_at_run")]
    public double? TsbAtRun { get; init; }

    [JsonPropertyName("aerobic_ef")]
    public double? AerobicEf { get; init; }

    [JsonPropertyName("z1_z2_pct")]
    public required double Z1Z2Percentage { get; init; }

    [JsonPropertyName("zone_breakdown")]
    public required IReadOnlyDictionary<string, ZoneBreakdownEntry> ZoneBreakdown { get; init; }

    [JsonPropertyName("intervals")]
    public required IReadOnlyList<string> Intervals { get; init; }

    [JsonPropertyName("intensity_class")]
    public required string IntensityClass { get; init; }

    [JsonPropertyName("athlete_note")]
    public string? AthleteNote { get; init; }

    [JsonPropertyName("analyzed_at")]
    public required DateTimeOffset AnalyzedAt { get; init; }

    [JsonPropertyName("zone_pace_stats")]
    public ZonePaceStats? ZonePaceStats { get; init; }

    [JsonPropertyName("review")]
    public required RunReviewEvaluation Review { get; init; }
}

/// <summary>
/// Represents a single heart-rate zone breakdown entry in a run review.
/// </summary>
public sealed record ZoneBreakdownEntry
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("seconds")]
    public required int Seconds { get; init; }

    [JsonPropertyName("pct")]
    public required double Percentage { get; init; }

    [JsonPropertyName("time_str")]
    public required string TimeText { get; init; }
}

/// <summary>
/// Represents the 4-stage evaluation payload inside a cached run review record.
/// </summary>
public sealed record RunReviewEvaluation
{
    [JsonPropertyName("schema_version")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("rating")]
    public required string Rating { get; init; }

    [JsonPropertyName("rating_label")]
    public required string RatingLabel { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("coach_tip")]
    public required string CoachTip { get; init; }

    [JsonPropertyName("cadence_feedback")]
    public required string CadenceFeedback { get; init; }

    [JsonPropertyName("athlete_note")]
    public string? AthleteNote { get; init; }

    [JsonPropertyName("zone_distribution_text")]
    public required string ZoneDistributionText { get; init; }

    [JsonPropertyName("stages")]
    public required RunReviewStages Stages { get; init; }
}

/// <summary>
/// Represents the four structured review stages.
/// </summary>
public sealed record RunReviewStages
{
    [JsonPropertyName("stage1_physiology")]
    public required Stage1Physiology Stage1Physiology { get; init; }

    [JsonPropertyName("stage2_compliance")]
    public required Stage2Compliance Stage2Compliance { get; init; }

    [JsonPropertyName("stage3_optimizations")]
    public required IReadOnlyList<Stage3OptimizationItem> Stage3Optimizations { get; init; }

    [JsonPropertyName("stage4_next_run")]
    public required Stage4NextRun Stage4NextRun { get; init; }
}

/// <summary>
/// Stage 1 physiological assessment and parsed intervals.
/// </summary>
public sealed record Stage1Physiology
{
    [JsonPropertyName("headline")]
    public required string Headline { get; init; }

    [JsonPropertyName("ef")]
    public double? EfficiencyFactor { get; init; }

    [JsonPropertyName("tsb_at_run")]
    public double? TsbAtRun { get; init; }

    [JsonPropertyName("parsed_intervals")]
    public required IReadOnlyList<ParsedReviewInterval> ParsedIntervals { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }
}

/// <summary>
/// Represents a parsed interval block inside Stage 1 physiology.
/// </summary>
public sealed record ParsedReviewInterval
{
    [JsonPropertyName("raw")]
    public required string Raw { get; init; }

    [JsonPropertyName("reps")]
    public required int Reps { get; init; }

    [JsonPropertyName("duration_s")]
    public required int DurationSeconds { get; init; }

    [JsonPropertyName("duration_str")]
    public required string DurationText { get; init; }

    [JsonPropertyName("avg_hr")]
    public required double AverageHeartRate { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; }
}

/// <summary>
/// Stage 2 plan compliance assessment.
/// </summary>
public sealed record Stage2Compliance
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("status_label")]
    public required string StatusLabel { get; init; }

    [JsonPropertyName("intended_type")]
    public required string IntendedType { get; init; }

    [JsonPropertyName("intent_source")]
    public required string IntentSource { get; init; }

    [JsonPropertyName("planned_slot")]
    public required string PlannedSlot { get; init; }

    [JsonPropertyName("intervals_icu_compliance")]
    public double? IntervalsIcuCompliance { get; init; }

    [JsonPropertyName("target_comparison")]
    public required string TargetComparison { get; init; }

    [JsonPropertyName("compliance_notes")]
    public string? ComplianceNotes { get; init; }
}

/// <summary>
/// Stage 3 actionable optimization recommendation.
/// </summary>
public sealed record Stage3OptimizationItem
{
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("actionable_advice")]
    public string? ActionableAdvice { get; init; }

    [JsonPropertyName("cli_command")]
    public string? CliCommand { get; init; }
}

/// <summary>
/// Stage 4 next-run directive and recovery window.
/// </summary>
public sealed record Stage4NextRun
{
    [JsonPropertyName("recovery_hours")]
    public required int RecoveryHours { get; init; }

    [JsonPropertyName("tsb_context")]
    public required string TsbContext { get; init; }

    [JsonPropertyName("next_slot")]
    public required string NextSlot { get; init; }

    [JsonPropertyName("next_workout_type")]
    public required string NextWorkoutType { get; init; }

    [JsonPropertyName("target_hr_max")]
    public required int TargetHeartRateMax { get; init; }

    [JsonPropertyName("duration_min")]
    public required int DurationMinutes { get; init; }

    [JsonPropertyName("directive")]
    public required string Directive { get; init; }

    [JsonPropertyName("optimization_focus")]
    public string? OptimizationFocus { get; init; }
}

/// <summary>
/// Per-second HR-zone GAP pace and split analysis attached to run reviews.
/// </summary>
public sealed record ZonePaceStats
{
    [JsonPropertyName("has_streams")]
    public required bool HasStreams { get; init; }

    [JsonPropertyName("min_zone_seconds")]
    public required int MinZoneSeconds { get; init; }

    [JsonPropertyName("total_running_seconds")]
    public required int TotalRunningSeconds { get; init; }

    [JsonPropertyName("walking_seconds_filtered")]
    public required int WalkingSecondsFiltered { get; init; }

    [JsonPropertyName("zones")]
    public required IReadOnlyDictionary<string, ZonePaceEntry> Zones { get; init; }

    [JsonPropertyName("first_half_gap_pace_s_per_km")]
    public double? FirstHalfGapPaceSecondsPerKm { get; init; }

    [JsonPropertyName("second_half_gap_pace_s_per_km")]
    public double? SecondHalfGapPaceSecondsPerKm { get; init; }

    [JsonPropertyName("first_half_avg_hr")]
    public double? FirstHalfAverageHeartRate { get; init; }

    [JsonPropertyName("second_half_avg_hr")]
    public double? SecondHalfAverageHeartRate { get; init; }

    [JsonPropertyName("pace_split_diff_s")]
    public double? PaceSplitDiffSeconds { get; init; }
}

/// <summary>
/// Per-zone GAP and raw pace metrics within <see cref="ZonePaceStats"/>.
/// </summary>
public sealed record ZonePaceEntry
{
    [JsonPropertyName("zone")]
    public required int Zone { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("running_seconds")]
    public required int RunningSeconds { get; init; }

    [JsonPropertyName("avg_hr")]
    public required double AverageHeartRate { get; init; }

    [JsonPropertyName("gap_speed_mps")]
    public required double GapSpeedMetersPerSecond { get; init; }

    [JsonPropertyName("gap_pace_s_per_km")]
    public required double GapPaceSecondsPerKm { get; init; }

    [JsonPropertyName("gap_pace_str")]
    public required string GapPaceText { get; init; }

    [JsonPropertyName("raw_speed_mps")]
    public required double RawSpeedMetersPerSecond { get; init; }

    [JsonPropertyName("raw_pace_s_per_km")]
    public required double RawPaceSecondsPerKm { get; init; }

    [JsonPropertyName("raw_pace_str")]
    public required string RawPaceText { get; init; }

    [JsonPropertyName("valid_for_trend")]
    public required bool ValidForTrend { get; init; }
}
