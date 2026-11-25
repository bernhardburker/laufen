using System.Globalization;

namespace Laufen.Domain;

public sealed record AthleteDossierSummary
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Classification { get; init; }
    public string? TrainingMode { get; init; }
    public int? TargetRunsPerWeek { get; init; }
    public bool? AllowConsecutiveRunDays { get; init; }
    public int? Z2MaxHeartRate { get; init; }
    public int? Z4RangeMin { get; init; }
    public int? Z4RangeMax { get; init; }
    public required int Lthr { get; init; }
    public required int ProfileRestingHr { get; init; }
    public AthleteGoalsProfile? Goals { get; init; }
    public AthleteTimeBudgets? TimeBudgets { get; init; }
}

public sealed record RollingStimulusLedger
{
    public int? DaysSinceLastRun { get; init; }
    public int? DaysSinceLastBaseAnchor { get; init; }
    public int? DaysSinceLastBaseRun { get; init; }
    public int? DaysSinceLastQualityStimulus { get; init; }
    public required int RunsLast7Days { get; init; }
    public required int RunsLast14Days { get; init; }
    public required double VolumeKmLast7Days { get; init; }
    public required double VolumeKmLast14Days { get; init; }
    public required double VolumeMinutesLast7Days { get; init; }
    public required double VolumeMinutesLast14Days { get; init; }
    public double? Rolling14DayZ1Z2Percentage { get; init; }
    public double? Rolling28DayZ1Z2Percentage { get; init; }
}

public sealed record DatedContextClue
{
    public required DateOnly Date { get; init; }
    public required string Source { get; init; }
    public required string Summary { get; init; }
    public required bool ContainsComplaint { get; init; }
    public required bool ContainsIllness { get; init; }
}

public sealed record BreakReentryContext
{
    public required int LongestGapDaysLast28Days { get; init; }
    public required bool HasRecentExtendedBreak { get; init; }
    public DateOnly? RecentBreakStartDate { get; init; }
    public DateOnly? RecentBreakEndDate { get; init; }
    public int? RecentBreakDurationDays { get; init; }
    public required int RunsCompletedSinceRecentBreak { get; init; }
    public required int CleanBaseRunsSinceRecentBreak { get; init; }
    public required IReadOnlyList<DatedContextClue> RecentContextClues { get; init; }
}

public sealed record BaseSessionSummary
{
    public required string ActivityId { get; init; }
    public required DateOnly Date { get; init; }
    public required string Name { get; init; }
    public required double DurationMinutes { get; init; }
    public required double DistanceKm { get; init; }
    public double? AverageHeartRate { get; init; }
    public double? Z1Z2Percentage { get; init; }
    public double? AerobicEfficiencyFactor { get; init; }
    public string? Z2GapPaceText { get; init; }
    public bool? Z2ValidForTrend { get; init; }
    public int? WalkingSecondsFiltered { get; init; }
    public double? FirstHalfAverageHeartRate { get; init; }
    public double? SecondHalfAverageHeartRate { get; init; }
    public double? CardiacDriftBpm { get; init; }
    public double? PaceSplitDiffSeconds { get; init; }
    public string? AthleteNote { get; init; }
}

public sealed record QualitySessionSummary
{
    public required string ActivityId { get; init; }
    public required DateOnly Date { get; init; }
    public required string Name { get; init; }
    public required string StimulusKind { get; init; }
    public required double DurationMinutes { get; init; }
    public required double DistanceKm { get; init; }
    public double? AverageHeartRate { get; init; }
    public double? CadenceSpm { get; init; }
    public required double Z3Minutes { get; init; }
    public required double Z4Minutes { get; init; }
    public required double Z5PlusMinutes { get; init; }
    public string? Z4GapPaceText { get; init; }
    public double? IntervalsIcuCompliance { get; init; }
    public string? PairedCalendarWorkoutName { get; init; }
    public required IReadOnlyList<ParsedIntervalSegment> ParsedIntervals { get; init; }
    public string? AthleteNote { get; init; }
}

public sealed record StimulusProgressionDossier
{
    public required RollingStimulusLedger Ledger { get; init; }
    public required BreakReentryContext BreakReentry { get; init; }
    public required IReadOnlyList<BaseSessionSummary> BaseProgressionChain { get; init; }
    public required IReadOnlyList<QualitySessionSummary> QualityProgressionChain { get; init; }
}

public sealed record DailyReadinessPoint
{
    public required DateOnly Date { get; init; }
    public int? RestingHr { get; init; }
    public double? Hrv { get; init; }
    public double? SleepHours { get; init; }
    public double? SleepScore { get; init; }
    public int? Soreness { get; init; }
    public int? Fatigue { get; init; }
    public int? Stress { get; init; }
    public string? Comments { get; init; }
}

public sealed record MedicalRedFlag
{
    public required string Code { get; init; }
    public required DateOnly Date { get; init; }
    public required string Detail { get; init; }
}

public sealed record ReadinessSignalDossier
{
    public DateOnly? LatestWellnessDate { get; init; }
    public int? WellnessAgeDays { get; init; }
    public required bool IsWellnessStale { get; init; }
    public int? RestingHrLatest { get; init; }
    public double? RestingHr30DayBaseline { get; init; }
    public double? RestingHrDelta { get; init; }
    public double? HrvLatest { get; init; }
    public double? Hrv14DayBaseline { get; init; }
    public double? HrvBaselineRatio { get; init; }
    public double? SleepHoursLatest { get; init; }
    public double? SleepHours14DayBaseline { get; init; }
    public double? SleepHoursDelta { get; init; }
    public double? SleepScoreLatest { get; init; }
    public double? SleepScore14DayBaseline { get; init; }
    public required IReadOnlyList<DailyReadinessPoint> ThreeDayTrend { get; init; }
    public double? Ctl { get; init; }
    public double? Atl { get; init; }
    public double? Tsb { get; init; }
    public required string LoadDataSource { get; init; }
    public int? Soreness { get; init; }
    public int? Fatigue { get; init; }
    public int? Stress { get; init; }
    public double? InjuryScore { get; init; }
    public string? WellnessComments { get; init; }
    public required IReadOnlyList<DatedContextClue> RecentRunNotes { get; init; }
    public int? DaysSinceLastComplaint { get; init; }
    public required IReadOnlyList<MedicalRedFlag> MedicalRedFlags { get; init; }
}

public sealed record ScheduledCalendarItem
{
    public required long Id { get; init; }
    public required DateOnly Date { get; init; }
    public required string Category { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool IsCompleted => !string.IsNullOrEmpty(PairedActivityId);
    public string? PairedActivityId { get; init; }
}

public sealed record UpcomingCalendarDossier
{
    public required IReadOnlyList<ScheduledCalendarItem> EventsOnTargetDate { get; init; }
    public required IReadOnlyList<ScheduledCalendarItem> UpcomingEventsNext7Days { get; init; }
    public required bool HasCompletedRunOnTargetDate { get; init; }
    public required bool HasUncompletedPlannedWorkoutOnTargetDate { get; init; }
}

public sealed record CoachIntelligenceDossier
{
    public required DateOnly ReferenceDate { get; init; }
    public required DateOnly TargetDate { get; init; }
    public required string TargetDayOfWeek { get; init; }
    public required AthleteDossierSummary Athlete { get; init; }
    public required StimulusProgressionDossier StimulusProgression { get; init; }
    public required ReadinessSignalDossier Readiness { get; init; }
    public required UpcomingCalendarDossier Calendar { get; init; }
    public required IReadOnlyList<string> DataQualityNotes { get; init; }
}

/// <summary>
/// Pure domain builder producing curated stimulus progression and continuous readiness dossiers for the agentic coach.
/// Enforces strict <c>&lt;= refDate</c> time-travel isolation, 80% pure aerobic threshold, and zero synthetic load fallbacks.
/// </summary>
public static class CoachIntelligenceDossierBuilder
{
    private const double PureBaseZ1Z2PercentageThreshold = 80.0;
    private const double BaseAnchorMinMinutes = 50.0;
    private const double CtlTimeConstantDays = 42.0;
    private const double AtlTimeConstantDays = 7.0;
    private const int ExtendedBreakMinDays = 7;

    private static readonly string[] QualityTitleKeywords =
        ["interval", "tempo", "schwelle", "threshold", "speed", "race", "wettkampf", "fartlek", "fahrtspiel"];

    private static readonly string[] ComplaintKeywords =
        ["krampf", "krämpf", "schmerz", "zerrung", "verletz", "abgebrochen", "rücken", "knie", "achilles", "wade", "stechen", "unwohl", "krank", "infekt", "erkält", "fieber", "schwindel", "übel"];

    private static readonly string[] IllnessKeywords =
        ["krank", "infekt", "erkält", "fieber", "husten", "hals", "corona", "covid", "unwohl", "schlapp", "magen", "übel", "sick", "ill", "flu", "unwell"];

    private static readonly string[] ReentryTitleKeywords =
        ["wiedereinstieg", "comeback", "pause", "re-entry", "rücken", "krampf", "schmerz", "wade", "knie", "achilles"];

    private static readonly HashSet<string> QualityIntensityClasses = new(
        ["schwellenlauf", "schwellen_intervall", "intervall", "vo2max"],
        StringComparer.OrdinalIgnoreCase);

    public static CoachIntelligenceDossier Build(
        AthleteProfile profile,
        IReadOnlyList<IntervalsActivityEntry> activities,
        IReadOnlyList<IntervalsWellnessEntry>? wellnessRecords,
        IReadOnlyDictionary<string, RunReviewRecord>? runReviews,
        IReadOnlyList<CalendarEventEntry>? calendarEvents,
        DateOnly refDate,
        DateOnly? targetDate = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(activities);

        var effectiveTargetDate = targetDate ?? refDate;
        var targetDayOfWeek = effectiveTargetDate.ToString("dddd", CultureInfo.InvariantCulture).ToLowerInvariant();

        var filteredActivitiesUpToRef = activities
            .Where(a => DateOnly.FromDateTime(a.StartDateLocal) <= refDate)
            .ToList();

        var validRunsDesc = PhysiologicalCalculator.FilterValidRuns(filteredActivitiesUpToRef)
            .OrderByDescending(a => a.StartDateLocal)
            .ToList();

        var calendarById = BuildCalendarLookup(calendarEvents);
        var projectedRunsDesc = ProjectRuns(validRunsDesc, runReviews, calendarById, profile.HeartRate, refDate);
        var datedWellnessDesc = (wellnessRecords ?? [])
            .Where(w => w.Id <= refDate)
            .OrderByDescending(w => w.Id)
            .ToList();

        var contextClues = CollectRecentContextClues(projectedRunsDesc, refDate);
        var breakReentry = BuildBreakReentryContext(projectedRunsDesc, contextClues, refDate);
        var ledger = BuildStimulusLedger(projectedRunsDesc, refDate);
        var baseChain = BuildBaseChain(projectedRunsDesc, refDate);
        var qualityChain = BuildQualityChain(projectedRunsDesc, refDate);
        var readiness = BuildReadinessDossier(
            datedWellnessDesc,
            filteredActivitiesUpToRef,
            contextClues,
            refDate);
        var calendar = BuildUpcomingCalendar(calendarEvents, effectiveTargetDate, refDate);
        var dataQualityNotes = BuildDataQualityNotes(
            projectedRunsDesc,
            validRunsDesc,
            runReviews,
            readiness,
            refDate);

        return new CoachIntelligenceDossier
        {
            ReferenceDate = refDate,
            TargetDate = effectiveTargetDate,
            TargetDayOfWeek = targetDayOfWeek,
            Athlete = BuildAthleteSummary(profile),
            StimulusProgression = new StimulusProgressionDossier
            {
                Ledger = ledger,
                BreakReentry = breakReentry,
                BaseProgressionChain = baseChain,
                QualityProgressionChain = qualityChain
            },
            Readiness = readiness,
            Calendar = calendar,
            DataQualityNotes = dataQualityNotes
        };
    }

    private static AthleteDossierSummary BuildAthleteSummary(AthleteProfile profile)
    {
        var z2Zone = profile.HeartRate.Zones.Values.FirstOrDefault(z => z.Zone == 2);
        var z4Zone = profile.HeartRate.Zones.Values.FirstOrDefault(z => z.Zone == 4);

        return new AthleteDossierSummary
        {
            Id = profile.Id,
            Name = string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Name : profile.DisplayName,
            Classification = profile.Classification,
            TrainingMode = profile.TrainingMode,
            TargetRunsPerWeek = profile.TargetRunsPerWeek,
            AllowConsecutiveRunDays = profile.AllowConsecutiveRunDays,
            Z2MaxHeartRate = z2Zone?.Max,
            Z4RangeMin = z4Zone?.Min,
            Z4RangeMax = z4Zone?.Max,
            Lthr = profile.HeartRate.Lthr,
            ProfileRestingHr = profile.HeartRate.RestingHr,
            Goals = profile.Goals,
            TimeBudgets = profile.TimeBudgets
        };
    }

    private static Dictionary<long, CalendarEventEntry> BuildCalendarLookup(IReadOnlyList<CalendarEventEntry>? calendarEvents)
    {
        var lookup = new Dictionary<long, CalendarEventEntry>();
        if (calendarEvents is null)
        {
            return lookup;
        }

        foreach (var evt in calendarEvents)
        {
            lookup[evt.Id] = evt;
        }

        return lookup;
    }

    private static List<ProjectedRun> ProjectRuns(
        List<IntervalsActivityEntry> validRunsDesc,
        IReadOnlyDictionary<string, RunReviewRecord>? runReviews,
        Dictionary<long, CalendarEventEntry> calendarById,
        HeartRateProfile heartRateProfile,
        DateOnly refDate)
    {
        var list = new List<ProjectedRun>(validRunsDesc.Count);
        foreach (var run in validRunsDesc)
        {
            RunReviewRecord? review = null;
            if (runReviews is not null
                && runReviews.TryGetValue(run.Id, out var foundReview)
                && DateOnly.FromDateTime(foundReview.Date) <= refDate)
            {
                review = foundReview;
            }

            var date = DateOnly.FromDateTime(run.StartDateLocal);
            var name = string.IsNullOrWhiteSpace(run.Name) ? "Run" : run.Name.Trim();
            var durationMinutes = Math.Round((run.MovingTime ?? 0) / 60.0, 1, MidpointRounding.ToEven);
            var distanceKm = Math.Round((run.Distance ?? 0.0) / 1000.0, 2, MidpointRounding.ToEven);
            double? avgHr = run.AverageHeartrate.HasValue ? Math.Round((double)run.AverageHeartrate.Value, 1, MidpointRounding.ToEven) : null;

            var zoneTimes = run.IcuHrZoneTimes ?? [];
            var z1 = zoneTimes.Count > 0 ? zoneTimes[0] : 0;
            var z2 = zoneTimes.Count > 1 ? zoneTimes[1] : 0;
            var z3 = zoneTimes.Count > 2 ? zoneTimes[2] : 0;
            var z4 = zoneTimes.Count > 3 ? zoneTimes[3] : 0;
            var z5Plus = zoneTimes.Count > 4 ? zoneTimes.Skip(4).Sum() : 0;
            var totalZoneSeconds = zoneTimes.Sum();

            double? z1Z2Pct = totalZoneSeconds > 0
                ? Math.Round(((z1 + z2) / (double)totalZoneSeconds) * 100.0, 1, MidpointRounding.ToEven)
                : review?.Z1Z2Percentage;

            double? aerobicEf = review?.AerobicEf;
            if (!aerobicEf.HasValue && run.AverageHeartrate is > 0 && run.MovingTime is > 0 && run.Distance is > 0)
            {
                var speedMetersPerMin = run.Distance.Value / (run.MovingTime.Value / 60.0);
                aerobicEf = Math.Round(speedMetersPerMin / run.AverageHeartrate.Value, 3, MidpointRounding.ToEven);
            }

            double? cadenceSpm = null;
            if (review is not null && review.CadenceSpm > 0.0)
            {
                cadenceSpm = Math.Round(review.CadenceSpm, 1, MidpointRounding.ToEven);
            }
            else if (run.AverageCadence is > 0.0)
            {
                var rawCad = run.AverageCadence.Value;
                cadenceSpm = Math.Round(rawCad < 110.0 ? rawCad * 2.0 : rawCad, 1, MidpointRounding.ToEven);
            }

            var isQuality = IsQualityRun(name, z4, z5Plus, z1Z2Pct, review);
            var note = MergeNotes(run.Description, review?.AthleteNote);
            var hasNoteComplaint = !string.IsNullOrEmpty(note) && ContainsAnyKeyword(note, ComplaintKeywords);
            var isPureBase = !isQuality && z1Z2Pct is >= PureBaseZ1Z2PercentageThreshold;

            var rawIntervalStrings = review?.Intervals ?? run.IntervalSummary;
            var parsedIntervals = IntervalSummaryParser.ParseAll(rawIntervalStrings, heartRateProfile);

            string? pairedCalendarName = null;
            if (run.PairedEventId.HasValue && calendarById.TryGetValue(run.PairedEventId.Value, out var calEvt))
            {
                pairedCalendarName = calEvt.Name;
            }

            list.Add(new ProjectedRun
            {
                Activity = run,
                Review = review,
                Date = date,
                Name = name,
                DurationMinutes = durationMinutes,
                DistanceKm = distanceKm,
                AverageHeartRate = avgHr,
                CadenceSpm = cadenceSpm,
                Z1Seconds = z1,
                Z2Seconds = z2,
                Z3Seconds = z3,
                Z4Seconds = z4,
                Z5PlusSeconds = z5Plus,
                TotalZoneSeconds = totalZoneSeconds,
                ZoneArrayLength = zoneTimes.Count,
                Z1Z2Percentage = z1Z2Pct,
                AerobicEfficiencyFactor = aerobicEf,
                IsQuality = isQuality,
                IsPureBase = isPureBase,
                IsBaseAnchor = isPureBase && durationMinutes >= BaseAnchorMinMinutes,
                AthleteNote = note,
                HasNoteComplaint = hasNoteComplaint,
                PairedCalendarWorkoutName = pairedCalendarName,
                ParsedIntervals = parsedIntervals
            });
        }

        return list;
    }

    private static bool IsQualityRun(
        string name,
        int z4Seconds,
        int z5PlusSeconds,
        double? z1Z2Percentage,
        RunReviewRecord? review)
    {
        if (ContainsAnyKeyword(name, QualityTitleKeywords))
        {
            return true;
        }

        if (z4Seconds > 480 || z5PlusSeconds > 240)
        {
            return true;
        }

        if (review is not null)
        {
            if (QualityIntensityClasses.Contains(review.IntensityClass))
            {
                return true;
            }

            if (string.Equals(review.IntensityClass, "tempo_z3", StringComparison.OrdinalIgnoreCase)
                && z1Z2Percentage is < 65.0)
            {
                return true;
            }
        }

        return false;
    }

    private static string? MergeNotes(string? description, string? reviewNote)
    {
        var left = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        var right = string.IsNullOrWhiteSpace(reviewNote) ? null : reviewNote.Trim();

        if (left is null)
        {
            return right;
        }

        if (right is null || string.Equals(left, right, StringComparison.Ordinal))
        {
            return left;
        }

        return $"{left} | {right}";
    }

    private static RollingStimulusLedger BuildStimulusLedger(List<ProjectedRun> runsDesc, DateOnly refDate)
    {
        if (runsDesc.Count == 0)
        {
            return new RollingStimulusLedger
            {
                DaysSinceLastRun = null,
                DaysSinceLastBaseAnchor = null,
                DaysSinceLastBaseRun = null,
                DaysSinceLastQualityStimulus = null,
                RunsLast7Days = 0,
                RunsLast14Days = 0,
                VolumeKmLast7Days = 0.0,
                VolumeKmLast14Days = 0.0,
                VolumeMinutesLast7Days = 0.0,
                VolumeMinutesLast14Days = 0.0,
                Rolling14DayZ1Z2Percentage = null,
                Rolling28DayZ1Z2Percentage = null
            };
        }

        var daysSinceLastRun = Math.Max(0, refDate.DayNumber - runsDesc[0].Date.DayNumber);
        int? daysSinceLastAnchor = null;
        int? daysSinceLastBase = null;
        int? daysSinceLastQuality = null;

        var start7 = refDate.AddDays(-6);
        var start14 = refDate.AddDays(-13);
        var start28 = refDate.AddDays(-27);

        var runs7 = 0;
        var runs14 = 0;
        var km7 = 0.0;
        var km14 = 0.0;
        var min7 = 0.0;
        var min14 = 0.0;

        var z12_14 = 0L;
        var zTot_14 = 0L;
        var z12_28 = 0L;
        var zTot_28 = 0L;

        foreach (var run in runsDesc)
        {
            if (daysSinceLastQuality is null && run.IsQuality)
            {
                daysSinceLastQuality = Math.Max(0, refDate.DayNumber - run.Date.DayNumber);
            }

            if (daysSinceLastBase is null && run.IsPureBase && !run.HasNoteComplaint)
            {
                daysSinceLastBase = Math.Max(0, refDate.DayNumber - run.Date.DayNumber);
            }

            if (daysSinceLastAnchor is null && run.IsBaseAnchor && !run.HasNoteComplaint)
            {
                daysSinceLastAnchor = Math.Max(0, refDate.DayNumber - run.Date.DayNumber);
            }

            if (run.Date >= start7)
            {
                runs7++;
                km7 += run.DistanceKm;
                min7 += run.DurationMinutes;
            }

            if (run.Date >= start14)
            {
                runs14++;
                km14 += run.DistanceKm;
                min14 += run.DurationMinutes;
                z12_14 += run.Z1Seconds + run.Z2Seconds;
                zTot_14 += run.TotalZoneSeconds;
            }

            if (run.Date >= start28)
            {
                z12_28 += run.Z1Seconds + run.Z2Seconds;
                zTot_28 += run.TotalZoneSeconds;
            }
        }

        double? pct14 = zTot_14 > 0 ? Math.Round((z12_14 / (double)zTot_14) * 100.0, 1, MidpointRounding.ToEven) : null;
        double? pct28 = zTot_28 > 0 ? Math.Round((z12_28 / (double)zTot_28) * 100.0, 1, MidpointRounding.ToEven) : null;

        return new RollingStimulusLedger
        {
            DaysSinceLastRun = daysSinceLastRun,
            DaysSinceLastBaseAnchor = daysSinceLastAnchor,
            DaysSinceLastBaseRun = daysSinceLastBase,
            DaysSinceLastQualityStimulus = daysSinceLastQuality,
            RunsLast7Days = runs7,
            RunsLast14Days = runs14,
            VolumeKmLast7Days = Math.Round(km7, 2, MidpointRounding.ToEven),
            VolumeKmLast14Days = Math.Round(km14, 2, MidpointRounding.ToEven),
            VolumeMinutesLast7Days = Math.Round(min7, 1, MidpointRounding.ToEven),
            VolumeMinutesLast14Days = Math.Round(min14, 1, MidpointRounding.ToEven),
            Rolling14DayZ1Z2Percentage = pct14,
            Rolling28DayZ1Z2Percentage = pct28
        };
    }

    private static List<DatedContextClue> CollectRecentContextClues(List<ProjectedRun> runsDesc, DateOnly refDate)
    {
        var cutoff28 = refDate.AddDays(-28);
        var clues = new List<DatedContextClue>();

        foreach (var run in runsDesc)
        {
            if (run.Date < cutoff28)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(run.AthleteNote))
            {
                clues.Add(new DatedContextClue
                {
                    Date = run.Date,
                    Source = "run_note",
                    Summary = $"{run.Name}: {run.AthleteNote}",
                    ContainsComplaint = ContainsAnyKeyword(run.AthleteNote, ComplaintKeywords),
                    ContainsIllness = ContainsAnyKeyword(run.AthleteNote, IllnessKeywords)
                });
            }

            if (ContainsAnyKeyword(run.Name, ReentryTitleKeywords))
            {
                clues.Add(new DatedContextClue
                {
                    Date = run.Date,
                    Source = "activity_title",
                    Summary = run.Name,
                    ContainsComplaint = ContainsAnyKeyword(run.Name, ComplaintKeywords),
                    ContainsIllness = ContainsAnyKeyword(run.Name, IllnessKeywords)
                });
            }
        }

        return clues;
    }

    private static BreakReentryContext BuildBreakReentryContext(
        List<ProjectedRun> runsDesc,
        List<DatedContextClue> contextClues,
        DateOnly refDate)
    {
        if (runsDesc.Count == 0)
        {
            return new BreakReentryContext
            {
                LongestGapDaysLast28Days = 0,
                HasRecentExtendedBreak = false,
                RecentBreakStartDate = null,
                RecentBreakEndDate = null,
                RecentBreakDurationDays = null,
                RunsCompletedSinceRecentBreak = 0,
                CleanBaseRunsSinceRecentBreak = 0,
                RecentContextClues = contextClues
            };
        }

        var distinctDatesAsc = runsDesc
            .Select(r => r.Date)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        var cutoff28 = refDate.AddDays(-28);
        var recentCutoff21 = refDate.AddDays(-21);
        var longestGap28 = Math.Max(0, refDate.DayNumber - distinctDatesAsc[^1].DayNumber);

        DateOnly? breakStart = null;
        DateOnly? breakEnd = null;
        int? breakDuration = null;

        for (var i = 1; i < distinctDatesAsc.Count; i++)
        {
            var prev = distinctDatesAsc[i - 1];
            var next = distinctDatesAsc[i];
            var gap = next.DayNumber - prev.DayNumber;

            if (next >= cutoff28 && gap > longestGap28)
            {
                longestGap28 = gap;
            }

            if (gap >= ExtendedBreakMinDays && next >= recentCutoff21)
            {
                breakStart = prev;
                breakEnd = next;
                breakDuration = gap;
            }
        }

        var ongoingGap = refDate.DayNumber - distinctDatesAsc[^1].DayNumber;
        if (ongoingGap >= ExtendedBreakMinDays)
        {
            breakStart = distinctDatesAsc[^1];
            breakEnd = null;
            breakDuration = ongoingGap;
        }

        var hasRecentBreak = breakDuration is >= ExtendedBreakMinDays;
        var runsSinceBreak = 0;
        var cleanBaseSinceBreak = 0;

        if (hasRecentBreak && breakEnd.HasValue)
        {
            foreach (var run in runsDesc)
            {
                if (run.Date < breakEnd.Value)
                {
                    break;
                }

                runsSinceBreak++;
                if (run.IsPureBase && !run.HasNoteComplaint)
                {
                    cleanBaseSinceBreak++;
                }
            }
        }

        return new BreakReentryContext
        {
            LongestGapDaysLast28Days = longestGap28,
            HasRecentExtendedBreak = hasRecentBreak,
            RecentBreakStartDate = breakStart,
            RecentBreakEndDate = breakEnd,
            RecentBreakDurationDays = breakDuration,
            RunsCompletedSinceRecentBreak = runsSinceBreak,
            CleanBaseRunsSinceRecentBreak = cleanBaseSinceBreak,
            RecentContextClues = contextClues
        };
    }

    private static List<BaseSessionSummary> BuildBaseChain(List<ProjectedRun> runsDesc, DateOnly refDate)
    {
        var cutoff56 = refDate.AddDays(-56);
        var items = new List<BaseSessionSummary>();

        foreach (var run in runsDesc)
        {
            if (run.Date < cutoff56)
            {
                break;
            }

            if (run.IsQuality)
            {
                continue;
            }

            var zps = run.Review?.ZonePaceStats;
            var z2Stats = zps?.Zones.Values.FirstOrDefault(z => z.Zone == 2);
            double? drift = null;
            if (zps?.FirstHalfAverageHeartRate is { } hr1 && zps.SecondHalfAverageHeartRate is { } hr2)
            {
                drift = Math.Round(hr2 - hr1, 1, MidpointRounding.ToEven);
            }

            items.Add(new BaseSessionSummary
            {
                ActivityId = run.Activity.Id,
                Date = run.Date,
                Name = run.Name,
                DurationMinutes = run.DurationMinutes,
                DistanceKm = run.DistanceKm,
                AverageHeartRate = run.AverageHeartRate,
                Z1Z2Percentage = run.Z1Z2Percentage,
                AerobicEfficiencyFactor = run.AerobicEfficiencyFactor,
                Z2GapPaceText = z2Stats?.GapPaceText,
                Z2ValidForTrend = z2Stats?.ValidForTrend,
                WalkingSecondsFiltered = zps?.WalkingSecondsFiltered,
                FirstHalfAverageHeartRate = zps?.FirstHalfAverageHeartRate,
                SecondHalfAverageHeartRate = zps?.SecondHalfAverageHeartRate,
                CardiacDriftBpm = drift,
                PaceSplitDiffSeconds = zps?.PaceSplitDiffSeconds,
                AthleteNote = run.AthleteNote
            });

            if (items.Count >= 6)
            {
                break;
            }
        }

        items.Reverse();
        return items;
    }

    private static List<QualitySessionSummary> BuildQualityChain(List<ProjectedRun> runsDesc, DateOnly refDate)
    {
        var cutoff56 = refDate.AddDays(-56);
        var items = new List<QualitySessionSummary>();

        foreach (var run in runsDesc)
        {
            if (run.Date < cutoff56)
            {
                break;
            }

            if (!run.IsQuality)
            {
                continue;
            }

            var zps = run.Review?.ZonePaceStats;
            var z4Stats = zps?.Zones.Values.FirstOrDefault(z => z.Zone == 4);

            items.Add(new QualitySessionSummary
            {
                ActivityId = run.Activity.Id,
                Date = run.Date,
                Name = run.Name,
                StimulusKind = ResolveStimulusKind(run),
                DurationMinutes = run.DurationMinutes,
                DistanceKm = run.DistanceKm,
                AverageHeartRate = run.AverageHeartRate,
                CadenceSpm = run.CadenceSpm,
                Z3Minutes = Math.Round(run.Z3Seconds / 60.0, 1, MidpointRounding.ToEven),
                Z4Minutes = Math.Round(run.Z4Seconds / 60.0, 1, MidpointRounding.ToEven),
                Z5PlusMinutes = Math.Round(run.Z5PlusSeconds / 60.0, 1, MidpointRounding.ToEven),
                Z4GapPaceText = z4Stats?.GapPaceText,
                IntervalsIcuCompliance = run.Activity.Compliance ?? run.Review?.Review.Stages.Stage2Compliance.IntervalsIcuCompliance,
                PairedCalendarWorkoutName = run.PairedCalendarWorkoutName,
                ParsedIntervals = run.ParsedIntervals,
                AthleteNote = run.AthleteNote
            });

            if (items.Count >= 5)
            {
                break;
            }
        }

        items.Reverse();
        return items;
    }

    private static string ResolveStimulusKind(ProjectedRun run)
    {
        if (run.Name.Contains("schwell", StringComparison.OrdinalIgnoreCase)
            || run.Name.Contains("threshold", StringComparison.OrdinalIgnoreCase)
            || string.Equals(run.Review?.IntensityClass, "schwellenlauf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(run.Review?.IntensityClass, "schwellen_intervall", StringComparison.OrdinalIgnoreCase))
        {
            return "threshold";
        }

        if (run.Name.Contains("interval", StringComparison.OrdinalIgnoreCase)
            || string.Equals(run.Review?.IntensityClass, "intervall", StringComparison.OrdinalIgnoreCase)
            || string.Equals(run.Review?.IntensityClass, "vo2max", StringComparison.OrdinalIgnoreCase)
            || run.Z5PlusSeconds > 240)
        {
            return "intervals";
        }

        if (run.Name.Contains("tempo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(run.Review?.IntensityClass, "tempo_z3", StringComparison.OrdinalIgnoreCase))
        {
            return "tempo";
        }

        return "quality";
    }

    private static ReadinessSignalDossier BuildReadinessDossier(
        List<IntervalsWellnessEntry> datedWellnessDesc,
        List<IntervalsActivityEntry> activitiesUpToRef,
        List<DatedContextClue> recentRunNotes,
        DateOnly refDate)
    {
        var latestW = datedWellnessDesc.Count > 0 ? datedWellnessDesc[0] : null;
        int? wellnessAgeDays = latestW is not null ? Math.Max(0, refDate.DayNumber - latestW.Id.DayNumber) : null;
        var isStale = !wellnessAgeDays.HasValue || wellnessAgeDays.Value > 1;

        var rhr30Cutoff = refDate.AddDays(-30);
        var baseline14Cutoff = refDate.AddDays(-14);

        var rhrSamples = datedWellnessDesc
            .Where(w => w.Id >= rhr30Cutoff && w.RestingHr.HasValue)
            .Select(w => (double)w.RestingHr!.Value)
            .ToList();
        double? rhrBaseline = rhrSamples.Count > 0
            ? Math.Round(rhrSamples.Average(), 1, MidpointRounding.ToEven)
            : null;

        var hrvSamples = datedWellnessDesc
            .Where(w => w.Id >= baseline14Cutoff && w.Hrv.HasValue)
            .Select(w => w.Hrv!.Value)
            .ToList();
        double? hrvBaseline = hrvSamples.Count > 0
            ? Math.Round(hrvSamples.Average(), 1, MidpointRounding.ToEven)
            : null;

        var sleepHoursSamples = datedWellnessDesc
            .Where(w => w.Id >= baseline14Cutoff && w.SleepSecs is > 0)
            .Select(w => w.SleepSecs!.Value / 3600.0)
            .ToList();
        double? sleepHoursBaseline = sleepHoursSamples.Count > 0
            ? Math.Round(sleepHoursSamples.Average(), 2, MidpointRounding.ToEven)
            : null;

        var sleepScoreSamples = datedWellnessDesc
            .Where(w => w.Id >= baseline14Cutoff && w.SleepScore.HasValue)
            .Select(w => w.SleepScore!.Value)
            .ToList();
        double? sleepScoreBaseline = sleepScoreSamples.Count > 0
            ? Math.Round(sleepScoreSamples.Average(), 1, MidpointRounding.ToEven)
            : null;

        var rhrLatest = latestW?.RestingHr;
        double? rhrDelta = rhrLatest.HasValue && rhrBaseline.HasValue
            ? Math.Round(rhrLatest.Value - rhrBaseline.Value, 1, MidpointRounding.ToEven)
            : null;

        double? hrvLatest = latestW?.Hrv is { } hrvVal ? Math.Round(hrvVal, 1, MidpointRounding.ToEven) : null;
        double? hrvRatio = hrvLatest.HasValue && hrvBaseline is > 0.0
            ? Math.Round(hrvLatest.Value / hrvBaseline.Value, 2, MidpointRounding.ToEven)
            : null;

        var sleepSource = latestW;
        if (latestW is not null
            && latestW.Id == refDate
            && latestW.SleepSecs is not > 0
            && datedWellnessDesc.Count > 1
            && datedWellnessDesc[1].Id >= refDate.AddDays(-1)
            && datedWellnessDesc[1].SleepSecs is > 0)
        {
            sleepSource = datedWellnessDesc[1];
        }

        double? sleepHoursLatest = sleepSource?.SleepSecs is > 0
            ? Math.Round(sleepSource.SleepSecs.Value / 3600.0, 2, MidpointRounding.ToEven)
            : null;
        double? sleepHoursDelta = sleepHoursLatest.HasValue && sleepHoursBaseline.HasValue
            ? Math.Round(sleepHoursLatest.Value - sleepHoursBaseline.Value, 2, MidpointRounding.ToEven)
            : null;
        double? sleepScoreLatest = sleepSource?.SleepScore is { } sc
            ? Math.Round(sc, 1, MidpointRounding.ToEven)
            : null;

        var threeDayTrend = datedWellnessDesc
            .Take(3)
            .Reverse()
            .Select(w => new DailyReadinessPoint
            {
                Date = w.Id,
                RestingHr = w.RestingHr,
                Hrv = w.Hrv.HasValue ? Math.Round(w.Hrv.Value, 1, MidpointRounding.ToEven) : null,
                SleepHours = w.SleepSecs is > 0 ? Math.Round(w.SleepSecs.Value / 3600.0, 2, MidpointRounding.ToEven) : null,
                SleepScore = w.SleepScore.HasValue ? Math.Round(w.SleepScore.Value, 1, MidpointRounding.ToEven) : null,
                Soreness = w.Soreness.HasValue ? (int)Math.Round(w.Soreness.Value, MidpointRounding.AwayFromZero) : null,
                Fatigue = w.Fatigue.HasValue ? (int)Math.Round(w.Fatigue.Value, MidpointRounding.AwayFromZero) : null,
                Stress = w.Stress.HasValue ? (int)Math.Round(w.Stress.Value, MidpointRounding.AwayFromZero) : null,
                Comments = string.IsNullOrWhiteSpace(w.Comments) ? null : w.Comments.Trim()
            })
            .ToList();

        var (ctl, atl, tsb, loadSource) = ComputeTrainingLoad(datedWellnessDesc, activitiesUpToRef, refDate);
        var medicalFlags = DetectMedicalRedFlags(datedWellnessDesc, recentRunNotes, rhrDelta, refDate);

        var complaintDates = recentRunNotes
            .Where(c => string.Equals(c.Source, "run_note", StringComparison.Ordinal) && c.ContainsComplaint)
            .Select(c => c.Date)
            .ToList();

        foreach (var w in datedWellnessDesc)
        {
            if ((w.Injury is > 0.0)
                || (!string.IsNullOrWhiteSpace(w.Comments) && ContainsAnyKeyword(w.Comments, ComplaintKeywords)))
            {
                complaintDates.Add(w.Id);
            }
        }

        int? daysSinceLastComplaint = complaintDates.Count > 0
            ? Math.Max(0, refDate.DayNumber - complaintDates.Max().DayNumber)
            : null;

        return new ReadinessSignalDossier
        {
            LatestWellnessDate = latestW?.Id,
            WellnessAgeDays = wellnessAgeDays,
            IsWellnessStale = isStale,
            RestingHrLatest = rhrLatest,
            RestingHr30DayBaseline = rhrBaseline,
            RestingHrDelta = rhrDelta,
            HrvLatest = hrvLatest,
            Hrv14DayBaseline = hrvBaseline,
            HrvBaselineRatio = hrvRatio,
            SleepHoursLatest = sleepHoursLatest,
            SleepHours14DayBaseline = sleepHoursBaseline,
            SleepHoursDelta = sleepHoursDelta,
            SleepScoreLatest = sleepScoreLatest,
            SleepScore14DayBaseline = sleepScoreBaseline,
            ThreeDayTrend = threeDayTrend,
            Ctl = ctl,
            Atl = atl,
            Tsb = tsb,
            LoadDataSource = loadSource,
            Soreness = latestW?.Soreness is { } s ? (int)Math.Round(s, MidpointRounding.AwayFromZero) : null,
            Fatigue = latestW?.Fatigue is { } f ? (int)Math.Round(f, MidpointRounding.AwayFromZero) : null,
            Stress = latestW?.Stress is { } st ? (int)Math.Round(st, MidpointRounding.AwayFromZero) : null,
            InjuryScore = latestW?.Injury,
            WellnessComments = string.IsNullOrWhiteSpace(latestW?.Comments) ? null : latestW.Comments.Trim(),
            RecentRunNotes = recentRunNotes,
            DaysSinceLastComplaint = daysSinceLastComplaint,
            MedicalRedFlags = medicalFlags
        };
    }

    private static (double? Ctl, double? Atl, double? Tsb, string Source) ComputeTrainingLoad(
        List<IntervalsWellnessEntry> datedWellnessDesc,
        List<IntervalsActivityEntry> activitiesUpToRef,
        DateOnly refDate)
    {
        var latestWellnessLoad = datedWellnessDesc
            .FirstOrDefault(w => w.Ctl.HasValue && w.Atl.HasValue);

        var latestActivityLoad = activitiesUpToRef
            .Where(a => (a.Type is null || string.Equals(a.Type, "Run", StringComparison.Ordinal))
                && a.IcuCtl.HasValue
                && a.IcuAtl.HasValue)
            .OrderByDescending(a => a.StartDateLocal)
            .FirstOrDefault();

        DateOnly? measurementDate = null;
        double? rawCtl = null;
        double? rawAtl = null;
        var baseSource = "none";

        if (latestWellnessLoad is not null)
        {
            measurementDate = latestWellnessLoad.Id;
            rawCtl = latestWellnessLoad.Ctl;
            rawAtl = latestWellnessLoad.Atl;
            baseSource = "wellness";
        }

        if (latestActivityLoad is not null)
        {
            var actDate = DateOnly.FromDateTime(latestActivityLoad.StartDateLocal);
            if (!measurementDate.HasValue || actDate > measurementDate.Value)
            {
                measurementDate = actDate;
                rawCtl = latestActivityLoad.IcuCtl;
                rawAtl = latestActivityLoad.IcuAtl;
                baseSource = "activity";
            }
        }

        if (!measurementDate.HasValue || !rawCtl.HasValue || !rawAtl.HasValue)
        {
            return (null, null, null, "none");
        }

        var ctl = Math.Round(rawCtl.Value, 1, MidpointRounding.ToEven);
        var atl = Math.Round(rawAtl.Value, 1, MidpointRounding.ToEven);
        var source = baseSource;

        var daysGap = refDate.DayNumber - measurementDate.Value.DayNumber;
        if (daysGap > 0)
        {
            ctl = Math.Round(ctl * Math.Exp(-daysGap / CtlTimeConstantDays), 1, MidpointRounding.ToEven);
            atl = Math.Round(atl * Math.Exp(-daysGap / AtlTimeConstantDays), 1, MidpointRounding.ToEven);
            source = $"{baseSource}_decayed";
        }

        var tsb = Math.Round(ctl - atl, 1, MidpointRounding.ToEven);
        return (ctl, atl, tsb, source);
    }

    private static List<MedicalRedFlag> DetectMedicalRedFlags(
        List<IntervalsWellnessEntry> datedWellnessDesc,
        List<DatedContextClue> recentRunNotes,
        double? rhrDelta,
        DateOnly refDate)
    {
        var flags = new List<MedicalRedFlag>();
        var latestW = datedWellnessDesc.Count > 0 ? datedWellnessDesc[0] : null;
        var latestAge = latestW is not null ? refDate.DayNumber - latestW.Id.DayNumber : (int?)null;

        if (latestW is not null && latestAge is <= 1)
        {
            if (latestW.Injury is > 0.0)
            {
                flags.Add(new MedicalRedFlag
                {
                    Code = "active_injury_reported",
                    Date = latestW.Id,
                    Detail = string.Create(CultureInfo.InvariantCulture, $"Wellness injury score {latestW.Injury.Value:F1} reported.")
                });
            }

            if (rhrDelta is >= 5.0 && latestW.RestingHr.HasValue)
            {
                flags.Add(new MedicalRedFlag
                {
                    Code = "severe_rhr_spike",
                    Date = latestW.Id,
                    Detail = string.Create(CultureInfo.InvariantCulture, $"Resting HR {latestW.RestingHr.Value} bpm (+{rhrDelta.Value:F1} bpm vs 30d baseline).")
                });
            }
        }

        var illnessCutoff = refDate.AddDays(-3);
        foreach (var w in datedWellnessDesc)
        {
            if (w.Id < illnessCutoff)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(w.Comments) && ContainsAnyKeyword(w.Comments, IllnessKeywords))
            {
                flags.Add(new MedicalRedFlag
                {
                    Code = "illness_symptom_comment",
                    Date = w.Id,
                    Detail = w.Comments.Trim()
                });
            }
        }

        foreach (var note in recentRunNotes)
        {
            if (note.Date >= illnessCutoff
                && string.Equals(note.Source, "run_note", StringComparison.Ordinal)
                && note.ContainsIllness)
            {
                flags.Add(new MedicalRedFlag
                {
                    Code = "illness_symptom_run_note",
                    Date = note.Date,
                    Detail = note.Summary
                });
            }
        }

        return flags;
    }

    private static UpcomingCalendarDossier BuildUpcomingCalendar(
        IReadOnlyList<CalendarEventEntry>? calendarEvents,
        DateOnly targetDate,
        DateOnly refDate)
    {
        if (calendarEvents is null || calendarEvents.Count == 0)
        {
            return new UpcomingCalendarDossier
            {
                EventsOnTargetDate = [],
                UpcomingEventsNext7Days = [],
                HasCompletedRunOnTargetDate = false,
                HasUncompletedPlannedWorkoutOnTargetDate = false
            };
        }

        var endDate = refDate.AddDays(7);
        var mapped = calendarEvents
            .Select(e => new ScheduledCalendarItem
            {
                Id = e.Id,
                Date = DateOnly.FromDateTime(e.StartDateLocal),
                Category = e.Category,
                Name = string.IsNullOrWhiteSpace(e.Name) ? e.Category : e.Name.Trim(),
                Description = string.IsNullOrWhiteSpace(e.Description) ? null : e.Description.Trim(),
                PairedActivityId = e.PairedActivityId
            })
            .OrderBy(e => e.Date)
            .ThenBy(e => e.Id)
            .ToList();

        var onTarget = mapped.Where(e => e.Date == targetDate).ToList();
        var next7 = mapped.Where(e => e.Date >= refDate && e.Date <= endDate).ToList();

        var hasCompleted = onTarget.Any(e =>
            string.Equals(e.Category, "WORKOUT", StringComparison.OrdinalIgnoreCase)
            && e.IsCompleted);
        var hasUncompleted = onTarget.Any(e =>
            string.Equals(e.Category, "WORKOUT", StringComparison.OrdinalIgnoreCase)
            && !e.IsCompleted);

        return new UpcomingCalendarDossier
        {
            EventsOnTargetDate = onTarget,
            UpcomingEventsNext7Days = next7,
            HasCompletedRunOnTargetDate = hasCompleted,
            HasUncompletedPlannedWorkoutOnTargetDate = hasUncompleted
        };
    }

    private static List<string> BuildDataQualityNotes(
        List<ProjectedRun> projectedRunsDesc,
        List<IntervalsActivityEntry> validRunsDesc,
        IReadOnlyDictionary<string, RunReviewRecord>? runReviews,
        ReadinessSignalDossier readiness,
        DateOnly refDate)
    {
        var notes = new List<string>();

        if (!readiness.LatestWellnessDate.HasValue)
        {
            notes.Add("No wellness records available up to reference date.");
        }
        else if (readiness.IsWellnessStale)
        {
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Wellness data is stale ({readiness.WellnessAgeDays} days old; latest {readiness.LatestWellnessDate.Value:yyyy-MM-dd})."));
        }

        if (string.Equals(readiness.LoadDataSource, "none", StringComparison.Ordinal))
        {
            notes.Add("No CTL/ATL training load history available; load signals are omitted.");
        }

        if (runReviews is not null && runReviews.Count > 0)
        {
            var validIds = validRunsDesc.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            var orphanCount = runReviews.Count(kvp =>
                DateOnly.FromDateTime(kvp.Value.Date) <= refDate && !validIds.Contains(kvp.Key));
            if (orphanCount > 0)
            {
                notes.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Excluded {orphanCount} orphaned run_reviews.json record(s) with no matching activity in intervals_activities.json."));
            }
        }

        var cutoff56 = refDate.AddDays(-56);
        var zoneCounts = projectedRunsDesc
            .Where(r => r.Date >= cutoff56 && r.ZoneArrayLength > 0)
            .Select(r => r.ZoneArrayLength)
            .Distinct()
            .OrderBy(c => c)
            .ToList();

        if (zoneCounts.Count > 1)
        {
            notes.Add($"HR zone array length changed within last 56 days ({string.Join(" vs ", zoneCounts)} zones).");
        }

        return notes;
    }

    private static bool ContainsAnyKeyword(string text, IReadOnlyList<string> keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record ProjectedRun
    {
        public required IntervalsActivityEntry Activity { get; init; }
        public RunReviewRecord? Review { get; init; }
        public required DateOnly Date { get; init; }
        public required string Name { get; init; }
        public required double DurationMinutes { get; init; }
        public required double DistanceKm { get; init; }
        public double? AverageHeartRate { get; init; }
        public double? CadenceSpm { get; init; }
        public required int Z1Seconds { get; init; }
        public required int Z2Seconds { get; init; }
        public required int Z3Seconds { get; init; }
        public required int Z4Seconds { get; init; }
        public required int Z5PlusSeconds { get; init; }
        public required int TotalZoneSeconds { get; init; }
        public required int ZoneArrayLength { get; init; }
        public double? Z1Z2Percentage { get; init; }
        public double? AerobicEfficiencyFactor { get; init; }
        public required bool IsQuality { get; init; }
        public required bool IsPureBase { get; init; }
        public required bool IsBaseAnchor { get; init; }
        public string? AthleteNote { get; init; }
        public required bool HasNoteComplaint { get; init; }
        public string? PairedCalendarWorkoutName { get; init; }
        public required IReadOnlyList<ParsedIntervalSegment> ParsedIntervals { get; init; }
    }
}
