using System.Globalization;

namespace Laufen.Domain;

/// <summary>
/// Result of the deterministic anti-churn hysteresis gate deciding whether the LLM planner should run.
/// </summary>
public sealed record AntiChurnHysteresisDecision(
    bool ShouldReplan,
    string ReasonCode,
    IReadOnlyList<string> TriggerReasons);

/// <summary>
/// Deterministic anti-churn evaluator keeping stable plans untouched during routine
/// morning/intraday checks while immediately unlocking replanning on meaningful changes.
/// </summary>
public static class AutopilotHysteresisEvaluator
{
    public static AntiChurnHysteresisDecision Evaluate(
        CoachIntelligenceDossier dossier,
        string trigger,
        string? athleteMessage,
        bool forceReplan = false)
    {
        ArgumentNullException.ThrowIfNull(dossier);
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger);

        var reasons = new List<string>();

        if (forceReplan)
        {
            reasons.Add("force_replan");
        }

        if (string.Equals(trigger, "post_run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trigger, "telegram_override", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"event_trigger:{trigger}");
        }

        if (!string.IsNullOrWhiteSpace(athleteMessage))
        {
            reasons.Add("athlete_message");
        }

        if (dossier.CurrentCompass is null)
        {
            reasons.Add("missing_compass");
        }

        if (dossier.Readiness.MedicalRedFlags.Count > 0)
        {
            reasons.Add("medical_red_flag");
        }

        if (dossier.Calendar.MissedAutoWorkoutsLast7Days.Count > 0)
        {
            reasons.Add("missed_auto_workouts");
        }

        var uncompletedWatchWorkouts = dossier.Calendar.UpcomingEventsNext7Days
            .Where(e => !e.IsCompleted && string.Equals(e.Category, "WORKOUT", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var isSickHold = string.Equals(dossier.CurrentCompass?.HealthState, "SickHold", StringComparison.OrdinalIgnoreCase);
        if (!isSickHold && uncompletedWatchWorkouts.Count < 2)
        {
            reasons.Add("watch_window_underfilled");
        }

        if (uncompletedWatchWorkouts.GroupBy(e => e.Date).Any(g => g.Count() > 1))
        {
            reasons.Add("duplicate_scheduled_day");
        }

        if (dossier.Readiness.RestingHrDelta is >= 3.0)
        {
            reasons.Add("rhr_spike");
        }

        if (dossier.Readiness.HrvBaselineRatio is < 0.85)
        {
            reasons.Add("hrv_drop");
        }

        if (dossier.Readiness.SleepHoursDelta is <= -1.5)
        {
            reasons.Add("sleep_deficit");
        }

        if (dossier.CurrentCompass is not null)
        {
            var hasRunAfterCompass = false;
            if (dossier.StimulusProgression.Ledger.DaysSinceLastRun is int daysSince)
            {
                var lastRunDate = dossier.ReferenceDate.AddDays(-daysSince);
                if (lastRunDate > dossier.CurrentCompass.UpdatedAt)
                {
                    hasRunAfterCompass = true;
                }
            }

            if (dossier.Calendar.HasCompletedRunOnTargetDate && dossier.TargetDate > dossier.CurrentCompass.UpdatedAt)
            {
                hasRunAfterCompass = true;
            }

            if (hasRunAfterCompass)
            {
                reasons.Add("completed_run_after_compass");
            }
        }

        return reasons.Count > 0
            ? new AntiChurnHysteresisDecision(true, reasons[0], reasons)
            : new AntiChurnHysteresisDecision(false, "hysteresis_stable_plan", []);
    }
}

public sealed record AutopilotCycleRequest
{
    public required string AthleteSlug { get; init; }
    public required AthleteRegistryEntry RegistryEntry { get; init; }
    public required AthleteProfile Profile { get; init; }
    public required IReadOnlyList<IntervalsActivityEntry> Activities { get; init; }
    public required IReadOnlyList<IntervalsWellnessEntry> WellnessRecords { get; init; }
    public required IReadOnlyDictionary<string, RunReviewRecord> RunReviews { get; init; }
    public required IReadOnlyList<CalendarEventEntry> CalendarEvents { get; init; }
    public required DateOnly ReferenceDate { get; init; }
    public DateOnly? TargetDate { get; init; }
    public IReadOnlySet<long>? AutoEventIds { get; init; }
    public IReadOnlySet<DateOnly>? LockedDates { get; init; }
    public BlockCompassSnapshot? CurrentCompass { get; init; }
    public string? PreviousHealthState { get; init; }
    public string? PreviousBlockFocus { get; init; }
    public string Trigger { get; init; } = "morning_sync";
    public string? AthleteMessage { get; init; }
    public int MaxValidationRetries { get; init; } = 1;
    public bool ForceReplan { get; init; }
    public bool ForceNarrate { get; init; }
    public bool IsDryRun { get; init; } = true;
}

public sealed record AutopilotCycleResult
{
    public required bool Succeeded { get; init; }
    public string? FailureReason { get; init; }
    public required CoachIntelligenceDossier Dossier { get; init; }
    public required AntiChurnHysteresisDecision Hysteresis { get; init; }
    public CoachPlanningOutcome? PlanningOutcome { get; init; }
    public CalendarSyncPlan? CalendarSyncPlan { get; init; }
    public required NarrativeTriggerEvaluation NarrativeGate { get; init; }
    public NarrativeGenerationOutcome? NarrativeOutcome { get; init; }
    public string? NarrativeError { get; init; }
    public TelegramOutgoingMessage? TelegramPreview { get; init; }
    public BlockCompassSnapshot? UpdatedCompass { get; init; }
}

/// <summary>
/// Unified single-cycle coordinator chaining Dossier -> Hysteresis -> Role 1 Planner ->
/// Calendar Sync Planner -> Role 2 Narrator (fault-isolated) -> Telegram Presenter.
/// </summary>
public sealed class AutopilotCycleOrchestrator(ICoachPlanningStrategy planner, IProgressNarrator? narrator = null)
{
    private readonly ICoachPlanningStrategy _planner = planner ?? throw new ArgumentNullException(nameof(planner));
    private readonly IProgressNarrator? _narrator = narrator;

    public async Task<AutopilotCycleResult> RunCycleAsync(
        AutopilotCycleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var targetDate = request.TargetDate ?? request.ReferenceDate;
        var dossier = CoachIntelligenceDossierBuilder.Build(
            request.Profile,
            request.Activities,
            request.WellnessRecords,
            request.RunReviews,
            request.CalendarEvents,
            request.ReferenceDate,
            targetDate,
            request.AutoEventIds,
            request.LockedDates,
            currentCompass: request.CurrentCompass);

        var hysteresis = AutopilotHysteresisEvaluator.Evaluate(
            dossier,
            request.Trigger,
            request.AthleteMessage,
            request.ForceReplan);

        CoachPlanningOutcome? planningOutcome = null;
        if (hysteresis.ShouldReplan)
        {
            planningOutcome = await _planner.PlanAsync(new CoachPlanningContext
            {
                Dossier = dossier,
                Trigger = request.Trigger,
                AthleteMessage = request.AthleteMessage,
                MaxValidationRetries = request.MaxValidationRetries
            }, cancellationToken).ConfigureAwait(false);

            if (!planningOutcome.Success)
            {
                var skippedGate = ProgressNarratorGate.Evaluate(
                    request.Trigger,
                    request.PreviousHealthState,
                    request.CurrentCompass?.HealthState,
                    request.PreviousBlockFocus,
                    request.CurrentCompass?.CurrentBlockFocus,
                    forceManual: false);

                return new AutopilotCycleResult
                {
                    Succeeded = false,
                    FailureReason = planningOutcome.FailureReason ?? "Planner failed.",
                    Dossier = dossier,
                    Hysteresis = hysteresis,
                    PlanningOutcome = planningOutcome,
                    CalendarSyncPlan = null,
                    NarrativeGate = skippedGate,
                    NarrativeOutcome = null,
                    NarrativeError = null,
                    TelegramPreview = null,
                    UpdatedCompass = request.CurrentCompass
                };
            }
        }

        CalendarSyncPlan? syncPlan = null;
        if (planningOutcome?.ReconciledSchedule is not null)
        {
            var effectiveAutoIds = dossier.Calendar.UpcomingEventsNext14Days
                .Where(e => string.Equals(e.Ownership, "auto", StringComparison.Ordinal))
                .Select(e => e.Id)
                .ToHashSet();
            if (request.AutoEventIds is not null)
            {
                effectiveAutoIds.UnionWith(request.AutoEventIds);
            }

            var effectiveLocks = dossier.Calendar.UserLockedDates.ToHashSet();
            if (request.LockedDates is not null)
            {
                effectiveLocks.UnionWith(request.LockedDates);
            }

            syncPlan = IntervalsCalendarSyncPlanner.BuildPlan(
                request.AthleteSlug,
                request.RegistryEntry.IntervalsId,
                planningOutcome.ReconciledSchedule,
                request.CalendarEvents,
                effectiveAutoIds,
                effectiveLocks,
                isDryRun: request.IsDryRun);
        }

        var effectiveCompass = planningOutcome?.Decision?.UpdatedCompass ?? request.CurrentCompass;
        var narrativeGate = ProgressNarratorGate.Evaluate(
            request.Trigger,
            request.PreviousHealthState,
            effectiveCompass?.HealthState,
            request.PreviousBlockFocus,
            effectiveCompass?.CurrentBlockFocus,
            request.ForceNarrate);

        NarrativeGenerationOutcome? narrativeOutcome = null;
        string? narrativeError = null;
        if (narrativeGate.ShouldNarrate && _narrator is not null)
        {
            try
            {
                var briefing = NarrativeBriefingDossierBuilder.Build(
                    request.Profile,
                    request.Activities,
                    request.WellnessRecords,
                    request.RunReviews,
                    request.CalendarEvents,
                    request.ReferenceDate,
                    narrativeGate.TriggerReason,
                    existingDossier: dossier,
                    reconciledSchedule: planningOutcome?.ReconciledSchedule,
                    currentCompass: effectiveCompass,
                    previousHealthState: request.PreviousHealthState,
                    latestTraceRationale: planningOutcome?.Decision?.Rationale);

                narrativeOutcome = await _narrator.NarrateAsync(new NarrativeGenerationContext
                {
                    Briefing = briefing,
                    MaxValidationRetries = request.MaxValidationRetries
                }, cancellationToken).ConfigureAwait(false);

                if (!narrativeOutcome.Success)
                {
                    narrativeError = narrativeOutcome.FailureReason ?? "ProgressNarrator returned failure.";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                narrativeError = ex.Message;
            }
        }

        var chatId = !string.IsNullOrWhiteSpace(request.RegistryEntry.TelegramChatId)
            ? request.RegistryEntry.TelegramChatId!
            : "dry-run-preview";
        TelegramOutgoingMessage? telegramPreview;
        if (string.Equals(request.Trigger, "post_run", StringComparison.OrdinalIgnoreCase))
        {
            var matchingReview = request.RunReviews.Values
                .Where(r => DateOnly.FromDateTime(r.Date) <= request.ReferenceDate)
                .OrderByDescending(r => r.Date)
                .FirstOrDefault();

            telegramPreview = matchingReview is not null
                ? TelegramAutopilotPresenter.FormatPostRunMessage(
                    chatId,
                    matchingReview,
                    narrativeOutcome?.Report,
                    planningOutcome?.ReconciledSchedule)
                : TelegramAutopilotPresenter.FormatMorningBriefing(
                    chatId,
                    dossier,
                    planningOutcome?.ReconciledSchedule,
                    effectiveCompass);
        }
        else
        {
            telegramPreview = TelegramAutopilotPresenter.FormatMorningBriefing(
                chatId,
                dossier,
                planningOutcome?.ReconciledSchedule,
                effectiveCompass);
        }

        return new AutopilotCycleResult
        {
            Succeeded = true,
            FailureReason = null,
            Dossier = dossier,
            Hysteresis = hysteresis,
            PlanningOutcome = planningOutcome,
            CalendarSyncPlan = syncPlan,
            NarrativeGate = narrativeGate,
            NarrativeOutcome = narrativeOutcome,
            NarrativeError = narrativeError,
            TelegramPreview = telegramPreview,
            UpdatedCompass = effectiveCompass
        };
    }
}
