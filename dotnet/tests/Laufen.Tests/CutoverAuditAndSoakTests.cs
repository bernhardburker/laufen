using System.Text.Json;
using Laufen.Cli;
using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class CutoverAuditAndSoakTests
{
    [Test]
    public void ParityAuditAllPassesAndEmitsMarkdownAndJson()
    {
        using var jsonOut = new StringWriter();
        using var jsonErr = new StringWriter();
        var jsonExit = LaufenCliRunner.Run(
            ["parity-audit", "--all", "--ref-date", "2026-11-25", "--json"],
            jsonOut,
            jsonErr,
            AppContext.BaseDirectory);

        Assert.That(jsonExit, Is.EqualTo(0), jsonErr.ToString() + jsonOut.ToString());
        using (var doc = JsonDocument.Parse(jsonOut.ToString()))
        {
            Assert.That(doc.RootElement.GetProperty("allPassed").GetBoolean(), Is.True);
            Assert.That(doc.RootElement.GetProperty("failedChecks").GetInt32(), Is.EqualTo(0));
            Assert.That(doc.RootElement.GetProperty("athletes").GetArrayLength(), Is.EqualTo(2));
        }

        using var mdOut = new StringWriter();
        using var mdErr = new StringWriter();
        var mdExit = LaufenCliRunner.Run(
            ["parity-audit", "--athlete", "berni", "--ref-date", "2026-11-25", "--markdown"],
            mdOut,
            mdErr,
            AppContext.BaseDirectory);

        Assert.That(mdExit, Is.EqualTo(0), mdErr.ToString());
        Assert.That(mdOut.ToString(), Does.Contain("## .NET 11 Cutover Parity Audit"));
        Assert.That(mdOut.ToString(), Does.Contain("SIMULATION-07"));
    }

    [Test]
    public void StagingSoakRunsAllNineStepsEndToEndWithZeroSecondPassChurn()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = LaufenCliRunner.Run(
            ["staging-soak", "--athlete", "berni", "--ref-date", "2026-11-25", "--json"],
            stdout,
            stderr,
            AppContext.BaseDirectory);

        Assert.That(exitCode, Is.EqualTo(0), stderr.ToString() + stdout.ToString());
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.That(doc.RootElement.GetProperty("passed").GetBoolean(), Is.True);
        var steps = doc.RootElement.GetProperty("steps");
        Assert.That(steps.GetArrayLength(), Is.EqualTo(9));

        foreach (var step in steps.EnumerateArray())
        {
            Assert.That(step.GetProperty("succeeded").GetBoolean(), Is.True, $"Step {step.GetProperty("stepName").GetString()} failed.");
        }

        var syncStep = steps.EnumerateArray().First(s => s.GetProperty("stepName").GetString() == "sync-calendar");
        Assert.That(syncStep.GetProperty("summary").GetString(), Does.Contain("SecondPassChurn=0"));

        // Production safety guard remains active by default
        var prodEntry = new AthleteRegistryEntry
        {
            IntervalsId = "i697752",
            DisplayName = "Berni",
            Active = true,
            DashboardSlug = "berni"
        };
        Assert.That(StagingSafetyGuard.EvaluateWritePermission("berni", prodEntry, allowProductionWrite: false).Allowed, Is.False);
    }
}
