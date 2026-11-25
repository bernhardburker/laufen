using System.Globalization;
using System.Text.Json;
using Laufen.Domain;
using Laufen.Infrastructure;

namespace Laufen.Cli;

/// <summary>
/// Central CLI entrypoint for Laufen.Cli supporting health checks, athlete listing, reports, and planning markers.
/// </summary>
public static class LaufenCliRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static int Run(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter? stderr = null,
        string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        stderr ??= TextWriter.Null;

        if (args.Count == 0 || (args.Count == 1 && string.Equals(args[0], "--health", StringComparison.Ordinal)))
        {
            return CliHealthRunner.Run(args, stdout);
        }

        if (!TryParseArgs(args, out var parsed, out var parseError))
        {
            stderr.WriteLine(parseError);
            return 1;
        }

        if (parsed.Command is "help" or "-h" or "--help")
        {
            WriteHelp(stdout);
            return 0;
        }

        try
        {
            if (string.Equals(parsed.Command, "validate-workout", StringComparison.Ordinal))
            {
                return ExecuteValidateWorkout(parsed, stdout, stderr, workingDirectory);
            }

            var layout = string.IsNullOrWhiteSpace(parsed.WorkspaceRoot)
                ? WorkspaceLayout.DiscoverFrom(workingDirectory)
                : WorkspaceLayout.FromRoot(parsed.WorkspaceRoot);

            return parsed.Command switch
            {
                "athletes" => ExecuteAthletes(layout, parsed.AsJson, stdout),
                "report" => ExecuteReport(layout, parsed, stdout),
                "markers" => ExecuteMarkers(layout, parsed, stdout),
                "dossier" => ExecuteDossier(layout, parsed, stdout),
                _ => UnknownCommand(parsed.Command, stderr)
            };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or DirectoryNotFoundException or FileNotFoundException or FormatException or JsonException)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int UnknownCommand(string command, TextWriter stderr)
    {
        stderr.WriteLine($"Unsupported command '{command}'. Run 'Laufen.Cli help' for available commands.");
        return 1;
    }

    private static int ExecuteAthletes(WorkspaceLayout layout, bool asJson, TextWriter stdout)
    {
        var registry = AthleteDataRepository.LoadRegistry(layout);

        if (asJson)
        {
            var payload = new
            {
                defaultAthlete = registry.DefaultAthlete,
                athletes = registry.Athletes.Select(kvp => new
                {
                    slug = kvp.Key,
                    displayName = kvp.Value.DisplayName,
                    intervalsId = kvp.Value.IntervalsId,
                    active = kvp.Value.Active,
                    dashboardSlug = kvp.Value.DashboardSlug
                })
            };
            stdout.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
            return 0;
        }

        stdout.WriteLine($"Default athlete: {registry.DefaultAthlete}");
        foreach (var (slug, entry) in registry.Athletes.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var status = entry.Active ? "active" : "inactive";
            stdout.WriteLine($"- {slug,-12} {entry.DisplayName,-16} ({entry.IntervalsId}, {status})");
        }

        return 0;
    }

    private static int ExecuteReport(WorkspaceLayout layout, ParsedCliArgs parsed, TextWriter stdout)
    {
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var slugs = AthleteDataRepository.ResolveTargetSlugs(registry, parsed.AthleteSlug, parsed.AllAthletes);
        var results = new List<object>(slugs.Count);

        foreach (var slug in slugs)
        {
            var data = AthleteDataRepository.LoadAthleteData(layout, registry, slug);
            var snapshot = PhysiologicalCalculator.CreateReportSnapshot(
                data.Profile,
                data.Activities,
                data.WellnessRecords,
                parsed.AsOfDate,
                parsed.RecentLimit,
                parsed.MaxWeeks);

            if (parsed.AsJson)
            {
                results.Add(new { slug, report = snapshot });
            }
            else
            {
                WriteReportText(stdout, slug, snapshot);
            }
        }

        if (parsed.AsJson)
        {
            object payload = parsed.AllAthletes
                ? new { athletes = results }
                : results[0];
            stdout.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        }

        return 0;
    }

    private static int ExecuteMarkers(WorkspaceLayout layout, ParsedCliArgs parsed, TextWriter stdout)
    {
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var slugs = AthleteDataRepository.ResolveTargetSlugs(registry, parsed.AthleteSlug, parsed.AllAthletes);
        var results = new List<object>(slugs.Count);

        foreach (var slug in slugs)
        {
            var data = AthleteDataRepository.LoadAthleteData(layout, registry, slug);
            var refDate = parsed.RefDate ?? DateOnly.FromDateTime(DateTime.Today);
            var calendarCtx = PlanningMarkersCalculator.GetCalendarContext(data.CalendarEvents, refDate);
            var snapshot = PlanningMarkersCalculator.ExtractPlanningMarkers(
                data.Profile,
                data.Activities,
                data.WellnessRecords,
                data.CoachHistory,
                calendarCtx,
                refDate,
                parsed.TargetDate);

            if (parsed.AsJson)
            {
                results.Add(new { slug, markers = snapshot });
            }
            else
            {
                WriteMarkersText(stdout, slug, snapshot);
            }
        }

        if (parsed.AsJson)
        {
            object payload = parsed.AllAthletes
                ? new { athletes = results }
                : results[0];
            stdout.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        }

        return 0;
    }

    private static int ExecuteDossier(WorkspaceLayout layout, ParsedCliArgs parsed, TextWriter stdout)
    {
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var slugs = AthleteDataRepository.ResolveTargetSlugs(registry, parsed.AthleteSlug, parsed.AllAthletes);
        var results = new List<object>(slugs.Count);

        foreach (var slug in slugs)
        {
            var data = AthleteDataRepository.LoadAthleteData(layout, registry, slug);
            var refDate = parsed.RefDate ?? DateOnly.FromDateTime(DateTime.Today);
            var dossier = CoachIntelligenceDossierBuilder.Build(
                data.Profile,
                data.Activities,
                data.WellnessRecords,
                data.RunReviews,
                data.CalendarEvents,
                refDate,
                parsed.TargetDate);

            if (parsed.AsJson)
            {
                results.Add(new { slug, dossier });
            }
            else
            {
                WriteDossierText(stdout, slug, dossier);
            }
        }

        if (parsed.AsJson)
        {
            object payload = parsed.AllAthletes
                ? new { athletes = results }
                : results[0];
            stdout.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        }

        return 0;
    }

    private static void WriteReportText(TextWriter stdout, string slug, AthleteReportSnapshot snapshot)
    {
        stdout.WriteLine($"=== Report: {snapshot.AthleteName} ({slug}) ===");
        stdout.WriteLine(
            $"Fitness/Form: CTL={snapshot.FitnessForm.LatestCtl:F1} ATL={snapshot.FitnessForm.LatestAtl:F1} " +
            $"TSB={snapshot.FitnessForm.CurrentTsb:F1} ({snapshot.FitnessForm.TsbLevel}, source={snapshot.FitnessForm.DataSource})");

        stdout.WriteLine($"Recent Runs ({snapshot.RecentRuns.Count}):");
        foreach (var run in snapshot.RecentRuns)
        {
            var pace = FormatPace(run.PaceSecondsPerKm);
            var z1z2 = run.Z1Z2Percentage.HasValue ? $"{run.Z1Z2Percentage.Value * 100:F0}%" : "-";
            var aef = run.AerobicEfficiencyFactor.HasValue ? $"{run.AerobicEfficiencyFactor.Value:F2}" : "-";
            stdout.WriteLine(
                $"  {run.StartDateLocal:yyyy-MM-dd} | {run.DistanceKm,5:F2} km | {pace}/km | " +
                $"HR {run.AverageHeartRate,5:F1} | Z1-2 {z1z2,4} | AEF {aef,4} | {run.Name}");
        }

        stdout.WriteLine($"Weekly Trends ({snapshot.WeeklyTrends.Count}):");
        foreach (var week in snapshot.WeeklyTrends)
        {
            var pace = FormatPace(week.AveragePaceSecondsPerKm);
            var z1z2 = week.Z1Z2Percentage.HasValue ? $"{week.Z1Z2Percentage.Value * 100:F0}%" : "-";
            stdout.WriteLine(
                $"  {week.IsoWeek} | {week.RunsCount,2} runs | {week.TotalDistanceKm,5:F2} km | " +
                $"avg {pace}/km | Z1-2 {z1z2,4} | load {week.TotalLoad,5:F0}");
        }
    }

    private static void WriteMarkersText(TextWriter stdout, string slug, PlanningMarkersSnapshot snapshot)
    {
        stdout.WriteLine($"=== Planning Markers: {slug} (ref={snapshot.ReferenceDate:yyyy-MM-dd}, target={snapshot.TargetDate:yyyy-MM-dd}) ===");
        stdout.WriteLine(
            $"Readiness: TSB={snapshot.Readiness.Tsb:F1}, RHR={snapshot.Readiness.RestingHrToday} " +
            $"(baseline {snapshot.Readiness.RestingHrBaseline:F1}), Sleep={snapshot.Readiness.SleepHoursLastNight:F1}h");
        stdout.WriteLine(
            $"Periodization: {snapshot.Periodization.TargetDayName} -> {snapshot.Periodization.SlotName} " +
            $"(role={snapshot.Periodization.Role}, preferred={snapshot.Periodization.PreferredType})");
        stdout.WriteLine(
            $"History: runs/week={snapshot.History.WeeklyRunsCompleted}/{snapshot.History.TargetRunsPerWeek}, " +
            $"daysSinceRun={snapshot.History.DaysSinceLastRun}, daysSinceQuality={snapshot.History.DaysSinceLastQuality}");
        stdout.WriteLine(
            $"Guardrails: qualityDiscouraged={snapshot.Guardrails.QualityDiscouraged}, " +
            $"restRecommended={snapshot.Guardrails.RestDayRecommended}, deload={snapshot.Guardrails.IsDeloadWeek}");
        if (snapshot.Guardrails.ActiveWarnings.Count > 0)
        {
            stdout.WriteLine("Warnings: " + string.Join("; ", snapshot.Guardrails.ActiveWarnings));
        }
    }

    private static void WriteDossierText(TextWriter stdout, string slug, CoachIntelligenceDossier dossier)
    {
        var a = dossier.Athlete;
        var r = dossier.Readiness;
        var l = dossier.StimulusProgression.Ledger;
        var b = dossier.StimulusProgression.BreakReentry;

        stdout.WriteLine($"=== Intelligence Dossier: {a.Name} ({slug}) ===");
        stdout.WriteLine($"Ref: {dossier.ReferenceDate:yyyy-MM-dd} | Target: {dossier.TargetDate:yyyy-MM-dd} ({dossier.TargetDayOfWeek}) | Z2 max: {FormatNullableInt(a.Z2MaxHeartRate)} bpm | Z4: {FormatNullableInt(a.Z4RangeMin)}-{FormatNullableInt(a.Z4RangeMax)} bpm | LTHR: {a.Lthr} bpm");

        if (a.Goals is not null || a.TimeBudgets is not null)
        {
            stdout.WriteLine($"Profile Hints: goal='{a.Goals?.PrimaryGoal ?? "-"}' | focus='{a.Goals?.CurrentFocus ?? "-"}' | weekday={FormatRange(a.TimeBudgets?.TypicalWeekdayMinMinutes, a.TimeBudgets?.TypicalWeekdayMaxMinutes)}m | weekend={FormatRange(a.TimeBudgets?.TypicalWeekendMinMinutes, a.TimeBudgets?.TypicalWeekendMaxMinutes)}m");
        }

        var loadStr = r.Ctl.HasValue && r.Atl.HasValue && r.Tsb.HasValue
            ? $"CTL={r.Ctl.Value:F1} ATL={r.Atl.Value:F1} TSB={r.Tsb.Value:+0.0;-0.0;0.0} (source={r.LoadDataSource})"
            : $"unavailable (source={r.LoadDataSource})";
        var wellnessDateStr = r.LatestWellnessDate.HasValue
            ? $"{r.LatestWellnessDate.Value:yyyy-MM-dd} (age={r.WellnessAgeDays}d, stale={r.IsWellnessStale})"
            : "none (stale=True)";
        stdout.WriteLine($"Load: {loadStr}");
        stdout.WriteLine($"Wellness [{wellnessDateStr}]: RHR={FormatNullableInt(r.RestingHrLatest)} (30d avg={FormatNullableDouble(r.RestingHr30DayBaseline, "F1")}, delta={FormatNullableSigned(r.RestingHrDelta)}), HRV={FormatNullableDouble(r.HrvLatest, "F1")} (14d avg={FormatNullableDouble(r.Hrv14DayBaseline, "F1")}, ratio={FormatNullableDouble(r.HrvBaselineRatio, "F2")}), Sleep={FormatNullableDouble(r.SleepHoursLatest, "F2")}h / score={FormatNullableDouble(r.SleepScoreLatest, "F0")} (14d avg={FormatNullableDouble(r.SleepHours14DayBaseline, "F2")}h)");
        stdout.WriteLine($"Medical Red Flags ({r.MedicalRedFlags.Count}): {(r.MedicalRedFlags.Count == 0 ? "none" : string.Join("; ", r.MedicalRedFlags.Select(f => $"{f.Date:yyyy-MM-dd} [{f.Code}] {f.Detail}")))}");

        stdout.WriteLine($"Stimulus Ledger: lastRun={FormatNullableDays(l.DaysSinceLastRun)}, lastBaseRun={FormatNullableDays(l.DaysSinceLastBaseRun)}, lastBaseAnchor={FormatNullableDays(l.DaysSinceLastBaseAnchor)}, lastQuality={FormatNullableDays(l.DaysSinceLastQualityStimulus)}");
        stdout.WriteLine($"Rolling Volume: 7d={l.RunsLast7Days} runs / {l.VolumeKmLast7Days:F2}km / {l.VolumeMinutesLast7Days:F0}m | 14d={l.RunsLast14Days} runs / {l.VolumeKmLast14Days:F2}km / {l.VolumeMinutesLast14Days:F0}m | Z1/Z2: 14d={FormatNullableDouble(l.Rolling14DayZ1Z2Percentage, "F1")}%, 28d={FormatNullableDouble(l.Rolling28DayZ1Z2Percentage, "F1")}%");
        stdout.WriteLine($"Break/Re-entry: longestGap28d={b.LongestGapDaysLast28Days}d | hasRecentExtendedBreak={b.HasRecentExtendedBreak} ({b.RecentBreakStartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-"} -> {b.RecentBreakEndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "ongoing"}, {FormatNullableDays(b.RecentBreakDurationDays)}) | runsSinceBreak={b.RunsCompletedSinceRecentBreak} (cleanBase={b.CleanBaseRunsSinceRecentBreak})");

        stdout.WriteLine($"Base Progression Chain ({dossier.StimulusProgression.BaseProgressionChain.Count}):");
        foreach (var session in dossier.StimulusProgression.BaseProgressionChain)
        {
            var drift = session.CardiacDriftBpm.HasValue ? $"{session.CardiacDriftBpm.Value:+0.0;-0.0;0.0}bpm" : "-";
            var tag = string.Equals(session.SessionQualityTag, "clean_base", StringComparison.Ordinal)
                ? string.Empty
                : $" [{session.SessionQualityTag}]";
            stdout.WriteLine($"  {session.Date:yyyy-MM-dd} | {session.DurationMinutes,4:F0}m | {session.DistanceKm,5:F2}km | HR {FormatNullableDouble(session.AverageHeartRate, "F0")} | Z1-2 {FormatNullableDouble(session.Z1Z2Percentage, "F1")}% | AEF {FormatNullableDouble(session.AerobicEfficiencyFactor, "F3")} | Z2GAP {session.Z2GapPaceText ?? "-"} | drift {drift}{tag} | {session.Name}");
        }

        stdout.WriteLine($"Quality Progression Chain ({dossier.StimulusProgression.QualityProgressionChain.Count}):");
        foreach (var q in dossier.StimulusProgression.QualityProgressionChain)
        {
            var workInts = q.ParsedIntervals
                .Where(seg => seg.Role is "quality" or "tempo")
                .Select(seg => $"{seg.Reps}x{FormatSeconds(seg.DurationSeconds)}@{seg.AverageHeartRate?.ToString("F0", CultureInfo.InvariantCulture) ?? "-"}bpm");
            var intSummary = string.Join(", ", workInts);
            if (string.IsNullOrEmpty(intSummary))
            {
                intSummary = $"{q.ParsedIntervals.Count} segments parsed";
            }

            stdout.WriteLine($"  {q.Date:yyyy-MM-dd} | {q.StimulusKind,-9} | {q.DurationMinutes,4:F0}m | {q.DistanceKm,5:F2}km | maxHR {FormatNullableInt(q.MaxHeartRate)} | Z4={q.Z4Minutes:F1}m Z5={q.Z5PlusMinutes:F1}m | cad {FormatNullableDouble(q.CadenceSpm, "F1")} | [{intSummary}] | {q.Name}");
        }

        stdout.WriteLine($"Calendar Target ({dossier.Calendar.EventsOnTargetDate.Count}) / Next 7d ({dossier.Calendar.UpcomingEventsNext7Days.Count}):");
        foreach (var evt in dossier.Calendar.UpcomingEventsNext7Days)
        {
            var status = evt.IsCompleted ? "completed" : "planned";
            stdout.WriteLine($"  {evt.Date:yyyy-MM-dd} | {evt.Category,-7} | {status,-9} | {evt.Name}");
        }

        stdout.WriteLine($"Recent Notes & Context Clues ({r.RecentRunNotes.Count}, daysSinceComplaint={FormatNullableDays(r.DaysSinceLastComplaint)}):");
        foreach (var clue in r.RecentRunNotes)
        {
            stdout.WriteLine($"  {clue.Date:yyyy-MM-dd} [{clue.Source}] {clue.Summary}");
        }

        stdout.WriteLine($"Data Quality Notes ({dossier.DataQualityNotes.Count}):");
        foreach (var note in dossier.DataQualityNotes)
        {
            stdout.WriteLine($"  - {note}");
        }
    }

    private static string FormatNullableInt(int? value) =>
        value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "-";

    private static string FormatNullableDays(int? value) =>
        value.HasValue ? $"{value.Value}d" : "-";

    private static string FormatNullableDouble(double? value, string format) =>
        value.HasValue ? value.Value.ToString(format, CultureInfo.InvariantCulture) : "-";

    private static string FormatNullableSigned(double? value) =>
        value.HasValue ? value.Value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) : "-";

    private static string FormatRange(int? min, int? max) =>
        $"{FormatNullableInt(min)}-{FormatNullableInt(max)}";

    private static string FormatSeconds(int seconds)
    {
        var mins = seconds / 60;
        var secs = seconds % 60;
        return mins > 0 && secs > 0 ? $"{mins}m{secs:D2}s" : mins > 0 ? $"{mins}m" : $"{secs}s";
    }

    private static string FormatPace(double secondsPerKm)
    {
        if (secondsPerKm <= 0)
        {
            return "-";
        }

        var rounded = (int)Math.Round(secondsPerKm, MidpointRounding.AwayFromZero);
        return $"{rounded / 60}:{rounded % 60:D2}";
    }

    private static void WriteHelp(TextWriter stdout)
    {
        stdout.WriteLine("Laufen.Cli (.NET 11 Shadow Runner)");
        stdout.WriteLine("Usage: Laufen.Cli <command> [options]");
        stdout.WriteLine();
        stdout.WriteLine("Commands:");
        stdout.WriteLine("  --health                  Emit runtime health JSON");
        stdout.WriteLine("  athletes                  List registered athletes");
        stdout.WriteLine("  report                    Display recent runs, weekly trends & CTL/ATL/TSB");
        stdout.WriteLine("  markers                   Extract planning markers & safety guardrails");
        stdout.WriteLine("  dossier                   Build curated stimulus progression & readiness dossier");
        stdout.WriteLine("  validate-workout          Validate a CoachPlanDecision JSON & compile Intervals.icu DSL");
        stdout.WriteLine("  help                      Show this help text");
        stdout.WriteLine();
        stdout.WriteLine("Options:");
        stdout.WriteLine("  --athlete, -a <slug>      Target athlete slug (defaults to registry default)");
        stdout.WriteLine("  --all                     Execute for all active athletes");
        stdout.WriteLine("  --workspace <path>        Override workspace root directory");
        stdout.WriteLine("  --json                    Emit machine-readable JSON");
        stdout.WriteLine("  --limit <N>               Recent runs limit for 'report' (default: 5)");
        stdout.WriteLine("  --weeks <N>               Max weeks for 'report' (default: 12)");
        stdout.WriteLine("  --as-of <YYYY-MM-DD>      Reference date for 'report' CTL/ATL decay");
        stdout.WriteLine("  --ref-date <YYYY-MM-DD>   Reference date for 'markers', 'dossier', and 'validate-workout'");
        stdout.WriteLine("  --target-date <YYYY-MM-DD> Target planning date for 'markers' and 'dossier'");
        stdout.WriteLine("  --input <path>            Input CoachPlanDecision JSON file for 'validate-workout'");
    }

    private static bool TryParseArgs(IReadOnlyList<string> args, out ParsedCliArgs parsed, out string error)
    {
        string? command = null;
        string? athleteSlug = null;
        bool allAthletes = false;
        string? workspaceRoot = null;
        bool asJson = false;
        int recentLimit = 5;
        int maxWeeks = 12;
        DateOnly? asOfDate = null;
        DateOnly? refDate = null;
        DateOnly? targetDate = null;
        string? inputPath = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--athlete" or "-a":
                    if (!TryReadValue(args, ref i, arg, out athleteSlug, out error))
                    {
                        parsed = default!;
                        return false;
                    }
                    break;
                case "--all":
                    allAthletes = true;
                    break;
                case "--workspace":
                    if (!TryReadValue(args, ref i, arg, out workspaceRoot, out error))
                    {
                        parsed = default!;
                        return false;
                    }
                    break;
                case "--json":
                    asJson = true;
                    break;
                case "--limit":
                    if (!TryReadInt(args, ref i, arg, out recentLimit, out error))
                    {
                        parsed = default!;
                        return false;
                    }
                    break;
                case "--weeks":
                    if (!TryReadInt(args, ref i, arg, out maxWeeks, out error))
                    {
                        parsed = default!;
                        return false;
                    }
                    break;
                case "--as-of":
                    if (!TryReadDate(args, ref i, arg, out asOfDate, out error))
                    {
                        parsed = default!;
                        return false;
                    }
                    break;
                case "--ref-date":
                    if (!TryReadDate(args, ref i, arg, out refDate, out error))
                    {
                        parsed = default!;
                        return false;
                    }
                    break;
                case "--target-date":
                    if (!TryReadDate(args, ref i, arg, out targetDate, out error))
                    {
                        parsed = default!;
                        return false;
                    }
                    break;
                case "--input":
                    if (!TryReadValue(args, ref i, arg, out inputPath, out error))
                    {
                        parsed = default!;
                        return false;
                    }
                    break;
                default:
                    if (arg.StartsWith('-') && arg is not ("-h" or "--help"))
                    {
                        error = $"Unknown option '{arg}'.";
                        parsed = default!;
                        return false;
                    }

                    if (command is null)
                    {
                        command = arg;
                    }
                    else
                    {
                        error = $"Unexpected positional argument '{arg}'.";
                        parsed = default!;
                        return false;
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            error = "Missing command. Run 'Laufen.Cli help' for available commands.";
            parsed = default!;
            return false;
        }

        error = string.Empty;
        parsed = new ParsedCliArgs(
            command,
            athleteSlug,
            allAthletes,
            workspaceRoot,
            asJson,
            recentLimit,
            maxWeeks,
            asOfDate,
            refDate,
            targetDate,
            inputPath);
        return true;
    }

    private static int ExecuteValidateWorkout(
        ParsedCliArgs parsed,
        TextWriter stdout,
        TextWriter stderr,
        string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(parsed.InputPath))
        {
            stderr.WriteLine("Missing required '--input <path>' option for 'validate-workout'.");
            return 1;
        }

        var decision = StrictJsonLoader.LoadFromFile<CoachPlanDecision>(parsed.InputPath);

        CoachIntelligenceDossier? dossier = null;
        if (!string.IsNullOrWhiteSpace(parsed.AthleteSlug) || parsed.RefDate.HasValue)
        {
            var layout = string.IsNullOrWhiteSpace(parsed.WorkspaceRoot)
                ? WorkspaceLayout.DiscoverFrom(workingDirectory)
                : WorkspaceLayout.FromRoot(parsed.WorkspaceRoot);
            var registry = AthleteDataRepository.LoadRegistry(layout);
            var slugs = AthleteDataRepository.ResolveTargetSlugs(registry, parsed.AthleteSlug, allActive: false);
            var data = AthleteDataRepository.LoadAthleteData(layout, registry, slugs[0]);
            var refDate = parsed.RefDate ?? decision.TargetDate;
            dossier = CoachIntelligenceDossierBuilder.Build(
                data.Profile,
                data.Activities,
                data.WellnessRecords,
                data.RunReviews,
                data.CalendarEvents,
                refDate,
                parsed.TargetDate ?? decision.TargetDate);
        }

        var validation = WorkoutModalityValidator.ValidateDecision(decision, dossier);
        if (!validation.IsValid)
        {
            if (parsed.AsJson)
            {
                var errPayload = new
                {
                    isValid = false,
                    action = decision.Action,
                    targetDate = decision.TargetDate,
                    errors = validation.Errors,
                    compiledPrimary = (CompiledWorkout?)null,
                    compiledSecondary = (CompiledWorkout?)null
                };
                stdout.WriteLine(JsonSerializer.Serialize(errPayload, JsonOptions));
            }
            else
            {
                stderr.WriteLine($"Validation failed ({validation.Errors.Count} error(s)):");
                foreach (var err in validation.Errors)
                {
                    stderr.WriteLine($"  [{err.Code}] {err.Field}: {err.Message}");
                }
            }

            return 1;
        }

        var compiledPrimary = decision.PrimaryWorkout is not null
            ? IntervalsWorkoutCompiler.Compile(decision.PrimaryWorkout)
            : null;
        var compiledSecondary = decision.SecondaryWorkout is not null
            ? IntervalsWorkoutCompiler.Compile(decision.SecondaryWorkout)
            : null;

        if (parsed.AsJson)
        {
            var okPayload = new
            {
                isValid = true,
                action = decision.Action,
                targetDate = decision.TargetDate,
                errors = validation.Errors,
                compiledPrimary,
                compiledSecondary
            };
            stdout.WriteLine(JsonSerializer.Serialize(okPayload, JsonOptions));
        }
        else
        {
            stdout.WriteLine($"Validation passed: action={decision.Action}, target={decision.TargetDate:yyyy-MM-dd}");
            if (compiledPrimary is not null)
            {
                stdout.WriteLine($"--- Primary: {compiledPrimary.Title} ({compiledPrimary.SessionType}, {compiledPrimary.TotalDurationSeconds / 60}m) ---");
                stdout.Write(compiledPrimary.DslText);
            }

            if (compiledSecondary is not null)
            {
                stdout.WriteLine($"--- Secondary: {compiledSecondary.Title} ({compiledSecondary.SessionType}, {compiledSecondary.TotalDurationSeconds / 60}m) ---");
                stdout.Write(compiledSecondary.DslText);
            }
        }

        return 0;
    }

    private static bool TryReadValue(IReadOnlyList<string> args, ref int index, string flag, out string value, out string error)
    {
        if (index + 1 >= args.Count)
        {
            value = string.Empty;
            error = $"Missing value for option '{flag}'.";
            return false;
        }

        index++;
        value = args[index];
        error = string.Empty;
        return true;
    }

    private static bool TryReadInt(IReadOnlyList<string> args, ref int index, string flag, out int value, out string error)
    {
        if (!TryReadValue(args, ref index, flag, out var raw, out error))
        {
            value = 0;
            return false;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 0)
        {
            error = $"Invalid non-negative integer '{raw}' for option '{flag}'.";
            return false;
        }

        return true;
    }

    private static bool TryReadDate(IReadOnlyList<string> args, ref int index, string flag, out DateOnly? value, out string error)
    {
        if (!TryReadValue(args, ref index, flag, out var raw, out error))
        {
            value = null;
            return false;
        }

        if (!DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
        {
            value = null;
            error = $"Invalid date '{raw}' for option '{flag}'. Expected YYYY-MM-DD.";
            return false;
        }

        value = parsedDate;
        return true;
    }

    private sealed record ParsedCliArgs(
        string Command,
        string? AthleteSlug,
        bool AllAthletes,
        string? WorkspaceRoot,
        bool AsJson,
        int RecentLimit,
        int MaxWeeks,
        DateOnly? AsOfDate,
        DateOnly? RefDate,
        DateOnly? TargetDate,
        string? InputPath);
}
