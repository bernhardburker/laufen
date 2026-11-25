using System.Globalization;

namespace Laufen.Domain;

public sealed record HrZoneBound(int Zone, string Name, int MinHr, int MaxHr);

public sealed record ZonePaceTrendPoint(
    string ActivityId,
    DateOnly Date,
    string Name,
    double GapPaceSecondsPerKm,
    string GapPaceText,
    double RawPaceSecondsPerKm,
    string RawPaceText,
    double AverageHeartRate,
    int RunningSeconds,
    double RollingGapPaceSecondsPerKm);

public sealed record ZonePaceTrendSummary(
    int Count,
    double? EarlyAverageSecondsPerKm,
    string? EarlyAveragePaceText,
    double? RecentAverageSecondsPerKm,
    string? RecentAveragePaceText,
    double? DeltaSecondsPerKm);

public sealed record ZonePaceTrendsResult(
    IReadOnlyDictionary<string, IReadOnlyList<ZonePaceTrendPoint>> Series,
    IReadOnlyDictionary<string, ZonePaceTrendSummary> Summary);

/// <summary>
/// Pure deterministic per-second HR-zone GAP pace and 1st/2nd-half split calculator.
/// </summary>
public static class ZonePaceCalculator
{
    public const double DefaultMinRunningSpeedMps = 1.67;
    public const int DefaultMinZoneSeconds = 120;
    private const int MaxUpperBoundHr = 250;

    public static string FormatPaceSeconds(double? secondsPerKm)
    {
        if (!secondsPerKm.HasValue || secondsPerKm.Value <= 0.0)
        {
            return "--:--";
        }

        var totalSeconds = (int)Math.Round(secondsPerKm.Value, MidpointRounding.AwayFromZero);
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return string.Create(CultureInfo.InvariantCulture, $"{minutes:D2}:{seconds:D2}/km");
    }

    public static IReadOnlyList<HrZoneBound> ResolveHrZoneBounds(
        HeartRateProfile? profile,
        IReadOnlyList<int>? activityHrZones = null)
    {
        if (profile is not null && profile.Zones.Count > 0)
        {
            var parsed = profile.Zones.Values
                .OrderBy(z => z.Zone)
                .Select(z => new HrZoneBound(
                    z.Zone,
                    string.IsNullOrWhiteSpace(z.Name) ? string.Create(CultureInfo.InvariantCulture, $"Zone {z.Zone}") : z.Name,
                    z.Min,
                    z.Max))
                .ToList();

            if (parsed.Count > 0)
            {
                var last = parsed[^1];
                parsed[^1] = last with { MaxHr = Math.Max(last.MaxHr, MaxUpperBoundHr) };
                return parsed;
            }
        }

        var rawCutoffs = (profile?.RawHrZones is { Count: >= 4 } profileRaw)
            ? profileRaw
            : (activityHrZones is { Count: >= 4 } actRaw ? actRaw : null);

        if (rawCutoffs is not null)
        {
            var list = new List<HrZoneBound>(rawCutoffs.Count);
            var prevMax = -1;
            for (var i = 0; i < rawCutoffs.Count; i++)
            {
                var zNum = i + 1;
                var bound = rawCutoffs[i];
                var zMax = zNum == rawCutoffs.Count ? Math.Max(bound, MaxUpperBoundHr) : bound;
                var zMin = zNum == 1 ? 0 : prevMax + 1;
                list.Add(new HrZoneBound(
                    zNum,
                    string.Create(CultureInfo.InvariantCulture, $"Zone {zNum}"),
                    zMin,
                    zMax));
                prevMax = bound;
            }

            return list;
        }

        var lthr = profile?.Lthr > 0 ? profile.Lthr : 170;
        var z1Max = (int)Math.Round(lthr * 0.85, MidpointRounding.AwayFromZero);
        var z2Max = (int)Math.Round(lthr * 0.89, MidpointRounding.AwayFromZero);
        var z3Max = (int)Math.Round(lthr * 0.94, MidpointRounding.AwayFromZero);
        var z4Max = (int)Math.Round(lthr * 1.00, MidpointRounding.AwayFromZero);

        return
        [
            new(1, "Zone 1", 0, z1Max),
            new(2, "Zone 2", z1Max + 1, z2Max),
            new(3, "Zone 3", z2Max + 1, z3Max),
            new(4, "Zone 4", z3Max + 1, z4Max),
            new(5, "Zone 5", z4Max + 1, MaxUpperBoundHr)
        ];
    }

    public static double MinettiGradeFactor(double grade)
    {
        var g = Math.Clamp(grade, -0.25, 0.25);
        var g2 = g * g;
        var g3 = g2 * g;
        var g4 = g3 * g;
        var g5 = g4 * g;

        var costG = (155.4 * g5)
            - (30.4 * g4)
            - (43.3 * g3)
            + (46.3 * g2)
            + (19.5 * g)
            + 3.6;

        return Math.Max(0.5, costG / 3.6);
    }

    public static IReadOnlyList<double?> ComputeGapSpeeds(
        IReadOnlyList<double?> speeds,
        IReadOnlyList<double?>? altitudes,
        IReadOnlyList<int?>? times = null,
        int windowHalf = 5)
    {
        ArgumentNullException.ThrowIfNull(speeds);

        var n = speeds.Count;
        var result = new double?[n];
        if (altitudes is null || altitudes.Count < n)
        {
            for (var i = 0; i < n; i++)
            {
                result[i] = speeds[i];
            }

            return result;
        }

        for (var i = 0; i < n; i++)
        {
            if (speeds[i] is not { } v || v <= 0.0)
            {
                result[i] = null;
                continue;
            }

            var lo = Math.Max(0, i - windowHalf);
            var hi = Math.Min(n - 1, i + windowHalf);

            if (hi == lo || altitudes[lo] is not { } altLo || altitudes[hi] is not { } altHi)
            {
                result[i] = v;
                continue;
            }

            var dt = (double)(hi - lo);
            if (times is not null && times.Count >= n && times[hi] is { } tHi && times[lo] is { } tLo)
            {
                dt = Math.Max(1.0, tHi - tLo);
            }

            var speedSum = 0.0;
            var speedCount = 0;
            for (var k = lo; k <= hi; k++)
            {
                if (speeds[k] is { } sk && sk > 0.0)
                {
                    speedSum += sk;
                    speedCount++;
                }
            }

            var avgV = speedCount > 0 ? speedSum / speedCount : v;
            var dist = avgV * dt;
            if (dist < 5.0)
            {
                result[i] = v;
                continue;
            }

            var grade = (altHi - altLo) / dist;
            result[i] = v * MinettiGradeFactor(grade);
        }

        return result;
    }

    public static ZonePaceStats ComputeZonePaceStats(
        ActivityStreamData stream,
        HeartRateProfile? profile,
        double minRunningSpeedMps = DefaultMinRunningSpeedMps,
        int minZoneSeconds = DefaultMinZoneSeconds,
        IReadOnlyList<int>? activityHrZones = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var hrs = stream.Heartrate;
        var speeds = stream.VelocitySmooth;
        if (hrs is null || speeds is null)
        {
            return EmptyStats(minZoneSeconds);
        }

        var n = Math.Min(hrs.Count, speeds.Count);
        if (n == 0)
        {
            return EmptyStats(minZoneSeconds);
        }

        var slicedSpeeds = speeds.Count == n ? speeds : speeds.Take(n).ToList();
        var rawAltitudes = stream.FixedAltitude ?? stream.Altitude;
        var slicedAltitudes = rawAltitudes is not null && rawAltitudes.Count >= n
            ? (rawAltitudes.Count == n ? rawAltitudes : rawAltitudes.Take(n).ToList())
            : null;
        var times = stream.Time;
        var slicedTimes = times is not null && times.Count >= n
            ? (times.Count == n ? times : times.Take(n).ToList())
            : null;

        var bounds = ResolveHrZoneBounds(profile, activityHrZones);
        var gapSpeeds = ComputeGapSpeeds(slicedSpeeds, slicedAltitudes, slicedTimes);

        var accByZone = new Dictionary<int, ZoneAccumulator>(bounds.Count);
        foreach (var bound in bounds)
        {
            accByZone[bound.Zone] = new ZoneAccumulator();
        }

        var runningSamples = new List<(double Hr, double RawV, double GapV)>();
        var totalRunningDt = 0.0;
        var walkingFiltered = 0.0;

        for (var i = 0; i < n; i++)
        {
            if (hrs[i] is not { } hr || speeds[i] is not { } rawV || hr <= 0.0 || rawV <= 0.0)
            {
                continue;
            }

            var dt = 1.0;
            if (slicedTimes is not null && i > 0 && slicedTimes[i] is { } currT && slicedTimes[i - 1] is { } prevT)
            {
                var delta = (double)(currT - prevT);
                if (delta is > 0.0 and <= 10.0)
                {
                    dt = delta;
                }
            }

            if (rawV < minRunningSpeedMps)
            {
                walkingFiltered += dt;
                continue;
            }

            var effGapV = gapSpeeds[i] is { } gv && gv > 0.0 ? gv : rawV;
            var zNum = ClassifyHrZone(hr, bounds);
            var acc = accByZone[zNum];
            acc.Seconds += dt;
            acc.HrSum += hr * dt;
            acc.RawVSum += rawV * dt;
            acc.GapVSum += effGapV * dt;

            totalRunningDt += dt;
            runningSamples.Add((hr, rawV, effGapV));
        }

        var zonesOut = new Dictionary<string, ZonePaceEntry>(StringComparer.Ordinal);
        foreach (var bound in bounds)
        {
            var acc = accByZone[bound.Zone];
            var sec = (int)Math.Round(acc.Seconds, MidpointRounding.ToEven);
            if (sec <= 0)
            {
                continue;
            }

            var rawVAvg = acc.RawVSum / acc.Seconds;
            var gapVAvg = acc.GapVSum / acc.Seconds;
            var rawPaceS = rawVAvg > 0.0 ? 1000.0 / rawVAvg : 0.0;
            var gapPaceS = gapVAvg > 0.0 ? 1000.0 / gapVAvg : 0.0;

            var key = string.Create(CultureInfo.InvariantCulture, $"z{bound.Zone}");
            zonesOut[key] = new ZonePaceEntry
            {
                Zone = bound.Zone,
                Name = bound.Name,
                RunningSeconds = sec,
                AverageHeartRate = Math.Round(acc.HrSum / acc.Seconds, 1, MidpointRounding.ToEven),
                GapSpeedMetersPerSecond = Math.Round(gapVAvg, 3, MidpointRounding.ToEven),
                GapPaceSecondsPerKm = Math.Round(gapPaceS, 1, MidpointRounding.ToEven),
                GapPaceText = FormatPaceSeconds(gapPaceS),
                RawSpeedMetersPerSecond = Math.Round(rawVAvg, 3, MidpointRounding.ToEven),
                RawPaceSecondsPerKm = Math.Round(rawPaceS, 1, MidpointRounding.ToEven),
                RawPaceText = FormatPaceSeconds(rawPaceS),
                ValidForTrend = sec >= minZoneSeconds
            };
        }

        double? firstHalfGapPace = null;
        double? secondHalfGapPace = null;
        double? firstHalfHr = null;
        double? secondHalfHr = null;
        double? paceSplitDiff = null;

        if (runningSamples.Count >= 20)
        {
            var mid = runningSamples.Count / 2;
            var h1 = runningSamples.Take(mid).ToList();
            var h2 = runningSamples.Skip(mid).ToList();

            var v1 = h1.Average(x => x.GapV);
            var v2 = h2.Average(x => x.GapV);
            if (v1 > 0.0 && v2 > 0.0)
            {
                firstHalfGapPace = Math.Round(1000.0 / v1, 1, MidpointRounding.ToEven);
                secondHalfGapPace = Math.Round(1000.0 / v2, 1, MidpointRounding.ToEven);
                paceSplitDiff = Math.Round(secondHalfGapPace.Value - firstHalfGapPace.Value, 1, MidpointRounding.ToEven);
            }

            firstHalfHr = Math.Round(h1.Average(x => x.Hr), 1, MidpointRounding.ToEven);
            secondHalfHr = Math.Round(h2.Average(x => x.Hr), 1, MidpointRounding.ToEven);
        }

        return new ZonePaceStats
        {
            HasStreams = true,
            MinZoneSeconds = minZoneSeconds,
            TotalRunningSeconds = (int)Math.Round(totalRunningDt, MidpointRounding.ToEven),
            WalkingSecondsFiltered = (int)Math.Round(walkingFiltered, MidpointRounding.ToEven),
            Zones = zonesOut,
            FirstHalfGapPaceSecondsPerKm = firstHalfGapPace,
            SecondHalfGapPaceSecondsPerKm = secondHalfGapPace,
            FirstHalfAverageHeartRate = firstHalfHr,
            SecondHalfAverageHeartRate = secondHalfHr,
            PaceSplitDiffSeconds = paceSplitDiff
        };
    }

    public static ZonePaceTrendsResult BuildZonePaceTrends(
        IReadOnlyDictionary<string, RunReviewRecord> reviews,
        DateOnly? maxDate = null,
        int rollingWindow = 5)
    {
        ArgumentNullException.ThrowIfNull(reviews);

        var zoneNums = new[] { 1, 2, 3, 4 };
        var rawPoints = zoneNums.ToDictionary(
            z => string.Create(CultureInfo.InvariantCulture, $"z{z}"),
            _ => new List<ZonePaceTrendPoint>(),
            StringComparer.Ordinal);

        var orderedReviews = reviews.Values
            .Where(r => r.ZonePaceStats is { HasStreams: true }
                && (!maxDate.HasValue || DateOnly.FromDateTime(r.Date) <= maxDate.Value))
            .OrderBy(r => r.Date)
            .ToList();

        foreach (var review in orderedReviews)
        {
            var stats = review.ZonePaceStats!;
            var runDate = DateOnly.FromDateTime(review.Date);

            foreach (var z in zoneNums)
            {
                var zData = stats.Zones.Values.FirstOrDefault(e => e.Zone == z);
                if (zData is null || !zData.ValidForTrend || zData.GapPaceSecondsPerKm <= 0.0)
                {
                    continue;
                }

                var key = string.Create(CultureInfo.InvariantCulture, $"z{z}");
                rawPoints[key].Add(new ZonePaceTrendPoint(
                    review.Id,
                    runDate,
                    review.Name,
                    zData.GapPaceSecondsPerKm,
                    zData.GapPaceText,
                    zData.RawPaceSecondsPerKm,
                    zData.RawPaceText,
                    zData.AverageHeartRate,
                    zData.RunningSeconds,
                    zData.GapPaceSecondsPerKm));
            }
        }

        var seriesOut = new Dictionary<string, IReadOnlyList<ZonePaceTrendPoint>>(StringComparer.Ordinal);
        var summaryOut = new Dictionary<string, ZonePaceTrendSummary>(StringComparer.Ordinal);

        foreach (var (key, pts) in rawPoints)
        {
            var windowSize = Math.Max(1, rollingWindow);
            for (var idx = 0; idx < pts.Count; idx++)
            {
                var start = Math.Max(0, idx - windowSize + 1);
                var count = idx - start + 1;
                var avg = pts.Skip(start).Take(count).Average(p => p.GapPaceSecondsPerKm);
                pts[idx] = pts[idx] with
                {
                    RollingGapPaceSecondsPerKm = Math.Round(avg, 1, MidpointRounding.ToEven)
                };
            }

            seriesOut[key] = pts;

            if (pts.Count >= 2)
            {
                var k = Math.Min(3, Math.Max(1, pts.Count / 3));
                var earlyAvg = pts.Take(k).Average(p => p.GapPaceSecondsPerKm);
                var recentAvg = pts.Skip(pts.Count - k).Average(p => p.GapPaceSecondsPerKm);
                var delta = Math.Round(recentAvg - earlyAvg, 1, MidpointRounding.ToEven);
                summaryOut[key] = new ZonePaceTrendSummary(
                    pts.Count,
                    Math.Round(earlyAvg, 1, MidpointRounding.ToEven),
                    FormatPaceSeconds(earlyAvg),
                    Math.Round(recentAvg, 1, MidpointRounding.ToEven),
                    FormatPaceSeconds(recentAvg),
                    delta);
            }
            else if (pts.Count == 1)
            {
                summaryOut[key] = new ZonePaceTrendSummary(
                    1,
                    pts[0].GapPaceSecondsPerKm,
                    pts[0].GapPaceText,
                    pts[0].GapPaceSecondsPerKm,
                    pts[0].GapPaceText,
                    0.0);
            }
            else
            {
                summaryOut[key] = new ZonePaceTrendSummary(0, null, null, null, null, null);
            }
        }

        return new ZonePaceTrendsResult(seriesOut, summaryOut);
    }

    private static int ClassifyHrZone(double hr, IReadOnlyList<HrZoneBound> bounds)
    {
        foreach (var bound in bounds)
        {
            if (hr >= bound.MinHr && hr <= bound.MaxHr)
            {
                return bound.Zone;
            }
        }

        return hr < bounds[0].MinHr ? bounds[0].Zone : bounds[^1].Zone;
    }

    private static ZonePaceStats EmptyStats(int minZoneSeconds) => new()
    {
        HasStreams = false,
        MinZoneSeconds = minZoneSeconds,
        TotalRunningSeconds = 0,
        WalkingSecondsFiltered = 0,
        Zones = new Dictionary<string, ZonePaceEntry>(StringComparer.Ordinal)
    };

    private sealed class ZoneAccumulator
    {
        public double Seconds { get; set; }
        public double HrSum { get; set; }
        public double RawVSum { get; set; }
        public double GapVSum { get; set; }
    }
}
