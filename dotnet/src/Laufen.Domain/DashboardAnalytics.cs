using System.Globalization;

namespace Laufen.Domain;

public sealed record RacePredictionEntry
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required double DistanceKm { get; init; }
    public required int PredictedSeconds { get; init; }
    public required string PredictedTimeText { get; init; }
    public required string TargetPaceText { get; init; }
    public required string ReadinessLabel { get; init; }
    public required string ReadinessSeverity { get; init; }
    public required string Note { get; init; }
    public required string Basis { get; init; }
}

public sealed record VolumeForecastWeek
{
    public required string IsoWeek { get; init; }
    public required double ProjectedDistanceKm { get; init; }
    public required double ProjectedLoad { get; init; }
    public required string PhaseTitle { get; init; }
    public required string Description { get; init; }
}

public sealed record DashboardRunRow
{
    public required string Id { get; init; }
    public required DateOnly Date { get; init; }
    public required string IsoWeek { get; init; }
    public required string Name { get; init; }
    public required double DistanceKm { get; init; }
    public required int MovingTimeSeconds { get; init; }
    public required string MovingTimeText { get; init; }
    public required double PaceSecondsPerKm { get; init; }
    public required string PaceText { get; init; }
    public required double AverageHeartRate { get; init; }
    public required double TrainingLoad { get; init; }
    public required int Z1Z2Seconds { get; init; }
    public required int TotalZoneSeconds { get; init; }
    public required double? Z1Z2Percentage { get; init; }
    public required double? AerobicEf { get; init; }
    public required IReadOnlyList<int> ZoneTimes { get; init; }
    public string? Z2GapPaceText { get; init; }
    public required string RatingLabel { get; init; }
    public string? Directive { get; init; }
    public string? AthleteNote { get; init; }
    public required string IntensityBucket { get; init; }
}

public sealed record DashboardSnapshot
{
    public required string Slug { get; init; }
    public required string DashboardSlug { get; init; }
    public required string AthleteName { get; init; }
    public required DateOnly ReferenceDate { get; init; }
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public required int TotalRuns { get; init; }
    public required double TotalDistanceKm { get; init; }
    public required int TotalDurationSeconds { get; init; }
    public required string AveragePaceText { get; init; }
    public required double AverageHeartRate { get; init; }
    public required double OverallZ1Z2Percentage { get; init; }
    public required double? BaseAerobicEf { get; init; }
    public required int StravaPlaceholderCount { get; init; }
    public required HeartRateProfile HeartRateProfile { get; init; }
    public required AthleteReportSnapshot ReportSnapshot { get; init; }
    public required ZonePaceTrendsResult ZonePaceTrends { get; init; }
    public required IReadOnlyList<RacePredictionEntry> RacePredictions { get; init; }
    public required IReadOnlyList<VolumeForecastWeek> VolumeForecast { get; init; }
    public required CoachIntelligenceDossier Dossier { get; init; }
    public BlockCompassSnapshot? CurrentCompass { get; init; }
    public MilestoneProgressSnapshot? Milestones { get; init; }
    public NarrativeReport? LatestNarrative { get; init; }
    public BanisterProjectionResult? Banister14DayProjection { get; init; }
    public required IReadOnlyList<RunReviewRecord> RecentReviews { get; init; }
    public required IReadOnlyList<AutopilotDecisionTraceEntry> RecentTraces { get; init; }
    public IReadOnlyList<DashboardRunRow> AllRuns { get; init; } = [];
}

/// <summary>
/// Deterministic Riegel race predictor (<c>T2 = T1 * (D2/D1)^1.06</c>).
/// </summary>
public static class RacePredictionCalculator
{
    private const double RiegelExponent = 1.06;

    private static readonly (string Key, string Name, double DistanceKm, double DistanceMeters)[] TargetDistances =
    [
        ("5k", "5 km", 5.0, 5000.0),
        ("10k", "10 km", 10.0, 10000.0),
        ("half_marathon", "Halbmarathon", 21.0975, 21097.5),
        ("marathon", "Marathon", 42.195, 42195.0)
    ];

    public static IReadOnlyList<RacePredictionEntry> Predict(
        IReadOnlyList<RunPhysiologicalMetrics> runs,
        AthleteProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(runs);

        double bestEquiv10k = double.PositiveInfinity;
        double refDistanceM = 0.0;
        double refTimeS = 0.0;
        string refDateText = string.Empty;
        string refName = string.Empty;

        foreach (var run in runs)
        {
            var distM = run.DistanceKm * 1000.0;
            if (distM >= 2500.0 && run.MovingTimeSeconds > 0)
            {
                var equiv10k = run.MovingTimeSeconds * Math.Pow(10000.0 / distM, RiegelExponent);
                if (equiv10k < bestEquiv10k)
                {
                    bestEquiv10k = equiv10k;
                    refDistanceM = distM;
                    refTimeS = run.MovingTimeSeconds;
                    refDateText = run.StartDateLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    refName = run.Name;
                }
            }
        }

        if (refDistanceM <= 0.0)
        {
            var fallbackPaceSec = ResolveFallbackPaceSeconds(profile);
            refDistanceM = 5000.0;
            refTimeS = fallbackPaceSec * 5.0;
            refDateText = "Profil-Richtwert";
            refName = "Schwellen-Richtwert";
        }

        var basisText = string.Create(
            CultureInfo.InvariantCulture,
            $"Basiert auf: {refDistanceM / 1000.0:F2} km in {FormatTimeSeconds(refTimeS)} ({refName}, {refDateText})");

        var totalKm = runs.Sum(r => r.DistanceKm);
        var longestRunKm = runs.Count > 0 ? runs.Max(r => r.DistanceKm) : 0.0;

        var predictions = new List<RacePredictionEntry>(TargetDistances.Length);
        foreach (var (key, name, distanceKm, distanceMeters) in TargetDistances)
        {
            var predTimeS = refTimeS * Math.Pow(distanceMeters / refDistanceM, RiegelExponent);
            var predPaceS = predTimeS / (distanceMeters / 1000.0);

            var (readinessLabel, readinessSeverity, note) = key switch
            {
                "5k" => (
                    "Hohe Zuverlaessigkeit",
                    "success",
                    "Optimal mit aktuellem Grundlagen- und Tempotraining abgesichert."),
                "10k" when longestRunKm >= 8.0 => (
                    "Solide Wettkampfbasis",
                    "success",
                    string.Create(CultureInfo.InvariantCulture, $"Mit Long Runs bis {longestRunKm:F1} km bereits gut fundiert.")),
                "10k" => (
                    "Gute Basis",
                    "info",
                    "Empfehlung: 1-2 laengere Laeufe ueber 8-10 km ergaenzen."),
                "half_marathon" when totalKm >= 60.0 && longestRunKm >= 12.0 => (
                    "Gute Vorbereitung",
                    "success",
                    "Gutes Fundament fuer den Halbmarathon vorhanden."),
                "half_marathon" => (
                    "Aufbauphase empfohlen",
                    "warning",
                    "Empfohlen: Wochenvolumen und Long Run schrittweise auf 14-16 km ausbauen."),
                _ => (
                    "Spezifischer Zyklus noetig",
                    "info",
                    "Erfordert 12-16 Wochen gezielten Aufbau mit langen Grundlageneinheiten.")
            };

            predictions.Add(new RacePredictionEntry
            {
                Key = key,
                Name = name,
                DistanceKm = distanceKm,
                PredictedSeconds = (int)Math.Round(predTimeS, MidpointRounding.AwayFromZero),
                PredictedTimeText = FormatTimeSeconds(predTimeS),
                TargetPaceText = FormatPaceSeconds(predPaceS),
                ReadinessLabel = readinessLabel,
                ReadinessSeverity = readinessSeverity,
                Note = note,
                Basis = basisText
            });
        }

        return predictions;
    }

    private static double ResolveFallbackPaceSeconds(AthleteProfile? profile)
    {
        if (profile?.PaceTargetsMinPerKm is not { Count: > 0 } targets)
        {
            return 360.0;
        }

        string? raw = null;
        if (targets.TryGetValue("tempo_run", out var tempo))
        {
            raw = tempo;
        }
        else if (targets.TryGetValue("easy_run", out var easy))
        {
            raw = easy;
        }

        if (!string.IsNullOrWhiteSpace(raw))
        {
            var firstPart = raw.Split('-', 2, StringSplitOptions.TrimEntries)[0];
            var parts = firstPart.Split(':');
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mins)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs))
            {
                return (mins * 60.0) + secs;
            }
        }

        return 360.0;
    }

    private static string FormatTimeSeconds(double seconds)
    {
        var sec = (int)Math.Round(seconds, MidpointRounding.AwayFromZero);
        if (sec < 3600)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{sec / 60}:{sec % 60:D2} min");
        }

        var hours = sec / 3600;
        var rem = sec % 3600;
        return string.Create(CultureInfo.InvariantCulture, $"{hours}h {rem / 60:D2}m {rem % 60:D2}s");
    }

    private static string FormatPaceSeconds(double secondsPerKm)
    {
        if (!double.IsFinite(secondsPerKm) || secondsPerKm <= 0.0)
        {
            return "-:-- /km";
        }

        var total = (int)Math.Round(secondsPerKm, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{total / 60}:{total % 60:D2} /km");
    }
}

/// <summary>
/// Progressive 4-week volume forecast using calendar-accurate <see cref="ISOWeek"/> arithmetic
/// so 53-week ISO years (e.g. 2026-W53) never roll over prematurely.
/// </summary>
public static class VolumeForecastCalculator
{
    private static readonly double[] Multipliers = [1.10, 1.20, 1.30, 0.95];

    private static readonly (string Title, string Description)[] WeekDescriptions =
    [
        ("Aufbauwoche 1", "Stetige Steigerung (+10%) nach der 10%-Regel"),
        ("Aufbauwoche 2", "Progressive Volumenanpassung mit solidem Long Run"),
        ("Spitzenwoche", "Hoechste Belastung des 4-Wochen-Trainingsblocks"),
        ("Regenerationswoche", "Aktive Erholung & Superkompensation (-25% vs Peak)")
    ];

    public static IReadOnlyList<VolumeForecastWeek> Generate(
        IReadOnlyList<WeeklyTrendSummary> trends,
        DateOnly refDate,
        int weeks = 4)
    {
        ArgumentNullException.ThrowIfNull(trends);

        var baseKm = 12.0;
        var anchorMonday = ISOWeek.ToDateOnly(ISOWeek.GetYear(refDate), ISOWeek.GetWeekOfYear(refDate), DayOfWeek.Monday);

        if (trends.Count > 0)
        {
            var recent = trends.TakeLast(3).ToList();
            var avgKm = recent.Average(t => t.TotalDistanceKm);
            if (avgKm > 0.0)
            {
                baseKm = avgKm;
            }

            if (TryParseIsoWeekMonday(trends[^1].IsoWeek, out var parsedMonday))
            {
                anchorMonday = parsedMonday;
            }
        }

        var count = Math.Max(1, weeks);
        var result = new List<VolumeForecastWeek>(count);
        for (var i = 0; i < count; i++)
        {
            var targetMonday = anchorMonday.AddDays(7 * (i + 1));
            var isoYear = ISOWeek.GetYear(targetMonday);
            var isoWeek = ISOWeek.GetWeekOfYear(targetMonday);
            var isoStr = string.Create(CultureInfo.InvariantCulture, $"{isoYear}-W{isoWeek:D2}");

            var mult = Multipliers[i % Multipliers.Length];
            var projDist = Math.Round(baseKm * mult, 1, MidpointRounding.ToEven);
            var projLoad = Math.Round(projDist * 8.5, 0, MidpointRounding.ToEven);
            var (title, desc) = WeekDescriptions[i % WeekDescriptions.Length];

            result.Add(new VolumeForecastWeek
            {
                IsoWeek = isoStr,
                ProjectedDistanceKm = projDist,
                ProjectedLoad = projLoad,
                PhaseTitle = title,
                Description = desc
            });
        }

        return result;
    }

    private static bool TryParseIsoWeekMonday(string? isoWeekText, out DateOnly monday)
    {
        monday = default;
        if (string.IsNullOrWhiteSpace(isoWeekText))
        {
            return false;
        }

        var parts = isoWeekText.Split("-W", StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var week)
            && week >= 1
            && week <= ISOWeek.GetWeeksInYear(year))
        {
            monday = ISOWeek.ToDateOnly(year, week, DayOfWeek.Monday);
            return true;
        }

        return false;
    }
}

/// <summary>
/// Builds a strict <c>&lt;= refDate</c> <see cref="DashboardSnapshot"/> combining telemetry,
/// 4-stage reviews, Role 2 narrative insights, and 14-day Banister load projection.
/// </summary>
public static class DashboardSnapshotBuilder
{
    public static DashboardSnapshot Build(
        string slug,
        string dashboardSlug,
        AthleteProfile profile,
        IReadOnlyList<IntervalsActivityEntry> activities,
        IReadOnlyList<IntervalsWellnessEntry>? wellnessRecords,
        IReadOnlyDictionary<string, RunReviewRecord>? runReviews,
        IReadOnlyList<CalendarEventEntry>? calendarEvents,
        DateOnly refDate,
        int maxWeeks = 12,
        BlockCompassSnapshot? currentCompass = null,
        NarrativeReport? latestNarrative = null,
        IReadOnlyList<AutopilotDecisionTraceEntry>? recentTraces = null,
        IReadOnlySet<long>? autoEventIds = null,
        IReadOnlySet<DateOnly>? userLockedDates = null,
        DateTimeOffset? generatedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(dashboardSlug);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(activities);

        var activitiesUpToRef = activities
            .Where(a => DateOnly.FromDateTime(a.StartDateLocal) <= refDate)
            .ToList();

        var reviewsUpToRef = (runReviews ?? new Dictionary<string, RunReviewRecord>(StringComparer.Ordinal))
            .Where(kvp => DateOnly.FromDateTime(kvp.Value.Date) <= refDate)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

        var stravaPlaceholderCount = activitiesUpToRef.Count(a =>
            string.Equals(a.Source, "STRAVA", StringComparison.OrdinalIgnoreCase) && a.Type is null);

        var validRunsDesc = PhysiologicalCalculator.FilterValidRuns(activitiesUpToRef)
            .OrderByDescending(a => a.StartDateLocal)
            .ToList();
        var allRunMetrics = validRunsDesc.Select(PhysiologicalCalculator.CalculateRunMetrics).ToList();

        var totalDistKm = Math.Round(allRunMetrics.Sum(r => r.DistanceKm), 2, MidpointRounding.ToEven);
        var totalTimeS = allRunMetrics.Sum(r => r.MovingTimeSeconds);
        var avgPaceText = totalDistKm > 0.0
            ? FormatPace(totalTimeS / totalDistKm)
            : "-:-- /km";

        var hrTime = allRunMetrics.Where(r => r.AverageHeartRate > 0.0).Sum(r => r.MovingTimeSeconds);
        var hrWeighted = allRunMetrics.Where(r => r.AverageHeartRate > 0.0).Sum(r => r.AverageHeartRate * r.MovingTimeSeconds);
        var avgHr = hrTime > 0 ? Math.Round(hrWeighted / hrTime, 1, MidpointRounding.ToEven) : 0.0;

        var z1Z2Total = allRunMetrics.Sum(r => r.Z1Z2Seconds);
        var zoneTotal = allRunMetrics.Sum(r => r.TotalZoneSeconds);
        var z1Z2Pct = zoneTotal > 0 ? Math.Round((z1Z2Total / (double)zoneTotal) * 100.0, 1, MidpointRounding.ToEven) : 0.0;

        var pureBaseEfs = allRunMetrics
            .Where(r => r.TotalZoneSeconds > 0 && (r.Z1Z2Seconds / (double)r.TotalZoneSeconds) >= 0.80 && r.AerobicEfficiencyFactor.HasValue)
            .Select(r => r.AerobicEfficiencyFactor!.Value)
            .ToList();
        var fallbackEfs = pureBaseEfs.Count > 0
            ? pureBaseEfs
            : allRunMetrics.Where(r => r.AerobicEfficiencyFactor.HasValue).Select(r => r.AerobicEfficiencyFactor!.Value).ToList();
        double? baseAef = fallbackEfs.Count > 0
            ? Math.Round(fallbackEfs.Average(), 3, MidpointRounding.ToEven)
            : null;

        var reportSnapshot = PhysiologicalCalculator.CreateReportSnapshot(
            profile,
            activitiesUpToRef,
            wellnessRecords,
            asOfDate: refDate,
            recentLimit: 25,
            maxWeeks: maxWeeks);

        var zonePaceTrends = ZonePaceCalculator.BuildZonePaceTrends(reviewsUpToRef, maxDate: refDate);
        var racePredictions = RacePredictionCalculator.Predict(allRunMetrics, profile);
        var volumeForecast = VolumeForecastCalculator.Generate(reportSnapshot.WeeklyTrends, refDate, weeks: 4);

        var dossier = CoachIntelligenceDossierBuilder.Build(
            profile,
            activitiesUpToRef,
            wellnessRecords,
            reviewsUpToRef,
            calendarEvents,
            refDate,
            refDate,
            autoEventIds,
            userLockedDates,
            currentCompass);

        BanisterProjectionResult? banister14d = null;
        if (dossier.Readiness.Ctl.HasValue && dossier.Readiness.Atl.HasValue)
        {
            var dailyLoads = new Dictionary<DateOnly, double>();
            foreach (var evt in dossier.Calendar.UpcomingEventsNext14Days)
            {
                if (!string.Equals(evt.Category, "WORKOUT", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var load = evt.EstimatedLoad ?? (evt.DurationSeconds.HasValue ? evt.DurationSeconds.Value / 60.0 * 0.9 : 0.0);
                dailyLoads[evt.Date] = dailyLoads.TryGetValue(evt.Date, out var existing) ? existing + load : load;
            }

            banister14d = TrainingLoadProjector.ProjectBanister(
                dossier.Readiness.Ctl.Value,
                dossier.Readiness.Atl.Value,
                refDate,
                dailyLoads,
                horizonDays: 14);
        }

        var recentReviews = reviewsUpToRef.Values
            .OrderByDescending(r => r.Date)
            .Take(25)
            .ToList();

        var actById = validRunsDesc.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var allRunRows = new List<DashboardRunRow>(allRunMetrics.Count);
        foreach (var metric in allRunMetrics)
        {
            reviewsUpToRef.TryGetValue(metric.Id, out var rev);
            actById.TryGetValue(metric.Id, out var rawAct);

            var z2Gap = rev?.ZonePaceStats?.Zones.Values.FirstOrDefault(z => z.Zone == 2)?.GapPaceText;
            var ratingLabel = rev?.Review.RatingLabel ?? (metric.Z1Z2Percentage switch
            {
                >= 80.0 => "Aerobe Basis",
                >= 60.0 => "Ueberwiegend aerob",
                _ => "Intensiv / Gemischt"
            });
            var bucket = metric.Z1Z2Percentage switch
            {
                >= 80.0 => "base",
                >= 60.0 => "mixed",
                null => "mixed",
                _ => "quality"
            };
            var note = !string.IsNullOrWhiteSpace(rev?.AthleteNote)
                ? rev.AthleteNote
                : rawAct?.AthleteNote;

            allRunRows.Add(new DashboardRunRow
            {
                Id = metric.Id,
                Date = DateOnly.FromDateTime(metric.StartDateLocal),
                IsoWeek = metric.IsoWeek,
                Name = metric.Name,
                DistanceKm = metric.DistanceKm,
                MovingTimeSeconds = metric.MovingTimeSeconds,
                MovingTimeText = rev?.MovingTimeText ?? FormatDurationMmSs(metric.MovingTimeSeconds),
                PaceSecondsPerKm = metric.PaceSecondsPerKm,
                PaceText = FormatPace(metric.PaceSecondsPerKm),
                AverageHeartRate = metric.AverageHeartRate,
                TrainingLoad = metric.TrainingLoad,
                Z1Z2Seconds = metric.Z1Z2Seconds,
                TotalZoneSeconds = metric.TotalZoneSeconds,
                Z1Z2Percentage = metric.Z1Z2Percentage,
                AerobicEf = metric.AerobicEfficiencyFactor,
                ZoneTimes = metric.ZoneTimes,
                Z2GapPaceText = z2Gap,
                RatingLabel = ratingLabel,
                Directive = rev?.Review.Stages.Stage4NextRun.Directive,
                AthleteNote = note,
                IntensityBucket = bucket
            });
        }

        var filteredTraces = (recentTraces ?? [])
            .Where(t => t.ReferenceDate <= refDate)
            .OrderByDescending(t => t.Timestamp)
            .Take(10)
            .ToList();

        return new DashboardSnapshot
        {
            Slug = slug,
            DashboardSlug = dashboardSlug,
            AthleteName = dossier.Athlete.Name,
            ReferenceDate = refDate,
            GeneratedAtUtc = generatedAtUtc ?? DateTimeOffset.UtcNow,
            TotalRuns = allRunMetrics.Count,
            TotalDistanceKm = totalDistKm,
            TotalDurationSeconds = totalTimeS,
            AveragePaceText = avgPaceText,
            AverageHeartRate = avgHr,
            OverallZ1Z2Percentage = z1Z2Pct,
            BaseAerobicEf = baseAef,
            StravaPlaceholderCount = stravaPlaceholderCount,
            HeartRateProfile = profile.HeartRate,
            ReportSnapshot = reportSnapshot,
            ZonePaceTrends = zonePaceTrends,
            RacePredictions = racePredictions,
            VolumeForecast = volumeForecast,
            Dossier = dossier,
            CurrentCompass = currentCompass,
            Milestones = dossier.Milestones,
            LatestNarrative = latestNarrative is not null && latestNarrative.ReferenceDate <= refDate ? latestNarrative : null,
            Banister14DayProjection = banister14d,
            RecentReviews = recentReviews,
            RecentTraces = filteredTraces,
            AllRuns = allRunRows
        };
    }

    private static string FormatDurationMmSs(int totalSeconds)
    {
        var secs = Math.Max(0, totalSeconds);
        var hrs = secs / 3600;
        var mins = (secs % 3600) / 60;
        var rem = secs % 60;
        return hrs > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hrs}:{mins:D2}:{rem:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"{mins}:{rem:D2}");
    }

    private static string FormatPace(double secondsPerKm)
    {
        var total = (int)Math.Round(secondsPerKm, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{total / 60}:{total % 60:D2} /km");
    }
}
