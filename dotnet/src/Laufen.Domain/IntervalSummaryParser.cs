using System.Globalization;
using System.Text.RegularExpressions;

namespace Laufen.Domain;

/// <summary>
/// Cleanly parsed interval summary segment extracted from Intervals.icu / review interval strings.
/// </summary>
public sealed record ParsedIntervalSegment
{
    public required string Raw { get; init; }
    public required int Reps { get; init; }
    public required int DurationSeconds { get; init; }
    public double? AverageHeartRate { get; init; }
    public int? Zone { get; init; }
    public required string Role { get; init; }
}

/// <summary>
/// Parses raw interval strings (e.g., <c>"3x 4m 162bpm"</c>, <c>"4x 7m40s 150bpm"</c>) without Python's seconds truncation bug.
/// </summary>
public static partial class IntervalSummaryParser
{
    [GeneratedRegex(
        @"^\s*(?<reps>\d+)x\s+(?:(?<mins>\d+)m(?:(?<secs>\d+)s)?|(?<onlysecs>\d+)s)\s+(?<hr>\d+(?:\.\d+)?)bpm\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntervalPattern();

    public static ParsedIntervalSegment? Parse(string? raw, HeartRateProfile? heartRateProfile = null)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        var match = IntervalPattern().Match(trimmed);
        if (!match.Success)
        {
            return null;
        }

        if (!int.TryParse(match.Groups["reps"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var reps)
            || reps <= 0)
        {
            return null;
        }

        var durationSeconds = 0;
        if (match.Groups["onlysecs"].Success)
        {
            durationSeconds = int.Parse(match.Groups["onlysecs"].Value, CultureInfo.InvariantCulture);
        }
        else
        {
            if (match.Groups["mins"].Success)
            {
                durationSeconds += int.Parse(match.Groups["mins"].Value, CultureInfo.InvariantCulture) * 60;
            }

            if (match.Groups["secs"].Success)
            {
                durationSeconds += int.Parse(match.Groups["secs"].Value, CultureInfo.InvariantCulture);
            }
        }

        if (durationSeconds <= 0)
        {
            return null;
        }

        var avgHr = double.Parse(match.Groups["hr"].Value, CultureInfo.InvariantCulture);
        var zone = ResolveZone(avgHr, heartRateProfile);
        var role = ResolveRole(zone);

        return new ParsedIntervalSegment
        {
            Raw = trimmed,
            Reps = reps,
            DurationSeconds = durationSeconds,
            AverageHeartRate = avgHr,
            Zone = zone,
            Role = role
        };
    }

    public static IReadOnlyList<ParsedIntervalSegment> ParseAll(
        IEnumerable<string>? rawIntervals,
        HeartRateProfile? heartRateProfile = null)
    {
        if (rawIntervals is null)
        {
            return [];
        }

        var segments = new List<ParsedIntervalSegment>();
        foreach (var raw in rawIntervals)
        {
            var parsed = Parse(raw, heartRateProfile);
            if (parsed is not null)
            {
                segments.Add(parsed);
            }
        }

        return segments;
    }

    private static int? ResolveZone(double heartRate, HeartRateProfile? profile)
    {
        if (profile is null || profile.Zones.Count == 0)
        {
            return null;
        }

        var orderedZones = profile.Zones.Values
            .OrderBy(z => z.Zone)
            .ToList();

        foreach (var zone in orderedZones)
        {
            if (heartRate >= zone.Min && heartRate <= zone.Max)
            {
                return zone.Zone;
            }
        }

        return heartRate < orderedZones[0].Min
            ? orderedZones[0].Zone
            : orderedZones[^1].Zone;
    }

    private static string ResolveRole(int? zone) => zone switch
    {
        1 => "recovery",
        2 => "aerobic",
        3 => "tempo",
        >= 4 => "quality",
        _ => "unknown"
    };
}
