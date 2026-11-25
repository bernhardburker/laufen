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
            var layout = string.IsNullOrWhiteSpace(parsed.WorkspaceRoot)
                ? WorkspaceLayout.DiscoverFrom(workingDirectory)
                : WorkspaceLayout.FromRoot(parsed.WorkspaceRoot);

            return parsed.Command switch
            {
                "athletes" => ExecuteAthletes(layout, parsed.AsJson, stdout),
                "report" => ExecuteReport(layout, parsed, stdout),
                "markers" => ExecuteMarkers(layout, parsed, stdout),
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
        stdout.WriteLine("  --ref-date <YYYY-MM-DD>   Reference date for 'markers'");
        stdout.WriteLine("  --target-date <YYYY-MM-DD> Target planning date for 'markers'");
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
            targetDate);
        return true;
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
        DateOnly? TargetDate);
}
