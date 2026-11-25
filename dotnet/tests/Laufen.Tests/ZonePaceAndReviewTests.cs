using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class ZonePaceAndReviewTests
{
    private static WorkspaceLayout ResolveRepositoryLayout() =>
        WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);

    [Test]
    public void StrictJsonLoaderLoadsAllRealBerniStreamFilesIncludingNullableAltitude()
    {
        var layout = ResolveRepositoryLayout();
        var streamsDir = Path.Combine(layout.AthletesDirectory, "berni", "streams");
        var files = Directory.GetFiles(streamsDir, "*.json");

        Assert.That(files, Is.Not.Empty);
        foreach (var file in files)
        {
            var activityId = Path.GetFileNameWithoutExtension(file);
            var stream = StrictJsonLoader.LoadActivityStream(layout, "berni", activityId);
            Assert.That(stream, Is.Not.Null, $"Stream '{activityId}' returned null.");
            Assert.That(stream!.Heartrate, Is.Not.Null, $"Stream '{activityId}' missing heartrate.");
        }
    }

    [TestCase("i213105633")]
    [TestCase("i209021118")]
    [TestCase("i208292808")]
    public void ZonePaceCalculatorMatchesPythonRunReviewsDifferentialParity(string activityId)
    {
        var layout = ResolveRepositoryLayout();
        var profile = StrictJsonLoader.LoadAthleteProfile(layout, "berni").Athlete;
        var pythonReviews = StrictJsonLoader.LoadRunReviews(layout, "berni");
        var stream = StrictJsonLoader.LoadActivityStream(layout, "berni", activityId);

        Assert.That(stream, Is.Not.Null);
        Assert.That(pythonReviews.TryGetValue(activityId, out var pyReview), Is.True);
        var pyZp = pyReview!.ZonePaceStats;
        Assert.That(pyZp, Is.Not.Null);
        Assert.That(pyZp!.HasStreams, Is.True);

        var dotNetZp = ZonePaceCalculator.ComputeZonePaceStats(stream!, profile.HeartRate);

        Assert.That(dotNetZp.HasStreams, Is.True);
        Assert.That(dotNetZp.WalkingSecondsFiltered, Is.EqualTo(pyZp.WalkingSecondsFiltered).Within(2));
        Assert.That(dotNetZp.TotalRunningSeconds, Is.EqualTo(pyZp.TotalRunningSeconds).Within(2));
        Assert.That(dotNetZp.FirstHalfAverageHeartRate, Is.EqualTo(pyZp.FirstHalfAverageHeartRate!.Value).Within(0.2));
        Assert.That(dotNetZp.SecondHalfAverageHeartRate, Is.EqualTo(pyZp.SecondHalfAverageHeartRate!.Value).Within(0.2));
        Assert.That(dotNetZp.PaceSplitDiffSeconds, Is.EqualTo(pyZp.PaceSplitDiffSeconds!.Value).Within(0.5));

        var pyZ2 = pyZp.Zones.Values.First(z => z.Zone == 2);
        var dnZ2 = dotNetZp.Zones.Values.First(z => z.Zone == 2);
        Assert.That(dnZ2.GapPaceSecondsPerKm, Is.EqualTo(pyZ2.GapPaceSecondsPerKm).Within(0.5));
        Assert.That(dnZ2.RawPaceSecondsPerKm, Is.EqualTo(pyZ2.RawPaceSecondsPerKm).Within(0.5));
        Assert.That(dnZ2.ValidForTrend, Is.EqualTo(pyZ2.ValidForTrend));
    }

    [Test]
    public void ZonePaceCalculatorHandlesNonUnitDtWithoutTruncation()
    {
        var stream = new ActivityStreamData
        {
            Time = [0, 2, 4, 6, 8, 10],
            Heartrate = [152, 152, 153, 153, 154, 154],
            VelocitySmooth = [2.2, 2.2, 2.2, 2.2, 2.2, 2.2],
            FixedAltitude = [160.0, 160.0, 160.0, 160.0, 160.0, 160.0]
        };

        var stats = ZonePaceCalculator.ComputeZonePaceStats(stream, profile: null);

        // First sample dt=1, next 5 samples dt=2 => total running seconds = 1 + 10 = 11s (not 6!)
        Assert.That(stats.TotalRunningSeconds, Is.EqualTo(11));
        Assert.That(stats.Zones.Values.Sum(z => z.RunningSeconds), Is.EqualTo(11));
    }

    [Test]
    public void PostRunReviewCalculatorEvaluatesCleanQualityAndCrampRunsWithoutIntervalTruncation()
    {
        var layout = ResolveRepositoryLayout();
        var profile = StrictJsonLoader.LoadAthleteProfile(layout, "berni").Athlete;
        var activities = StrictJsonLoader.LoadIntervalsActivities(layout, "berni");
        var byId = activities.ToDictionary(a => a.Id);

        // 1. Clean comeback run (i213105633)
        var comeback = byId["i213105633"];
        var comebackStream = StrictJsonLoader.LoadActivityStream(layout, "berni", comeback.Id);
        var comebackReview = PostRunReviewCalculator.EvaluateRun(comeback, profile, comebackStream);
        Assert.That(comebackReview.Review.SchemaVersion, Is.EqualTo(2));
        Assert.That(comebackReview.IntensityClass, Is.EqualTo("aerobe_basis"));
        Assert.That(comebackReview.ZonePaceStats, Is.Not.Null);
        Assert.That(comebackReview.Review.Stages.Stage4NextRun.RecoveryHours, Is.EqualTo(24));
        Assert.That(comebackReview.Review.Stages.Stage4NextRun.TargetHeartRateMax, Is.EqualTo(159));

        // 2. Quality threshold run (i209021118) — fixes W-21 ("3x 4m 162bpm" => 240s, avgHr=162)
        var quality = byId["i209021118"];
        var qualityStream = StrictJsonLoader.LoadActivityStream(layout, "berni", quality.Id);
        var qualityReview = PostRunReviewCalculator.EvaluateRun(quality, profile, qualityStream);
        Assert.That(qualityReview.IntensityClass, Is.EqualTo("schwellen_intervall").Or.EqualTo("schwellenlauf"));
        Assert.That(qualityReview.Review.Stages.Stage4NextRun.RecoveryHours, Is.EqualTo(48));
        var threeByFour = qualityReview.Review.Stages.Stage1Physiology.ParsedIntervals
            .FirstOrDefault(i => i.Raw == "3x 4m 162bpm");
        Assert.That(threeByFour, Is.Not.Null);
        Assert.That(threeByFour!.DurationSeconds, Is.EqualTo(240));
        Assert.That(threeByFour.AverageHeartRate, Is.EqualTo(162.0));

        // 3. Aborted calf-cramp run (i203449631) — 48h recovery + complaint optimization
        var cramp = byId["i203449631"];
        var crampStream = StrictJsonLoader.LoadActivityStream(layout, "berni", cramp.Id);
        var crampReview = PostRunReviewCalculator.EvaluateRun(cramp, profile, crampStream);
        Assert.That(crampReview.Review.Stages.Stage4NextRun.RecoveryHours, Is.EqualTo(48));
        Assert.That(crampReview.Review.Stages.Stage4NextRun.NextWorkoutType, Is.EqualTo("recovery_run"));
        Assert.That(
            crampReview.Review.Stages.Stage3Optimizations.Any(o => o.Category == "recovery"),
            Is.True);
    }

    [Test]
    public void BuildZonePaceTrendsComputesChronologicalRollingAverages()
    {
        var layout = ResolveRepositoryLayout();
        var pythonReviews = StrictJsonLoader.LoadRunReviews(layout, "berni");
        var trends = ZonePaceCalculator.BuildZonePaceTrends(pythonReviews, maxDate: new DateOnly(2026, 11, 25));

        Assert.That(trends.Series.ContainsKey("z2"), Is.True);
        Assert.That(trends.Series["z2"].Count, Is.GreaterThanOrEqualTo(3));
        Assert.That(trends.Summary["z2"].Count, Is.EqualTo(trends.Series["z2"].Count));
        Assert.That(trends.Summary["z2"].RecentAveragePaceText, Is.Not.Null);
    }
}
