using System.Globalization;

namespace Laufen.Domain;

public sealed record AthletePlanningInfo
{
    public required string Classification { get; init; }
    public required string TrainingMode { get; init; }
    public required int Z2MaxHr { get; init; }
    public required int Z4RangeMin { get; init; }
    public required int Z4RangeMax { get; init; }
    public required int Lthr { get; init; }
}

public sealed record ReadinessMarkers
{
    public required double Tsb { get; init; }
    public required double Atl { get; init; }
    public required double Ctl { get; init; }
    public required double SleepHoursLastNight { get; init; }
    public required double SleepScoreLastNight { get; init; }
    public required bool SleepIsToday { get; init; }
    public required DateOnly? SleepDate { get; init; }
    public required int RestingHrToday { get; init; }
    public required double RestingHrBaseline { get; init; }
    public required double RhrDelta { get; init; }
    public required bool ElevatedRhr { get; init; }
    public required double HrvToday { get; init; }
    public required int? Soreness { get; init; }
    public required int? Fatigue { get; init; }
    public required string? Injury { get; init; }
    public required string? IllnessComment { get; init; }
    public required bool HealthWarning { get; init; }
}

public sealed record LastRunSummary
{
    public required DateOnly Date { get; init; }
    public required string Type { get; init; }
    public required string Name { get; init; }
    public required double DistanceKm { get; init; }
    public required double DurationMin { get; init; }
    public required double AverageHr { get; init; }
    public required double Load { get; init; }
    public required double Z1Z2Percentage { get; init; }
    public required bool HasMuscularComplaint { get; init; }
    public required string? AthleteNote { get; init; }
}

public sealed record LastQualityRunSummary
{
    public required DateOnly Date { get; init; }
    public required string Type { get; init; }
    public required string Name { get; init; }
    public required double DistanceKm { get; init; }
    public required double Z4Minutes { get; init; }
    public required double Z5Minutes { get; init; }
    public required double Load { get; init; }
}

public sealed record RunHistoryMarkers
{
    public required int DaysSinceLastRun { get; init; }
    public required int TargetDaysSinceLastRun { get; init; }
    public required int DaysSinceLastQuality { get; init; }
    public required int TargetDaysSinceLastQuality { get; init; }
    public required int DaysSinceLastBaseRun { get; init; }
    public required bool BaseAnchorCompleted { get; init; }
    public required IReadOnlyList<DateOnly> MissedAnchorDays { get; init; }
    public required LastRunSummary? LastRun { get; init; }
    public required LastQualityRunSummary? LastQualityRun { get; init; }
    public required int WeeklyRunsCompleted { get; init; }
    public required int TargetRunsPerWeek { get; init; }
    public required double LastWeekTotalKm { get; init; }
    public required double LastWeekZ1Z2Percentage { get; init; }
    public required int ConsecutiveBuildWeeks { get; init; }
    public required int BaseAnchorsInLast14Days { get; init; }
}

public sealed record PeriodizationMarkers
{
    public required string TargetDayName { get; init; }
    public required int SlotId { get; init; }
    public required string SlotName { get; init; }
    public required string Role { get; init; }
    public required IReadOnlyList<string> AllowedTypes { get; init; }
    public required string PreferredType { get; init; }
    public required string SlotPurpose { get; init; }
    public required bool RequiresBaseAnchor { get; init; }
}

public sealed record CoachContextMarkers
{
    public required bool HasOpenCycle { get; init; }
    public required string? OpenCycleId { get; init; }
    public required DateOnly? OpenCycleDate { get; init; }
    public required string? TargetType { get; init; }
    public required int? TargetHrMax { get; init; }
    public required string? Recommendation { get; init; }
    public required int RunsCompletedSinceCycle { get; init; }
    public required bool UnfulfilledBaseDirective { get; init; }
}

public sealed record CalendarContextMarkers
{
    public required bool HasCalendar { get; init; }
    public required bool HasCompletedWorkoutToday { get; init; }
    public required bool HasPlannedWorkoutToday { get; init; }
    public required bool HasRestNoteToday { get; init; }
    public required int EventsTodayCount { get; init; }

    public static CalendarContextMarkers Empty { get; } = new()
    {
        HasCalendar = false,
        HasCompletedWorkoutToday = false,
        HasPlannedWorkoutToday = false,
        HasRestNoteToday = false,
        EventsTodayCount = 0
    };
}

public sealed record GuardrailEvaluation
{
    public required bool QualityDiscouraged { get; init; }
    public required bool FatigueWarning { get; init; }
    public required bool RestDayRecommended { get; init; }
    public required bool VolumeSpikeAlert { get; init; }
    public required bool HealthWarning { get; init; }
    public required bool BaseAnchorMissing { get; init; }
    public required bool CoachDirectiveOverride { get; init; }
    public required bool IsDeloadWeek { get; init; }
    public required bool ReentryRecommended { get; init; }
    public required bool CalendarRestDay { get; init; }
    public required bool SlotAllowsThreshold { get; init; }
    public required IReadOnlyList<string> ActiveWarnings { get; init; }
}

public sealed record PlanningMarkersSnapshot
{
    public required DateOnly ReferenceDate { get; init; }
    public required DateOnly TargetDate { get; init; }
    public required AthletePlanningInfo Athlete { get; init; }
    public required ReadinessMarkers Readiness { get; init; }
    public required RunHistoryMarkers History { get; init; }
    public required PeriodizationMarkers Periodization { get; init; }
    public required CoachContextMarkers CoachContext { get; init; }
    public required CalendarContextMarkers CalendarContext { get; init; }
    public required GuardrailEvaluation Guardrails { get; init; }
}

/// <summary>
/// Pure domain calculator for readiness markers, periodization slots, and safety guardrails.
/// </summary>
public static class PlanningMarkersCalculator
{
    private static readonly string[] QualityKeywords =
        ["interval", "tempo", "schwelle", "threshold", "speed", "race", "wettkampf"];

    private static readonly string[] ComplaintKeywords =
        ["krampf", "krämpf", "schmerz", "zerrung", "verletz", "abgebrochen", "unwohl", "krank", "schwindel", "übel", "stechen"];

    private static readonly string[] IllnessKeywords =
        ["krank", "infekt", "erkält", "fieber", "husten", "hals", "corona", "covid", "unwohl", "schlapp", "magen", "übel", "sick", "ill", "flu", "unwell"];

    private static readonly HashSet<string> BaseCoachTargets =
        new(["easy_run", "recovery_run", "rest_day", "long_run"], StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<IntervalsActivityEntry> FilterActivitiesUpToDate(
        IEnumerable<IntervalsActivityEntry> activities,
        DateOnly? refDate)
    {
        ArgumentNullException.ThrowIfNull(activities);

        if (!refDate.HasValue)
        {
            return activities.ToList();
        }

        return activities
            .Where(a => DateOnly.FromDateTime(a.StartDateLocal) <= refDate.Value)
            .ToList();
    }

    public static bool IsQualityRun(IntervalsActivityEntry run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var name = run.Name ?? string.Empty;
        if (QualityKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var zTimes = run.IcuHrZoneTimes;
        if (zTimes is { Count: >= 5 })
        {
            var z4Time = zTimes[3];
            var z5Plus = zTimes.Skip(4).Sum();
            if (z5Plus > 240 || z4Time > 480)
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasMuscularOrHealthComplaint(IntervalsActivityEntry run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var note = run.Description;
        if (string.IsNullOrWhiteSpace(note))
        {
            return false;
        }

        return ComplaintKeywords.Any(kw => note.Contains(kw, StringComparison.OrdinalIgnoreCase));
    }

    public static int CalculateConsecutiveBuildWeeks(
        IEnumerable<IntervalsActivityEntry> runs,
        DateOnly refDate,
        double minLoad = 65.0,
        double minDistanceMeters = 12000.0)
    {
        ArgumentNullException.ThrowIfNull(runs);

        var runList = runs.ToList();
        var daysSinceMonday = ((int)refDate.DayOfWeek + 6) % 7;
        var currentMonday = refDate.AddDays(-daysSinceMonday);
        var consecutiveBuild = 0;

        for (var w = 1; w <= 4; w++)
        {
            var weekStart = currentMonday.AddDays(-7 * w);
            var weekEnd = weekStart.AddDays(6);

            var weekRuns = runList
                .Where(r =>
                {
                    var d = DateOnly.FromDateTime(r.StartDateLocal);
                    return d >= weekStart && d <= weekEnd;
                })
                .ToList();

            if (weekRuns.Count == 0)
            {
                break;
            }

            var totalLoad = weekRuns.Sum(r => (double)(r.IcuTrainingLoad ?? 0));
            var totalDist = weekRuns.Sum(r => r.Distance ?? 0.0);
            var hasQuality = weekRuns.Any(IsQualityRun);

            if (hasQuality && (totalLoad >= minLoad || totalDist >= minDistanceMeters))
            {
                consecutiveBuild++;
            }
            else
            {
                break;
            }
        }

        return consecutiveBuild;
    }

    public static int CountBaseAnchors14Days(
        IEnumerable<IntervalsActivityEntry> runs,
        DateOnly refDate)
    {
        ArgumentNullException.ThrowIfNull(runs);

        var start14d = refDate.AddDays(-14);
        var count = 0;

        foreach (var r in runs)
        {
            var d = DateOnly.FromDateTime(r.StartDateLocal);
            if (d < start14d || d > refDate)
            {
                continue;
            }

            if (!IsQualityRun(r) && !HasMuscularOrHealthComplaint(r))
            {
                var distM = r.Distance ?? 0.0;
                var durS = (double)(r.MovingTime ?? r.ElapsedTime ?? 0);
                if (distM >= 4000.0 || durS >= 1500.0)
                {
                    count++;
                }
            }
        }

        return count;
    }

    public static ReadinessMarkers GetReadinessMarkers(
        IEnumerable<IntervalsWellnessEntry>? wellnessRecords,
        IEnumerable<IntervalsActivityEntry> activities,
        DateOnly refDate)
    {
        ArgumentNullException.ThrowIfNull(activities);

        var datedRecords = (wellnessRecords ?? [])
            .Where(w => w.Id <= refDate)
            .OrderByDescending(w => w.Id)
            .ToList();

        var latestW = datedRecords.FirstOrDefault();

        var recentRhr = datedRecords
            .Take(30)
            .Where(w => w.RestingHr.HasValue)
            .Select(w => (double)w.RestingHr!.Value)
            .ToList();

        var rhrBaseline = recentRhr.Count > 0
            ? Math.Round(recentRhr.Average(), 1, MidpointRounding.ToEven)
            : 48.0;

        var sleepSecs = latestW?.SleepSecs;
        var isToday = latestW is not null && latestW.Id == refDate;
        var hasSleep = sleepSecs is > 0;
        var sleepIsToday = isToday && hasSleep;
        DateOnly? sleepDate = latestW?.Id;

        double sleepHours;
        double sleepScore;

        if (isToday && hasSleep)
        {
            sleepHours = Math.Round(sleepSecs!.Value / 3600.0, 2, MidpointRounding.ToEven);
            sleepScore = latestW!.SleepScore ?? 80.0;
        }
        else if (isToday && !hasSleep)
        {
            var prevW = datedRecords.Count > 1 ? datedRecords[1] : null;
            sleepHours = prevW?.SleepSecs is { } prevSecs
                ? Math.Round(prevSecs / 3600.0, 2, MidpointRounding.ToEven)
                : 7.5;
            sleepScore = prevW?.SleepScore ?? 80.0;
            sleepDate = prevW?.Id;
        }
        else
        {
            sleepHours = sleepSecs is { } s
                ? Math.Round(s / 3600.0, 2, MidpointRounding.ToEven)
                : 7.5;
            sleepScore = latestW?.SleepScore ?? 80.0;
        }

        var restingHrToday = latestW?.RestingHr ?? (int)rhrBaseline;
        var hrvToday = latestW?.Hrv is { } hrv
            ? Math.Round(hrv, 1, MidpointRounding.ToEven)
            : 70.0;
        var rhrDelta = Math.Round(restingHrToday - rhrBaseline, 1, MidpointRounding.ToEven);
        var elevatedRhr = rhrDelta >= 4.0;

        int? soreness = latestW?.Soreness is { } sor ? (int)sor : null;
        int? fatigue = latestW?.Fatigue is { } fat ? (int)fat : null;

        string? injuryDesc = null;
        string? illnessComment = null;

        foreach (var rec in datedRecords.Take(3))
        {
            if (injuryDesc is null && rec.Injury is > 0)
            {
                injuryDesc = rec.Injury.Value.ToString(CultureInfo.InvariantCulture);
            }

            var comm = rec.Comments?.Trim();
            if (illnessComment is null
                && !string.IsNullOrEmpty(comm)
                && IllnessKeywords.Any(kw => comm.Contains(kw, StringComparison.OrdinalIgnoreCase)))
            {
                illnessComment = comm;
            }
        }

        var healthWarning = injuryDesc is not null
            || illnessComment is not null
            || elevatedRhr
            || soreness is >= 4
            || fatigue is >= 4;

        var ctl = latestW?.Ctl;
        var atl = latestW?.Atl;

        if (!ctl.HasValue || !atl.HasValue)
        {
            var latestRunWithLoad = FilterActivitiesUpToDate(activities, refDate)
                .Where(a => string.Equals(a.Type, "Run", StringComparison.Ordinal))
                .OrderByDescending(a => a.StartDateLocal)
                .FirstOrDefault();

            if (latestRunWithLoad?.IcuCtl is { } runCtl && latestRunWithLoad.IcuAtl is { } runAtl)
            {
                ctl = runCtl;
                atl = runAtl;
            }
            else
            {
                ctl = 13.0;
                atl = 15.0;
            }
        }

        var tsb = Math.Round(ctl.Value - atl.Value, 1, MidpointRounding.ToEven);

        return new ReadinessMarkers
        {
            Tsb = tsb,
            Atl = Math.Round(atl.Value, 1, MidpointRounding.ToEven),
            Ctl = Math.Round(ctl.Value, 1, MidpointRounding.ToEven),
            SleepHoursLastNight = sleepHours,
            SleepScoreLastNight = sleepScore,
            SleepIsToday = sleepIsToday,
            SleepDate = sleepDate,
            RestingHrToday = restingHrToday,
            RestingHrBaseline = rhrBaseline,
            RhrDelta = rhrDelta,
            ElevatedRhr = elevatedRhr,
            HrvToday = hrvToday,
            Soreness = soreness,
            Fatigue = fatigue,
            Injury = injuryDesc,
            IllnessComment = illnessComment,
            HealthWarning = healthWarning
        };
    }

    public static RunHistoryMarkers GetRunHistoryMarkers(
        IEnumerable<IntervalsActivityEntry> activities,
        DateOnly refDate,
        DateOnly? targetDate = null,
        AthleteProfile? athleteProfile = null)
    {
        ArgumentNullException.ThrowIfNull(activities);

        var runs = FilterActivitiesUpToDate(activities, refDate)
            .Where(a => string.Equals(a.Type, "Run", StringComparison.Ordinal))
            .OrderByDescending(a => a.StartDateLocal)
            .ToList();

        var targetRunsPerWeek = athleteProfile?.TargetRunsPerWeek ?? 3;

        if (runs.Count == 0)
        {
            return new RunHistoryMarkers
            {
                DaysSinceLastRun = 999,
                TargetDaysSinceLastRun = 999,
                DaysSinceLastQuality = 999,
                TargetDaysSinceLastQuality = 999,
                DaysSinceLastBaseRun = 999,
                BaseAnchorCompleted = true,
                MissedAnchorDays = [],
                LastRun = null,
                LastQualityRun = null,
                WeeklyRunsCompleted = 0,
                TargetRunsPerWeek = targetRunsPerWeek,
                LastWeekTotalKm = 0.0,
                LastWeekZ1Z2Percentage = 0.0,
                ConsecutiveBuildWeeks = 0,
                BaseAnchorsInLast14Days = 0
            };
        }

        var latest = runs[0];
        var latestDate = DateOnly.FromDateTime(latest.StartDateLocal);
        var daysSinceLastRun = Math.Max(0, refDate.DayNumber - latestDate.DayNumber);
        var targetDaysSinceLastRun = targetDate.HasValue
            ? Math.Max(0, targetDate.Value.DayNumber - latestDate.DayNumber)
            : daysSinceLastRun;

        var movingTimeS = (double)(latest.MovingTime ?? latest.ElapsedTime ?? 0);
        var distanceM = latest.Distance ?? 0.0;
        var zTimes = latest.IcuHrZoneTimes ?? [];
        var z1Z2S = zTimes.Count >= 2 ? zTimes[0] + zTimes[1] : 0;
        var totalZS = zTimes.Sum();
        var z1Z2Pct = totalZS > 0
            ? Math.Round((z1Z2S / (double)totalZS) * 100.0, 1, MidpointRounding.ToEven)
            : 0.0;

        var latestNote = string.IsNullOrWhiteSpace(latest.Description) ? null : latest.Description.Trim();
        var latestHasComplaint = HasMuscularOrHealthComplaint(latest);

        var lastRunSummary = new LastRunSummary
        {
            Date = latestDate,
            Type = IsQualityRun(latest) ? "quality_run" : "easy_run",
            Name = string.IsNullOrEmpty(latest.Name) ? "Lauf" : latest.Name,
            DistanceKm = Math.Round(distanceM / 1000.0, 2, MidpointRounding.ToEven),
            DurationMin = Math.Round(movingTimeS / 60.0, 1, MidpointRounding.ToEven),
            AverageHr = Math.Round((double)(latest.AverageHeartrate ?? 0), 1, MidpointRounding.ToEven),
            Load = Math.Round((double)(latest.IcuTrainingLoad ?? 0), 1, MidpointRounding.ToEven),
            Z1Z2Percentage = z1Z2Pct,
            HasMuscularComplaint = latestHasComplaint,
            AthleteNote = latestNote
        };

        var daysSinceLastBaseRun = 999;
        foreach (var r in runs)
        {
            if (!IsQualityRun(r) && !HasMuscularOrHealthComplaint(r))
            {
                var bDate = DateOnly.FromDateTime(r.StartDateLocal);
                daysSinceLastBaseRun = Math.Max(0, refDate.DayNumber - bDate.DayNumber);
                break;
            }
        }

        var checkDate = targetDate ?? refDate;
        var slots = athleteProfile?.WeeklySlots ?? [];
        var anchorAllowedDays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in slots)
        {
            if (string.Equals(slot.Role, "base_anchor", StringComparison.OrdinalIgnoreCase)
                || slot.IsMandatoryBase == true)
            {
                foreach (var dayName in slot.AllowedDays)
                {
                    anchorAllowedDays.Add(dayName);
                }
            }
        }

        var baseAnchorCompleted = true;
        var missedAnchorDays = new List<DateOnly>();
        if (anchorAllowedDays.Count > 0)
        {
            var recentAnchorDates = new List<DateOnly>();
            for (var i = 1; i <= 7; i++)
            {
                var pastD = checkDate.AddDays(-i);
                var dayName = pastD.ToString("dddd", CultureInfo.InvariantCulture);
                if (anchorAllowedDays.Contains(dayName))
                {
                    recentAnchorDates.Add(pastD);
                }
            }

            if (recentAnchorDates.Count > 0)
            {
                var minAnchorD = recentAnchorDates.Min();
                var anchorSet = recentAnchorDates.ToHashSet();
                var anchorDone = false;

                foreach (var r in runs)
                {
                    var rDate = DateOnly.FromDateTime(r.StartDateLocal);
                    if (anchorSet.Contains(rDate) && !HasMuscularOrHealthComplaint(r))
                    {
                        anchorDone = true;
                        break;
                    }

                    if (rDate >= minAnchorD && rDate <= refDate && !IsQualityRun(r) && !HasMuscularOrHealthComplaint(r))
                    {
                        anchorDone = true;
                        break;
                    }
                }

                baseAnchorCompleted = anchorDone;
                if (!anchorDone)
                {
                    missedAnchorDays = recentAnchorDates.OrderBy(d => d).ToList();
                }
            }
        }

        LastQualityRunSummary? lastQualitySummary = null;
        var daysSinceLastQuality = 999;
        var targetDaysSinceLastQuality = 999;
        foreach (var r in runs)
        {
            if (IsQualityRun(r))
            {
                var qDate = DateOnly.FromDateTime(r.StartDateLocal);
                daysSinceLastQuality = Math.Max(0, refDate.DayNumber - qDate.DayNumber);
                targetDaysSinceLastQuality = targetDate.HasValue
                    ? Math.Max(0, targetDate.Value.DayNumber - qDate.DayNumber)
                    : daysSinceLastQuality;

                var qZTimes = r.IcuHrZoneTimes ?? [];
                var z4Min = Math.Round((qZTimes.Count > 3 ? qZTimes[3] : 0) / 60.0, 1, MidpointRounding.ToEven);
                var z5Min = qZTimes.Count > 4
                    ? Math.Round(qZTimes.Skip(4).Sum() / 60.0, 1, MidpointRounding.ToEven)
                    : 0.0;
                var qName = string.IsNullOrEmpty(r.Name) ? "Quality Run" : r.Name;
                var qType = qName.Contains("interval", StringComparison.OrdinalIgnoreCase) ? "intervals" : "tempo_run";

                lastQualitySummary = new LastQualityRunSummary
                {
                    Date = qDate,
                    Type = qType,
                    Name = qName,
                    DistanceKm = Math.Round((r.Distance ?? 0.0) / 1000.0, 2, MidpointRounding.ToEven),
                    Z4Minutes = z4Min,
                    Z5Minutes = z5Min,
                    Load = Math.Round((double)(r.IcuTrainingLoad ?? 0), 1, MidpointRounding.ToEven)
                };
                break;
            }
        }

        var refDt = refDate.ToDateTime(TimeOnly.MinValue);
        var currentIsoYear = ISOWeek.GetYear(refDt);
        var currentIsoWeek = ISOWeek.GetWeekOfYear(refDt);
        var prevWeekDt = refDate.AddDays(-7).ToDateTime(TimeOnly.MinValue);
        var prevIsoYear = ISOWeek.GetYear(prevWeekDt);
        var prevIsoWeek = ISOWeek.GetWeekOfYear(prevWeekDt);

        var runsThisWeek = 0;
        var lastWeekDistM = 0.0;
        var lastWeekZ1Z2S = 0.0;
        var lastWeekTotalZS = 0.0;

        foreach (var r in runs)
        {
            var rYear = ISOWeek.GetYear(r.StartDateLocal);
            var rWeek = ISOWeek.GetWeekOfYear(r.StartDateLocal);
            if (rYear == currentIsoYear && rWeek == currentIsoWeek)
            {
                runsThisWeek++;
            }
            else if (rYear == prevIsoYear && rWeek == prevIsoWeek)
            {
                lastWeekDistM += r.Distance ?? 0.0;
                var rz = r.IcuHrZoneTimes;
                if (rz is { Count: > 0 })
                {
                    lastWeekZ1Z2S += rz.Count >= 2 ? rz[0] + rz[1] : 0;
                    lastWeekTotalZS += rz.Sum();
                }
            }
        }

        var lastWeekZPct = lastWeekTotalZS > 0.0
            ? Math.Round((lastWeekZ1Z2S / lastWeekTotalZS) * 100.0, 1, MidpointRounding.ToEven)
            : 0.0;

        return new RunHistoryMarkers
        {
            DaysSinceLastRun = daysSinceLastRun,
            TargetDaysSinceLastRun = targetDaysSinceLastRun,
            DaysSinceLastQuality = daysSinceLastQuality,
            TargetDaysSinceLastQuality = targetDaysSinceLastQuality,
            DaysSinceLastBaseRun = daysSinceLastBaseRun,
            BaseAnchorCompleted = baseAnchorCompleted,
            MissedAnchorDays = missedAnchorDays,
            LastRun = lastRunSummary,
            LastQualityRun = lastQualitySummary,
            WeeklyRunsCompleted = runsThisWeek,
            TargetRunsPerWeek = targetRunsPerWeek,
            LastWeekTotalKm = Math.Round(lastWeekDistM / 1000.0, 2, MidpointRounding.ToEven),
            LastWeekZ1Z2Percentage = lastWeekZPct,
            ConsecutiveBuildWeeks = CalculateConsecutiveBuildWeeks(runs, refDate),
            BaseAnchorsInLast14Days = CountBaseAnchors14Days(runs, refDate)
        };
    }

    public static PeriodizationMarkers GetPeriodizationMarkers(
        DateOnly targetDate,
        AthleteProfile? athleteProfile)
    {
        var targetDayName = targetDate.ToString("dddd", CultureInfo.InvariantCulture).ToLowerInvariant();
        var slots = athleteProfile?.WeeklySlots ?? [];
        var matchedSlot = slots.FirstOrDefault(s =>
            s.AllowedDays.Any(d => string.Equals(d, targetDayName, StringComparison.OrdinalIgnoreCase)));

        if (matchedSlot is null)
        {
            return new PeriodizationMarkers
            {
                TargetDayName = targetDayName,
                SlotId = 2,
                SlotName = "Flexible Slot",
                Role = "flexible",
                AllowedTypes = ["easy_run", "tempo_run"],
                PreferredType = "easy_run",
                SlotPurpose = "Flexible aerobe Basis oder Erhaltung",
                RequiresBaseAnchor = false
            };
        }

        return new PeriodizationMarkers
        {
            TargetDayName = targetDayName,
            SlotId = matchedSlot.Slot,
            SlotName = matchedSlot.Name,
            Role = matchedSlot.Role,
            AllowedTypes = matchedSlot.AllowedTypes ?? ["easy_run"],
            PreferredType = matchedSlot.PreferredType ?? "easy_run",
            SlotPurpose = matchedSlot.Description ?? string.Empty,
            RequiresBaseAnchor = matchedSlot.RequiresBaseAnchor ?? false
        };
    }

    public static CoachContextMarkers GetCoachContextMarkers(
        IEnumerable<CoachHistoryEntry>? historyRecords,
        IEnumerable<IntervalsActivityEntry> activities,
        DateOnly refDate)
    {
        ArgumentNullException.ThrowIfNull(activities);

        var records = historyRecords?.ToList() ?? [];
        CoachHistoryEntry? openCycle = null;
        var evaluatedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in records)
        {
            if (entry.EvaluatedRunIds is not null)
            {
                foreach (var eid in entry.EvaluatedRunIds)
                {
                    evaluatedIds.Add(eid);
                }
            }

            if (string.Equals(entry.Status, "offen", StringComparison.Ordinal))
            {
                openCycle = entry;
            }
        }

        if (openCycle is null)
        {
            return new CoachContextMarkers
            {
                HasOpenCycle = false,
                OpenCycleId = null,
                OpenCycleDate = null,
                TargetType = null,
                TargetHrMax = null,
                Recommendation = null,
                RunsCompletedSinceCycle = 0,
                UnfulfilledBaseDirective = false
            };
        }

        var targetType = (openCycle.TargetType ?? "easy_run").ToLowerInvariant();
        var runsSince = 0;

        foreach (var act in activities)
        {
            if (!string.Equals(act.Type, "Run", StringComparison.Ordinal))
            {
                continue;
            }

            var actDate = DateOnly.FromDateTime(act.StartDateLocal);
            if (actDate > refDate)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(act.Id) && evaluatedIds.Contains(act.Id))
            {
                continue;
            }

            if (actDate >= openCycle.Date)
            {
                runsSince++;
            }
        }

        var unfulfilledBase = BaseCoachTargets.Contains(targetType) && runsSince == 0;

        return new CoachContextMarkers
        {
            HasOpenCycle = true,
            OpenCycleId = openCycle.Id,
            OpenCycleDate = openCycle.Date,
            TargetType = targetType,
            TargetHrMax = openCycle.TargetHrMax,
            Recommendation = openCycle.Recommendation,
            RunsCompletedSinceCycle = runsSince,
            UnfulfilledBaseDirective = unfulfilledBase
        };
    }

    public static GuardrailEvaluation CalculateGuardrails(
        ReadinessMarkers readiness,
        RunHistoryMarkers history,
        PeriodizationMarkers periodization,
        CoachContextMarkers? coachContext = null,
        CalendarContextMarkers? calendarContext = null)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(periodization);

        var tsb = readiness.Tsb;
        var sleepH = readiness.SleepHoursLastNight;
        var sleepScore = readiness.SleepScoreLastNight;
        var daysSinceLastQuality = history.DaysSinceLastQuality;
        var daysSinceLastRun = history.DaysSinceLastRun;
        var lastDuration = history.LastRun?.DurationMin ?? 0.0;
        var consecBuild = history.ConsecutiveBuildWeeks;

        var qualityDiscouraged = daysSinceLastQuality < 3 || tsb < -8.0;
        var fatigueWarning = sleepH < 6.0 || sleepScore < 72.0 || tsb < -10.0;

        var calCtx = calendarContext ?? CalendarContextMarkers.Empty;
        var restDayRecommended = daysSinceLastRun <= 0 || calCtx.HasCompletedWorkoutToday;
        var calendarRestDay = calCtx.HasRestNoteToday;
        var volumeSpikeAlert = lastDuration >= 60.0 && daysSinceLastRun <= 1;

        var lastRunComplaint = history.LastRun?.HasMuscularComplaint ?? false;
        var healthWarning = readiness.HealthWarning || lastRunComplaint;

        var baseAnchorMissing = periodization.RequiresBaseAnchor && !history.BaseAnchorCompleted;
        var coachDirectiveOverride = coachContext?.UnfulfilledBaseDirective ?? false;
        var isDeloadWeek = consecBuild >= 3;
        var reentryRecommended = daysSinceLastRun >= 7;

        var slotAllowsThreshold =
            periodization.AllowedTypes.Contains("tempo_run", StringComparer.Ordinal)
            && !qualityDiscouraged
            && !fatigueWarning
            && !healthWarning
            && !baseAnchorMissing
            && !coachDirectiveOverride
            && !isDeloadWeek
            && !reentryRecommended
            && !calendarRestDay;

        var warnings = new List<string>();
        if (qualityDiscouraged)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Qualitätsreiz abgeraten: Letzte Qualität vor {daysSinceLastQuality} Tag(en) oder TSB {tsb} < -8.0"));
        }

        if (fatigueWarning)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Erhöhte Ermüdung: Schlaf {sleepH:F1}h (Score {sleepScore:F0}) oder TSB {tsb}"));
        }

        if (volumeSpikeAlert)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Hohe Vorbelastung: Letzter Lauf dauerte {lastDuration:F0} Min."));
        }

        if (healthWarning)
        {
            if (lastRunComplaint && history.LastRun is not null)
            {
                warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Muskel-/Beschwerdeschutz aktiv: Letzter Lauf ({history.LastRun.Date:yyyy-MM-dd}) verzeichnete Beschwerden/Krämpfe."));
            }

            if (!string.IsNullOrEmpty(readiness.Injury))
            {
                warnings.Add($"Verletzungsschutz aktiv: {readiness.Injury}");
            }

            if (!string.IsNullOrEmpty(readiness.IllnessComment))
            {
                warnings.Add($"Krankheits-/Unwohlsein-Warnung: {readiness.IllnessComment}");
            }

            if (readiness.ElevatedRhr)
            {
                warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Erhöhter Ruhepuls: {readiness.RestingHrToday} bpm (+{readiness.RhrDelta} bpm über Baseline)"));
            }
        }

        if (baseAnchorMissing)
        {
            var missedStr = history.MissedAnchorDays.Count > 0
                ? string.Join(", ", history.MissedAnchorDays.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                : "Slot 3";
            warnings.Add($"Basis-Anker fehlt: Wochenend-/Grundlagenlauf ({missedStr}) wurde ausgelassen – Z2-Fundament hat Vorrang vor Intervallen.");
        }

        if (coachDirectiveOverride && coachContext is not null)
        {
            var cid = coachContext.OpenCycleId ?? "offen";
            var cdate = coachContext.OpenCycleDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
            var ctype = coachContext.TargetType ?? "easy_run";
            warnings.Add($"Offene Coach-Vorgabe ({cid} vom {cdate}): '{ctype}' steht noch unvollendet aus.");
        }

        if (isDeloadWeek)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Deload-Woche aktiv: {consecBuild} aufeinanderfolgende Belastungswochen – aktive Superkompensation."));
        }

        if (reentryRecommended)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Wiedereinstieg empfohlen: Letzter Lauf vor {daysSinceLastRun} Tag(en) – sanfter Einstieg (25–30 Min.)."));
        }

        if (calendarRestDay)
        {
            warnings.Add("Kalender-Ruhetag: Im Kalender ist für heute explizit ein Ruhetag eingetragen.");
        }

        if (restDayRecommended)
        {
            warnings.Add("Ruhetag empfohlen: Am selben Kalendertag wurde bereits gelaufen.");
        }

        return new GuardrailEvaluation
        {
            QualityDiscouraged = qualityDiscouraged,
            FatigueWarning = fatigueWarning,
            RestDayRecommended = restDayRecommended,
            VolumeSpikeAlert = volumeSpikeAlert,
            HealthWarning = healthWarning,
            BaseAnchorMissing = baseAnchorMissing,
            CoachDirectiveOverride = coachDirectiveOverride,
            IsDeloadWeek = isDeloadWeek,
            ReentryRecommended = reentryRecommended,
            CalendarRestDay = calendarRestDay,
            SlotAllowsThreshold = slotAllowsThreshold,
            ActiveWarnings = warnings
        };
    }

    public static HeartRateZone? FindHeartRateZone(AthleteProfile? profile, int zoneNumber)
    {
        if (profile is null)
        {
            return null;
        }

        return profile.HeartRate.Zones.Values.FirstOrDefault(z => z.Zone == zoneNumber)
            ?? profile.HeartRate.Zones
                .FirstOrDefault(kvp =>
                    kvp.Key.StartsWith($"z{zoneNumber}_", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kvp.Key, $"z{zoneNumber}", StringComparison.OrdinalIgnoreCase))
                .Value;
    }

    public static PlanningMarkersSnapshot ExtractPlanningMarkers(
        AthleteProfile? profile,
        IReadOnlyList<IntervalsActivityEntry> activities,
        IReadOnlyList<IntervalsWellnessEntry>? wellnessRecords = null,
        IReadOnlyList<CoachHistoryEntry>? coachHistory = null,
        CalendarContextMarkers? calendarContext = null,
        DateOnly? refDate = null,
        DateOnly? targetDate = null)
    {
        ArgumentNullException.ThrowIfNull(activities);

        var today = refDate ?? DateOnly.FromDateTime(DateTime.Today);
        var plannedTarget = targetDate ?? today.AddDays(1);
        var filteredActivities = FilterActivitiesUpToDate(activities, today);

        var z2 = FindHeartRateZone(profile, 2);
        var z4 = FindHeartRateZone(profile, 4);

        var athleteInfo = new AthletePlanningInfo
        {
            Classification = profile?.Classification ?? "Hobbyläufer",
            TrainingMode = profile?.TrainingMode ?? "flexi_hybrid",
            Z2MaxHr = z2?.Max ?? 158,
            Z4RangeMin = z4?.Min ?? 169,
            Z4RangeMax = z4?.Max ?? 177,
            Lthr = profile?.HeartRate.Lthr ?? 178
        };

        var readiness = GetReadinessMarkers(wellnessRecords, filteredActivities, today);
        var history = GetRunHistoryMarkers(filteredActivities, today, plannedTarget, profile);
        var periodization = GetPeriodizationMarkers(plannedTarget, profile);
        var coachCtx = GetCoachContextMarkers(coachHistory, filteredActivities, today);
        var calCtx = calendarContext ?? CalendarContextMarkers.Empty;
        var guardrails = CalculateGuardrails(readiness, history, periodization, coachCtx, calCtx);

        return new PlanningMarkersSnapshot
        {
            ReferenceDate = today,
            TargetDate = plannedTarget,
            Athlete = athleteInfo,
            Readiness = readiness,
            History = history,
            Periodization = periodization,
            CoachContext = coachCtx,
            CalendarContext = calCtx,
            Guardrails = guardrails
        };
    }
}
