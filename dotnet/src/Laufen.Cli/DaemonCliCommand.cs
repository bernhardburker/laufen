using System.Text.Json;
using Laufen.Domain;
using Laufen.Infrastructure;

namespace Laufen.Cli;

public static partial class LaufenCliRunner
{
    private static int ExecuteDaemon(
        ParsedCliArgs parsed,
        TextWriter stdout,
        TextWriter stderr,
        string? workingDirectory)
    {
        var layout = string.IsNullOrWhiteSpace(parsed.WorkspaceRoot)
            ? WorkspaceLayout.DiscoverFrom(workingDirectory)
            : WorkspaceLayout.FromRoot(parsed.WorkspaceRoot);
        var registry = AthleteDataRepository.LoadRegistry(layout);

        var effectiveDryRun = parsed.DryRun || (!parsed.Apply && !parsed.Send);
        if (!effectiveDryRun && !parsed.AllowProductionWrite)
        {
            foreach (var (slug, entry) in registry.Athletes.Where(kvp => kvp.Value.Active))
            {
                var permission = StagingSafetyGuard.EvaluateWritePermission(slug, entry, parsed.AllowProductionWrite);
                if (!permission.Allowed)
                {
                    stderr.WriteLine(permission.Reason);
                    return 1;
                }
            }
        }

        var effectiveProvider = string.IsNullOrWhiteSpace(parsed.Provider) ? "agy" : parsed.Provider;
        string? stubJson = null;
        if (!string.IsNullOrWhiteSpace(parsed.StubResponsePath))
        {
            stubJson = File.ReadAllText(parsed.StubResponsePath);
        }

        ILlmClient plannerLlm;
        ILlmClient narratorLlm;
        try
        {
            var isDefaultStub = effectiveProvider.Trim().ToLowerInvariant() is "stub" or "mock" or "test" && stubJson is null;
            plannerLlm = isDefaultStub
                ? new SimulationStubLlmClient()
                : LlmClientFactory.Create(effectiveProvider, parsed.Model, stubJson);
            narratorLlm = isDefaultStub
                ? new NarrativeStubLlmClient()
                : LlmClientFactory.Create(effectiveProvider, parsed.Model, stubJson);
        }
        catch (ArgumentException ex)
        {
            stderr.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        ITelegramBotClient? telegramClient = null;
        var botToken = DotEnvLoader.ResolveSecret(layout, "TELEGRAM_BOT_TOKEN");
        if (!string.IsNullOrWhiteSpace(botToken) && !effectiveDryRun)
        {
            telegramClient = new TelegramBotHttpClient(botToken);
        }

        IIntervalsIcuClient? intervalsClient = null;
        var apiKey = DotEnvLoader.ResolveSecret(layout, "INTERVALS_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey) && !effectiveDryRun)
        {
            var apiBase = DotEnvLoader.ResolveSecret(layout, "INTERVALS_API_BASE") ?? "https://intervals.icu/api/v1";
            intervalsClient = new IntervalsIcuHttpClient(apiKey, apiBase);
        }

        IDaemonClock clock = parsed.RefDate is { } d
            ? new FixedDaemonClock(new DateTimeOffset(d.Year, d.Month, d.Day, 7, 0, 0, TimeSpan.Zero))
            : SystemDaemonClock.Instance;

        var config = new DaemonRuntimeConfig
        {
            PollIntervalSeconds = parsed.PollIntervalSeconds,
            ActivityPollIntervalMinutes = parsed.ActivityPollMinutes,
            DryRun = effectiveDryRun,
            ApplyCalendar = parsed.Apply,
            SendTelegram = parsed.Send,
            AllowProductionWrite = parsed.AllowProductionWrite,
            MaxValidationRetries = parsed.Retries
        };

        var runner = new AutopilotDaemonRunner(clock);

        if (parsed.Once || effectiveDryRun)
        {
            var summary = runner.RunSingleTickAsync(
                layout,
                config,
                telegramClient,
                intervalsClient,
                plannerLlm,
                narratorLlm).GetAwaiter().GetResult();

            if (parsed.AsJson)
            {
                stdout.WriteLine(JsonSerializer.Serialize(summary, JsonOptions));
            }
            else
            {
                stdout.WriteLine($"=== Autopilot Daemon Tick ({summary.Timestamp:yyyy-MM-dd HH:mm:ss zzz}, dryRun={summary.DryRun}) ===");
                stdout.WriteLine($"Updates={summary.UpdatesProcessed}, Callbacks={summary.CallbacksAnswered}, Overrides={summary.OverridesExecuted}");
                stdout.WriteLine($"Scheduled: morning={summary.MorningBriefingsTriggered}, evening={summary.EveningPreviewsTriggered}, postRun={summary.PostRunReviewsTriggered}, dashboards={summary.DashboardsRebuilt}");
                foreach (var err in summary.Errors)
                {
                    stdout.WriteLine($"  [ERROR] {err}");
                }
            }

            return summary.Errors.Count == 0 ? 0 : 1;
        }

        using var cts = new CancellationTokenSource();
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            cts.Cancel();
        }
        void OnProcessExit(object? sender, EventArgs e)
        {
            cts.Cancel();
        }

        Console.CancelKeyPress += OnCancel;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        try
        {
            runner.RunLoopAsync(
                layout,
                config,
                telegramClient,
                intervalsClient,
                plannerLlm,
                narratorLlm,
                cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Clean shutdown via SIGINT / SIGTERM
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        }

        return 0;
    }
}
