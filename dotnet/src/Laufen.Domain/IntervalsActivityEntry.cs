using System.Text.Json;
using System.Text.Json.Serialization;

namespace Laufen.Domain;

/// <summary>
/// Represents a single Intervals.icu activity record in <c>athletes/&lt;slug&gt;/intervals_activities.json</c>.
/// </summary>
public sealed record IntervalsActivityEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("start_date_local")]
    public required DateTime StartDateLocal { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("icu_ignore_time")]
    public bool? IcuIgnoreTime { get; init; }

    [JsonPropertyName("icu_pm_cp")]
    public JsonElement? IcuPmCp { get; init; }

    [JsonPropertyName("icu_pm_w_prime")]
    public JsonElement? IcuPmWPrime { get; init; }

    [JsonPropertyName("icu_pm_p_max")]
    public JsonElement? IcuPmPMax { get; init; }

    [JsonPropertyName("icu_pm_ftp")]
    public JsonElement? IcuPmFtp { get; init; }

    [JsonPropertyName("icu_pm_ftp_secs")]
    public JsonElement? IcuPmFtpSecs { get; init; }

    [JsonPropertyName("icu_pm_ftp_watts")]
    public JsonElement? IcuPmFtpWatts { get; init; }

    [JsonPropertyName("icu_ignore_power")]
    public bool? IcuIgnorePower { get; init; }

    [JsonPropertyName("icu_rolling_cp")]
    public JsonElement? IcuRollingCp { get; init; }

    [JsonPropertyName("icu_rolling_w_prime")]
    public JsonElement? IcuRollingWPrime { get; init; }

    [JsonPropertyName("icu_rolling_p_max")]
    public JsonElement? IcuRollingPMax { get; init; }

    [JsonPropertyName("icu_rolling_ftp")]
    public JsonElement? IcuRollingFtp { get; init; }

    [JsonPropertyName("icu_rolling_ftp_delta")]
    public JsonElement? IcuRollingFtpDelta { get; init; }

    [JsonPropertyName("icu_training_load")]
    public int? IcuTrainingLoad { get; init; }

    [JsonPropertyName("icu_atl")]
    public double? IcuAtl { get; init; }

    [JsonPropertyName("icu_ctl")]
    public double? IcuCtl { get; init; }

    [JsonPropertyName("ss_p_max")]
    public JsonElement? SsPMax { get; init; }

    [JsonPropertyName("ss_w_prime")]
    public JsonElement? SsWPrime { get; init; }

    [JsonPropertyName("ss_cp")]
    public JsonElement? SsCp { get; init; }

    [JsonPropertyName("paired_event_id")]
    public long? PairedEventId { get; init; }

    [JsonPropertyName("icu_ftp")]
    public JsonElement? IcuFtp { get; init; }

    [JsonPropertyName("icu_joules")]
    public JsonElement? IcuJoules { get; init; }

    [JsonPropertyName("icu_recording_time")]
    public int? IcuRecordingTime { get; init; }

    [JsonPropertyName("elapsed_time")]
    public int? ElapsedTime { get; init; }

    [JsonPropertyName("icu_weighted_avg_watts")]
    public JsonElement? IcuWeightedAvgWatts { get; init; }

    [JsonPropertyName("carbs_used")]
    public JsonElement? CarbsUsed { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("start_date")]
    public DateTimeOffset? StartDate { get; init; }

    [JsonPropertyName("distance")]
    public double? Distance { get; init; }

    [JsonPropertyName("icu_distance")]
    public double? IcuDistance { get; init; }

    [JsonPropertyName("moving_time")]
    public int? MovingTime { get; init; }

    [JsonPropertyName("coasting_time")]
    public int? CoastingTime { get; init; }

    [JsonPropertyName("total_elevation_gain")]
    public double? TotalElevationGain { get; init; }

    [JsonPropertyName("total_elevation_loss")]
    public double? TotalElevationLoss { get; init; }

    [JsonPropertyName("timezone")]
    public JsonElement? Timezone { get; init; }

    [JsonPropertyName("trainer")]
    public bool? Trainer { get; init; }

    [JsonPropertyName("sub_type")]
    public JsonElement? SubType { get; init; }

    [JsonPropertyName("commute")]
    public bool? Commute { get; init; }

    [JsonPropertyName("race")]
    public bool? Race { get; init; }

    [JsonPropertyName("max_speed")]
    public double? MaxSpeed { get; init; }

    [JsonPropertyName("average_speed")]
    public double? AverageSpeed { get; init; }

    [JsonPropertyName("device_watts")]
    public bool? DeviceWatts { get; init; }

    [JsonPropertyName("has_heartrate")]
    public bool? HasHeartrate { get; init; }

    [JsonPropertyName("max_heartrate")]
    public int? MaxHeartrate { get; init; }

    [JsonPropertyName("average_heartrate")]
    public int? AverageHeartrate { get; init; }

    [JsonPropertyName("average_cadence")]
    public double? AverageCadence { get; init; }

    [JsonPropertyName("calories")]
    public int? Calories { get; init; }

    [JsonPropertyName("average_temp")]
    public double? AverageTemp { get; init; }

    [JsonPropertyName("min_temp")]
    public double? MinTemp { get; init; }

    [JsonPropertyName("max_temp")]
    public double? MaxTemp { get; init; }

    [JsonPropertyName("avg_lr_balance")]
    public JsonElement? AvgLrBalance { get; init; }

    [JsonPropertyName("gap")]
    public double? Gap { get; init; }

    [JsonPropertyName("gap_model")]
    public string? GapModel { get; init; }

    [JsonPropertyName("use_elevation_correction")]
    public bool? UseElevationCorrection { get; init; }

    [JsonPropertyName("gear")]
    public JsonElement? Gear { get; init; }

    [JsonPropertyName("perceived_exertion")]
    public JsonElement? PerceivedExertion { get; init; }

    [JsonPropertyName("device_name")]
    public string? DeviceName { get; init; }

    [JsonPropertyName("power_meter")]
    public JsonElement? PowerMeter { get; init; }

    [JsonPropertyName("power_meter_serial")]
    public JsonElement? PowerMeterSerial { get; init; }

    [JsonPropertyName("power_meter_battery")]
    public JsonElement? PowerMeterBattery { get; init; }

    [JsonPropertyName("crank_length")]
    public JsonElement? CrankLength { get; init; }

    [JsonPropertyName("external_id")]
    public string? ExternalId { get; init; }

    [JsonPropertyName("file_sport_index")]
    public int? FileSportIndex { get; init; }

    [JsonPropertyName("file_type")]
    public string? FileType { get; init; }

    [JsonPropertyName("icu_athlete_id")]
    public required string IcuAthleteId { get; init; }

    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; init; }

    [JsonPropertyName("icu_sync_date")]
    public DateTimeOffset? IcuSyncDate { get; init; }

    [JsonPropertyName("analyzed")]
    public DateTimeOffset? Analyzed { get; init; }

    [JsonPropertyName("icu_w_prime")]
    public JsonElement? IcuWPrime { get; init; }

    [JsonPropertyName("p_max")]
    public JsonElement? PMax { get; init; }

    [JsonPropertyName("threshold_pace")]
    public JsonElement? ThresholdPace { get; init; }

    [JsonPropertyName("icu_hr_zones")]
    public IReadOnlyList<int>? IcuHrZones { get; init; }

    [JsonPropertyName("pace_zones")]
    public JsonElement? PaceZones { get; init; }

    [JsonPropertyName("lthr")]
    public int? Lthr { get; init; }

    [JsonPropertyName("icu_resting_hr")]
    public int? IcuRestingHr { get; init; }

    [JsonPropertyName("icu_weight")]
    public double? IcuWeight { get; init; }

    [JsonPropertyName("icu_power_zones")]
    public JsonElement? IcuPowerZones { get; init; }

    [JsonPropertyName("icu_sweet_spot_min")]
    public int? IcuSweetSpotMin { get; init; }

    [JsonPropertyName("icu_sweet_spot_max")]
    public int? IcuSweetSpotMax { get; init; }

    [JsonPropertyName("icu_power_spike_threshold")]
    public JsonElement? IcuPowerSpikeThreshold { get; init; }

    [JsonPropertyName("trimp")]
    public double? Trimp { get; init; }

    [JsonPropertyName("icu_warmup_time")]
    public int? IcuWarmupTime { get; init; }

    [JsonPropertyName("icu_cooldown_time")]
    public int? IcuCooldownTime { get; init; }

    [JsonPropertyName("icu_chat_id")]
    public JsonElement? IcuChatId { get; init; }

    [JsonPropertyName("icu_ignore_hr")]
    public bool? IcuIgnoreHr { get; init; }

    [JsonPropertyName("ignore_velocity")]
    public bool? IgnoreVelocity { get; init; }

    [JsonPropertyName("ignore_pace")]
    public bool? IgnorePace { get; init; }

    [JsonPropertyName("ignore_parts")]
    public JsonElement? IgnoreParts { get; init; }

    [JsonPropertyName("icu_training_load_data")]
    public int? IcuTrainingLoadData { get; init; }

    [JsonPropertyName("interval_summary")]
    public IReadOnlyList<string>? IntervalSummary { get; init; }

    [JsonPropertyName("skyline_chart_bytes")]
    public string? SkylineChartBytes { get; init; }

    [JsonPropertyName("stream_types")]
    public IReadOnlyList<string>? StreamTypes { get; init; }

    [JsonPropertyName("has_weather")]
    public bool? HasWeather { get; init; }

    [JsonPropertyName("has_segments")]
    public bool? HasSegments { get; init; }

    [JsonPropertyName("power_field_names")]
    public IReadOnlyList<string>? PowerFieldNames { get; init; }

    [JsonPropertyName("power_field")]
    public string? PowerField { get; init; }

    [JsonPropertyName("icu_zone_times")]
    public JsonElement? IcuZoneTimes { get; init; }

    [JsonPropertyName("icu_hr_zone_times")]
    public IReadOnlyList<int>? IcuHrZoneTimes { get; init; }

    [JsonPropertyName("pace_zone_times")]
    public JsonElement? PaceZoneTimes { get; init; }

    [JsonPropertyName("gap_zone_times")]
    public JsonElement? GapZoneTimes { get; init; }

    [JsonPropertyName("use_gap_zone_times")]
    public bool? UseGapZoneTimes { get; init; }

    [JsonPropertyName("tiz_order")]
    public string? TizOrder { get; init; }

    [JsonPropertyName("custom_zones")]
    public JsonElement? CustomZones { get; init; }

    [JsonPropertyName("icu_achievements")]
    public IReadOnlyList<JsonElement>? IcuAchievements { get; init; }

    [JsonPropertyName("icu_intervals_edited")]
    public bool? IcuIntervalsEdited { get; init; }

    [JsonPropertyName("lock_intervals")]
    public JsonElement? LockIntervals { get; init; }

    [JsonPropertyName("icu_lap_count")]
    public int? IcuLapCount { get; init; }

    [JsonPropertyName("icu_joules_above_ftp")]
    public JsonElement? IcuJoulesAboveFtp { get; init; }

    [JsonPropertyName("icu_max_wbal_depletion")]
    public JsonElement? IcuMaxWbalDepletion { get; init; }

    [JsonPropertyName("icu_hrr")]
    public IntervalsHeartRateRecovery? IcuHrr { get; init; }

    [JsonPropertyName("icu_sync_error")]
    public JsonElement? IcuSyncError { get; init; }

    [JsonPropertyName("icu_color")]
    public JsonElement? IcuColor { get; init; }

    [JsonPropertyName("icu_power_hr_z2")]
    public JsonElement? IcuPowerHrZ2 { get; init; }

    [JsonPropertyName("icu_power_hr_z2_mins")]
    public JsonElement? IcuPowerHrZ2Mins { get; init; }

    [JsonPropertyName("icu_cadence_z2")]
    public JsonElement? IcuCadenceZ2 { get; init; }

    [JsonPropertyName("icu_rpe")]
    public double? IcuRpe { get; init; }

    [JsonPropertyName("feel")]
    public double? Feel { get; init; }

    [JsonPropertyName("kg_lifted")]
    public JsonElement? KgLifted { get; init; }

    [JsonPropertyName("decoupling")]
    public JsonElement? Decoupling { get; init; }

    [JsonPropertyName("icu_median_time_delta")]
    public int? IcuMedianTimeDelta { get; init; }

    [JsonPropertyName("p30s_exponent")]
    public double? P30sExponent { get; init; }

    [JsonPropertyName("workout_shift_secs")]
    public JsonElement? WorkoutShiftSecs { get; init; }

    [JsonPropertyName("coach_tick")]
    public JsonElement? CoachTick { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("oauth_client_id")]
    public JsonElement? OauthClientId { get; init; }

    [JsonPropertyName("oauth_client_name")]
    public JsonElement? OauthClientName { get; init; }

    [JsonPropertyName("strava_id")]
    public string? StravaId { get; init; }

    [JsonPropertyName("icu_average_watts")]
    public JsonElement? IcuAverageWatts { get; init; }

    [JsonPropertyName("icu_variability_index")]
    public JsonElement? IcuVariabilityIndex { get; init; }

    [JsonPropertyName("icu_efficiency_factor")]
    public JsonElement? IcuEfficiencyFactor { get; init; }

    [JsonPropertyName("icu_power_hr")]
    public JsonElement? IcuPowerHr { get; init; }

    [JsonPropertyName("session_rpe")]
    public double? SessionRpe { get; init; }

    [JsonPropertyName("average_stride")]
    public double? AverageStride { get; init; }

    [JsonPropertyName("icu_intensity")]
    public double? IcuIntensity { get; init; }

    [JsonPropertyName("average_altitude")]
    public double? AverageAltitude { get; init; }

    [JsonPropertyName("min_altitude")]
    public double? MinAltitude { get; init; }

    [JsonPropertyName("max_altitude")]
    public double? MaxAltitude { get; init; }

    [JsonPropertyName("athlete_max_hr")]
    public int? AthleteMaxHr { get; init; }

    [JsonPropertyName("group")]
    public string? Group { get; init; }

    [JsonPropertyName("polarization_index")]
    public double? PolarizationIndex { get; init; }

    [JsonPropertyName("compliance")]
    public double? Compliance { get; init; }

    [JsonPropertyName("average_weather_temp")]
    public JsonElement? AverageWeatherTemp { get; init; }

    [JsonPropertyName("min_weather_temp")]
    public JsonElement? MinWeatherTemp { get; init; }

    [JsonPropertyName("max_weather_temp")]
    public JsonElement? MaxWeatherTemp { get; init; }

    [JsonPropertyName("average_feels_like")]
    public JsonElement? AverageFeelsLike { get; init; }

    [JsonPropertyName("min_feels_like")]
    public JsonElement? MinFeelsLike { get; init; }

    [JsonPropertyName("max_feels_like")]
    public JsonElement? MaxFeelsLike { get; init; }

    [JsonPropertyName("average_wind_speed")]
    public JsonElement? AverageWindSpeed { get; init; }

    [JsonPropertyName("average_wind_gust")]
    public JsonElement? AverageWindGust { get; init; }

    [JsonPropertyName("prevailing_wind_deg")]
    public JsonElement? PrevailingWindDeg { get; init; }

    [JsonPropertyName("headwind_percent")]
    public JsonElement? HeadwindPercent { get; init; }

    [JsonPropertyName("tailwind_percent")]
    public JsonElement? TailwindPercent { get; init; }

    [JsonPropertyName("average_clouds")]
    public JsonElement? AverageClouds { get; init; }

    [JsonPropertyName("max_rain")]
    public JsonElement? MaxRain { get; init; }

    [JsonPropertyName("max_snow")]
    public JsonElement? MaxSnow { get; init; }

    [JsonPropertyName("carbs_ingested")]
    public JsonElement? CarbsIngested { get; init; }

    [JsonPropertyName("route_id")]
    public JsonElement? RouteId { get; init; }

    [JsonPropertyName("pace")]
    public double? Pace { get; init; }

    [JsonPropertyName("hr_load")]
    public double? HrLoad { get; init; }

    [JsonPropertyName("hr_load_type")]
    public string? HrLoadType { get; init; }

    [JsonPropertyName("pace_load")]
    public JsonElement? PaceLoad { get; init; }

    [JsonPropertyName("pace_load_type")]
    public JsonElement? PaceLoadType { get; init; }

    [JsonPropertyName("power_load")]
    public JsonElement? PowerLoad { get; init; }

    [JsonPropertyName("strain_score")]
    public JsonElement? StrainScore { get; init; }

    [JsonPropertyName("tags")]
    public JsonElement? Tags { get; init; }

    [JsonPropertyName("attachments")]
    public JsonElement? Attachments { get; init; }

    [JsonPropertyName("recording_stops")]
    public IReadOnlyList<int>? RecordingStops { get; init; }

    [JsonPropertyName("average_vertical_oscillation")]
    public double? AverageVerticalOscillation { get; init; }

    [JsonPropertyName("average_vertical_ratio")]
    public double? AverageVerticalRatio { get; init; }

    [JsonPropertyName("average_stance_time")]
    public double? AverageStanceTime { get; init; }

    [JsonPropertyName("average_stance_time_balance")]
    public JsonElement? AverageStanceTimeBalance { get; init; }

    [JsonPropertyName("average_stance_time_percent")]
    public double? AverageStanceTimePercent { get; init; }

    [JsonPropertyName("average_step_length")]
    public double? AverageStepLength { get; init; }

    [JsonPropertyName("average_vertical_speed")]
    public JsonElement? AverageVerticalSpeed { get; init; }

    [JsonPropertyName("average_impact_loading_rate")]
    public JsonElement? AverageImpactLoadingRate { get; init; }

    [JsonPropertyName("average_leg_spring_stiffness")]
    public JsonElement? AverageLegSpringStiffness { get; init; }

    [JsonPropertyName("lengths")]
    public JsonElement? Lengths { get; init; }

    [JsonPropertyName("pool_length")]
    public JsonElement? PoolLength { get; init; }

    [JsonPropertyName("submax_fatigue_test")]
    public JsonElement? SubmaxFatigueTest { get; init; }

    [JsonPropertyName("analysis_issues")]
    public JsonElement? AnalysisIssues { get; init; }

    [JsonPropertyName("_note")]
    public string? Note { get; init; }
}

/// <summary>
/// Represents the heart-rate recovery window metrics inside an Intervals.icu activity.
/// </summary>
public sealed record IntervalsHeartRateRecovery
{
    [JsonPropertyName("start_index")]
    public required int StartIndex { get; init; }

    [JsonPropertyName("end_index")]
    public required int EndIndex { get; init; }

    [JsonPropertyName("start_time")]
    public required int StartTime { get; init; }

    [JsonPropertyName("end_time")]
    public required int EndTime { get; init; }

    [JsonPropertyName("start_bpm")]
    public required int StartBpm { get; init; }

    [JsonPropertyName("end_bpm")]
    public required int EndBpm { get; init; }

    [JsonPropertyName("average_watts")]
    public JsonElement? AverageWatts { get; init; }

    [JsonPropertyName("hrr")]
    public required int Hrr { get; init; }
}
