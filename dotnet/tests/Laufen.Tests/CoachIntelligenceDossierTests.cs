using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class CoachIntelligenceDossierTests
{
    private static WorkspaceLayout ResolveRepositoryLayout() =>
        WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);

    [Test]
    public void IntervalSummaryParserParsesMinutesOnlyWithoutTruncatingHeartRate()
    {
        var layout = ResolveRepositoryLayout();
        var profile = StrictJsonLoader.LoadAthleteProfile(layout, "berni").Athlete;

        var parsedThreshold = IntervalSummaryParser.Parse("3x 4m 162bpm", profile.HeartRate);
        Assert.That(parsedThreshold, Is.Not.Null);
        Assert.That(parsedThreshold!.Reps, Is.EqualTo(3));
        Assert.That(parsedThreshold.DurationSeconds, Is.EqualTo(240));
        Assert.That(parsedThreshold.AverageHeartRate, Is.EqualTo(162.0));
        Assert.That(parsedThreshold.Zone, Is.EqualTo(3));
        Assert.That(parsedThreshold.Role, Is.EqualTo("tempo"));

        var parsedMixed = IntervalSummaryParser.Parse("4x 7m40s 150bpm", profile.HeartRate);
        Assert.That(parsedMixed, Is.Not.Null);
        Assert.That(parsedMixed!.Reps, Is.EqualTo(4));
        Assert.That(parsedMixed.DurationSeconds, Is.EqualTo(460));
        Assert.That(parsedMixed.AverageHeartRate, Is.EqualTo(150.0));
        Assert.That(parsedMixed.Zone, Is.EqualTo(1));
        Assert.That(parsedMixed.Role, Is.EqualTo("recovery"));

        var parsedSecondsOnly = IntervalSummaryParser.Parse("1x 69s 154bpm", profile.HeartRate);
        Assert.That(parsedSecondsOnly, Is.Not.Null);
        Assert.That(parsedSecondsOnly!.DurationSeconds, Is.EqualTo(69));
        Assert.That(parsedSecondsOnly.AverageHeartRate, Is.EqualTo(154.0));
        Assert.That(parsedSecondsOnly.Zone, Is.EqualTo(2));
        Assert.That(parsedSecondsOnly.Role, Is.EqualTo("aerobic"));

        var parsedFiveMin = IntervalSummaryParser.Parse("1x 5m 154bpm", profile.HeartRate);
        Assert.That(parsedFiveMin, Is.Not.Null);
        Assert.That(parsedFiveMin!.DurationSeconds, Is.EqualTo(300));
        Assert.That(parsedFiveMin.AverageHeartRate, Is.EqualTo(154.0));

        Assert.That(IntervalSummaryParser.Parse("not an interval", profile.HeartRate), Is.Null);
        Assert.That(IntervalSummaryParser.Parse(null, profile.HeartRate), Is.Null);
    }

    [Test]
    public void BuildDossierForBerniOn20261125SurfacesBreakReentryStaleWellnessAndCleanIntervals()
    {
        var layout = ResolveRepositoryLayout();
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var data = AthleteDataRepository.LoadAthleteData(layout, registry, "berni");
        var refDate = new DateOnly(2026, 11, 25);

        var dossier = CoachIntelligenceDossierBuilder.Build(
            data.Profile,
            data.Activities,
            data.WellnessRecords,
            data.RunReviews,
            data.CalendarEvents,
            refDate);

        Assert.That(dossier.ReferenceDate, Is.EqualTo(refDate));
        Assert.That(dossier.TargetDate, Is.EqualTo(refDate));
        Assert.That(dossier.TargetDayOfWeek, Is.EqualTo("wednesday"));

        // Break & re-entry detection (10-day gap: 2026-11-12 -> 2026-11-22)
        var breakCtx = dossier.StimulusProgression.BreakReentry;
        Assert.That(breakCtx.HasRecentExtendedBreak, Is.True);
        Assert.That(breakCtx.LongestGapDaysLast28Days, Is.EqualTo(10));
        Assert.That(breakCtx.RecentBreakStartDate, Is.EqualTo(new DateOnly(2026, 11, 12)));
        Assert.That(breakCtx.RecentBreakEndDate, Is.EqualTo(new DateOnly(2026, 11, 22)));
        Assert.That(breakCtx.RecentBreakDurationDays, Is.EqualTo(10));
        Assert.That(breakCtx.RunsCompletedSinceRecentBreak, Is.EqualTo(1));
        Assert.That(breakCtx.CleanBaseRunsSinceRecentBreak, Is.EqualTo(1));

        // Stale wellness (latest 2026-11-22 -> age 3 days) & decayed CTL/ATL below 2026-11-22 values (13.5 / 11.8)
        Assert.That(dossier.Readiness.LatestWellnessDate, Is.EqualTo(new DateOnly(2026, 11, 22)));
        Assert.That(dossier.Readiness.WellnessAgeDays, Is.EqualTo(3));
        Assert.That(dossier.Readiness.IsWellnessStale, Is.True);
        Assert.That(dossier.Readiness.LoadDataSource, Is.EqualTo("wellness_decayed"));
        Assert.That(dossier.Readiness.Ctl, Is.LessThan(13.5));
        Assert.That(dossier.Readiness.Atl, Is.LessThan(11.8));
        Assert.That(dossier.Readiness.MedicalRedFlags, Is.Empty);

        // Base progression chain includes i213105633 with Z2 GAP pace & 4.0 bpm cardiac drift
        var comebackRun = dossier.StimulusProgression.BaseProgressionChain
            .FirstOrDefault(s => s.ActivityId == "i213105633");
        Assert.That(comebackRun, Is.Not.Null);
        Assert.That(comebackRun!.Z2GapPaceText, Is.EqualTo("07:58/km"));
        Assert.That(comebackRun.CardiacDriftBpm, Is.EqualTo(4.0).Within(0.01));
        Assert.That(comebackRun.Z1Z2Percentage, Is.EqualTo(100.0));

        // Quality progression chain parses 3x 4m @ 162bpm cleanly for i209021118
        var lastQuality = dossier.StimulusProgression.QualityProgressionChain
            .FirstOrDefault(q => q.ActivityId == "i209021118");
        Assert.That(lastQuality, Is.Not.Null);
        var threeByFour = lastQuality!.ParsedIntervals
            .FirstOrDefault(seg => seg.Raw == "3x 4m 162bpm");
        Assert.That(threeByFour, Is.Not.Null);
        Assert.That(threeByFour!.DurationSeconds, Is.EqualTo(240));
        Assert.That(threeByFour.AverageHeartRate, Is.EqualTo(162.0));

        // Context clues capture 2026-11-22 back clue & 2026-10-30 calf-cramp note
        Assert.That(
            breakCtx.RecentContextClues.Any(c =>
                c.Date == new DateOnly(2026, 11, 22)
                && c.Summary.Contains("Rücken", StringComparison.OrdinalIgnoreCase)),
            Is.True);
        Assert.That(
            breakCtx.RecentContextClues.Any(c =>
                c.Date == new DateOnly(2026, 10, 30)
                && c.Summary.Contains("Waden", StringComparison.OrdinalIgnoreCase)),
            Is.True);

        // Calendar events on 2026-11-25 contain Slot 2A and Slot 2B
        Assert.That(dossier.Calendar.EventsOnTargetDate, Has.Count.EqualTo(2));
        Assert.That(dossier.Calendar.HasUncompletedPlannedWorkoutOnTargetDate, Is.True);

        // Data quality notes warn on stale wellness and 7 vs 5 HR zone schema shift
        Assert.That(dossier.DataQualityNotes.Any(n => n.Contains("stale", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(dossier.DataQualityNotes.Any(n => n.Contains("5 vs 7", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public void BuildDossierStrictlyEnforcesRefDateCutoffWithoutFutureLeaks()
    {
        var layout = ResolveRepositoryLayout();
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var data = AthleteDataRepository.LoadAthleteData(layout, registry, "berni");
        var refDate = new DateOnly(2026, 11, 11);

        var dossier = CoachIntelligenceDossierBuilder.Build(
            data.Profile,
            data.Activities,
            data.WellnessRecords,
            data.RunReviews,
            data.CalendarEvents,
            refDate);

        Assert.That(dossier.Readiness.LatestWellnessDate, Is.EqualTo(refDate));
        Assert.That(dossier.Readiness.IsWellnessStale, Is.False);
        Assert.That(dossier.Readiness.LoadDataSource, Is.EqualTo("wellness"));
        Assert.That(dossier.StimulusProgression.BaseProgressionChain.All(s => s.Date <= refDate), Is.True);
        Assert.That(dossier.StimulusProgression.QualityProgressionChain.All(s => s.Date <= refDate), Is.True);
        Assert.That(dossier.StimulusProgression.QualityProgressionChain.Any(s => s.ActivityId == "i209021118"), Is.False);
        Assert.That(dossier.StimulusProgression.BaseProgressionChain.Any(s => s.ActivityId == "i213105633"), Is.False);
        Assert.That(dossier.Calendar.EventsOnTargetDate, Is.Empty);
    }

    [Test]
    public void BuildDossierForBerniTestSurfacesOrphanedReviewsWithoutFabricatingLoadFallback()
    {
        var layout = ResolveRepositoryLayout();
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var data = AthleteDataRepository.LoadAthleteData(layout, registry, "berni-test");
        var refDate = new DateOnly(2026, 11, 25);

        var dossier = CoachIntelligenceDossierBuilder.Build(
            data.Profile,
            data.Activities,
            data.WellnessRecords,
            data.RunReviews,
            data.CalendarEvents,
            refDate);

        Assert.That(dossier.StimulusProgression.Ledger.DaysSinceLastRun, Is.Null);
        Assert.That(dossier.StimulusProgression.BaseProgressionChain, Is.Empty);
        Assert.That(dossier.StimulusProgression.QualityProgressionChain, Is.Empty);
        Assert.That(dossier.Readiness.Ctl, Is.EqualTo(0.0));
        Assert.That(dossier.Readiness.Atl, Is.EqualTo(0.0));
        Assert.That(dossier.Readiness.Tsb, Is.EqualTo(0.0));
        Assert.That(dossier.Readiness.LoadDataSource, Is.EqualTo("wellness_decayed"));
        Assert.That(dossier.DataQualityNotes.Any(n => n.Contains("orphaned", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public void BuildDossierWithNoLoadHistoryReturnsNullLoadInsteadOfFallback()
    {
        var layout = ResolveRepositoryLayout();
        var profile = StrictJsonLoader.LoadAthleteProfile(layout, "berni-test").Athlete;
        var refDate = new DateOnly(2026, 11, 25);

        var dossier = CoachIntelligenceDossierBuilder.Build(profile, [], [], null, null, refDate);

        Assert.That(dossier.Readiness.Ctl, Is.Null);
        Assert.That(dossier.Readiness.Atl, Is.Null);
        Assert.That(dossier.Readiness.Tsb, Is.Null);
        Assert.That(dossier.Readiness.LoadDataSource, Is.EqualTo("none"));
        Assert.That(dossier.DataQualityNotes.Any(n => n.Contains("No CTL/ATL", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public void MedicalRedFlagsDistinguishAcuteSignalsFromStaleOrMildSleepDips()
    {
        var layout = ResolveRepositoryLayout();
        var baseProfile = StrictJsonLoader.LoadAthleteProfile(layout, "berni").Athlete;
        var refDate = new DateOnly(2026, 11, 25);

        var baselineEntries = Enumerable.Range(1, 10)
            .Select(offset => new IntervalsWellnessEntry
            {
                Id = refDate.AddDays(-offset),
                Updated = DateTimeOffset.UtcNow,
                RestingHr = 47,
                Hrv = 75.0,
                SleepSecs = 27000,
                SleepScore = 85.0,
                TempWeight = false,
                TempRestingHr = false
            })
            .ToList();

        // 1. Acute illness + severe RHR spike today -> 2 red flags
        var acuteWellness = new List<IntervalsWellnessEntry>(baselineEntries)
        {
            new()
            {
                Id = refDate,
                Updated = DateTimeOffset.UtcNow,
                RestingHr = 53,
                Hrv = 52.0,
                SleepSecs = 23400,
                SleepScore = 68.0,
                Comments = "Fieber und Halsschmerzen",
                TempWeight = false,
                TempRestingHr = false
            }
        };

        var acuteDossier = CoachIntelligenceDossierBuilder.Build(baseProfile, [], acuteWellness, null, null, refDate);
        Assert.That(acuteDossier.Readiness.MedicalRedFlags, Has.Count.EqualTo(2));
        Assert.That(acuteDossier.Readiness.MedicalRedFlags.Any(f => f.Code == "severe_rhr_spike"), Is.True);
        Assert.That(acuteDossier.Readiness.MedicalRedFlags.Any(f => f.Code == "illness_symptom_comment"), Is.True);

        // 2. Mild sleep dip today (6.2h, score 68, normal RHR) -> zero medical red flags
        var mildDipWellness = new List<IntervalsWellnessEntry>(baselineEntries)
        {
            new()
            {
                Id = refDate,
                Updated = DateTimeOffset.UtcNow,
                RestingHr = 48,
                Hrv = 72.0,
                SleepSecs = (int)(6.2 * 3600),
                SleepScore = 68.0,
                TempWeight = false,
                TempRestingHr = false
            }
        };

        var mildDossier = CoachIntelligenceDossierBuilder.Build(baseProfile, [], mildDipWellness, null, null, refDate);
        Assert.That(mildDossier.Readiness.MedicalRedFlags, Is.Empty);

        // 3. Stale RHR spike (3 days old) -> zero medical red flags
        var staleSpikeWellness = new List<IntervalsWellnessEntry>(baselineEntries)
        {
            new()
            {
                Id = refDate.AddDays(-3),
                Updated = DateTimeOffset.UtcNow,
                RestingHr = 55,
                Hrv = 55.0,
                SleepSecs = 27000,
                SleepScore = 80.0,
                TempWeight = false,
                TempRestingHr = false
            }
        };

        var staleDossier = CoachIntelligenceDossierBuilder.Build(baseProfile, [], staleSpikeWellness, null, null, refDate);
        Assert.That(staleDossier.Readiness.MedicalRedFlags, Is.Empty);
    }

    [Test]
    public void AthleteProfileDeserializesWithAndWithoutOptionalGoalsAndTimeBudgets()
    {
        const string jsonWithExtensions = """
            {
              "athlete": {
                "id": "i1",
                "name": "TestRunner",
                "heart_rate": {
                  "resting_hr": 48,
                  "max_hr": 190,
                  "lthr": 175,
                  "zones": {
                    "z2": { "zone": 2, "name": "Zone 2", "min": 145, "max": 158 }
                  }
                },
                "goals": {
                  "primary_goal": "Half Marathon durability",
                  "target_horizon": "2027-04",
                  "current_focus": "Build aerobic long run to 70m"
                },
                "time_budgets": {
                  "typical_weekday_min_minutes": 35,
                  "typical_weekday_max_minutes": 45,
                  "typical_weekend_min_minutes": 50,
                  "typical_weekend_max_minutes": 75
                }
              }
            }
            """;

        const string jsonWithoutExtensions = """
            {
              "athlete": {
                "id": "i1",
                "name": "TestRunner",
                "heart_rate": {
                  "resting_hr": 48,
                  "max_hr": 190,
                  "lthr": 175,
                  "zones": {
                    "z2": { "zone": 2, "name": "Zone 2", "min": 145, "max": 158 }
                  }
                }
              }
            }
            """;

        var withExt = StrictJsonLoader.Deserialize<AthleteProfileDocument>(jsonWithExtensions).Athlete;
        Assert.That(withExt.Goals, Is.Not.Null);
        Assert.That(withExt.Goals!.PrimaryGoal, Is.EqualTo("Half Marathon durability"));
        Assert.That(withExt.TimeBudgets, Is.Not.Null);
        Assert.That(withExt.TimeBudgets!.TypicalWeekendMaxMinutes, Is.EqualTo(75));

        var withoutExt = StrictJsonLoader.Deserialize<AthleteProfileDocument>(jsonWithoutExtensions).Athlete;
        Assert.That(withoutExt.Goals, Is.Null);
        Assert.That(withoutExt.TimeBudgets, Is.Null);
    }
}
