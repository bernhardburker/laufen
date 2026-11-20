using System.Globalization;

namespace Laufen.Domain;

/// <summary>
/// Deterministic physiological metrics computed for a single running activity.
/// </summary>
public sealed record RunPhysiologicalMetrics
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DateTime StartDateLocal { get; init; }
    public required string IsoWeek { get; init; }
    public required double DistanceKm { get; init; }
    public required int MovingTimeSeconds { get; init; }
    public required double AverageHeartRate { get; init; }
    public required double PaceSecondsPerKm { get; init; }
    public required double TrainingLoad { get; init; }
    public required double? AerobicEfficiencyFactor { get; init; }
    public required int Z1Z2Seconds { get; init; }
    public required int TotalZoneSeconds { get; init; }
    public required double? Z1Z2Percentage { get; init; }
    public required IReadOnlyList<int> ZoneTimes { get; init; }
}

/// <summary>
/// Aggregated weekly running volume, intensity distribution, and aerobic efficiency metrics.
/// </summary>
public sealed record WeeklyTrendSummary
{
    public required string IsoWeek { get; init; }
    public required int RunsCount { get; init; }
    public required double TotalDistanceKm { get; init; }
    public required double LongestRunKm { get; init; }
    public required int TotalTimeSeconds { get; init; }
    public required double TotalLoad { get; init; }
    public required double AveragePaceSecondsPerKm { get; init; }
    public required double AverageHeartRate { get; init; }
    public required double? Z1Z2Percentage { get; init; }
    public required double? AerobicEfficiencyFactor { get; init; }
    public required double? BaseAerobicEfficiencyFactor { get; init; }
    public required double? BaseAveragePaceSecondsPerKm { get; init; }
    public required int BaseRunsCount { get; init; }
    public required IReadOnlyList<int> ZoneBreakdownSeconds { get; init; }
    public required IReadOnlyList<double> ZoneBreakdownPercentages { get; init; }
}

/// <summary>
/// A single chronological point in the Banister CTL/ATL/TSB timeline.
/// </summary>
public sealed record FitnessFormPoint
{
    public required DateOnly Date { get; init; }
    public required double Ctl { get; init; }
    public required double Atl { get; init; }
    public required double Tsb { get; init; }
    public required string Name { get; init; }
}

/// <summary>
/// Banister impulse-response fitness (CTL), fatigue (ATL), and form (TSB) summary.
/// </summary>
public sealed record FitnessFormSummary
{
    public required double LatestCtl { get; init; }
    public required double LatestAtl { get; init; }
    public required double CurrentTsb { get; init; }
    public required string DataSource { get; init; }
    public required string TsbLevel { get; init; }
    public required IReadOnlyList<FitnessFormPoint> Timeline { get; init; }
}

/// <summary>
/// Combined physiological report snapshot for an athlete.
/// </summary>
public sealed record AthleteReportSnapshot
{
    public required string AthleteId { get; init; }
    public required string AthleteName { get; init; }
    public required int? TargetRunsPerWeek { get; init; }
    public required int? Z2MaxHeartRate { get; init; }
    public required IReadOnlyList<RunPhysiologicalMetrics> RecentRuns { get; init; }
    public required IReadOnlyList<WeeklyTrendSummary> WeeklyTrends { get; init; }
    public required FitnessFormSummary FitnessForm { get; init; }
}

/// <summary>
/// Pure domain calculators for AEF, weekly volume/zone trends, and Banister CTL/ATL/TSB.
/// </summary>
public static class PhysiologicalCalculator
{
    private const double CtlTimeConstantDays = 42.0;
    private const double AtlTimeConstantDays = 7.0;
    private const double BaseRunZ1Z2Threshold = 0.70;
    private const double DefaultFallbackCtl = 14.6;
    private const double DefaultFallbackAtl = 16.6;

    public static IReadOnlyList<IntervalsActivityEntry> FilterValidRuns(IEnumerable<IntervalsActivityEntry> activities)
    {
        ArgumentNullException.ThrowIfNull(activities);

        return activities
            .Where(a => string.Equals(a.Type, "Run", StringComparison.Ordinal)
                && a.Distance is > 0.0
                && a.MovingTime is > 0)
            .ToList();
    }

    public static string ExtractIsoWeek(DateTime dateTime)
    {
        var isoYear = ISOWeek.GetYear(dateTime);
        var isoWeek = ISOWeek.GetWeekOfYear(dateTime);
        return string.Create(CultureInfo.InvariantCulture, $"{isoYear}-W{isoWeek:D2}");
    }

    public static RunPhysiologicalMetrics CalculateRunMetrics(IntervalsActivityEntry run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var distanceMeters = run.Distance ?? 0.0;
        var movingTimeSeconds = run.MovingTime ?? 0;
        var rawDistanceKm = distanceMeters / 1000.0;
        var paceSecondsPerKm = rawDistanceKm > 0.0 ? movingTimeSeconds / rawDistanceKm : 0.0;
        var avgHr = (double)(run.AverageHeartrate ?? 0);

        double? aerobicEf = null;
        if (avgHr > 0.0 && movingTimeSeconds > 0)
        {
            var speedMetersPerMin = distanceMeters / (movingTimeSeconds / 60.0);
            aerobicEf = Math.Round(speedMetersPerMin / avgHr, 3, MidpointRounding.ToEven);
        }

        var zoneTimes = run.IcuHrZoneTimes ?? [];
        var z1Z2Seconds = 0;
        var totalZoneSeconds = 0;
        if (zoneTimes.Count >= 2)
        {
            z1Z2Seconds = zoneTimes[0] + zoneTimes[1];
            totalZoneSeconds = zoneTimes.Sum();
        }

        double? z1Z2Pct = totalZoneSeconds > 0
            ? Math.Round((z1Z2Seconds / (double)totalZoneSeconds) * 100.0, 1, MidpointRounding.ToEven)
            : null;

        var trainingLoad = (double)(run.IcuTrainingLoad ?? 0);

        return new RunPhysiologicalMetrics
        {
            Id = run.Id,
            Name = string.IsNullOrEmpty(run.Name) ? "Run" : run.Name,
            StartDateLocal = run.StartDateLocal,
            IsoWeek = ExtractIsoWeek(run.StartDateLocal),
            DistanceKm = Math.Round(rawDistanceKm, 2, MidpointRounding.ToEven),
            MovingTimeSeconds = movingTimeSeconds,
            AverageHeartRate = Math.Round(avgHr, 1, MidpointRounding.ToEven),
            PaceSecondsPerKm = paceSecondsPerKm,
            TrainingLoad = Math.Round(trainingLoad, 1, MidpointRounding.ToEven),
            AerobicEfficiencyFactor = aerobicEf,
            Z1Z2Seconds = z1Z2Seconds,
            TotalZoneSeconds = totalZoneSeconds,
            Z1Z2Percentage = z1Z2Pct,
            ZoneTimes = zoneTimes
        };
    }

    public static IReadOnlyList<WeeklyTrendSummary> AggregateWeeklyTrends(
        IEnumerable<IntervalsActivityEntry> activities,
        int maxWeeks = 12)
    {
        ArgumentNullException.ThrowIfNull(activities);

        var validRuns = FilterValidRuns(activities);
        if (validRuns.Count == 0)
        {
            return [];
        }

        var parsedRuns = validRuns
            .Select(CalculateRunMetrics)
            .OrderBy(r => r.StartDateLocal)
            .ToList();

        var groupedByWeek = new Dictionary<string, List<RunPhysiologicalMetrics>>(StringComparer.Ordinal);
        foreach (var run in parsedRuns)
        {
            if (!groupedByWeek.TryGetValue(run.IsoWeek, out var list))
            {
                list = [];
                groupedByWeek[run.IsoWeek] = list;
            }

            list.Add(run);
        }

        var sortedWeeks = groupedByWeek.Keys
            .OrderBy(w => w, StringComparer.Ordinal)
            .ToList();

        if (maxWeeks > 0 && sortedWeeks.Count > maxWeeks)
        {
            sortedWeeks = sortedWeeks.Skip(sortedWeeks.Count - maxWeeks).ToList();
        }

        var trends = new List<WeeklyTrendSummary>(sortedWeeks.Count);
        foreach (var week in sortedWeeks)
        {
            var weekRuns = groupedByWeek[week];
            var totalDistance = weekRuns.Sum(r => r.DistanceKm);
            var totalTime = weekRuns.Sum(r => r.MovingTimeSeconds);
            var totalLoad = weekRuns.Sum(r => r.TrainingLoad);

            var avgPaceSeconds = totalDistance > 0.0 ? totalTime / totalDistance : 0.0;

            var hrWeightSum = weekRuns.Where(r => r.AverageHeartRate > 0.0).Sum(r => r.AverageHeartRate * r.MovingTimeSeconds);
            var hrTimeSum = weekRuns.Where(r => r.AverageHeartRate > 0.0).Sum(r => r.MovingTimeSeconds);
            var avgHr = hrTimeSum > 0 ? hrWeightSum / hrTimeSum : 0.0;

            var weekZ1Z2 = weekRuns.Sum(r => r.Z1Z2Seconds);
            var weekZoneTotal = weekRuns.Sum(r => r.TotalZoneSeconds);
            double? z1Z2Pct = weekZoneTotal > 0
                ? Math.Round((weekZ1Z2 / (double)weekZoneTotal) * 100.0, 1, MidpointRounding.ToEven)
                : null;

            var efValues = weekRuns
                .Where(r => r.AerobicEfficiencyFactor.HasValue)
                .Select(r => r.AerobicEfficiencyFactor!.Value)
                .ToList();
            double? avgEf = efValues.Count > 0
                ? Math.Round(efValues.Average(), 3, MidpointRounding.ToEven)
                : null;

            var baseRuns = weekRuns
                .Where(r => r.TotalZoneSeconds > 0 && (r.Z1Z2Seconds / (double)r.TotalZoneSeconds) >= BaseRunZ1Z2Threshold)
                .ToList();

            var baseEfValues = baseRuns
                .Where(r => r.AerobicEfficiencyFactor.HasValue)
                .Select(r => r.AerobicEfficiencyFactor!.Value)
                .ToList();
            double? baseAvgEf = baseEfValues.Count > 0
                ? Math.Round(baseEfValues.Average(), 3, MidpointRounding.ToEven)
                : null;

            var baseDistance = baseRuns.Sum(r => r.DistanceKm);
            var baseTime = baseRuns.Sum(r => r.MovingTimeSeconds);
            double? baseAvgPaceSeconds = baseDistance > 0.0 ? baseTime / baseDistance : null;

            var longestKm = weekRuns.Max(r => r.DistanceKm);

            var z1 = weekRuns.Sum(r => r.ZoneTimes.Count > 0 ? r.ZoneTimes[0] : 0);
            var z2 = weekRuns.Sum(r => r.ZoneTimes.Count > 1 ? r.ZoneTimes[1] : 0);
            var z3 = weekRuns.Sum(r => r.ZoneTimes.Count > 2 ? r.ZoneTimes[2] : 0);
            var z4 = weekRuns.Sum(r => r.ZoneTimes.Count > 3 ? r.ZoneTimes[3] : 0);
            var z5 = weekRuns.Sum(r => r.ZoneTimes.Count > 4 ? r.ZoneTimes.Skip(4).Sum() : 0);
            var zTotal = z1 + z2 + z3 + z4 + z5;

            int[] zoneBreakdownSeconds = [z1, z2, z3, z4, z5];
            double[] zoneBreakdownPct =
            [
                zTotal > 0 ? Math.Round((z1 / (double)zTotal) * 100.0, 1, MidpointRounding.ToEven) : 0.0,
                zTotal > 0 ? Math.Round((z2 / (double)zTotal) * 100.0, 1, MidpointRounding.ToEven) : 0.0,
                zTotal > 0 ? Math.Round((z3 / (double)zTotal) * 100.0, 1, MidpointRounding.ToEven) : 0.0,
                zTotal > 0 ? Math.Round((z4 / (double)zTotal) * 100.0, 1, MidpointRounding.ToEven) : 0.0,
                zTotal > 0 ? Math.Round((z5 / (double)zTotal) * 100.0, 1, MidpointRounding.ToEven) : 0.0
            ];

            trends.Add(new WeeklyTrendSummary
            {
                IsoWeek = week,
                RunsCount = weekRuns.Count,
                TotalDistanceKm = Math.Round(totalDistance, 2, MidpointRounding.ToEven),
                LongestRunKm = Math.Round(longestKm, 2, MidpointRounding.ToEven),
                TotalTimeSeconds = totalTime,
                TotalLoad = Math.Round(totalLoad, 1, MidpointRounding.ToEven),
                AveragePaceSecondsPerKm = avgPaceSeconds,
                AverageHeartRate = Math.Round(avgHr, 1, MidpointRounding.ToEven),
                Z1Z2Percentage = z1Z2Pct,
                AerobicEfficiencyFactor = avgEf,
                BaseAerobicEfficiencyFactor = baseAvgEf,
                BaseAveragePaceSecondsPerKm = baseAvgPaceSeconds,
                BaseRunsCount = baseRuns.Count,
                ZoneBreakdownSeconds = zoneBreakdownSeconds,
                ZoneBreakdownPercentages = zoneBreakdownPct
            });
        }

        return trends;
    }

    public static FitnessFormSummary CalculateFitnessFormTrend(
        IEnumerable<IntervalsActivityEntry> activities,
        IEnumerable<IntervalsWellnessEntry>? wellnessRecords = null,
        DateOnly? asOfDate = null)
    {
        ArgumentNullException.ThrowIfNull(activities);

        var timeline = new List<FitnessFormPoint>();
        double? latestCtl = null;
        double? latestAtl = null;
        DateOnly? latestRunDate = null;

        foreach (var act in activities.OrderBy(a => a.StartDateLocal))
        {
            if (act.Type is not null && !string.Equals(act.Type, "Run", StringComparison.Ordinal))
            {
                continue;
            }

            var actDate = DateOnly.FromDateTime(act.StartDateLocal);
            if (asOfDate.HasValue && actDate > asOfDate.Value)
            {
                continue;
            }

            if (act.IcuCtl.HasValue && act.IcuAtl.HasValue)
            {
                var ctlVal = Math.Round(act.IcuCtl.Value, 1, MidpointRounding.ToEven);
                var atlVal = Math.Round(act.IcuAtl.Value, 1, MidpointRounding.ToEven);
                var tsbVal = Math.Round(ctlVal - atlVal, 1, MidpointRounding.ToEven);

                latestCtl = ctlVal;
                latestAtl = atlVal;
                latestRunDate = actDate;

                timeline.Add(new FitnessFormPoint
                {
                    Date = actDate,
                    Ctl = ctlVal,
                    Atl = atlVal,
                    Tsb = tsbVal,
                    Name = string.IsNullOrEmpty(act.Name) ? "Lauf" : act.Name
                });
            }
        }

        var currentCtl = latestCtl ?? DefaultFallbackCtl;
        var currentAtl = latestAtl ?? DefaultFallbackAtl;
        var dataSource = "activities";
        var measurementDate = latestRunDate;

        if (wellnessRecords is not null && asOfDate.HasValue)
        {
            var latestWellness = wellnessRecords
                .Where(w => w.Id <= asOfDate.Value && w.Ctl.HasValue && w.Atl.HasValue)
                .OrderByDescending(w => w.Id)
                .FirstOrDefault();

            if (latestWellness is not null && (!measurementDate.HasValue || latestWellness.Id >= measurementDate.Value))
            {
                currentCtl = Math.Round(latestWellness.Ctl!.Value, 1, MidpointRounding.ToEven);
                currentAtl = Math.Round(latestWellness.Atl!.Value, 1, MidpointRounding.ToEven);
                dataSource = "wellness";
                measurementDate = latestWellness.Id;
            }
        }

        if (asOfDate.HasValue && measurementDate.HasValue)
        {
            var daysGap = asOfDate.Value.DayNumber - measurementDate.Value.DayNumber;
            if (daysGap > 0)
            {
                currentCtl = Math.Round(currentCtl * Math.Exp(-daysGap / CtlTimeConstantDays), 1, MidpointRounding.ToEven);
                currentAtl = Math.Round(currentAtl * Math.Exp(-daysGap / AtlTimeConstantDays), 1, MidpointRounding.ToEven);
                dataSource = "decayed";
            }
        }

        var currentTsb = Math.Round(currentCtl - currentAtl, 1, MidpointRounding.ToEven);
        var tsbLevel = currentTsb switch
        {
            > 5.0 => "fresh",
            >= -10.0 => "neutral",
            >= -25.0 => "productive",
            _ => "fatigued"
        };

        return new FitnessFormSummary
        {
            LatestCtl = currentCtl,
            LatestAtl = currentAtl,
            CurrentTsb = currentTsb,
            DataSource = dataSource,
            TsbLevel = tsbLevel,
            Timeline = timeline
        };
    }

    public static AthleteReportSnapshot CreateReportSnapshot(
        AthleteProfile profile,
        IReadOnlyList<IntervalsActivityEntry> activities,
        IReadOnlyList<IntervalsWellnessEntry>? wellnessRecords = null,
        DateOnly? asOfDate = null,
        int recentLimit = 5,
        int maxWeeks = 12)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(activities);

        var recentRuns = FilterValidRuns(activities)
            .OrderByDescending(r => r.StartDateLocal)
            .Take(Math.Max(0, recentLimit))
            .Select(CalculateRunMetrics)
            .ToList();

        var weeklyTrends = AggregateWeeklyTrends(activities, maxWeeks);
        var fitnessForm = CalculateFitnessFormTrend(activities, wellnessRecords, asOfDate);

        int? z2MaxHr = profile.HeartRate.Zones.Values
            .FirstOrDefault(z => z.Zone == 2)?.Max;

        return new AthleteReportSnapshot
        {
            AthleteId = profile.Id,
            AthleteName = string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Name : profile.DisplayName,
            TargetRunsPerWeek = profile.TargetRunsPerWeek,
            Z2MaxHeartRate = z2MaxHr,
            RecentRuns = recentRuns,
            WeeklyTrends = weeklyTrends,
            FitnessForm = fitnessForm
        };
    }
}
