using System.Text.Json.Serialization;

namespace Laufen.Domain;

/// <summary>
/// Represents a daily Intervals.icu wellness record in <c>athletes/&lt;slug&gt;/intervals_wellness.json</c>.
/// </summary>
public sealed record IntervalsWellnessEntry
{
    [JsonPropertyName("id")]
    public required DateOnly Id { get; init; }

    [JsonPropertyName("ctl")]
    public double? Ctl { get; init; }

    [JsonPropertyName("atl")]
    public double? Atl { get; init; }

    [JsonPropertyName("rampRate")]
    public double? RampRate { get; init; }

    [JsonPropertyName("ctlLoad")]
    public double? CtlLoad { get; init; }

    [JsonPropertyName("atlLoad")]
    public double? AtlLoad { get; init; }

    [JsonPropertyName("sportInfo")]
    public IReadOnlyList<WellnessSportInfo>? SportInfo { get; init; }

    [JsonPropertyName("updated")]
    public required DateTimeOffset Updated { get; init; }

    [JsonPropertyName("weight")]
    public double? Weight { get; init; }

    [JsonPropertyName("restingHR")]
    public int? RestingHr { get; init; }

    [JsonPropertyName("hrv")]
    public double? Hrv { get; init; }

    [JsonPropertyName("hrvSDNN")]
    public double? HrvSdnn { get; init; }

    [JsonPropertyName("menstrualPhase")]
    public string? MenstrualPhase { get; init; }

    [JsonPropertyName("menstrualPhasePredicted")]
    public string? MenstrualPhasePredicted { get; init; }

    [JsonPropertyName("kcalConsumed")]
    public double? KcalConsumed { get; init; }

    [JsonPropertyName("sleepSecs")]
    public int? SleepSecs { get; init; }

    [JsonPropertyName("sleepScore")]
    public double? SleepScore { get; init; }

    [JsonPropertyName("sleepQuality")]
    public int? SleepQuality { get; init; }

    [JsonPropertyName("avgSleepingHR")]
    public double? AvgSleepingHr { get; init; }

    [JsonPropertyName("soreness")]
    public double? Soreness { get; init; }

    [JsonPropertyName("fatigue")]
    public double? Fatigue { get; init; }

    [JsonPropertyName("stress")]
    public double? Stress { get; init; }

    [JsonPropertyName("mood")]
    public double? Mood { get; init; }

    [JsonPropertyName("motivation")]
    public double? Motivation { get; init; }

    [JsonPropertyName("injury")]
    public double? Injury { get; init; }

    [JsonPropertyName("spO2")]
    public double? SpO2 { get; init; }

    [JsonPropertyName("systolic")]
    public double? Systolic { get; init; }

    [JsonPropertyName("diastolic")]
    public double? Diastolic { get; init; }

    [JsonPropertyName("hydration")]
    public double? Hydration { get; init; }

    [JsonPropertyName("hydrationVolume")]
    public double? HydrationVolume { get; init; }

    [JsonPropertyName("readiness")]
    public double? Readiness { get; init; }

    [JsonPropertyName("baevskySI")]
    public double? BaevskySi { get; init; }

    [JsonPropertyName("bloodGlucose")]
    public double? BloodGlucose { get; init; }

    [JsonPropertyName("lactate")]
    public double? Lactate { get; init; }

    [JsonPropertyName("bodyFat")]
    public double? BodyFat { get; init; }

    [JsonPropertyName("abdomen")]
    public double? Abdomen { get; init; }

    [JsonPropertyName("vo2max")]
    public double? Vo2max { get; init; }

    [JsonPropertyName("comments")]
    public string? Comments { get; init; }

    [JsonPropertyName("steps")]
    public int? Steps { get; init; }

    [JsonPropertyName("floorsClimbed")]
    public int? FloorsClimbed { get; init; }

    [JsonPropertyName("respiration")]
    public double? Respiration { get; init; }

    [JsonPropertyName("carbohydrates")]
    public double? Carbohydrates { get; init; }

    [JsonPropertyName("protein")]
    public double? Protein { get; init; }

    [JsonPropertyName("fatTotal")]
    public double? FatTotal { get; init; }

    [JsonPropertyName("locked")]
    public bool? Locked { get; init; }

    [JsonPropertyName("tempWeight")]
    public required bool TempWeight { get; init; }

    [JsonPropertyName("tempRestingHR")]
    public required bool TempRestingHr { get; init; }
}

/// <summary>
/// Represents per-sport eFTP/W'/pMax metadata inside <see cref="IntervalsWellnessEntry.SportInfo"/>.
/// </summary>
public sealed record WellnessSportInfo
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("eftp")]
    public double? Eftp { get; init; }

    [JsonPropertyName("wPrime")]
    public double? WPrime { get; init; }

    [JsonPropertyName("pMax")]
    public double? PMax { get; init; }
}
