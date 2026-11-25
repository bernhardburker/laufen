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

            if (string.Equals(parsed.Command, "project-plan", StringComparison.Ordinal))
            {
                return ExecuteProjectPlan(parsed, stdout, stderr, workingDirectory);
            }

            if (string.Equals(parsed.Command, "coach-plan", StringComparison.Ordinal))
            {
                return ExecuteCoachPlan(parsed, stdout, stderr, workingDirectory);
            }

            if (string.Equals(parsed.Command, "simulate", StringComparison.Ordinal))
            {
                return ExecuteSimulate(parsed, stdout, stderr, workingDirectory);
            }

            if (string.Equals(parsed.Command, "narrate", StringComparison.Ordinal))
            {
                return ExecuteNarrate(parsed, stdout, stderr, workingDirectory);
            }

            if (string.Equals(parsed.Command, "review", StringComparison.Ordinal))
            {
                return ExecuteReview(parsed, stdout, stderr, workingDirectory);
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
                parsed.TargetDate,
                parsed.AutoEventIds,
                parsed.LockedDates);

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

        stdout.WriteLine($"Calendar Target ({dossier.Calendar.EventsOnTargetDate.Count}) / Next 14d ({dossier.Calendar.UpcomingEventsNext14Days.Count}):");
        foreach (var evt in dossier.Calendar.UpcomingEventsNext14Days)
        {
            var status = evt.IsCompleted ? "completed" : "planned";
            var load = evt.EstimatedLoad.HasValue ? $", {evt.EstimatedLoad.Value:F0} TSS" : string.Empty;
            stdout.WriteLine($"  {evt.Date:yyyy-MM-dd} | {evt.Category,-7} | {status,-9} | [{evt.Ownership}{load}] | {evt.Name}");
        }

        if (dossier.Calendar.MissedAutoWorkoutsLast7Days.Count > 0)
        {
            stdout.WriteLine($"Missed Auto Workouts Last 7d ({dossier.Calendar.MissedAutoWorkoutsLast7Days.Count}):");
            foreach (var missed in dossier.Calendar.MissedAutoWorkoutsLast7Days)
            {
                stdout.WriteLine($"  {missed.Date:yyyy-MM-dd} | [auto/unrun] | {missed.Name}");
            }
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
        stdout.WriteLine("  project-plan              Validate & reconcile a 14-day AutopilotPlanDecision + Banister curve");
        stdout.WriteLine("  coach-plan                Run RollingAutopilotCoach via stub/agy/gemini (--dry-run supported)");
        stdout.WriteLine("  simulate                  Run multi-day self-healing simulation scenarios & invariant auditor");
        stdout.WriteLine("  narrate                   Run decoupled Role 2 ProgressNarrator (--dry-run supported)");
        stdout.WriteLine("  review                    Compute deterministic 4-stage post-run reviews & stream GAP stats");
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
        stdout.WriteLine("  --ref-date <YYYY-MM-DD>   Reference date for 'markers', 'dossier', 'validate-workout', 'project-plan', 'coach-plan', 'narrate'");
        stdout.WriteLine("  --target-date <YYYY-MM-DD> Target planning date for 'markers' and 'dossier'");
        stdout.WriteLine("  --input <path>            Input decision JSON file for 'validate-workout' and 'project-plan'");
        stdout.WriteLine("  --activity-id <id>        Target activity ID for 'review'");
        stdout.WriteLine("  --auto-event-ids <csv>    Comma-separated calendar event IDs managed as 'auto' ownership");
        stdout.WriteLine("  --locked-dates <csv>      Comma-separated YYYY-MM-DD dates marked as 'user_locked'");
        stdout.WriteLine("  --trigger <name>          Autopilot trigger: morning_sync|post_run|telegram_override|intraday_check");
        stdout.WriteLine("  --provider <name>         LLM provider: stub|agy|gemini (coach-plan/narrate default: agy; simulate default: stub)");
        stdout.WriteLine("  --model <name>            Override LLM model name");
        stdout.WriteLine("  --message <text>          Optional athlete Telegram message for 'coach-plan'");
        stdout.WriteLine("  --stub-response <path>    JSON response file for '--provider stub'");
        stdout.WriteLine("  --scenario <name|all>     Simulation scenario for 'simulate' (default: all)");
        stdout.WriteLine("  --output-dir <path>       Write isolated simulation report.md and timeline.json artifacts");
        stdout.WriteLine("  --retries <N>             Validation self-correction retries for 'coach-plan' / 'simulate' / 'narrate' (default: 1)");
        stdout.WriteLine("  --force                   Force narrative generation even when ProgressNarratorGate suppresses");
        stdout.WriteLine("  --dry-run                 Skip writing block_compass.json / decision_trace_dotnet.jsonl / narrative artifacts");
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
        IReadOnlySet<long>? autoEventIds = null;
        IReadOnlySet<DateOnly>? lockedDates = null;
        string trigger = "morning_sync";
        string? provider = null;
        string? model = null;
        string? message = null;
        string? stubResponsePath = null;
        string? scenarioName = null;
        string? outputDir = null;
        int retries = 1;
        bool dryRun = false;
        bool force = false;
        string? activityId = null;

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
                case "--auto-event-ids":
                    if (!TryReadLongSet(args, ref i, arg, out autoEventIds, out error))
                    {
                        parsed = default!;
                        return false;
                    }
                    break;
                case "--locked-dates":
                    if (!TryReadDateSet(args, ref i, arg, out lockedDates, out error))
                    {
                        parsed = default!;
                        return false;
                    }
                    break;
                case "--trigger":
                    if (!TryReadValue(args, ref i, arg, out trigger, out error))
                    {
                        parsed = default!;
                        return false;
                    }
                    break;
                case "--provider":
                    if (!TryReadValue(args, ref i, arg, out provider, out error))
                    {
                        parsed = default!;
                        return false;
                    }
                    break;
                case "--model":
                    if (!TryReadValue(args, ref i, arg, out model, out error))
                    {
                        parsed = default!;
                        return false;
                    }
                    break;
                case "--message":
                    if (!TryReadValue(args, ref i, arg, out message, out error))
                    {
                        parsed = default!;
                        return false;
                    }
                    break;
                case "--stub-response":
                    if (!TryReadValue(args, ref i, arg, out stubResponsePath, out error))
                    {
                        parsed = default!;
                        return false;
                    }
                    break;
                case "--scenario":
                    if (!TryReadValue(args, ref i, arg, out scenarioName, out error))
                    {
                        parsed = default!;
                        return false;
                    }
                    break;
                case "--output-dir":
                    if (!TryReadValue(args, ref i, arg, out outputDir, out error))
                    {
                        parsed = default!;
                        return false;
                    }
                    break;
                case "--retries":
                    if (!TryReadInt(args, ref i, arg, out retries, out error))
                    {
                        parsed = default!;
                        return false;
                    }
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--activity-id":
                    if (!TryReadValue(args, ref i, arg, out activityId, out error))
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
            inputPath,
            autoEventIds,
            lockedDates,
            trigger,
            provider,
            model,
            message,
            stubResponsePath,
            scenarioName,
            outputDir,
            retries,
            dryRun,
            force,
            activityId);
        return true;
    }

    private static int ExecuteCoachPlan(
        ParsedCliArgs parsed,
        TextWriter stdout,
        TextWriter stderr,
        string? workingDirectory)
    {
        var layout = string.IsNullOrWhiteSpace(parsed.WorkspaceRoot)
            ? WorkspaceLayout.DiscoverFrom(workingDirectory)
            : WorkspaceLayout.FromRoot(parsed.WorkspaceRoot);
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var slugs = AthleteDataRepository.ResolveTargetSlugs(registry, parsed.AthleteSlug, allActive: false);
        var slug = slugs[0];
        var data = AthleteDataRepository.LoadAthleteData(layout, registry, slug);

        var refDate = parsed.RefDate ?? DateOnly.FromDateTime(DateTime.Today);
        var existingCompass = AutopilotStateStore.LoadBlockCompass(layout, slug);
        var dossier = CoachIntelligenceDossierBuilder.Build(
            data.Profile,
            data.Activities,
            data.WellnessRecords,
            data.RunReviews,
            data.CalendarEvents,
            refDate,
            parsed.TargetDate ?? refDate,
            parsed.AutoEventIds,
            parsed.LockedDates,
            currentCompass: existingCompass);

        string? stubJson = null;
        if (!string.IsNullOrWhiteSpace(parsed.StubResponsePath))
        {
            stubJson = File.ReadAllText(parsed.StubResponsePath);
        }

        var effectiveProvider = string.IsNullOrWhiteSpace(parsed.Provider) ? "agy" : parsed.Provider;
        ILlmClient llmClient;
        try
        {
            llmClient = LlmClientFactory.Create(effectiveProvider, parsed.Model, stubJson);
        }
        catch (ArgumentException ex)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        var strategy = new RollingAutopilotStrategy(llmClient);
        var context = new CoachPlanningContext
        {
            Dossier = dossier,
            Trigger = parsed.Trigger,
            AthleteMessage = parsed.Message,
            MaxValidationRetries = parsed.Retries
        };

        var outcome = strategy.PlanAsync(context).GetAwaiter().GetResult();

        if (outcome.Success && !parsed.DryRun && outcome.Decision is not null)
        {
            AutopilotStateStore.SaveBlockCompass(layout, slug, outcome.Decision.UpdatedCompass);
            AutopilotStateStore.AppendDecisionTrace(layout, slug, outcome.TraceEntry);
        }

        if (parsed.AsJson)
        {
            var payload = new
            {
                slug,
                dryRun = parsed.DryRun,
                provider = effectiveProvider,
                outcome
            };
            stdout.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
            return outcome.Success ? 0 : 1;
        }

        if (!outcome.Success || outcome.ReconciledSchedule is null || outcome.Decision is null)
        {
            stderr.WriteLine($"Coach planning failed ({outcome.Attempts.Count} attempt(s)): {outcome.FailureReason}");
            foreach (var attempt in outcome.Attempts)
            {
                var errCount = attempt.ValidationErrors.Count;
                stderr.WriteLine($"  Attempt {attempt.AttemptNumber}: llmOk={attempt.LlmSucceeded}, parsed={attempt.JsonParsed}, errors={errCount}, elapsed={attempt.ElapsedSeconds:F1}s");
                if (!string.IsNullOrWhiteSpace(attempt.LlmError))
                {
                    stderr.WriteLine($"    llmError: {attempt.LlmError}");
                }
                if (!string.IsNullOrWhiteSpace(attempt.ParseError))
                {
                    stderr.WriteLine($"    parseError: {attempt.ParseError}");
                }
                foreach (var err in attempt.ValidationErrors)
                {
                    stderr.WriteLine($"    [{err.Code}] {err.Field}: {err.Message}");
                }
            }

            return 1;
        }

        WriteCoachPlanText(stdout, slug, effectiveProvider, parsed.DryRun, outcome);
        return 0;
    }

    private static int ExecuteNarrate(
        ParsedCliArgs parsed,
        TextWriter stdout,
        TextWriter stderr,
        string? workingDirectory)
    {
        var layout = string.IsNullOrWhiteSpace(parsed.WorkspaceRoot)
            ? WorkspaceLayout.DiscoverFrom(workingDirectory)
            : WorkspaceLayout.FromRoot(parsed.WorkspaceRoot);
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var slugs = AthleteDataRepository.ResolveTargetSlugs(registry, parsed.AthleteSlug, allActive: false);
        var slug = slugs[0];
        var data = AthleteDataRepository.LoadAthleteData(layout, registry, slug);

        var refDate = parsed.RefDate ?? DateOnly.FromDateTime(DateTime.Today);
        var existingCompass = AutopilotStateStore.LoadBlockCompass(layout, slug);
        var latestTrace = AutopilotStateStore.LoadLatestDecisionTrace(layout, slug);

        var dossier = CoachIntelligenceDossierBuilder.Build(
            data.Profile,
            data.Activities,
            data.WellnessRecords,
            data.RunReviews,
            data.CalendarEvents,
            refDate,
            parsed.TargetDate ?? refDate,
            parsed.AutoEventIds,
            parsed.LockedDates,
            currentCompass: existingCompass);

        var gate = ProgressNarratorGate.Evaluate(
            parsed.Trigger,
            previousHealthState: latestTrace?.HealthState,
            currentHealthState: existingCompass?.HealthState,
            previousBlockFocus: null,
            currentBlockFocus: existingCompass?.CurrentBlockFocus,
            forceManual: parsed.Force);

        var effectiveProvider = string.IsNullOrWhiteSpace(parsed.Provider) ? "agy" : parsed.Provider;

        if (!gate.ShouldNarrate)
        {
            if (parsed.AsJson)
            {
                var skipPayload = new
                {
                    slug,
                    dryRun = parsed.DryRun,
                    provider = effectiveProvider,
                    narrated = false,
                    skipped = true,
                    triggerReason = gate.TriggerReason,
                    reasons = gate.Reasons,
                    outcome = (NarrativeGenerationOutcome?)null
                };
                stdout.WriteLine(JsonSerializer.Serialize(skipPayload, JsonOptions));
            }
            else
            {
                stdout.WriteLine($"=== Progress Narrator: {slug} (suppressed) ===");
                stdout.WriteLine($"Trigger '{parsed.Trigger}' suppressed: no completed run, health transition, or block focus shift (use --force to override).");
            }

            return 0;
        }

        string? stubJson = null;
        if (!string.IsNullOrWhiteSpace(parsed.StubResponsePath))
        {
            stubJson = File.ReadAllText(parsed.StubResponsePath);
        }

        ILlmClient llmClient;
        try
        {
            llmClient = (effectiveProvider.Trim().ToLowerInvariant() is "stub" or "mock" or "test") && stubJson is null
                ? new NarrativeStubLlmClient()
                : LlmClientFactory.Create(effectiveProvider, parsed.Model, stubJson);
        }
        catch (ArgumentException ex)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        var briefing = NarrativeBriefingDossierBuilder.Build(
            data.Profile,
            data.Activities,
            data.WellnessRecords,
            data.RunReviews,
            data.CalendarEvents,
            refDate,
            gate.TriggerReason,
            existingDossier: dossier,
            reconciledSchedule: null,
            currentCompass: existingCompass,
            previousHealthState: latestTrace?.HealthState,
            latestTraceRationale: latestTrace?.Rationale);

        var narrator = new LlmProgressNarrator(llmClient);
        var outcome = narrator.NarrateAsync(new NarrativeGenerationContext
        {
            Briefing = briefing,
            MaxValidationRetries = parsed.Retries
        }).GetAwaiter().GetResult();

        if (outcome.Success && !parsed.DryRun && outcome.Report is not null)
        {
            AutopilotStateStore.SaveNarrativeArtifacts(layout, slug, outcome.Report);
        }

        if (parsed.AsJson)
        {
            var payload = new
            {
                slug,
                dryRun = parsed.DryRun,
                provider = effectiveProvider,
                narrated = outcome.Success,
                skipped = false,
                triggerReason = gate.TriggerReason,
                reasons = gate.Reasons,
                outcome
            };
            stdout.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
            return outcome.Success ? 0 : 1;
        }

        if (!outcome.Success || outcome.Report is null)
        {
            stderr.WriteLine($"Progress narration failed ({outcome.Attempts.Count} attempt(s)): {outcome.FailureReason}");
            return 1;
        }

        WriteNarrateText(stdout, slug, effectiveProvider, parsed.DryRun, outcome);
        return 0;
    }

    private static void WriteNarrateText(
        TextWriter stdout,
        string slug,
        string provider,
        bool dryRun,
        NarrativeGenerationOutcome outcome)
    {
        var report = outcome.Report!;
        var elapsed = outcome.Attempts.Sum(a => a.ElapsedSeconds);
        stdout.WriteLine($"=== Progress Narrator: {slug} (dryRun={dryRun}, provider={provider}, model={outcome.ModelUsed ?? "-"}, trigger={report.TriggerReason}, attempts={outcome.Attempts.Count}, elapsed={elapsed:F1}s) ===");
        stdout.WriteLine($"Status [{report.StatusSeverity.ToUpperInvariant()} / {report.StatusBadge}]: {report.StatusTitle}");
        stdout.WriteLine($"Insights ({report.Insights.Count}):");
        foreach (var insight in report.Insights)
        {
            stdout.WriteLine($"  [{insight.Severity}] {insight.Title} ({insight.Badge}): {insight.Text}");
        }

        if (report.ScheduleHighlights.Count > 0)
        {
            stdout.WriteLine($"Schedule Highlights ({report.ScheduleHighlights.Count}):");
            foreach (var item in report.ScheduleHighlights)
            {
                stdout.WriteLine($"  - {item}");
            }
        }

        stdout.WriteLine();
        stdout.WriteLine(report.CommentaryMarkdown);
    }

    private static int ExecuteReview(
        ParsedCliArgs parsed,
        TextWriter stdout,
        TextWriter stderr,
        string? workingDirectory)
    {
        var layout = string.IsNullOrWhiteSpace(parsed.WorkspaceRoot)
            ? WorkspaceLayout.DiscoverFrom(workingDirectory)
            : WorkspaceLayout.FromRoot(parsed.WorkspaceRoot);
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var slugs = AthleteDataRepository.ResolveTargetSlugs(registry, parsed.AthleteSlug, allActive: false);
        var slug = slugs[0];
        var data = AthleteDataRepository.LoadAthleteData(layout, registry, slug);

        var refDate = parsed.RefDate;
        var validRuns = PhysiologicalCalculator.FilterValidRuns(data.Activities)
            .Where(a => !refDate.HasValue || DateOnly.FromDateTime(a.StartDateLocal) <= refDate.Value)
            .OrderByDescending(a => a.StartDateLocal)
            .ToList();

        List<IntervalsActivityEntry> targetRuns;
        if (!string.IsNullOrWhiteSpace(parsed.ActivityId))
        {
            targetRuns = validRuns
                .Where(a => string.Equals(a.Id, parsed.ActivityId.Trim(), StringComparison.Ordinal))
                .ToList();
            if (targetRuns.Count == 0)
            {
                stderr.WriteLine($"Activity '{parsed.ActivityId}' not found among valid runs for '{slug}'.");
                return 1;
            }
        }
        else
        {
            var limit = parsed.RecentLimit > 0 ? parsed.RecentLimit : 5;
            targetRuns = validRuns.Take(limit).ToList();
        }

        var calendarById = data.CalendarEvents.ToDictionary(e => e.Id);
        var openCycle = data.CoachHistory
            .Where(c => string.Equals(c.Status, "offen", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Date)
            .FirstOrDefault();

        var existingDotNet = AutopilotStateStore.LoadDotNetRunReviews(layout, slug);
        var updatedReviews = new Dictionary<string, RunReviewRecord>(existingDotNet, StringComparer.Ordinal);
        var evaluatedList = new List<RunReviewRecord>(targetRuns.Count);

        foreach (var run in targetRuns)
        {
            var stream = StrictJsonLoader.LoadActivityStream(layout, slug, run.Id);
            CalendarEventEntry? pairedEvt = null;
            if (run.PairedEventId.HasValue)
            {
                calendarById.TryGetValue(run.PairedEventId.Value, out pairedEvt);
            }

            var review = PostRunReviewCalculator.EvaluateRun(
                run,
                data.Profile,
                stream,
                pairedEvt,
                openCycle);
            evaluatedList.Add(review);
            updatedReviews[run.Id] = review;
        }

        if (!parsed.DryRun)
        {
            AutopilotStateStore.SaveDotNetRunReviews(layout, slug, updatedReviews);
        }

        if (parsed.AsJson)
        {
            var payload = new
            {
                slug,
                dryRun = parsed.DryRun,
                count = evaluatedList.Count,
                reviews = evaluatedList
            };
            stdout.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
            return 0;
        }

        WriteReviewText(stdout, slug, parsed.DryRun, evaluatedList);
        return 0;
    }

    private static void WriteReviewText(
        TextWriter stdout,
        string slug,
        bool dryRun,
        List<RunReviewRecord> reviews)
    {
        stdout.WriteLine($"=== 4-Stage Post-Run Reviews: {slug} (dryRun={dryRun}, count={reviews.Count}) ===");
        foreach (var r in reviews)
        {
            var z2Gap = r.ZonePaceStats?.Zones.Values.FirstOrDefault(z => z.Zone == 2)?.GapPaceText ?? "-";
            stdout.WriteLine($"  {r.Date:yyyy-MM-dd} [{r.Id}] {r.Name}");
            stdout.WriteLine($"    Stage 1: {r.DistanceKm:F2}km | {r.MovingTimeText} | HR {r.AverageHeartRate:F0} (max {r.MaxHeartRate}) | Z1/Z2 {r.Z1Z2Percentage:F1}% | AEF {r.AerobicEf?.ToString("F3", CultureInfo.InvariantCulture) ?? "-"} | Z2-GAP {z2Gap}");
            stdout.WriteLine($"    Stage 2: [{r.Review.Stages.Stage2Compliance.StatusLabel}] {r.Review.Stages.Stage2Compliance.PlannedSlot} -> {r.Review.Stages.Stage2Compliance.TargetComparison}");
            stdout.WriteLine($"    Stage 4: recovery={r.Review.Stages.Stage4NextRun.RecoveryHours}h | {r.Review.Stages.Stage4NextRun.Directive}");
        }
    }

    private static int ExecuteSimulate(
        ParsedCliArgs parsed,
        TextWriter stdout,
        TextWriter stderr,
        string? workingDirectory)
    {
        var layout = string.IsNullOrWhiteSpace(parsed.WorkspaceRoot)
            ? WorkspaceLayout.DiscoverFrom(workingDirectory)
            : WorkspaceLayout.FromRoot(parsed.WorkspaceRoot);
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var slugs = AthleteDataRepository.ResolveTargetSlugs(registry, parsed.AthleteSlug, allActive: false);
        var slug = slugs[0];
        var profile = AthleteDataRepository.LoadAthleteData(layout, registry, slug).Profile;

        var scenarioArg = string.IsNullOrWhiteSpace(parsed.ScenarioName) ? "all" : parsed.ScenarioName.Trim();
        List<SimulationScenario> scenarios;
        try
        {
            scenarios = string.Equals(scenarioArg, "all", StringComparison.OrdinalIgnoreCase)
                ? AutopilotScenarioCatalog.GetAllNames().Select(AutopilotScenarioCatalog.GetScenario).ToList()
                : [AutopilotScenarioCatalog.GetScenario(scenarioArg)];
        }
        catch (ArgumentException ex)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        var effectiveProvider = string.IsNullOrWhiteSpace(parsed.Provider) ? "stub" : parsed.Provider.Trim().ToLowerInvariant();
        ILlmClient llmClient;
        try
        {
            llmClient = effectiveProvider is "stub" or "mock" or "test"
                ? new SimulationStubLlmClient()
                : LlmClientFactory.Create(effectiveProvider, parsed.Model);
        }
        catch (ArgumentException ex)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        var strategy = new RollingAutopilotStrategy(llmClient);
        var results = new List<SimulationRunResult>(scenarios.Count);
        var artifactPaths = new List<object>();

        foreach (var scenario in scenarios)
        {
            var result = AutopilotSimulator.RunAsync(
                scenario,
                profile,
                slug,
                effectiveProvider,
                strategy,
                parsed.Retries).GetAwaiter().GetResult();
            results.Add(result);

            if (!parsed.DryRun && !string.IsNullOrWhiteSpace(parsed.OutputDir))
            {
                var (reportPath, timelinePath) = SimulationReportWriter.WriteArtifacts(parsed.OutputDir, result);
                artifactPaths.Add(new { scenario = scenario.Name, reportPath, timelinePath });
            }
        }

        var allPassed = results.All(r => r.Passed);

        if (parsed.AsJson)
        {
            var payload = new
            {
                slug,
                provider = effectiveProvider,
                dryRun = parsed.DryRun,
                allPassed,
                artifacts = artifactPaths,
                scenarios = results
            };
            stdout.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
            return allPassed ? 0 : 1;
        }

        WriteSimulateText(stdout, slug, effectiveProvider, parsed.DryRun, results, parsed.OutputDir);
        return allPassed ? 0 : 1;
    }

    private static void WriteSimulateText(
        TextWriter stdout,
        string slug,
        string provider,
        bool dryRun,
        List<SimulationRunResult> results,
        string? outputDir)
    {
        var passedCount = results.Count(r => r.Passed);
        var totalElapsed = results.Sum(r => r.TotalElapsedSeconds);
        stdout.WriteLine($"=== Autopilot Multi-Day Simulation: {slug} (provider={provider}, dryRun={dryRun}, passed={passedCount}/{results.Count}, elapsed={totalElapsed:F1}s) ===");

        foreach (var res in results)
        {
            var verdict = res.Passed ? "PASS" : "FAIL";
            var failCount = res.AllFindings.Count(f => string.Equals(f.Severity, "fail", StringComparison.Ordinal));
            stdout.WriteLine($"Scenario: {res.ScenarioName} [{verdict}] | steps={res.TotalSteps}, attempts={res.TotalAttempts}, failures={failCount}, elapsed={res.TotalElapsedSeconds:F1}s");
            foreach (var step in res.Steps)
            {
                var status = step.Outcome.Success ? "ok" : "FAILED";
                var upserts = step.Outcome.ReconciledSchedule?.CompiledUpserts.Count ?? 0;
                var deletes = step.Outcome.ReconciledSchedule?.DeletedEventIds.Count ?? 0;
                var msgTag = !string.IsNullOrWhiteSpace(step.AthleteMessage) ? $" | msg=\"{step.AthleteMessage}\"" : string.Empty;
                var runTag = !string.IsNullOrWhiteSpace(step.InjectedRunName) ? $" | run=\"{step.InjectedRunName}\"" : string.Empty;
                stdout.WriteLine($"  Day +{step.DayOffset} ({step.Date:yyyy-MM-dd ddd}) {step.Trigger,-17} | {status,-6} | health={step.HealthStateBefore ?? "-"}->{step.HealthStateAfter ?? "-"} | watch={step.ScheduledWatchCount} preview={step.ScheduledPreviewCount} upserts={upserts} deletes={deletes}{runTag}{msgTag}");

                foreach (var finding in step.AuditFindings)
                {
                    stdout.WriteLine($"    [{finding.Severity.ToUpperInvariant()} {finding.AuditCode}] {finding.Message}");
                }
            }
        }

        if (!dryRun && !string.IsNullOrWhiteSpace(outputDir))
        {
            stdout.WriteLine($"Artifacts written under: {outputDir}");
        }
    }

    private static void WriteCoachPlanText(
        TextWriter stdout,
        string slug,
        string provider,
        bool dryRun,
        CoachPlanningOutcome outcome)
    {
        var schedule = outcome.ReconciledSchedule!;
        var decision = outcome.Decision!;
        var totalElapsed = outcome.Attempts.Sum(a => a.ElapsedSeconds);

        stdout.WriteLine($"=== Autopilot Coach Plan: {slug} (dryRun={dryRun}, provider={provider}, model={outcome.ModelUsed ?? "-"}, attempts={outcome.Attempts.Count}, elapsed={totalElapsed:F1}s) ===");
        foreach (var attempt in outcome.Attempts)
        {
            var status = attempt.LlmSucceeded && attempt.JsonParsed && attempt.ValidationErrors.Count == 0
                ? "ok"
                : string.Join(", ", attempt.ValidationErrors.Select(e => e.Code));
            if (string.IsNullOrEmpty(status))
            {
                status = attempt.ParseError ?? attempt.LlmError ?? "failed";
            }
            stdout.WriteLine($"  Attempt {attempt.AttemptNumber} ({attempt.ElapsedSeconds:F1}s): {status}");
        }

        WriteProjectedPlanText(stdout, slug, schedule);

        stdout.WriteLine("Rationale:");
        stdout.WriteLine($"  Observations: {string.Join(" | ", decision.Rationale.Observations)}");
        stdout.WriteLine($"  Tradeoff:     {decision.Rationale.TradeoffWeighed}");
        stdout.WriteLine($"  Intent:       {decision.Rationale.ProgressionIntent}");
        stdout.WriteLine($"  Rejected:     {decision.Rationale.RejectedAlternative}");

        stdout.WriteLine($"Updated BlockCompass ({decision.UpdatedCompass.HealthState ?? decision.HealthState}):");
        stdout.WriteLine($"  Focus:   {decision.UpdatedCompass.CurrentBlockFocus}");
        stdout.WriteLine($"  Base:    {decision.UpdatedCompass.BaseMilestone}");
        stdout.WriteLine($"  Quality: {decision.UpdatedCompass.QualityMilestone}");
        stdout.WriteLine($"  Summary: {decision.UpdatedCompass.LastDecisionSummary}");

        stdout.WriteLine($"Compiled Intervals.icu DSL Upserts ({schedule.CompiledUpserts.Count}):");
        foreach (var item in schedule.CompiledUpserts)
        {
            stdout.WriteLine($"--- {item.Date:yyyy-MM-dd (ddd)} [{item.Tier}] {item.Title} ({item.SessionType}, {item.TotalDurationSeconds / 60}m, {item.EstimatedLoad:F1} TSS) ---");
            if (item.CompiledWorkout is not null)
            {
                stdout.Write(item.CompiledWorkout.DslText);
            }
        }
    }

    private static int ExecuteProjectPlan(
        ParsedCliArgs parsed,
        TextWriter stdout,
        TextWriter stderr,
        string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(parsed.InputPath))
        {
            stderr.WriteLine("Missing required '--input <path>' option for 'project-plan'.");
            return 1;
        }

        var decision = StrictJsonLoader.LoadFromFile<AutopilotPlanDecision>(parsed.InputPath);
        var layout = string.IsNullOrWhiteSpace(parsed.WorkspaceRoot)
            ? WorkspaceLayout.DiscoverFrom(workingDirectory)
            : WorkspaceLayout.FromRoot(parsed.WorkspaceRoot);
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var slugs = AthleteDataRepository.ResolveTargetSlugs(registry, parsed.AthleteSlug, allActive: false);
        var slug = slugs[0];
        var data = AthleteDataRepository.LoadAthleteData(layout, registry, slug);
        var refDate = parsed.RefDate ?? decision.ReferenceDate;

        var dossier = CoachIntelligenceDossierBuilder.Build(
            data.Profile,
            data.Activities,
            data.WellnessRecords,
            data.RunReviews,
            data.CalendarEvents,
            refDate,
            parsed.TargetDate ?? refDate,
            parsed.AutoEventIds,
            parsed.LockedDates);

        var validation = AutopilotScheduleGatekeeper.Validate(decision, dossier);
        if (!validation.IsValid)
        {
            if (parsed.AsJson)
            {
                var errPayload = new
                {
                    slug,
                    isValid = false,
                    errors = validation.Errors,
                    schedule = (ReconciledAutopilotSchedule?)null
                };
                stdout.WriteLine(JsonSerializer.Serialize(errPayload, JsonOptions));
            }
            else
            {
                stderr.WriteLine($"Autopilot plan validation failed ({validation.Errors.Count} error(s)):");
                foreach (var err in validation.Errors)
                {
                    stderr.WriteLine($"  [{err.Code}] {err.Field}: {err.Message}");
                }
            }

            return 1;
        }

        var reconciled = AutopilotScheduleGatekeeper.Reconcile(decision, dossier);

        if (parsed.AsJson)
        {
            var okPayload = new
            {
                slug,
                isValid = true,
                errors = validation.Errors,
                schedule = reconciled
            };
            stdout.WriteLine(JsonSerializer.Serialize(okPayload, JsonOptions));
        }
        else
        {
            WriteProjectedPlanText(stdout, slug, reconciled);
        }

        return 0;
    }

    private static void WriteProjectedPlanText(
        TextWriter stdout,
        string slug,
        ReconciledAutopilotSchedule schedule)
    {
        stdout.WriteLine($"=== 14-Day Autopilot Projection: {slug} (ref={schedule.ReferenceDate:yyyy-MM-dd}, trigger={schedule.Trigger}, health={schedule.HealthState}) ===");
        stdout.WriteLine($"Deleted Event IDs ({schedule.DeletedEventIds.Count}): {(schedule.DeletedEventIds.Count == 0 ? "none" : string.Join(", ", schedule.DeletedEventIds))}");

        stdout.WriteLine($"Days 1-7 Watch Commit Window ({schedule.WatchCommitWindow.Count} session(s)):");
        foreach (var item in schedule.WatchCommitWindow)
        {
            var dur = item.TotalDurationSeconds.HasValue ? $"{item.TotalDurationSeconds.Value / 60}m" : "-";
            stdout.WriteLine($"  {item.Date:yyyy-MM-dd (ddd)} | [{item.Ownership}] | {item.SessionType ?? item.Source,-19} | {dur,4} | {item.EstimatedLoad,4:F1} TSS | {item.Title}");
        }

        stdout.WriteLine($"Days 8-14 Preview Window ({schedule.PreviewWindow.Count} session(s)):");
        foreach (var item in schedule.PreviewWindow)
        {
            var dur = item.TotalDurationSeconds.HasValue ? $"{item.TotalDurationSeconds.Value / 60}m" : "-";
            stdout.WriteLine($"  {item.Date:yyyy-MM-dd (ddd)} | [{item.Ownership}] | {item.SessionType ?? item.Source,-19} | {dur,4} | {item.EstimatedLoad,4:F1} TSS | {item.Title}");
        }

        if (schedule.BanisterProjection is { } banister)
        {
            stdout.WriteLine(
                $"Banister 14d Projection: CTL {banister.StartingCtl:F1} -> {banister.EndingCtl:F1} | " +
                $"ATL {banister.StartingAtl:F1} -> {banister.EndingAtl:F1} | " +
                $"TSB {banister.StartingTsb:+0.0;-0.0;0.0} -> {banister.EndingTsb:+0.0;-0.0;0.0} " +
                $"(min {banister.MinProjectedTsb:+0.0;-0.0;0.0} on {banister.MinTsbDate:yyyy-MM-dd}, total={banister.TotalProjectedLoad:F1} TSS)");
        }
        else
        {
            stdout.WriteLine("Banister 14d Projection: unavailable (no CTL/ATL baseline)");
        }
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

    private static bool TryReadLongSet(
        IReadOnlyList<string> args,
        ref int index,
        string flag,
        out IReadOnlySet<long>? values,
        out string error)
    {
        if (!TryReadValue(args, ref index, flag, out var raw, out error))
        {
            values = null;
            return false;
        }

        var set = new HashSet<long>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                values = null;
                error = $"Invalid integer ID '{part}' for option '{flag}'.";
                return false;
            }

            set.Add(id);
        }

        values = set;
        return true;
    }

    private static bool TryReadDateSet(
        IReadOnlyList<string> args,
        ref int index,
        string flag,
        out IReadOnlySet<DateOnly>? values,
        out string error)
    {
        if (!TryReadValue(args, ref index, flag, out var raw, out error))
        {
            values = null;
            return false;
        }

        var set = new HashSet<DateOnly>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!DateOnly.TryParseExact(part, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                values = null;
                error = $"Invalid date '{part}' for option '{flag}'. Expected YYYY-MM-DD.";
                return false;
            }

            set.Add(d);
        }

        values = set;
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
        string? InputPath,
        IReadOnlySet<long>? AutoEventIds,
        IReadOnlySet<DateOnly>? LockedDates,
        string Trigger,
        string? Provider,
        string? Model,
        string? Message,
        string? StubResponsePath,
        string? ScenarioName,
        string? OutputDir,
        int Retries,
        bool DryRun,
        bool Force,
        string? ActivityId);
}
