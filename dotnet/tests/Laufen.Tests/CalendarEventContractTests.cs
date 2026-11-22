using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class CalendarEventContractTests
{
    private static WorkspaceLayout ResolveRepositoryLayout()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "athletes", "registry.json");
            if (File.Exists(candidate))
            {
                return WorkspaceLayout.FromRoot(current.FullName);
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing 'athletes/registry.json'.");
    }

    [Test]
    public void LoadCalendarEventsFromRealBerniWorkspaceDeserializesStrictly()
    {
        var layout = ResolveRepositoryLayout();

        var events = StrictJsonLoader.LoadCalendarEvents(layout, "berni");

        Assert.That(events, Is.Not.Empty);
        Assert.That(events.All(e => e.Id > 0), Is.True);
        Assert.That(events.All(e => !string.IsNullOrWhiteSpace(e.AthleteId)), Is.True);
        Assert.That(events.All(e => !string.IsNullOrWhiteSpace(e.Category)), Is.True);
        Assert.That(events.Any(e => e.WorkoutDoc?.Steps is { Count: > 0 }), Is.True);
    }

    [Test]
    public void StrictDeserializerSupportsMinimalRestNoteEvent()
    {
        const string noteJson = """
            [
              {
                "id": 999001,
                "athlete_id": "i697752",
                "category": "NOTE",
                "start_date_local": "2026-11-15T00:00:00",
                "name": "🛋️ Ruhetag (Coach)"
              }
            ]
            """;

        var events = StrictJsonLoader.Deserialize<List<CalendarEventEntry>>(noteJson);

        Assert.That(events, Has.Count.EqualTo(1));
        var ctx = PlanningMarkersCalculator.GetCalendarContext(events, new DateOnly(2026, 11, 15));
        Assert.That(ctx.HasCalendar, Is.True);
        Assert.That(ctx.HasRestNoteToday, Is.True);
        Assert.That(ctx.HasCompletedWorkoutToday, Is.False);
        Assert.That(ctx.HasPlannedWorkoutToday, Is.False);
    }

    [Test]
    public void StrictDeserializerRejectsUnknownCalendarEventField()
    {
        const string jsonWithDrift = """
            [
              {
                "id": 999002,
                "athlete_id": "i697752",
                "category": "WORKOUT",
                "start_date_local": "2026-11-15T07:00:00",
                "unexpected_calendar_field": true
              }
            ]
            """;

        Assert.Throws<JsonException>(() => StrictJsonLoader.Deserialize<List<CalendarEventEntry>>(jsonWithDrift));
    }

    [TestCase("2026-11-12")]
    [TestCase("2026-11-14")]
    [TestCase("2026-11-11")]
    public void GetCalendarContextMatchesPythonExtractMarkersOnRealBerniEvents(string refDateIso)
    {
        var layout = ResolveRepositoryLayout();
        var events = StrictJsonLoader.LoadCalendarEvents(layout, "berni");
        var refDate = DateOnly.Parse(refDateIso, CultureInfo.InvariantCulture);

        var cs = PlanningMarkersCalculator.GetCalendarContext(events, refDate);

        var pythonExec = Path.Combine(layout.RootDirectory, ".venv", "bin", "python");
        const string script = """
            import datetime, json, sys
            from pathlib import Path
            from src.planner.extract_markers import get_calendar_context

            events = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
            ref_d = datetime.date.fromisoformat(sys.argv[2])
            print(json.dumps(get_calendar_context(events, ref_d)))
            """;

        var eventsPath = Path.Combine(layout.AthletesDirectory, "berni", "calendar_events.json");
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExec,
            WorkingDirectory = layout.RootDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add(eventsPath);
        startInfo.ArgumentList.Add(refDateIso);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Python process.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.That(process.ExitCode, Is.EqualTo(0), stderr);

        using var pyDoc = JsonDocument.Parse(stdout);
        var py = pyDoc.RootElement;

        Assert.That(cs.HasCalendar, Is.EqualTo(py.GetProperty("has_calendar").GetBoolean()));
        Assert.That(cs.EventsTodayCount, Is.EqualTo(py.GetProperty("events_today_count").GetInt32()));
        Assert.That(cs.HasCompletedWorkoutToday, Is.EqualTo(py.GetProperty("today_completed_workout").ValueKind != JsonValueKind.Null));
        Assert.That(cs.HasPlannedWorkoutToday, Is.EqualTo(py.GetProperty("today_planned_workout").ValueKind != JsonValueKind.Null));
        Assert.That(cs.HasRestNoteToday, Is.EqualTo(py.GetProperty("today_rest_note").ValueKind != JsonValueKind.Null));
    }
}
