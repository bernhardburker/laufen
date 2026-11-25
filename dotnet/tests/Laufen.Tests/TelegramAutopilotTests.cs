using System.Net;
using System.Text;
using System.Text.Json;
using Laufen.Domain;
using Laufen.Infrastructure;
using NUnit.Framework;

namespace Laufen.Tests;

[TestFixture]
public sealed class TelegramAutopilotTests
{
    private static readonly DateOnly RefDate = new(2026, 11, 25);

    [Test]
    public void StrictJsonLoaderDeserializesRealBerniTelegramState()
    {
        var layout = WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);
        var state = StrictJsonLoader.LoadTelegramState(layout, "berni");

        Assert.That(state, Is.Not.Null);
        Assert.That(state!.LastBriefingDate, Is.EqualTo("2026-11-04"));
        Assert.That(state.NotifiedActivityIds, Is.Not.Null);
        Assert.That(state.NotifiedActivityIds, Does.Contain("i205281485"));
        Assert.That(state.CurrentProposal, Is.Not.Null);
        Assert.That(state.CurrentProposal!.EventId, Is.EqualTo(151981823L));
    }

    [Test]
    public void RegistryEntrySupportsGranularNotificationPreferences()
    {
        const string json = """
            {
              "default_athlete": "demo",
              "athletes": {
                "demo": {
                  "intervals_id": "i999",
                  "display_name": "Demo",
                  "active": true,
                  "dashboard_slug": "demo",
                  "telegram_chat_id": "42",
                  "briefing_enabled": true,
                  "briefing_time": "07:15",
                  "morning_briefing_enabled": true,
                  "evening_briefing_time": "19:45",
                  "evening_briefing_enabled": true,
                  "post_run_summary_enabled": false
                }
              }
            }
            """;

        var registry = StrictJsonLoader.Deserialize<AthleteRegistry>(json);
        var entry = registry.Athletes["demo"];

        Assert.That(TelegramPreferencePolicy.IsMorningBriefingEnabled(entry), Is.True);
        Assert.That(TelegramPreferencePolicy.IsEveningBriefingEnabled(entry), Is.True);
        Assert.That(TelegramPreferencePolicy.IsPostRunSummaryEnabled(entry), Is.False);
        Assert.That(TelegramPreferencePolicy.GetMorningBriefingTime(entry), Is.EqualTo("07:15"));
        Assert.That(TelegramPreferencePolicy.GetEveningBriefingTime(entry), Is.EqualTo("19:45"));
    }

    [Test]
    public void InteractionRouterEnforcesAuthIdempotencyAndLockVsOverrideRouting()
    {
        var entry = new AthleteRegistryEntry
        {
            IntervalsId = "i697752",
            DisplayName = "Berni",
            Active = true,
            DashboardSlug = "berni",
            TelegramChatId = "157754568",
            BriefingEnabled = true
        };

        Assert.That(TelegramInteractionRouter.IsUserAuthorized(entry, "157754568"), Is.True);
        Assert.That(TelegramInteractionRouter.IsUserAuthorized(entry, "999999"), Is.False);
        Assert.That(TelegramInteractionRouter.IsUserAuthorized(entry, null), Is.False);

        var sentState = TelegramInteractionRouter.MarkMorningBriefingSent(null, RefDate);
        Assert.That(TelegramInteractionRouter.ShouldSendMorningBriefing(sentState, RefDate), Is.False);
        Assert.That(TelegramInteractionRouter.ShouldSendMorningBriefing(sentState, RefDate, force: true), Is.True);
        Assert.That(TelegramInteractionRouter.ShouldSendMorningBriefing(sentState, RefDate.AddDays(1)), Is.True);

        var notifiedState = TelegramInteractionRouter.MarkActivityNotified(sentState, "i213105633");
        Assert.That(TelegramInteractionRouter.ShouldNotifyCompletedRun(notifiedState, "i213105633"), Is.False);
        Assert.That(TelegramInteractionRouter.ShouldNotifyCompletedRun(notifiedState, "i999999"), Is.True);

        var acceptRoute = TelegramInteractionRouter.RouteCallback("accept:2026-11-26", RefDate);
        Assert.That(acceptRoute.RequiresLlmReplan, Is.False);
        Assert.That(acceptRoute.LocksDate, Is.True);
        Assert.That(acceptRoute.TargetDate, Is.EqualTo(new DateOnly(2026, 11, 26)));

        var keepRestRoute = TelegramInteractionRouter.RouteCallback("keep_rest:2026-11-27", RefDate);
        Assert.That(keepRestRoute.RequiresLlmReplan, Is.False);
        Assert.That(keepRestRoute.LocksDate, Is.True);

        var easierRoute = TelegramInteractionRouter.RouteCallback("easier:2026-11-26", RefDate);
        Assert.That(easierRoute.RequiresLlmReplan, Is.True);
        Assert.That(easierRoute.Trigger, Is.EqualTo("telegram_override"));
        Assert.That(easierRoute.LocksDate, Is.False);

        var restRoute = TelegramInteractionRouter.RouteCallback("rest:2026-11-26", RefDate);
        Assert.That(restRoute.RequiresLlmReplan, Is.True);
        Assert.That(restRoute.LocksDate, Is.True);

        var reentryRoute = TelegramInteractionRouter.RouteCallback("reentry_ready:2026-11-26", RefDate);
        Assert.That(reentryRoute.RequiresLlmReplan, Is.True);
        Assert.That(reentryRoute.Kind, Is.EqualTo("reentry_ready"));

        var freeText = TelegramInteractionRouter.RouteFreeText("Beine heute schwer, max 30m bitte", RefDate);
        Assert.That(freeText.RequiresLlmReplan, Is.True);
        Assert.That(freeText.Trigger, Is.EqualTo("telegram_override"));
        Assert.That(freeText.AthleteMessage, Is.EqualTo("Beine heute schwer, max 30m bitte"));
    }

    [Test]
    public void PresenterFormatsMorningBriefingWithoutAerobicPaceLeaksAndSupportsSickHoldBanner()
    {
        var layout = WorkspaceLayout.DiscoverFrom(AppContext.BaseDirectory);
        var registry = AthleteDataRepository.LoadRegistry(layout);
        var data = AthleteDataRepository.LoadAthleteData(layout, registry, "berni");
        var dossier = CoachIntelligenceDossierBuilder.Build(
            data.Profile,
            data.Activities,
            data.WellnessRecords,
            data.RunReviews,
            data.CalendarEvents,
            RefDate,
            RefDate);

        var schedule = new ReconciledAutopilotSchedule
        {
            ReferenceDate = RefDate,
            Trigger = "morning_sync",
            HealthState = "Normal",
            WatchCommitWindow =
            [
                new ReconciledScheduleWorkout
                {
                    Date = RefDate,
                    Tier = "watch_commit",
                    Ownership = "auto",
                    Source = "llm_decision",
                    Title = "Easy Base 06:00/km",
                    SessionType = "easy",
                    TotalDurationSeconds = 2400,
                    EstimatedLoad = 45,
                    CompiledWorkout = new CompiledWorkout(
                        "Easy Base",
                        "easy",
                        2400,
                        "Warmup\n- 10m Z1 HR\n\nMain Set\n- 25m 06:00-06:20/km Pace\n\nCooldown\n- 5m Z1 HR\n")
                }
            ],
            PreviewWindow = [],
            CompiledUpserts = [],
            DeletedEventIds = []
        };

        var normalMessage = TelegramAutopilotPresenter.FormatMorningBriefing("157754568", dossier, schedule);
        Assert.That(normalMessage.Text, Does.Not.Contain("/km"));
        Assert.That(normalMessage.Text, Does.Contain("Z2 HF"));
        Assert.That(normalMessage.ReplyMarkup, Is.Not.Null);
        Assert.That(normalMessage.ReplyMarkup!.InlineKeyboard.SelectMany(r => r).Select(b => b.CallbackData), Does.Contain("accept:2026-11-25"));

        var sickCompass = new BlockCompassSnapshot
        {
            UpdatedAt = RefDate,
            CurrentBlockFocus = "SickHold recovery",
            BaseMilestone = "Pause",
            QualityMilestone = "Pause",
            ObservedAthletePatterns = [],
            LastDecisionSummary = "SickHold",
            HealthState = "SickHold"
        };

        var sickMessage = TelegramAutopilotPresenter.FormatMorningBriefing("157754568", dossier, schedule: null, compass: sickCompass);
        Assert.That(sickMessage.Text, Does.Contain("SickHold aktiv"));
        Assert.That(sickMessage.ReplyMarkup!.InlineKeyboard[0].Select(b => b.CallbackData), Does.Contain("reentry_ready:2026-11-25"));
    }

    [Test]
    public async Task TelegramBotHttpClientSendsAndParsesRequestsCleanlyWithoutLeakingToken()
    {
        string? capturedSendBody = null;
        var requests = new List<string>();
        using var handler = new FakeTelegramHandler(req =>
        {
            requests.Add($"{req.Method} {req.RequestUri!.PathAndQuery}");
            if (req.RequestUri.AbsolutePath.EndsWith("/sendMessage", StringComparison.Ordinal))
            {
                capturedSendBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return CreateJsonResponse(HttpStatusCode.OK, """{"ok":true,"result":{"message_id":98765}}""");
            }

            if (req.RequestUri.AbsolutePath.EndsWith("/answerCallbackQuery", StringComparison.Ordinal))
            {
                return CreateJsonResponse(HttpStatusCode.OK, """{"ok":true,"result":true}""");
            }

            if (req.RequestUri.AbsolutePath.EndsWith("/getUpdates", StringComparison.Ordinal))
            {
                return CreateJsonResponse(
                    HttpStatusCode.OK,
                    """{"ok":true,"result":[{"update_id":101,"message":{"message_id":5,"chat":{"id":157754568},"text":"Heute leichter","date":1795600000}},{"update_id":102,"callback_query":{"id":"cb-1","from":{"id":157754568},"data":"accept:2026-11-25","message":{"message_id":4,"chat":{"id":157754568},"text":"Briefing"}}}]}""");
            }

            return CreateJsonResponse(HttpStatusCode.InternalServerError, """{"ok":false,"description":"fail"}""");
        });

        using var httpClient = new HttpClient(handler);
        var client = new TelegramBotHttpClient("secret-token-xyz", httpClient);

        var outgoing = new TelegramOutgoingMessage
        {
            ChatId = "157754568",
            Text = "<b>Guten Morgen</b>",
            ReplyMarkup = TelegramAutopilotPresenter.BuildMorningKeyboard(RefDate, hasWorkoutToday: true, isSickHold: false),
            TargetDate = RefDate,
            MessageKind = "morning_briefing"
        };

        var messageId = await client.SendMessageAsync(outgoing).ConfigureAwait(false);
        Assert.That(messageId, Is.EqualTo(98765L));
        Assert.That(capturedSendBody, Is.Not.Null);
        using (var doc = JsonDocument.Parse(capturedSendBody!))
        {
            Assert.That(doc.RootElement.GetProperty("chat_id").GetString(), Is.EqualTo("157754568"));
            Assert.That(doc.RootElement.GetProperty("parse_mode").GetString(), Is.EqualTo("HTML"));
            Assert.That(doc.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard").GetArrayLength(), Is.EqualTo(2));
        }

        await client.AnswerCallbackQueryAsync("cb-1", "Bestätigt").ConfigureAwait(false);
        var updates = await client.GetUpdatesAsync(offset: 100, timeoutSeconds: 0).ConfigureAwait(false);
        Assert.That(updates, Has.Count.EqualTo(2));
        Assert.That(updates[0].Message!.Text, Is.EqualTo("Heute leichter"));
        Assert.That(updates[1].CallbackQuery!.Data, Is.EqualTo("accept:2026-11-25"));
        Assert.That(requests, Has.Count.EqualTo(3));

        using var errHandler = new FakeTelegramHandler(_ =>
            CreateJsonResponse(HttpStatusCode.Unauthorized, """{"ok":false,"description":"Unauthorized"}"""));
        using var errHttp = new HttpClient(errHandler);
        var errClient = new TelegramBotHttpClient("super-secret-bot-token", errHttp);
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            _ = await errClient.SendMessageAsync(outgoing).ConfigureAwait(false));
        Assert.That(ex!.Message, Does.Not.Contain("super-secret-bot-token"));
    }

    [Test]
    public void AutopilotStateStoreMergesAndPersistsDotNetTelegramState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"laufen-tg-state-{Guid.NewGuid():N}");
        try
        {
            var layout = WorkspaceLayout.FromRoot(tempRoot);
            Directory.CreateDirectory(layout.AthletesDirectory);

            var locked = TelegramInteractionRouter.ApplyStateLock(null, new DateOnly(2026, 11, 26));
            var sent = TelegramInteractionRouter.MarkMorningBriefingSent(locked, new DateOnly(2026, 11, 25));
            AutopilotStateStore.SaveDotNetTelegramState(layout, "berni-test", sent);

            var loaded = AutopilotStateStore.LoadDotNetTelegramState(layout, "berni-test");
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.LastDispatchedBriefingDate, Is.EqualTo("2026-11-25"));

            var pyState = new TelegramStateRecord
            {
                UserLockedDates = [new DateOnly(2026, 11, 28)]
            };
            var merged = AutopilotStateStore.MergeUserLockedDates(pyState, loaded, new HashSet<DateOnly> { new(2026, 11, 29) });
            Assert.That(merged, Is.EquivalentTo(new[]
            {
                new DateOnly(2026, 11, 26),
                new DateOnly(2026, 11, 28),
                new DateOnly(2026, 11, 29)
            }));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode status, string json)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class FakeTelegramHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }
}
