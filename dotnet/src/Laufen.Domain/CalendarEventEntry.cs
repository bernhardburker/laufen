using System.Text.Json;
using System.Text.Json.Serialization;

namespace Laufen.Domain;

/// <summary>
/// Represents an Intervals.icu calendar event from <c>athletes/&lt;slug&gt;/calendar_events.json</c>.
/// </summary>
public sealed record CalendarEventEntry
{
    [JsonPropertyName("id")]
    public required long Id { get; init; }

    [JsonPropertyName("uid")]
    public string? Uid { get; init; }

    [JsonPropertyName("athlete_id")]
    public required string AthleteId { get; init; }

    [JsonPropertyName("calendar_id")]
    public int? CalendarId { get; init; }

    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("start_date_local")]
    public required DateTime StartDateLocal { get; init; }

    [JsonPropertyName("end_date_local")]
    public DateTime? EndDateLocal { get; init; }

    [JsonPropertyName("updated")]
    public DateTimeOffset? Updated { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("sub_type")]
    public JsonElement? SubType { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("paired_activity_id")]
    public string? PairedActivityId { get; init; }

    [JsonPropertyName("distance")]
    public double? Distance { get; init; }

    [JsonPropertyName("moving_time")]
    public int? MovingTime { get; init; }

    [JsonPropertyName("icu_training_load")]
    public double? IcuTrainingLoad { get; init; }

    [JsonPropertyName("icu_intensity")]
    public double? IcuIntensity { get; init; }

    [JsonPropertyName("icu_ctl")]
    public double? IcuCtl { get; init; }

    [JsonPropertyName("icu_atl")]
    public double? IcuAtl { get; init; }

    [JsonPropertyName("icu_ftp")]
    public JsonElement? IcuFtp { get; init; }

    [JsonPropertyName("indoor")]
    public bool? Indoor { get; init; }

    [JsonPropertyName("color")]
    public string? Color { get; init; }

    [JsonPropertyName("external_id")]
    public string? ExternalId { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string>? Tags { get; init; }

    [JsonPropertyName("attachments")]
    public JsonElement? Attachments { get; init; }

    [JsonPropertyName("created_by_id")]
    public string? CreatedById { get; init; }

    [JsonPropertyName("shared_event_id")]
    public JsonElement? SharedEventId { get; init; }

    [JsonPropertyName("oauth_client_id")]
    public JsonElement? OauthClientId { get; init; }

    [JsonPropertyName("athlete_cannot_edit")]
    public bool? AthleteCannotEdit { get; init; }

    [JsonPropertyName("hide_from_athlete")]
    public bool? HideFromAthlete { get; init; }

    [JsonPropertyName("structure_read_only")]
    public bool? StructureReadOnly { get; init; }

    [JsonPropertyName("show_as_note")]
    public bool? ShowAsNote { get; init; }

    [JsonPropertyName("show_on_ctl_line")]
    public bool? ShowOnCtlLine { get; init; }

    [JsonPropertyName("not_on_fitness_chart")]
    public bool? NotOnFitnessChart { get; init; }

    [JsonPropertyName("for_week")]
    public bool? ForWeek { get; init; }

    [JsonPropertyName("entered")]
    public bool? Entered { get; init; }

    [JsonPropertyName("training_availability")]
    public string? TrainingAvailability { get; init; }

    [JsonPropertyName("target")]
    public string? Target { get; init; }

    [JsonPropertyName("load_target")]
    public double? LoadTarget { get; init; }

    [JsonPropertyName("time_target")]
    public int? TimeTarget { get; init; }

    [JsonPropertyName("distance_target")]
    public double? DistanceTarget { get; init; }

    [JsonPropertyName("max_training_time")]
    public JsonElement? MaxTrainingTime { get; init; }

    [JsonPropertyName("can_train_sports")]
    public JsonElement? CanTrainSports { get; init; }

    [JsonPropertyName("ctl_days")]
    public JsonElement? CtlDays { get; init; }

    [JsonPropertyName("atl_days")]
    public JsonElement? AtlDays { get; init; }

    [JsonPropertyName("carbs_per_hour")]
    public JsonElement? CarbsPerHour { get; init; }

    [JsonPropertyName("carbs_used")]
    public JsonElement? CarbsUsed { get; init; }

    [JsonPropertyName("joules")]
    public JsonElement? Joules { get; init; }

    [JsonPropertyName("joules_above_ftp")]
    public JsonElement? JoulesAboveFtp { get; init; }

    [JsonPropertyName("p_max")]
    public JsonElement? PMax { get; init; }

    [JsonPropertyName("w_prime")]
    public JsonElement? WPrime { get; init; }

    [JsonPropertyName("ss_cp")]
    public JsonElement? SsCp { get; init; }

    [JsonPropertyName("ss_p_max")]
    public JsonElement? SsPMax { get; init; }

    [JsonPropertyName("ss_w_prime")]
    public JsonElement? SsWPrime { get; init; }

    [JsonPropertyName("strain_score")]
    public double? StrainScore { get; init; }

    [JsonPropertyName("plan_applied")]
    public JsonElement? PlanApplied { get; init; }

    [JsonPropertyName("plan_athlete_id")]
    public JsonElement? PlanAthleteId { get; init; }

    [JsonPropertyName("plan_folder_id")]
    public JsonElement? PlanFolderId { get; init; }

    [JsonPropertyName("plan_name")]
    public JsonElement? PlanName { get; init; }

    [JsonPropertyName("plan_workout_id")]
    public JsonElement? PlanWorkoutId { get; init; }

    [JsonPropertyName("push_errors")]
    public JsonElement? PushErrors { get; init; }

    [JsonPropertyName("workout_doc")]
    public WorkoutDoc? WorkoutDoc { get; init; }
}

/// <summary>
/// Parsed Intervals.icu workout structure embedded in a calendar event.
/// </summary>
public sealed record WorkoutDoc
{
    [JsonPropertyName("steps")]
    public IReadOnlyList<WorkoutStep>? Steps { get; init; }

    [JsonPropertyName("locales")]
    public IReadOnlyList<JsonElement>? Locales { get; init; }

    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, JsonElement>? Options { get; init; }

    [JsonPropertyName("distance")]
    public double? Distance { get; init; }

    [JsonPropertyName("duration")]
    public int? Duration { get; init; }

    [JsonPropertyName("zoneTimes")]
    public IReadOnlyList<WorkoutZoneTime>? ZoneTimes { get; init; }

    [JsonPropertyName("strain_score")]
    public double? StrainScore { get; init; }

    [JsonPropertyName("average_watts")]
    public double? AverageWatts { get; init; }

    [JsonPropertyName("normalized_power")]
    public double? NormalizedPower { get; init; }

    [JsonPropertyName("variability_index")]
    public double? VariabilityIndex { get; init; }

    [JsonPropertyName("polarization_index")]
    public double? PolarizationIndex { get; init; }
}

/// <summary>
/// A single workout step or repeat block inside <see cref="WorkoutDoc"/>.
/// </summary>
public sealed record WorkoutStep
{
    [JsonPropertyName("duration")]
    public int? Duration { get; init; }

    [JsonPropertyName("distance")]
    public double? Distance { get; init; }

    [JsonPropertyName("warmup")]
    public bool? Warmup { get; init; }

    [JsonPropertyName("cooldown")]
    public bool? Cooldown { get; init; }

    [JsonPropertyName("reps")]
    public int? Reps { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("hr")]
    public WorkoutStepTarget? Hr { get; init; }

    [JsonPropertyName("steps")]
    public IReadOnlyList<WorkoutStep>? Steps { get; init; }
}

/// <summary>
/// Heart-rate target specification for a <see cref="WorkoutStep"/>.
/// </summary>
public sealed record WorkoutStepTarget
{
    [JsonPropertyName("units")]
    public string? Units { get; init; }

    [JsonPropertyName("value")]
    public double? Value { get; init; }

    [JsonPropertyName("start")]
    public double? Start { get; init; }

    [JsonPropertyName("end")]
    public double? End { get; init; }
}

/// <summary>
/// Expected time per heart-rate zone in <see cref="WorkoutDoc"/>.
/// </summary>
public sealed record WorkoutZoneTime
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("secs")]
    public required int Seconds { get; init; }
}
