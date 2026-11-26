using System.Globalization;
using System.Text.Json.Serialization;

namespace Laufen.Domain;

public sealed record StageMilestone
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("window_start")]
    public required DateOnly WindowStart { get; init; }

    [JsonPropertyName("window_end")]
    public required DateOnly WindowEnd { get; init; }

    [JsonPropertyName("target_value")]
    public required double TargetValue { get; init; }

    [JsonPropertyName("current_value")]
    public required double CurrentValue { get; init; }

    [JsonPropertyName("unit")]
    public required string Unit { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("progress_ratio")]
    public required double ProgressRatio { get; init; }

    [JsonPropertyName("evidence")]
    public required string Evidence { get; init; }
}

public sealed record MacroNorthStarSummary
{
    [JsonPropertyName("window_weeks")]
    public required int WindowWeeks { get; init; }

    [JsonPropertyName("z2_gap_sample_count")]
    public required int Z2GapSampleCount { get; init; }

    [JsonPropertyName("z2_gap_early_pace_text")]
    public string? Z2GapEarlyPaceText { get; init; }

    [JsonPropertyName("z2_gap_recent_pace_text")]
    public string? Z2GapRecentPaceText { get; init; }

    [JsonPropertyName("z2_gap_delta_seconds_per_km")]
    public double? Z2GapDeltaSecondsPerKm { get; init; }

    [JsonPropertyName("base_aef_early")]
    public double? BaseAefEarly { get; init; }

    [JsonPropertyName("base_aef_recent")]
    public double? BaseAefRecent { get; init; }

    [JsonPropertyName("base_aef_delta")]
    public double? BaseAefDelta { get; init; }

    [JsonPropertyName("trend_direction")]
    public required string TrendDirection { get; init; }
}

public sealed record MilestoneProgressSnapshot
{
    [JsonPropertyName("reference_date")]
    public required DateOnly ReferenceDate { get; init; }

    [JsonPropertyName("active_milestones")]
    public required IReadOnlyList<StageMilestone> ActiveMilestones { get; init; }

    [JsonPropertyName("north_star")]
    public required MacroNorthStarSummary NorthStar { get; init; }
}

/// <summary>
/// Deterministic v2 evaluator for measurable 14-day process/durability/capacity milestones
/// and 6-12 week physiological North-Star outcomes (Z2 GAP pace &amp; pure-base AEF).
/// </summary>
public static class StageMilestoneEvaluator
{
    private const int NorthStarWindowWeeks = 12;

    public static MilestoneProgressSnapshot Evaluate(
        CoachIntelligenceDossier dossier,
        IReadOnlyDictionary<string, RunReviewRecord>? runReviews = null,
        BlockCompassSnapshot? compass = null)
    {
        ArgumentNullException.ThrowIfNull(dossier);

        var effectiveCompass = compass ?? dossier.CurrentCompass;
        var profile = new AthleteProfile
        {
            Id = dossier.Athlete.Id,
            Name = dossier.Athlete.Name,
            TargetRunsPerWeek = dossier.Athlete.TargetRunsPerWeek,
            TimeBudgets = dossier.Athlete.TimeBudgets,
            HeartRate = new HeartRateProfile
            {
                RestingHr = dossier.Athlete.ProfileRestingHr,
                MaxHr = 190,
                Lthr = dossier.Athlete.Lthr,
                Zones = new Dictionary<string, HeartRateZone>(StringComparer.Ordinal)
            }
        };

        return EvaluateComponents(
            profile,
            dossier.StimulusProgression.BaseProgressionChain,
            dossier.StimulusProgression.QualityProgressionChain,
            dossier.StimulusProgression.Ledger,
            dossier.Readiness,
            dossier.ReferenceDate,
            runReviews,
            effectiveCompass);
    }

    public static MilestoneProgressSnapshot EvaluateComponents(
        AthleteProfile profile,
        IReadOnlyList<BaseSessionSummary> baseChain,
        IReadOnlyList<QualitySessionSummary> qualityChain,
        RollingStimulusLedger ledger,
        ReadinessSignalDossier readiness,
        DateOnly refDate,
        IReadOnlyDictionary<string, RunReviewRecord>? runReviews = null,
        BlockCompassSnapshot? compass = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(baseChain);
        ArgumentNullException.ThrowIfNull(qualityChain);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(readiness);

        var windowStart = refDate.AddDays(-13);
        var windowEnd = refDate;
        var healthState = compass?.HealthState ?? "Normal";
        var isSickHold = string.Equals(healthState, "SickHold", StringComparison.OrdinalIgnoreCase);
        var isReEntry = string.Equals(healthState, "ReEntry", StringComparison.OrdinalIgnoreCase);
        var hasRedFlags = readiness.MedicalRedFlags.Count > 0;

        var aerobicMilestone = BuildAerobicDurabilityMilestone(
            profile,
            baseChain,
            windowStart,
            windowEnd,
            isSickHold);

        var thresholdMilestone = BuildThresholdCapacityMilestone(
            qualityChain,
            windowStart,
            windowEnd,
            isSickHold || isReEntry || hasRedFlags,
            healthState,
            hasRedFlags);

        var consistencyMilestone = BuildConsistencyMilestone(
            profile,
            ledger,
            windowStart,
            windowEnd,
            isSickHold);

        var northStar = BuildNorthStar(baseChain, runReviews, refDate);

        return new MilestoneProgressSnapshot
        {
            ReferenceDate = refDate,
            ActiveMilestones = [aerobicMilestone, thresholdMilestone, consistencyMilestone],
            NorthStar = northStar
        };
    }

    private static StageMilestone BuildAerobicDurabilityMilestone(
        AthleteProfile profile,
        IReadOnlyList<BaseSessionSummary> baseChain,
        DateOnly windowStart,
        DateOnly windowEnd,
        bool isSickHold)
    {
        var cleanInWindow = baseChain
            .Where(b => b.Date >= windowStart
                && !b.IsCompromised
                && (b.Z1Z2Percentage is >= 80.0)
                && (!b.CardiacDriftBpm.HasValue || b.CardiacDriftBpm.Value <= 6.0))
            .ToList();

        var currentLongest = cleanInWindow.Count > 0
            ? Math.Round(cleanInWindow.Max(b => b.DurationMinutes), 1, MidpointRounding.ToEven)
            : 0.0;

        var historicalClean = baseChain
            .Where(b => !b.IsCompromised && (b.Z1Z2Percentage is >= 80.0))
            .ToList();
        var baselineLongest = historicalClean.Count > 0
            ? historicalClean.Max(b => b.DurationMinutes)
            : 40.0;

        var weekendCap = (double)(profile.TimeBudgets?.TypicalWeekendMaxMinutes ?? 60);
        var effectiveCap = Math.Max(45.0, weekendCap);
        var targetMinutes = baselineLongest >= effectiveCap
            ? effectiveCap
            : Math.Clamp(Math.Ceiling((baselineLongest + 5.0) / 5.0) * 5.0, 45.0, effectiveCap);

        var ratio = Math.Round(Math.Clamp(currentLongest / targetMinutes, 0.0, 1.0), 2, MidpointRounding.ToEven);
        var status = isSickHold
            ? "paused_health"
            : currentLongest >= targetMinutes
                ? "achieved"
                : currentLongest >= targetMinutes * 0.70
                    ? "on_track"
                    : "behind";

        var evidence = cleanInWindow.Count > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Laengster sauberer Z1/Z2-Lauf (14d, Drift <=6 bpm): {currentLongest:F0}m / {targetMinutes:F0}m Ziel")
            : "Noch kein sauberer Z1/Z2-Ausdauerlauf ohne erhoehten Drift im 14-Tage-Fenster.";

        return new StageMilestone
        {
            Id = "ms-14d-aerobic-durability",
            Category = "aerobic_durability",
            Title = "Aerobe Ausdauer-Haltbarkeit (Z1/Z2 >=80%, Drift <=6 bpm)",
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            TargetValue = targetMinutes,
            CurrentValue = currentLongest,
            Unit = "min",
            Status = status,
            ProgressRatio = ratio,
            Evidence = evidence
        };
    }

    private static StageMilestone BuildThresholdCapacityMilestone(
        IReadOnlyList<QualitySessionSummary> qualityChain,
        DateOnly windowStart,
        DateOnly windowEnd,
        bool healthPaused,
        string healthState,
        bool hasRedFlags)
    {
        var qualityInWindow = qualityChain.Where(q => q.Date >= windowStart).ToList();
        var currentZ4Plus = Math.Round(
            qualityInWindow.Sum(q => q.Z4Minutes + q.Z5PlusMinutes),
            1,
            MidpointRounding.ToEven);

        var recentBaseline = qualityChain.Count > 0
            ? qualityChain.Max(q => q.Z4Minutes + q.Z5PlusMinutes)
            : 16.0;
        var targetMinutes = Math.Clamp(Math.Ceiling(Math.Max(20.0, recentBaseline) / 2.0) * 2.0, 20.0, 30.0);
        var ratio = Math.Round(Math.Clamp(currentZ4Plus / targetMinutes, 0.0, 1.0), 2, MidpointRounding.ToEven);

        string status;
        string evidence;
        if (healthPaused)
        {
            status = "paused_health";
            var reason = hasRedFlags ? "medizinisches Warnsignal" : $"Status {healthState}";
            evidence = $"Qualitaets-Etappenziel pausiert ({reason}); Fokus auf aerobe Stabilisierung.";
        }
        else
        {
            status = currentZ4Plus >= targetMinutes
                ? "achieved"
                : currentZ4Plus >= targetMinutes * 0.50
                    ? "on_track"
                    : "behind";
            evidence = string.Create(
                CultureInfo.InvariantCulture,
                $"{currentZ4Plus:F1}m in Z4+ innerhalb 14d aus {qualityInWindow.Count} Qualitaetseinheit(en).");
        }

        return new StageMilestone
        {
            Id = "ms-14d-threshold-capacity",
            Category = "threshold_capacity",
            Title = "Schwellen-Kapazitaet (kumulierte Z4+ Minuten / 14d)",
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            TargetValue = targetMinutes,
            CurrentValue = currentZ4Plus,
            Unit = "min",
            Status = status,
            ProgressRatio = ratio,
            Evidence = evidence
        };
    }

    private static StageMilestone BuildConsistencyMilestone(
        AthleteProfile profile,
        RollingStimulusLedger ledger,
        DateOnly windowStart,
        DateOnly windowEnd,
        bool isSickHold)
    {
        var weeklyTarget = Math.Max(1, profile.TargetRunsPerWeek ?? 3);
        var targetRuns = weeklyTarget * 2.0;
        var currentRuns = (double)ledger.RunsLast14Days;
        var ratio = Math.Round(Math.Clamp(currentRuns / targetRuns, 0.0, 1.0), 2, MidpointRounding.ToEven);

        var status = isSickHold
            ? "paused_health"
            : currentRuns >= targetRuns
                ? "achieved"
                : currentRuns >= Math.Max(1.0, targetRuns - 2.0)
                    ? "on_track"
                    : "behind";

        var evidence = string.Create(
            CultureInfo.InvariantCulture,
            $"{ledger.RunsLast14Days} von {targetRuns:F0} geplanten Einheiten ({ledger.VolumeKmLast14Days:F1} km / {ledger.VolumeMinutesLast14Days:F0}m) in 14 Tagen.");

        return new StageMilestone
        {
            Id = "ms-14d-consistency-budget",
            Category = "consistency_budget",
            Title = "14-Tage Trainings-Konstanz",
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            TargetValue = targetRuns,
            CurrentValue = currentRuns,
            Unit = "runs",
            Status = status,
            ProgressRatio = ratio,
            Evidence = evidence
        };
    }

    private static MacroNorthStarSummary BuildNorthStar(
        IReadOnlyList<BaseSessionSummary> baseChain,
        IReadOnlyDictionary<string, RunReviewRecord>? runReviews,
        DateOnly refDate)
    {
        var cutoff84 = refDate.AddDays(-NorthStarWindowWeeks * 7);
        var filteredReviews = (runReviews ?? new Dictionary<string, RunReviewRecord>(StringComparer.Ordinal))
            .Where(kvp =>
            {
                var d = DateOnly.FromDateTime(kvp.Value.Date);
                return d >= cutoff84 && d <= refDate;
            })
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

        var z2Trends = ZonePaceCalculator.BuildZonePaceTrends(filteredReviews, maxDate: refDate);
        z2Trends.Summary.TryGetValue("z2", out var z2Summary);

        var aefs = filteredReviews.Values
            .Where(r => r.Z1Z2Percentage >= 80.0
                && r.AerobicEf is > 0.0
                && string.IsNullOrWhiteSpace(r.AthleteNote))
            .OrderBy(r => r.Date)
            .Select(r => r.AerobicEf!.Value)
            .ToList();

        if (aefs.Count < 2)
        {
            aefs = baseChain
                .Where(b => !b.IsCompromised && b.Z1Z2Percentage is >= 80.0 && b.AerobicEfficiencyFactor is > 0.0)
                .OrderBy(b => b.Date)
                .Select(b => b.AerobicEfficiencyFactor!.Value)
                .ToList();
        }

        double? aefEarly = null;
        double? aefRecent = null;
        double? aefDelta = null;
        if (aefs.Count >= 2)
        {
            var k = Math.Min(3, Math.Max(1, aefs.Count / 3));
            aefEarly = Math.Round(aefs.Take(k).Average(), 3, MidpointRounding.ToEven);
            aefRecent = Math.Round(aefs.Skip(aefs.Count - k).Average(), 3, MidpointRounding.ToEven);
            aefDelta = Math.Round(aefRecent.Value - aefEarly.Value, 3, MidpointRounding.ToEven);
        }
        else if (aefs.Count == 1)
        {
            aefEarly = Math.Round(aefs[0], 3, MidpointRounding.ToEven);
            aefRecent = aefEarly;
            aefDelta = 0.0;
        }

        var z2Count = z2Summary?.Count ?? 0;
        var z2Delta = z2Summary?.DeltaSecondsPerKm;

        string direction;
        if ((z2Count >= 2 && z2Delta is <= -4.0) || aefDelta is >= 0.02)
        {
            direction = "improving";
        }
        else if ((z2Count >= 2 && z2Delta is >= 6.0) || aefDelta is <= -0.03)
        {
            direction = "declining";
        }
        else if (z2Count >= 2 || aefs.Count >= 2)
        {
            direction = "stable";
        }
        else
        {
            direction = "insufficient_data";
        }

        return new MacroNorthStarSummary
        {
            WindowWeeks = NorthStarWindowWeeks,
            Z2GapSampleCount = z2Count,
            Z2GapEarlyPaceText = z2Summary?.EarlyAveragePaceText,
            Z2GapRecentPaceText = z2Summary?.RecentAveragePaceText,
            Z2GapDeltaSecondsPerKm = z2Delta,
            BaseAefEarly = aefEarly,
            BaseAefRecent = aefRecent,
            BaseAefDelta = aefDelta,
            TrendDirection = direction
        };
    }
}
