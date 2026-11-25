using System.Text.Json;
using Laufen.Cli;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class CliRunnerTests
{
    private static string ResolveWorkspaceRoot() =>
        WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory).RootDirectory;

    [Test]
    public void RunWithHealthFlagReturnsOkJson()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(["--health"], stdout, stderr);
        using var doc = JsonDocument.Parse(stdout.ToString().Trim());

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stderr.ToString(), Is.Empty);
        Assert.That(doc.RootElement.GetProperty("status").GetString(), Is.EqualTo("ok"));
    }

    [Test]
    public void RunHelpReturnsZeroAndListsCommands()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(["help"], stdout, stderr);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stdout.ToString(), Does.Contain("report"));
        Assert.That(stdout.ToString(), Does.Contain("markers"));
        Assert.That(stdout.ToString(), Does.Contain("dossier"));
        Assert.That(stdout.ToString(), Does.Contain("athletes"));
    }

    [Test]
    public void RunAthletesJsonReturnsRegisteredTenants()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(
            ["athletes", "--json", "--workspace", ResolveWorkspaceRoot()],
            stdout,
            stderr);

        Assert.That(exitCode, Is.EqualTo(0));
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.That(doc.RootElement.GetProperty("defaultAthlete").GetString(), Is.EqualTo("berni"));
        Assert.That(doc.RootElement.GetProperty("athletes").GetArrayLength(), Is.GreaterThanOrEqualTo(2));
    }

    [TestCase("berni")]
    [TestCase("berni-test")]
    public void RunReportJsonForTenantProducesValidSnapshot(string slug)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(
            ["report", "--athlete", slug, "--limit", "3", "--weeks", "4", "--as-of", "2026-11-11", "--json"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.That(doc.RootElement.GetProperty("slug").GetString(), Is.EqualTo(slug));

        var report = doc.RootElement.GetProperty("report");
        Assert.That(report.GetProperty("recentRuns").GetArrayLength(), Is.LessThanOrEqualTo(3));
        Assert.That(report.GetProperty("weeklyTrends").GetArrayLength(), Is.LessThanOrEqualTo(4));
        Assert.That(report.GetProperty("fitnessForm").GetProperty("latestCtl").GetDouble(), Is.InRange(0.0, 200.0));
    }

    [Test]
    public void RunReportTextForDefaultAthleteFormatsReadableOutput()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(
            ["report", "--limit", "2", "--as-of", "2026-11-11"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
        var text = stdout.ToString();
        Assert.That(text, Does.Contain("=== Report:"));
        Assert.That(text, Does.Contain("Fitness/Form:"));
        Assert.That(text, Does.Contain("Recent Runs"));
        Assert.That(text, Does.Contain("Weekly Trends"));
    }

    [Test]
    public void RunReportAllJsonReturnsActiveAthletes()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(
            ["report", "--all", "--limit", "2", "--json"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.That(doc.RootElement.GetProperty("athletes").GetArrayLength(), Is.GreaterThanOrEqualTo(1));
    }

    [TestCase("berni")]
    [TestCase("berni-test")]
    public void RunMarkersJsonForTenantProducesValidSnapshot(string slug)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(
            ["markers", "--athlete", slug, "--ref-date", "2026-11-11", "--target-date", "2026-11-12", "--json"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.That(doc.RootElement.GetProperty("slug").GetString(), Is.EqualTo(slug));

        var markers = doc.RootElement.GetProperty("markers");
        Assert.That(markers.GetProperty("referenceDate").GetString(), Is.EqualTo("2026-11-11"));
        Assert.That(markers.GetProperty("targetDate").GetString(), Is.EqualTo("2026-11-12"));
        Assert.That(markers.GetProperty("guardrails").TryGetProperty("activeWarnings", out _), Is.True);
    }

    [Test]
    public void RunMarkersTextFormatsReadableOutput()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(
            ["markers", "--athlete", "berni", "--ref-date", "2026-11-11"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
        var text = stdout.ToString();
        Assert.That(text, Does.Contain("=== Planning Markers: berni"));
        Assert.That(text, Does.Contain("Readiness:"));
        Assert.That(text, Does.Contain("Periodization:"));
        Assert.That(text, Does.Contain("Guardrails:"));
    }

    [Test]
    public void RunWithUnknownAthleteReturnsErrorExitCode()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(
            ["report", "--athlete", "nonexistent-athlete"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(stderr.ToString(), Does.Contain("Unknown athlete slug"));
    }

    [Test]
    public void RunWithInvalidDateReturnsErrorExitCode()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(
            ["markers", "--ref-date", "11-11-2026"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(stderr.ToString(), Does.Contain("Expected YYYY-MM-DD"));
    }

    [TestCase("berni")]
    [TestCase("berni-test")]
    public void RunDossierJsonForTenantProducesValidSnapshot(string slug)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(
            ["dossier", "--athlete", slug, "--ref-date", "2026-11-25", "--json"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.That(doc.RootElement.GetProperty("slug").GetString(), Is.EqualTo(slug));

        var dossier = doc.RootElement.GetProperty("dossier");
        Assert.That(dossier.GetProperty("referenceDate").GetString(), Is.EqualTo("2026-11-25"));
        Assert.That(dossier.GetProperty("stimulusProgression").TryGetProperty("ledger", out _), Is.True);
        Assert.That(dossier.GetProperty("readiness").TryGetProperty("medicalRedFlags", out _), Is.True);
    }

    [Test]
    public void RunDossierTextFormatsReadableOutput()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = LaufenCliRunner.Run(
            ["dossier", "--athlete", "berni", "--ref-date", "2026-11-25"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(0), stderr.ToString());
        var text = stdout.ToString();
        Assert.That(text, Does.Contain("=== Intelligence Dossier:"));
        Assert.That(text, Does.Contain("Base Progression Chain"));
        Assert.That(text, Does.Contain("Quality Progression Chain"));
        Assert.That(text, Does.Contain("Data Quality Notes"));
    }
}
