using System.Globalization;

namespace Laufen.Domain;

/// <summary>
/// Pure deterministic 4-stage post-run evaluator conforming to <see cref="RunReviewRecord"/> (Schema v2)
/// and <c>SYSTEM_SPEC.md</c> Coaching DNA without Python's interval truncation or dict-key HR lookup bugs.
/// </summary>
public static class PostRunReviewCalculator
{
    private static readonly string[] DefaultZoneNames =
    [
        "Zone 1 (Recovery)",
        "Zone 2 (Aerobic)",
        "Zone 3 (Tempo)",
        "Zone 4 (Threshold/Schwelle)",
        "Zone 5 (VO2max)",
        "Zone 6 (Anaerobic)",
        "Zone 7 (Neuromuscular)"
    ];

    private static readonly string[] QualityKeywords =
        ["schwelle", "threshold", "tempo", "intervall", "interval", "speed", "fartlek", "fahrtspiel"];

    private static readonly string[] BaseKeywords =
        ["easy", "grundlage", "base", "long run", "anker", "rekom", "recovery", "wiedereinstieg", "comeback"];

    private static readonly string[] ComplaintKeywords =
        ["krampf", "krämpf", "schmerz", "zerrung", "verletz", "abgebrochen", "beschwerde", "verhärtung", "verspannt", "wade", "rücken", "knie", "achilles", "krank", "infekt", "erkält", "fieber", "cramp", "calf", "spasm", "tightness"];

    public static RunReviewRecord EvaluateRun(
        IntervalsActivityEntry activity,
        AthleteProfile profile,
        ActivityStreamData? stream = null,
        CalendarEventEntry? pairedCalendarEvent = null,
        CoachHistoryEntry? openCoachCycle = null,
        DateTimeOffset? evaluatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(profile);

        var runDate = DateOnly.FromDateTime(activity.StartDateLocal);
        var movingTimeSeconds = Math.Max(0, activity.MovingTime ?? 0);
        var distanceMeters = Math.Max(0.0, activity.Distance ?? 0.0);
        var distanceKm = Math.Round(distanceMeters / 1000.0, 2, MidpointRounding.ToEven);
        var paceSecondsPerKm = distanceKm > 0.0 ? movingTimeSeconds / distanceKm : 0.0;

        var avgHr = Math.Round((double)(activity.AverageHeartrate ?? 0), 1, MidpointRounding.ToEven);
        var maxHr = activity.MaxHeartrate ?? 0;

        var rawCadence = activity.AverageCadence ?? 0.0;
        var cadenceSpm = rawCadence > 0.0
            ? Math.Round(rawCadence <= 100.0 ? rawCadence * 2.0 : rawCadence, 1, MidpointRounding.ToEven)
            : 0.0;

        var elevationGain = Math.Round(activity.TotalElevationGain ?? 0.0, 1, MidpointRounding.ToEven);
        var trainingLoad = Math.Round((double)(activity.IcuTrainingLoad ?? 0), 1, MidpointRounding.ToEven);

        var icuCtl = activity.IcuCtl;
        var icuAtl = activity.IcuAtl;
        double? tsbAtRun = icuCtl.HasValue && icuAtl.HasValue
            ? Math.Round(icuCtl.Value - icuAtl.Value, 1, MidpointRounding.ToEven)
            : null;

        double? aerobicEf = null;
        if (avgHr > 0.0 && movingTimeSeconds > 0 && distanceMeters > 0.0)
        {
            var speedMetersPerMin = distanceMeters / (movingTimeSeconds / 60.0);
            aerobicEf = Math.Round(speedMetersPerMin / avgHr, 3, MidpointRounding.ToEven);
        }

        var zoneTimes = activity.IcuHrZoneTimes ?? [];
        var totalZoneSeconds = zoneTimes.Sum();
        var zoneBreakdown = new Dictionary<string, ZoneBreakdownEntry>(StringComparer.Ordinal);
        var z1Z2Seconds = 0;

        for (var idx = 0; idx < zoneTimes.Count; idx++)
        {
            var sec = zoneTimes[idx];
            var pct = totalZoneSeconds > 0
                ? Math.Round((sec / (double)totalZoneSeconds) * 100.0, 1, MidpointRounding.ToEven)
                : 0.0;
            var zKey = string.Create(CultureInfo.InvariantCulture, $"z{idx + 1}");
            var zName = idx < DefaultZoneNames.Length
                ? DefaultZoneNames[idx]
                : string.Create(CultureInfo.InvariantCulture, $"Zone {idx + 1}");

            zoneBreakdown[zKey] = new ZoneBreakdownEntry
            {
                Name = zName,
                Seconds = sec,
                Percentage = pct,
                TimeText = FormatSecondsMinSec(sec)
            };

            if (idx is 0 or 1)
            {
                z1Z2Seconds += sec;
            }
        }

        var z1Z2Pct = totalZoneSeconds > 0
            ? Math.Round((z1Z2Seconds / (double)totalZoneSeconds) * 100.0, 1, MidpointRounding.ToEven)
            : 0.0;

        var z2Zone = profile.HeartRate.Zones.Values.FirstOrDefault(z => z.Zone == 2);
        var z4Zone = profile.HeartRate.Zones.Values.FirstOrDefault(z => z.Zone == 4);
        var lthr = profile.HeartRate.Lthr > 0 ? profile.HeartRate.Lthr : 178;
        var z2Max = z2Zone?.Max ?? (int)Math.Round(lthr * 0.89, MidpointRounding.AwayFromZero);
        var z4Min = z4Zone?.Min ?? (lthr - 8);
        var z4Max = z4Zone?.Max ?? (lthr - 1);

        var intensityClass = ClassifyRunIntensity(z1Z2Pct, zoneBreakdown, avgHr, z2Max, lthr);
        var rawIntervalStrings = activity.IntervalSummary ?? [];
        var parsedIntervals = ParseIntervalsCleanly(rawIntervalStrings, profile.HeartRate);

        var zonePaceStats = stream is not null
            ? ZonePaceCalculator.ComputeZonePaceStats(stream, profile.HeartRate, activityHrZones: activity.IcuHrZones)
            : null;

        var runName = string.IsNullOrWhiteSpace(activity.Name) ? "Lauf" : activity.Name.Trim();
        var athleteNote = string.IsNullOrWhiteSpace(activity.Description) ? null : activity.Description.Trim();
        var hasComplaint = !string.IsNullOrEmpty(athleteNote) && ContainsAny(athleteNote, ComplaintKeywords);

        var z3Sec = zoneBreakdown.TryGetValue("z3", out var z3Entry) ? z3Entry.Seconds : 0;
        var z4Sec = zoneBreakdown.TryGetValue("z4", out var z4Entry) ? z4Entry.Seconds : 0;
        var z1Str = zoneBreakdown.TryGetValue("z1", out var z1Entry) ? z1Entry.TimeText : "0s";
        var z2Str = zoneBreakdown.TryGetValue("z2", out var z2Entry) ? z2Entry.TimeText : "0s";
        var z3Str = zoneBreakdown.TryGetValue("z3", out var z3E) ? z3E.TimeText : "0s";
        var z4Str = zoneBreakdown.TryGetValue("z4", out var z4E) ? z4E.TimeText : "0s";

        var stage2 = BuildStage2Compliance(
            activity,
            runDate,
            runName,
            intensityClass,
            z1Z2Pct,
            z3Sec,
            z4Sec,
            avgHr,
            z2Max,
            z4Min,
            z4Max,
            profile,
            pairedCalendarEvent,
            openCoachCycle,
            athleteNote,
            hasComplaint);

        var stage1Summary = BuildStage1Summary(
            avgHr,
            maxHr,
            z1Z2Pct,
            z3Str,
            z4Str,
            trainingLoad,
            zonePaceStats);

        var stage1 = new Stage1Physiology
        {
            Headline = string.Create(
                CultureInfo.InvariantCulture,
                $"{FormatHeadlineIntensity(intensityClass)}: {distanceKm:F2} km in {FormatDuration(movingTimeSeconds)}"),
            EfficiencyFactor = aerobicEf,
            TsbAtRun = tsbAtRun,
            ParsedIntervals = parsedIntervals,
            Summary = stage1Summary
        };

        var stage3 = BuildStage3Optimizations(
            stage2.IntendedType,
            z3Sec,
            z4Sec,
            cadenceSpm,
            maxHr,
            parsedIntervals,
            zonePaceStats,
            athleteNote,
            hasComplaint);

        var stage4 = BuildStage4NextRun(
            runDate,
            movingTimeSeconds,
            intensityClass,
            trainingLoad,
            tsbAtRun,
            icuCtl,
            icuAtl,
            z2Max,
            profile,
            stage2,
            stage3,
            athleteNote,
            hasComplaint);

        var (rating, ratingLabel, overviewSummary, coachTip) = BuildTopLevelEvaluation(
            intensityClass,
            stage2.Status,
            z1Z2Pct,
            avgHr,
            maxHr,
            z2Max,
            lthr,
            z1Str,
            z2Str,
            z3Str,
            z4Str,
            trainingLoad);

        var cadenceFeedback = BuildCadenceFeedback(cadenceSpm);

        return new RunReviewRecord
        {
            Id = activity.Id,
            Date = activity.StartDateLocal,
            Name = runName,
            DistanceKm = distanceKm,
            MovingTimeSeconds = movingTimeSeconds,
            MovingTimeText = FormatDuration(movingTimeSeconds),
            PaceText = ZonePaceCalculator.FormatPaceSeconds(paceSecondsPerKm),
            AverageHeartRate = avgHr,
            MaxHeartRate = maxHr,
            CadenceSpm = cadenceSpm,
            ElevationGainMeters = elevationGain,
            TrainingLoad = trainingLoad,
            IcuCtl = icuCtl,
            IcuAtl = icuAtl,
            TsbAtRun = tsbAtRun,
            AerobicEf = aerobicEf,
            Z1Z2Percentage = z1Z2Pct,
            ZoneBreakdown = zoneBreakdown,
            Intervals = rawIntervalStrings,
            IntensityClass = intensityClass,
            AthleteNote = athleteNote,
            AnalyzedAt = evaluatedAtUtc ?? DateTimeOffset.UtcNow,
            ZonePaceStats = zonePaceStats,
            Review = new RunReviewEvaluation
            {
                SchemaVersion = 2,
                Rating = rating,
                RatingLabel = ratingLabel,
                Summary = overviewSummary,
                CoachTip = coachTip,
                CadenceFeedback = cadenceFeedback,
                AthleteNote = athleteNote,
                ZoneDistributionText = $"Z1: {z1Str} | Z2: {z2Str} | Z3: {z3Str} | Z4: {z4Str}",
                Stages = new RunReviewStages
                {
                    Stage1Physiology = stage1,
                    Stage2Compliance = stage2,
                    Stage3Optimizations = stage3,
                    Stage4NextRun = stage4
                }
            }
        };
    }

    private static string ClassifyRunIntensity(
        double z1Z2Pct,
        Dictionary<string, ZoneBreakdownEntry> zoneBreakdown,
        double avgHr,
        int z2Max,
        int lthr)
    {
        var z3Pct = zoneBreakdown.TryGetValue("z3", out var z3) ? z3.Percentage : 0.0;
        var z4Pct = zoneBreakdown.TryGetValue("z4", out var z4) ? z4.Percentage : 0.0;
        var z5Pct = zoneBreakdown.TryGetValue("z5", out var z5) ? z5.Percentage : 0.0;

        var z3Seconds = zoneBreakdown.TryGetValue("z3", out var z3S) ? z3S.Seconds : 0;
        var z4Seconds = zoneBreakdown.TryGetValue("z4", out var z4S) ? z4S.Seconds : 0;
        var z5Seconds = zoneBreakdown.TryGetValue("z5", out var z5S) ? z5S.Seconds : 0;

        if (z5Pct >= 10.0 || (z4Seconds + z5Seconds >= 900 && avgHr >= lthr - 5))
        {
            return "schwellen_intervall";
        }

        if (z4Seconds >= 720 || (z4Pct >= 25.0 && z3Seconds + z4Seconds >= 1200))
        {
            return "schwellenlauf";
        }

        if (z1Z2Pct >= 80.0 && avgHr <= z2Max)
        {
            return "aerobe_basis";
        }

        if (z1Z2Pct >= 60.0)
        {
            return "ueberwiegend_aerob";
        }

        if (z3Pct >= 35.0 && z4Seconds < 720)
        {
            return "tempo_z3";
        }

        return "gemischte_intensitaet";
    }

    private static List<ParsedReviewInterval> ParseIntervalsCleanly(
        IReadOnlyList<string> rawIntervals,
        HeartRateProfile heartRateProfile)
    {
        var result = new List<ParsedReviewInterval>(rawIntervals.Count);
        foreach (var raw in rawIntervals)
        {
            var parsed = IntervalSummaryParser.Parse(raw, heartRateProfile);
            if (parsed is not null)
            {
                result.Add(new ParsedReviewInterval
                {
                    Raw = parsed.Raw,
                    Reps = parsed.Reps,
                    DurationSeconds = parsed.DurationSeconds,
                    DurationText = FormatSecondsMinSec(parsed.DurationSeconds),
                    AverageHeartRate = parsed.AverageHeartRate ?? 0.0,
                    Role = parsed.Role
                });
            }
            else if (!string.IsNullOrWhiteSpace(raw))
            {
                result.Add(new ParsedReviewInterval
                {
                    Raw = raw.Trim(),
                    Reps = 1,
                    DurationSeconds = 0,
                    DurationText = raw.Trim(),
                    AverageHeartRate = 0.0,
                    Role = "info"
                });
            }
        }

        return result;
    }

    private static Stage2Compliance BuildStage2Compliance(
        IntervalsActivityEntry activity,
        DateOnly runDate,
        string runName,
        string intensityClass,
        double z1Z2Pct,
        int z3Seconds,
        int z4Seconds,
        double avgHr,
        int z2Max,
        int z4Min,
        int z4Max,
        AthleteProfile profile,
        CalendarEventEntry? pairedCalendarEvent,
        CoachHistoryEntry? openCoachCycle,
        string? athleteNote,
        bool hasComplaint)
    {
        var weekday = runDate.ToString("dddd", CultureInfo.InvariantCulture).ToLowerInvariant();
        var slots = profile.WeeklySlots ?? [];
        var matchedSlot = slots.FirstOrDefault(s =>
            s.AllowedDays.Any(d => string.Equals(d, weekday, StringComparison.OrdinalIgnoreCase)));
        var slotId = matchedSlot?.Slot ?? 2;
        var slotRole = matchedSlot?.Role ?? "flexible";

        string intendedType;
        string plannedSlot;
        string intentSource;

        if (pairedCalendarEvent is not null && !string.IsNullOrWhiteSpace(pairedCalendarEvent.Name))
        {
            var calName = pairedCalendarEvent.Name;
            var calIsQuality = ContainsAny(calName, QualityKeywords);
            intendedType = calIsQuality
                ? (calName.Contains("interval", StringComparison.OrdinalIgnoreCase) && !calName.Contains("schwell", StringComparison.OrdinalIgnoreCase)
                    ? "intervals"
                    : "threshold")
                : "easy_run";
            plannedSlot = calName.Trim();
            intentSource = "calendar_event";
        }
        else if (openCoachCycle is not null && openCoachCycle.Date <= runDate && !string.IsNullOrWhiteSpace(openCoachCycle.TargetType))
        {
            intendedType = openCoachCycle.TargetType.Contains("tempo", StringComparison.OrdinalIgnoreCase)
                || openCoachCycle.TargetType.Contains("threshold", StringComparison.OrdinalIgnoreCase)
                || openCoachCycle.TargetType.Contains("interval", StringComparison.OrdinalIgnoreCase)
                ? "threshold"
                : "easy_run";
            plannedSlot = string.Create(CultureInfo.InvariantCulture, $"Slot {slotId} (Coach-Vorgabe)");
            intentSource = "coach_history";
        }
        else if (ContainsAny(runName, QualityKeywords))
        {
            intendedType = runName.Contains("schwell", StringComparison.OrdinalIgnoreCase)
                || runName.Contains("threshold", StringComparison.OrdinalIgnoreCase)
                || runName.Contains("tempo", StringComparison.OrdinalIgnoreCase)
                ? "threshold"
                : "intervals";
            plannedSlot = string.Create(CultureInfo.InvariantCulture, $"Slot {slotId} (Qualitätsreiz)");
            intentSource = "name";
        }
        else if (ContainsAny(runName, BaseKeywords))
        {
            intendedType = "easy_run";
            plannedSlot = string.Create(CultureInfo.InvariantCulture, $"Slot {slotId} (Aerobe Basis)");
            intentSource = "name";
        }
        else if (string.Equals(slotRole, "base_anchor", StringComparison.OrdinalIgnoreCase))
        {
            intendedType = "easy_run";
            plannedSlot = string.Create(CultureInfo.InvariantCulture, $"Slot {slotId} (Wochenende – Aerober Anker)");
            intentSource = "slot";
        }
        else if (string.Equals(slotRole, "quality", StringComparison.OrdinalIgnoreCase))
        {
            intendedType = "tempo_run";
            plannedSlot = string.Create(CultureInfo.InvariantCulture, $"Slot {slotId} (Wochenanfang – Tempo/Intervalle)");
            intentSource = "slot";
        }
        else if (z4Seconds >= 600 || intensityClass is "schwellenlauf" or "schwellen_intervall")
        {
            intendedType = "threshold";
            plannedSlot = string.Create(CultureInfo.InvariantCulture, $"Slot {slotId} (Wochenmitte – Schwellen-Option)");
            intentSource = "slot+physiology";
        }
        else
        {
            intendedType = "easy_run";
            plannedSlot = string.Create(CultureInfo.InvariantCulture, $"Slot {slotId} (Wochenmitte – Aerobe Basis)");
            intentSource = "slot";
        }

        string status;
        string statusLabel;
        string targetComparison;

        if (intendedType is "threshold" or "tempo_run" or "intervals")
        {
            if (z4Seconds >= 720 || intensityClass is "schwellenlauf" or "schwellen_intervall")
            {
                status = "erfüllt";
                statusLabel = "Erfüllt";
                targetComparison = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{FormatSecondsMinSec(z4Seconds)} in Zone 4 (Ziel: >= 12m @ {z4Min}-{z4Max} bpm). {FormatSecondsMinSec(z3Seconds)} in Zone 3 durch Anlauf-Lag und Pausen.");
            }
            else if (z4Seconds >= 360)
            {
                status = "teilweise";
                statusLabel = "Teilweise erfüllt";
                targetComparison = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{FormatSecondsMinSec(z4Seconds)} in Zone 4 (Ziel: >= 12m). Schwellenreiz vorzeitig beendet oder gedrosselt.");
            }
            else
            {
                status = "abweichend";
                statusLabel = "Abweichend";
                targetComparison = "Qualitätsreiz zugunsten einer lockeren Belastung reduziert.";
            }
        }
        else
        {
            if (z1Z2Pct >= 80.0 && avgHr <= z2Max)
            {
                status = "erfüllt";
                statusLabel = "Erfüllt";
                targetComparison = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{z1Z2Pct:F1}% in Zone 1/2 bei Ø {(int)Math.Round(avgHr)} bpm (Ziel: >= 80% <= {z2Max} bpm). Aerobes Fundament optimal gepflegt.");
            }
            else if (z1Z2Pct >= 60.0 || avgHr <= z2Max + 3)
            {
                status = "teilweise";
                statusLabel = "Teilweise erfüllt";
                targetComparison = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{z1Z2Pct:F1}% in Zone 1/2 mit {FormatSecondsMinSec(z3Seconds)} in Zone 3 (Drift an Steigungen/im Schlussteil).");
            }
            else
            {
                status = "intensiver_als_geplant";
                statusLabel = "Intensiver als geplant";
                targetComparison = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Lauf lag bei {z1Z2Pct:F1}% Z1/Z2 und Ø {(int)Math.Round(avgHr)} bpm über der aeroben Vorgabe.");
            }
        }

        string? complianceNotes = null;
        if (hasComplaint && !string.IsNullOrEmpty(athleteNote))
        {
            complianceNotes = $"Einheit kontrolliert angepasst wegen gemeldeter Beschwerden ('{athleteNote}'). Verletzungsschutz hatte Vorrang.";
            targetComparison = $"Wegen Beschwerden angepasst ('{athleteNote}'). Schutzreaktion zur Entlastung.";
            if (status is "teilweise" or "abweichend")
            {
                status = "angepasst";
                statusLabel = "Angepasst (Beschwerden)";
            }
        }

        return new Stage2Compliance
        {
            Status = status,
            StatusLabel = statusLabel,
            IntendedType = intendedType,
            IntentSource = intentSource,
            PlannedSlot = plannedSlot,
            IntervalsIcuCompliance = activity.Compliance,
            TargetComparison = targetComparison,
            ComplianceNotes = complianceNotes
        };
    }

    private static string BuildStage1Summary(
        double avgHr,
        int maxHr,
        double z1Z2Pct,
        string z3Str,
        string z4Str,
        double trainingLoad,
        ZonePaceStats? zonePaceStats)
    {
        var zpNote = string.Empty;
        var splitNote = string.Empty;

        if (zonePaceStats is { HasStreams: true })
        {
            var parts = new List<string>();
            for (var z = 1; z <= 4; z++)
            {
                var entry = zonePaceStats.Zones.Values.FirstOrDefault(e => e.Zone == z);
                if (entry is not null && entry.ValidForTrend && !string.IsNullOrWhiteSpace(entry.GapPaceText))
                {
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"Z{z} {entry.GapPaceText}"));
                }
            }

            if (parts.Count > 0)
            {
                zpNote = $" GAP-Pace/Zone: {string.Join(", ", parts)}.";
            }

            if (zonePaceStats.FirstHalfGapPaceSecondsPerKm.HasValue && zonePaceStats.SecondHalfGapPaceSecondsPerKm.HasValue)
            {
                var p1 = ZonePaceCalculator.FormatPaceSeconds(zonePaceStats.FirstHalfGapPaceSecondsPerKm);
                var p2 = ZonePaceCalculator.FormatPaceSeconds(zonePaceStats.SecondHalfGapPaceSecondsPerKm);
                splitNote = $" Split 1./2. Hälfte (GAP): {p1} -> {p2}.";
            }
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Herzfrequenz lag bei Ø {(int)Math.Round(avgHr)} bpm (Max: {maxHr} bpm). Zonenverteilung: Z1/Z2 {z1Z2Pct:F1}%, Z3 {z3Str}, Z4 {z4Str}. Training Load: {(int)Math.Round(trainingLoad)}.{zpNote}{splitNote}");
    }

    private static List<Stage3OptimizationItem> BuildStage3Optimizations(
        string intendedType,
        int z3Seconds,
        int z4Seconds,
        double cadenceSpm,
        int maxHr,
        IReadOnlyList<ParsedReviewInterval> parsedIntervals,
        ZonePaceStats? zonePaceStats,
        string? athleteNote,
        bool hasComplaint)
    {
        var list = new List<Stage3OptimizationItem>();

        if (hasComplaint && !string.IsNullOrEmpty(athleteNote))
        {
            list.Add(new Stage3OptimizationItem
            {
                Category = "recovery",
                Title = "Beschwerde-Schutz & Regeneration",
                Text = $"Gemeldete Beschwerden beachten ('{athleteNote}'): Mindestens 48 Stunden keine intensiven Reize, Fokus auf Mobilität und Erholung.",
                ActionableAdvice = "48h auf Tempo verzichten und erst bei Beschwerdefreiheit locker in Z1/Z2 wieder einsteigen."
            });
        }

        if (intendedType is "threshold" or "tempo_run" or "intervals")
        {
            if (maxHr >= 173 && z3Seconds >= 480)
            {
                list.Add(new Stage3OptimizationItem
                {
                    Category = "pacing",
                    Title = "Intervall-Einstieg über Pace/RPE steuern",
                    Text = "Kardialen Anlauf-Lag in den ersten 2 Minuten über gleichmäßigen Rhythmus überbrücken, statt sofort zu überpacen."
                });
            }

            if (z3Seconds >= 600 || parsedIntervals.Any(p => p.Role == "recovery" && p.AverageHeartRate >= 155.0))
            {
                list.Add(new Stage3OptimizationItem
                {
                    Category = "recovery",
                    Title = "Gehpausen zur Pulsabsenkung nutzen",
                    Text = "Bei erhöhtem Pausendurchschnitt 60–90 Sekunden zügiges Gehen einbauen, um den Puls sauber vor dem nächsten Block zu senken."
                });
            }

            if (z3Seconds >= 0.75 * Math.Max(z4Seconds, 1) && z4Seconds >= 600)
            {
                list.Add(new Stage3OptimizationItem
                {
                    Category = "alternative_workout",
                    Title = "Alternative: 5x 4m Micro-Threshold",
                    Text = "Bei deutlichem Z3-Drift ermöglichen kürzere 4m-Schwellenblöcke mit 90s Gehpause stabilere Laktatkontrolle.",
                    CliCommand = "./run plan --type micro_threshold --date tomorrow"
                });
            }
        }
        else
        {
            if (z3Seconds > 300 || (cadenceSpm > 0.0 && cadenceSpm < 155.0))
            {
                list.Add(new Stage3OptimizationItem
                {
                    Category = "cadence",
                    Title = "Kadenzökonomie & Gehpausen an Anstiegen",
                    Text = "Schrittfrequenz durch lockere, kurze Schritte Richtung 158–165 spm halten und an Anstiegen frühzeitig ins zügige Gehen wechseln, statt in Z3 zu driften."
                });
            }

            if (zonePaceStats is { FirstHalfAverageHeartRate: { } h1, SecondHalfAverageHeartRate: { } h2 } && (h2 - h1) >= 6.0)
            {
                list.Add(new Stage3OptimizationItem
                {
                    Category = "cardiac_drift",
                    Title = "Kardialer Drift & Run-Walk",
                    Text = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Herzfrequenz stieg in der 2. Hälfte um {h2 - h1:F1} bpm; kurze Gehpausen ab der Laufmitte stabilisieren die aerobe Effizienz.")
                });
            }
        }

        if (list.Count == 0)
        {
            list.Add(new Stage3OptimizationItem
            {
                Category = "maintenance",
                Title = "Saubere Reizausführung",
                Text = "Die Belastungsdosierung war ausgewogen und ohne Fehlbelastung."
            });
        }

        return list;
    }

    private static Stage4NextRun BuildStage4NextRun(
        DateOnly runDate,
        int movingTimeSeconds,
        string intensityClass,
        double trainingLoad,
        double? tsbAtRun,
        double? ctl,
        double? atl,
        int z2Max,
        AthleteProfile profile,
        Stage2Compliance stage2,
        List<Stage3OptimizationItem> optimizations,
        string? athleteNote,
        bool hasComplaint)
    {
        var isQuality = intensityClass is "schwellenlauf" or "schwellen_intervall" or "tempo_z3"
            || trainingLoad >= 55.0;
        var isLongRun = movingTimeSeconds >= 3600;

        var recoveryHours = hasComplaint || isQuality
            ? 48
            : (isLongRun ? 36 : 24);

        var tsbPart = tsbAtRun.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"TSB {tsbAtRun.Value:+0.0;-0.0;0.0}")
            : "TSB neutral";
        var tsbContext = ctl.HasValue && atl.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"{tsbPart} (CTL {ctl.Value:F1} / ATL {atl.Value:F1})")
            : tsbPart;

        var weekday = runDate.ToString("dddd", CultureInfo.InvariantCulture).ToLowerInvariant();
        var currSlotNum = weekday switch
        {
            "monday" or "tuesday" => 1,
            "wednesday" or "thursday" or "friday" => 2,
            "saturday" or "sunday" => 3,
            _ => 2
        };
        var nextSlotNum = currSlotNum >= 3 ? 1 : currSlotNum + 1;
        var nextSlotCfg = (profile.WeeklySlots ?? []).FirstOrDefault(s => s.Slot == nextSlotNum);
        var nextSlotName = nextSlotCfg?.Name ?? nextSlotNum switch
        {
            1 => "Slot 1 (Wochenanfang)",
            2 => "Slot 2 (Wochenmitte)",
            _ => "Slot 3 (Wochenende)"
        };
        var nextRole = nextSlotCfg?.Role ?? (nextSlotNum == 1 ? "quality" : nextSlotNum == 3 ? "base_anchor" : "flexible");

        var nextWorkoutType = hasComplaint
            ? "recovery_run"
            : (string.Equals(nextRole, "quality", StringComparison.OrdinalIgnoreCase) && !isQuality ? "intervals" : "easy_run");
        var durationMin = hasComplaint ? 30 : (string.Equals(nextRole, "base_anchor", StringComparison.OrdinalIgnoreCase) ? 55 : 45);
        var focus = optimizations[0].Title;

        string directive;
        if (hasComplaint && !string.IsNullOrEmpty(athleteNote))
        {
            directive = string.Create(
                CultureInfo.InvariantCulture,
                $"Wegen gemeldeter Beschwerden ('{athleteNote}') mindestens {recoveryHours}h Erholung einhalten. Kein Tempotraining; nächster Testlauf maximal locker in Z1/Z2.");
        }
        else if (isQuality)
        {
            directive = string.Create(
                CultureInfo.InvariantCulture,
                $"Mindestens 48h Regeneration ohne intensive Spitzen ({tsbContext}). Nächste Einheit ({nextSlotName}) rein aerob <= {z2Max} bpm ohne Pace-Vorgabe.");
        }
        else if (recoveryHours == 36)
        {
            directive = string.Create(
                CultureInfo.InvariantCulture,
                $"Langer Grundlagenlauf abgeschlossen (36h Erholungsfenster). Nächste Einheit: {nextSlotName} entsprechend aktueller Bereitschaft einordnen.");
        }
        else
        {
            directive = string.Create(
                CultureInfo.InvariantCulture,
                $"Erholung verläuft planmäßig (24h Erholungsfenster, {stage2.StatusLabel}). Nächste Einheit: {nextSlotName}.");
        }

        return new Stage4NextRun
        {
            RecoveryHours = recoveryHours,
            TsbContext = tsbContext,
            NextSlot = nextSlotName,
            NextWorkoutType = nextWorkoutType,
            TargetHeartRateMax = z2Max,
            DurationMinutes = durationMin,
            Directive = directive,
            OptimizationFocus = focus
        };
    }

    private static (string Rating, string RatingLabel, string Summary, string CoachTip) BuildTopLevelEvaluation(
        string intensityClass,
        string complianceStatus,
        double z1Z2Pct,
        double avgHr,
        int maxHr,
        int z2Max,
        int lthr,
        string z1Str,
        string z2Str,
        string z3Str,
        string z4Str,
        double trainingLoad)
    {
        if (intensityClass is "schwellenlauf" or "schwellen_intervall")
        {
            var label = string.Equals(complianceStatus, "erfüllt", StringComparison.Ordinal)
                ? "Gezielter Schwellenlauf (Plan erfüllt)"
                : "Schwellen- / Tempolauf";
            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"Intensive Belastung nahe der Laktatschwelle ({lthr} bpm). {z4Str} in Zone 4 bei Ø {(int)Math.Round(avgHr)} bpm (Max: {maxHr} bpm). Training Load: {(int)Math.Round(trainingLoad)}.");
            return ("intensiv", label, summary, "Wichtiger Temporeiz – mindestens 48 Stunden lockere Regeneration einhalten.");
        }

        if (z1Z2Pct >= 85.0)
        {
            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"Starke aerobe Zonendisziplin ({z1Z2Pct:F1}% in Z1/Z2) bei Ø {(int)Math.Round(avgHr)} bpm (<= {z2Max} bpm). Z1: {z1Str}, Z2: {z2Str}.");
            return ("optimal", "Optimaler Grundlagenlauf", summary, "Genau dieser Bereich stärkt Kapillarisierung und aerobe Ökonomie.");
        }

        if (z1Z2Pct >= 60.0 || intensityClass == "ueberwiegend_aerob")
        {
            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"Solider Grundlagenschwerpunkt ({z1Z2Pct:F1}% in Z1/Z2), mit {z3Str} in Zone 3 bei Ø {(int)Math.Round(avgHr)} bpm (Max: {maxHr} bpm).");
            return ("solide", "Überwiegend aerob mit Z3-Drift", summary, "An Steigungen früher in zügiges Gehen wechseln, um Z3-Drift zu begrenzen.");
        }

        var tempoSummary = string.Create(
            CultureInfo.InvariantCulture,
            $"Zügiger Dauerlauf ({z1Z2Pct:F1}% Z1/Z2, Z3 {z3Str}, Z4 {z4Str}) bei Ø {(int)Math.Round(avgHr)} bpm.");
        return ("zuegig", "Zügiger Dauerlauf (Zone 3)", tempoSummary, "Spürbarer Reiz – vor der nächsten Qualitätseinheit 48 Stunden locker einplanen.");
    }

    private static string BuildCadenceFeedback(double cadenceSpm)
    {
        if (cadenceSpm <= 0.0)
        {
            return "Keine Schrittfrequenz-Sensordaten vorhanden.";
        }

        if (cadenceSpm < 155.0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Ø {(int)Math.Round(cadenceSpm)} spm ist leicht niedrig. Schrittfrequenz behutsam Richtung 158–165 spm steigern.");
        }

        if (cadenceSpm <= 172.0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Ø {(int)Math.Round(cadenceSpm)} spm liegt im ökonomischen Bereich.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Ø {(int)Math.Round(cadenceSpm)} spm zeigt eine hohe, aktive Schrittfrequenz.");
    }

    private static string FormatHeadlineIntensity(string intensityClass) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(intensityClass.Replace('_', ' '));

    private static string FormatDuration(int seconds)
    {
        var hrs = seconds / 3600;
        var mins = (seconds % 3600) / 60;
        var secs = seconds % 60;
        return hrs > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hrs:D2}:{mins:D2}:{secs:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"{mins:D2}:{secs:D2}");
    }

    private static string FormatSecondsMinSec(int seconds)
    {
        if (seconds <= 0)
        {
            return "0s";
        }

        var mins = seconds / 60;
        var secs = seconds % 60;
        if (mins > 0)
        {
            return secs > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{mins}m {secs:D2}s")
                : string.Create(CultureInfo.InvariantCulture, $"{mins}m");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{secs}s");
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> keywords)
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
}
