using System.Text.Json;
using Laufen.Cli;
using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

public class SkeletonSmokeTests
{
    [Test]
    public void RuntimeManifestCreateDefaultTargetsNet11ShadowMode()
    {
        var manifest = RuntimeManifest.CreateDefault();

        Assert.That(manifest.ApplicationName, Is.EqualTo("Laufen.DotNet"));
        Assert.That(manifest.TargetFramework, Is.EqualTo("net11.0"));
        Assert.That(manifest.ExecutionMode, Is.EqualTo("shadow-test-only"));
    }

    [Test]
    public void WorkspaceLayoutFromRootResolvesAthletesDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "laufen-workspace-sample");

        var layout = WorkspaceLayout.FromRoot(root);

        Assert.That(layout.RootDirectory, Is.EqualTo(Path.GetFullPath(root)));
        Assert.That(layout.AthletesDirectory, Is.EqualTo(Path.Combine(Path.GetFullPath(root), "athletes")));
    }

    [Test]
    public void CliHealthRunnerWithHealthFlagReturnsZeroAndValidJsonPayload()
    {
        using var output = new StringWriter();

        var exitCode = CliHealthRunner.Run(["--health"], output);
        var json = output.ToString().Trim();
        using var document = JsonDocument.Parse(json);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(document.RootElement.GetProperty("status").GetString(), Is.EqualTo("ok"));
        Assert.That(document.RootElement.GetProperty("targetFramework").GetString(), Is.EqualTo("net11.0"));
        Assert.That(document.RootElement.GetProperty("executionMode").GetString(), Is.EqualTo("shadow-test-only"));
    }

    [Test]
    public void CliHealthRunnerWithUnknownArgumentReturnsNonZeroExitCode()
    {
        using var output = new StringWriter();

        var exitCode = CliHealthRunner.Run(["--unknown"], output);

        Assert.That(exitCode, Is.EqualTo(1));
    }
}
