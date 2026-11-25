namespace Laufen.Domain;

public sealed record BanisterProjectedDay(
    DateOnly Date,
    double PlannedLoad,
    double Ctl,
    double Atl,
    double Tsb);

public sealed record BanisterProjectionResult(
    double StartingCtl,
    double StartingAtl,
    double StartingTsb,
    IReadOnlyList<BanisterProjectedDay> DailyPoints,
    double EndingCtl,
    double EndingAtl,
    double EndingTsb,
    double MinProjectedTsb,
    DateOnly MinTsbDate,
    double TotalProjectedLoad);

/// <summary>
/// Deterministic step-weighted TSS load estimator and 14-day Banister EWMA forward projector.
/// </summary>
public static class TrainingLoadProjector
{
    private const double CtlTimeConstantDays = 42.0;
    private const double AtlTimeConstantDays = 7.0;
    private const double PaceRangeIntensityFactor = 1.04;

    private static readonly double[] ZoneIntensityFactors = [0.62, 0.77, 0.86, 0.95, 1.05];

    public static double EstimateWorkoutLoad(WorkoutDefinition workout)
    {
        ArgumentNullException.ThrowIfNull(workout);
        if (workout.Steps is null || workout.Steps.Count == 0)
        {
            return 0.0;
        }

        var totalTss = 0.0;
        foreach (var step in workout.Steps)
        {
            if (string.Equals(step.Kind, "repeat", StringComparison.Ordinal))
            {
                var reps = Math.Max(0, step.Reps ?? 0);
                var workTss = step.Work is not null
                    ? ComputeSegmentTss(step.Work.DurationSeconds, step.Work.Target)
                    : 0.0;
                var recTss = step.Recovery is not null
                    ? ComputeSegmentTss(step.Recovery.DurationSeconds, step.Recovery.Target)
                    : 0.0;
                totalTss += reps * (workTss + recTss);
            }
            else if (step.DurationSeconds is > 0 && step.Target is not null)
            {
                totalTss += ComputeSegmentTss(step.DurationSeconds.Value, step.Target);
            }
        }

        return Math.Round(totalTss, 1, MidpointRounding.ToEven);
    }

    public static BanisterProjectionResult ProjectBanister(
        double startingCtl,
        double startingAtl,
        DateOnly refDate,
        IReadOnlyDictionary<DateOnly, double> dailyPlannedLoads,
        int horizonDays = 14)
    {
        ArgumentNullException.ThrowIfNull(dailyPlannedLoads);
        if (horizonDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(horizonDays), "Horizon days must be positive.");
        }

        var kCtl = Math.Exp(-1.0 / CtlTimeConstantDays);
        var kAtl = Math.Exp(-1.0 / AtlTimeConstantDays);

        var ctl = startingCtl;
        var atl = startingAtl;
        var points = new List<BanisterProjectedDay>(horizonDays);
        var totalLoad = 0.0;

        var minTsb = double.PositiveInfinity;
        var minTsbDate = refDate;

        for (var offset = 0; offset < horizonDays; offset++)
        {
            var date = refDate.AddDays(offset);
            var load = dailyPlannedLoads.TryGetValue(date, out var rawLoad)
                ? Math.Max(0.0, rawLoad)
                : 0.0;

            ctl = (ctl * kCtl) + (load * (1.0 - kCtl));
            atl = (atl * kAtl) + (load * (1.0 - kAtl));
            var tsb = ctl - atl;

            var roundedLoad = Math.Round(load, 1, MidpointRounding.ToEven);
            var roundedCtl = Math.Round(ctl, 1, MidpointRounding.ToEven);
            var roundedAtl = Math.Round(atl, 1, MidpointRounding.ToEven);
            var roundedTsb = Math.Round(tsb, 1, MidpointRounding.ToEven);

            totalLoad += roundedLoad;
            if (roundedTsb < minTsb)
            {
                minTsb = roundedTsb;
                minTsbDate = date;
            }

            points.Add(new BanisterProjectedDay(date, roundedLoad, roundedCtl, roundedAtl, roundedTsb));
        }

        var last = points[^1];
        return new BanisterProjectionResult(
            Math.Round(startingCtl, 1, MidpointRounding.ToEven),
            Math.Round(startingAtl, 1, MidpointRounding.ToEven),
            Math.Round(startingCtl - startingAtl, 1, MidpointRounding.ToEven),
            points,
            last.Ctl,
            last.Atl,
            last.Tsb,
            minTsb,
            minTsbDate,
            Math.Round(totalLoad, 1, MidpointRounding.ToEven));
    }

    private static double ComputeSegmentTss(int durationSeconds, WorkoutTargetSpec target)
    {
        if (durationSeconds <= 0)
        {
            return 0.0;
        }

        var intensityFactor = ResolveIntensityFactor(target);
        var hours = durationSeconds / 3600.0;
        return intensityFactor * intensityFactor * hours * 100.0;
    }

    private static double ResolveIntensityFactor(WorkoutTargetSpec target) => target.Modality switch
    {
        "hr_zone" => ResolveHrZoneIntensityFactor(target.HrZoneStart ?? 2, target.HrZoneEnd),
        "pace_range" => PaceRangeIntensityFactor,
        "rpe" => Math.Clamp(0.52 + ((target.Rpe ?? 5) * 0.055), 0.58, 1.08),
        _ => 0.77
    };

    private static double ResolveHrZoneIntensityFactor(int zoneStart, int? zoneEnd)
    {
        var start = Math.Clamp(zoneStart, 1, ZoneIntensityFactors.Length);
        var end = zoneEnd.HasValue ? Math.Clamp(zoneEnd.Value, start, ZoneIntensityFactors.Length) : start;

        var sum = 0.0;
        for (var z = start; z <= end; z++)
        {
            sum += ZoneIntensityFactors[z - 1];
        }

        return sum / (end - start + 1);
    }
}
