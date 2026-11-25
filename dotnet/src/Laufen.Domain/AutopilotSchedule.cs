using System.Globalization;
using System.Text.Json.Serialization;

namespace Laufen.Domain;

/// <summary>
/// Root 14-day self-healing autopilot decision emitted by the agentic coach.
/// </summary>
public sealed record AutopilotPlanDecision
{
    [JsonPropertyName("trigger")]
    public required string Trigger { get; init; }

    [JsonPropertyName("reference_date")]
    public required DateOnly ReferenceDate { get; init; }

    [JsonPropertyName("horizon_days")]
    public required int HorizonDays { get; init; }

    [JsonPropertyName("health_state")]
    public required string HealthState { get; init; }

    [JsonPropertyName("operations")]
    public required IReadOnlyList<ScheduledDayOperation> Operations { get; init; }

    [JsonPropertyName("rationale")]
    public required CoachDecisionRationale Rationale { get; init; }

    [JsonPropertyName("updated_compass")]
    public required BlockCompassSnapshot UpdatedCompass { get; init; }
}

/// <summary>
/// Single calendar reconciliation operation (<c>keep</c>, <c>upsert</c>, or <c>delete</c>) within the 14-day horizon.
/// </summary>
public sealed record ScheduledDayOperation
{
    [JsonPropertyName("date")]
    public required DateOnly Date { get; init; }

    [JsonPropertyName("operation")]
    public required string Operation { get; init; }

    [JsonPropertyName("event_id")]
    public long? EventId { get; init; }

    [JsonPropertyName("workout")]
    public WorkoutDefinition? Workout { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record ReconciledScheduleWorkout
{
    public required DateOnly Date { get; init; }
    public required string Tier { get; init; }
    public required string Ownership { get; init; }
    public required string Source { get; init; }
    public long? ExistingEventId { get; init; }
    public required string Title { get; init; }
    public string? SessionType { get; init; }
    public int? TotalDurationSeconds { get; init; }
    public required double EstimatedLoad { get; init; }
    public CompiledWorkout? CompiledWorkout { get; init; }
}

public sealed record ReconciledAutopilotSchedule
{
    public required DateOnly ReferenceDate { get; init; }
    public required string Trigger { get; init; }
    public required string HealthState { get; init; }
    public required IReadOnlyList<ReconciledScheduleWorkout> WatchCommitWindow { get; init; }
    public required IReadOnlyList<ReconciledScheduleWorkout> PreviewWindow { get; init; }
    public required IReadOnlyList<ReconciledScheduleWorkout> CompiledUpserts { get; init; }
    public required IReadOnlyList<long> DeletedEventIds { get; init; }
    public BanisterProjectionResult? BanisterProjection { get; init; }
}

/// <summary>
/// Deterministic gatekeeper and reconciler enforcing ownership (<c>auto</c> vs <c>user_locked</c>),
/// intraday freeze, <c>SickHold -> ReEntry</c> progression safety, consecutive-day preferences, and modality rules.
/// </summary>
public static class AutopilotScheduleGatekeeper
{
    private const int RequiredHorizonDays = 14;
    private const int WatchCommitDays = 7;

    private static readonly HashSet<string> AllowedTriggers = new(
        ["morning_sync", "post_run", "telegram_override", "intraday_check"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> AllowedHealthStates = new(
        ["Normal", "SickHold", "ReEntry"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> AllowedOperations = new(
        ["keep", "upsert", "delete"],
        StringComparer.Ordinal);

    private static readonly string[] QualityEventTitleKeywords =
        ["interval", "tempo", "schwelle", "threshold", "speed", "fartlek", "fahrtspiel", "race"];

    public static WorkoutValidationResult Validate(
        AutopilotPlanDecision decision,
        CoachIntelligenceDossier dossier)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(dossier);

        var errors = new List<WorkoutValidationError>();

        if (!AllowedTriggers.Contains(decision.Trigger))
        {
            errors.Add(new WorkoutValidationError(
                "invalid_autopilot_trigger",
                "trigger",
                $"Trigger '{decision.Trigger}' is invalid. Expected morning_sync, post_run, telegram_override, or intraday_check."));
        }

        if (decision.HorizonDays != RequiredHorizonDays)
        {
            errors.Add(new WorkoutValidationError(
                "invalid_horizon_days",
                "horizon_days",
                $"Horizon days must be {RequiredHorizonDays}, received {decision.HorizonDays}."));
        }

        if (!AllowedHealthStates.Contains(decision.HealthState))
        {
            errors.Add(new WorkoutValidationError(
                "invalid_health_state",
                "health_state",
                $"Health state '{decision.HealthState}' is invalid. Expected Normal, SickHold, or ReEntry."));
        }

        WorkoutModalityValidator.ValidateRationale(decision.Rationale, errors);
        WorkoutModalityValidator.ValidateCompass(decision.UpdatedCompass, errors);

        if (decision.UpdatedCompass is not null
            && !string.IsNullOrWhiteSpace(decision.UpdatedCompass.HealthState)
            && !string.Equals(decision.UpdatedCompass.HealthState, decision.HealthState, StringComparison.Ordinal))
        {
            errors.Add(new WorkoutValidationError(
                "compass_health_state_mismatch",
                "updated_compass.health_state",
                $"Compass health_state '{decision.UpdatedCompass.HealthState}' must match decision health_state '{decision.HealthState}'."));
        }

        if (decision.Operations is null)
        {
            errors.Add(new WorkoutValidationError("missing_operations", "operations", "Operations list is required."));
            return new WorkoutValidationResult(false, errors);
        }

        var horizonStart = decision.ReferenceDate;
        var horizonEnd = decision.ReferenceDate.AddDays(RequiredHorizonDays - 1);
        var eventsByDate = dossier.Calendar.UpcomingEventsNext14Days
            .GroupBy(e => e.Date)
            .ToDictionary(g => g.Key, g => g.ToList());
        var eventsById = dossier.Calendar.UpcomingEventsNext14Days
            .ToDictionary(e => e.Id);
        var lockedDateSet = dossier.Calendar.UserLockedDates.ToHashSet();

        var upsertDates = new HashSet<DateOnly>();

        for (var i = 0; i < decision.Operations.Count; i++)
        {
            var op = decision.Operations[i];
            var opField = string.Create(CultureInfo.InvariantCulture, $"operations[{i}]");

            if (!AllowedOperations.Contains(op.Operation))
            {
                errors.Add(new WorkoutValidationError(
                    "invalid_day_operation",
                    $"{opField}.operation",
                    $"Unsupported operation '{op.Operation}'. Expected keep, upsert, or delete."));
                continue;
            }

            if (op.Date < horizonStart || op.Date > horizonEnd)
            {
                errors.Add(new WorkoutValidationError(
                    "operation_outside_horizon",
                    $"{opField}.date",
                    $"Operation date {op.Date:yyyy-MM-dd} is outside horizon [{horizonStart:yyyy-MM-dd}, {horizonEnd:yyyy-MM-dd}]."));
            }

            if (string.Equals(op.Operation, "upsert", StringComparison.Ordinal))
            {
                if (!upsertDates.Add(op.Date))
                {
                    errors.Add(new WorkoutValidationError(
                        "multiple_upserts_same_date",
                        $"{opField}.date",
                        $"Multiple upserts on {op.Date:yyyy-MM-dd} are forbidden."));
                }

                if (op.Workout is null)
                {
                    errors.Add(new WorkoutValidationError(
                        "missing_upsert_workout",
                        $"{opField}.workout",
                        "upsert operation requires a workout."));
                }
                else
                {
                    WorkoutModalityValidator.ValidateWorkoutInternal(op.Workout, $"{opField}.workout.", errors);
                }

                if (string.Equals(decision.HealthState, "SickHold", StringComparison.Ordinal))
                {
                    errors.Add(new WorkoutValidationError(
                        "sickhold_forbids_planned_workouts",
                        $"{opField}.operation",
                        "SickHold health state forbids scheduling workouts; use delete/keep only."));
                }
            }
            else if (op.Workout is not null)
            {
                errors.Add(new WorkoutValidationError(
                    "unexpected_workout_payload",
                    $"{opField}.workout",
                    $"Operation '{op.Operation}' must not include a workout payload."));
            }

            if (op.Operation is "upsert" or "delete")
            {
                ValidateOwnershipAndFreeze(op, decision, eventsByDate, eventsById, lockedDateSet, opField, errors);
            }
        }

        if (dossier.Readiness.MedicalRedFlags.Count > 0)
        {
            ValidateMedicalRedFlagRules(decision, errors);
        }

        var reconciledPreview = BuildReconciledScheduleItems(decision, dossier);
        ValidateReentryRampUp(reconciledPreview, dossier, errors);
        ValidateConsecutiveRunDays(reconciledPreview, dossier, errors);

        return new WorkoutValidationResult(errors.Count == 0, errors);
    }

    public static ReconciledAutopilotSchedule Reconcile(
        AutopilotPlanDecision decision,
        CoachIntelligenceDossier dossier)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(dossier);

        var validation = Validate(decision, dossier);
        if (!validation.IsValid)
        {
            var codes = string.Join(", ", validation.Errors.Select(e => $"{e.Code} ({e.Field})"));
            throw new InvalidOperationException($"Cannot reconcile invalid autopilot plan: {codes}");
        }

        var watchCutoff = decision.ReferenceDate.AddDays(WatchCommitDays - 1);
        var (items, deletedIds) = BuildReconciledWithDeletes(decision, dossier, compileUpserts: true, watchCutoff);

        var watchWindow = items.Where(i => i.Date <= watchCutoff).ToList();
        var previewWindow = items.Where(i => i.Date > watchCutoff).ToList();
        var compiledUpserts = items.Where(i => string.Equals(i.Source, "upsert", StringComparison.Ordinal)).ToList();

        BanisterProjectionResult? banister = null;
        if (dossier.Readiness.Ctl.HasValue && dossier.Readiness.Atl.HasValue)
        {
            var dailyLoads = new Dictionary<DateOnly, double>();
            foreach (var item in items)
            {
                dailyLoads[item.Date] = dailyLoads.TryGetValue(item.Date, out var existing)
                    ? existing + item.EstimatedLoad
                    : item.EstimatedLoad;
            }

            banister = TrainingLoadProjector.ProjectBanister(
                dossier.Readiness.Ctl.Value,
                dossier.Readiness.Atl.Value,
                decision.ReferenceDate,
                dailyLoads,
                decision.HorizonDays);
        }

        return new ReconciledAutopilotSchedule
        {
            ReferenceDate = decision.ReferenceDate,
            Trigger = decision.Trigger,
            HealthState = decision.HealthState,
            WatchCommitWindow = watchWindow,
            PreviewWindow = previewWindow,
            CompiledUpserts = compiledUpserts,
            DeletedEventIds = deletedIds,
            BanisterProjection = banister
        };
    }

    private static void ValidateOwnershipAndFreeze(
        ScheduledDayOperation op,
        AutopilotPlanDecision decision,
        Dictionary<DateOnly, List<ScheduledCalendarItem>> eventsByDate,
        Dictionary<long, ScheduledCalendarItem> eventsById,
        HashSet<DateOnly> lockedDateSet,
        string opField,
        List<WorkoutValidationError> errors)
    {
        if (string.Equals(decision.Trigger, "intraday_check", StringComparison.Ordinal)
            && op.Date == decision.ReferenceDate)
        {
            errors.Add(new WorkoutValidationError(
                "same_day_intraday_freeze",
                $"{opField}.date",
                $"Same-day intraday freeze prevents mutating {op.Date:yyyy-MM-dd} during '{decision.Trigger}'."));
        }

        if (op.EventId.HasValue && eventsById.TryGetValue(op.EventId.Value, out var targeted))
        {
            if (string.Equals(targeted.Ownership, "completed", StringComparison.Ordinal))
            {
                errors.Add(new WorkoutValidationError(
                    "completed_event_immutable",
                    $"{opField}.event_id",
                    $"Completed event {targeted.Id} on {targeted.Date:yyyy-MM-dd} cannot be modified or deleted."));
            }
            else if (string.Equals(targeted.Ownership, "user_locked", StringComparison.Ordinal)
                && !string.Equals(decision.Trigger, "telegram_override", StringComparison.Ordinal))
            {
                errors.Add(new WorkoutValidationError(
                    "user_locked_event_immutable",
                    $"{opField}.event_id",
                    $"User-locked event {targeted.Id} on {targeted.Date:yyyy-MM-dd} cannot be mutated under trigger '{decision.Trigger}'."));
            }

            return;
        }

        var dateEvents = eventsByDate.TryGetValue(op.Date, out var list) ? list : [];
        var workoutEvents = dateEvents
            .Where(e => string.Equals(e.Category, "WORKOUT", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (workoutEvents.Any(e => string.Equals(e.Ownership, "completed", StringComparison.Ordinal)))
        {
            errors.Add(new WorkoutValidationError(
                "completed_event_immutable",
                $"{opField}.date",
                $"Completed workout on {op.Date:yyyy-MM-dd} cannot be overwritten or deleted."));
        }

        var isDateUserLocked = lockedDateSet.Contains(op.Date)
            || workoutEvents.Any(e => string.Equals(e.Ownership, "user_locked", StringComparison.Ordinal));
        if (isDateUserLocked && !string.Equals(decision.Trigger, "telegram_override", StringComparison.Ordinal))
        {
            errors.Add(new WorkoutValidationError(
                "user_locked_event_immutable",
                $"{opField}.date",
                $"Date {op.Date:yyyy-MM-dd} is user_locked and cannot be mutated under trigger '{decision.Trigger}'."));
        }
    }

    private static void ValidateMedicalRedFlagRules(
        AutopilotPlanDecision decision,
        List<WorkoutValidationError> errors)
    {
        if (string.Equals(decision.HealthState, "Normal", StringComparison.Ordinal))
        {
            errors.Add(new WorkoutValidationError(
                "medical_red_flag_requires_sickhold_or_recovery",
                "health_state",
                "Active medical red flags forbid 'Normal' health_state; transition to 'SickHold' or 'ReEntry'."));
        }

        var watchCutoff = decision.ReferenceDate.AddDays(WatchCommitDays - 1);
        for (var i = 0; i < decision.Operations.Count; i++)
        {
            var op = decision.Operations[i];
            if (op.Date <= watchCutoff
                && string.Equals(op.Operation, "upsert", StringComparison.Ordinal)
                && op.Workout is not null
                && (WorkoutModalityValidator.IsQualitySessionType(op.Workout.SessionType)
                    || string.Equals(op.Workout.SessionType, "long_run", StringComparison.Ordinal)))
            {
                errors.Add(new WorkoutValidationError(
                    "medical_red_flag_requires_sickhold_or_recovery",
                    string.Create(CultureInfo.InvariantCulture, $"operations[{i}].workout.session_type"),
                    $"Active medical red flags block '{op.Workout.SessionType}' inside the watch commit window."));
            }
        }
    }

    private static List<ReconciledScheduleWorkout> BuildReconciledScheduleItems(
        AutopilotPlanDecision decision,
        CoachIntelligenceDossier dossier)
    {
        var watchCutoff = decision.ReferenceDate.AddDays(WatchCommitDays - 1);
        var (items, _) = BuildReconciledWithDeletes(decision, dossier, compileUpserts: false, watchCutoff);
        return items;
    }

    private static (List<ReconciledScheduleWorkout> Items, List<long> DeletedIds) BuildReconciledWithDeletes(
        AutopilotPlanDecision decision,
        CoachIntelligenceDossier dossier,
        bool compileUpserts,
        DateOnly watchCutoff)
    {
        var horizonStart = decision.ReferenceDate;
        var horizonEnd = decision.ReferenceDate.AddDays(RequiredHorizonDays - 1);

        var activeExisting = dossier.Calendar.UpcomingEventsNext14Days
            .Where(e => string.Equals(e.Category, "WORKOUT", StringComparison.OrdinalIgnoreCase)
                && e.Date >= horizonStart
                && e.Date <= horizonEnd)
            .ToList();

        var deletedIds = new List<long>();
        var deletedIdSet = new HashSet<long>();

        void MarkDeleted(long id)
        {
            if (deletedIdSet.Add(id))
            {
                deletedIds.Add(id);
            }
        }

        // 1. Apply explicit deletes
        foreach (var op in decision.Operations)
        {
            if (!string.Equals(op.Operation, "delete", StringComparison.Ordinal))
            {
                continue;
            }

            if (op.EventId.HasValue)
            {
                var removed = activeExisting.RemoveAll(e =>
                    e.Id == op.EventId.Value && string.Equals(e.Ownership, "auto", StringComparison.Ordinal));
                if (removed > 0)
                {
                    MarkDeleted(op.EventId.Value);
                }
            }
            else
            {
                var toRemove = activeExisting
                    .Where(e => e.Date == op.Date && string.Equals(e.Ownership, "auto", StringComparison.Ordinal))
                    .ToList();
                foreach (var evt in toRemove)
                {
                    activeExisting.Remove(evt);
                    MarkDeleted(evt.Id);
                }
            }
        }

        var result = new List<ReconciledScheduleWorkout>();

        // 2. Apply upserts (replacing any auto items on the upserted date)
        var upsertedDates = new HashSet<DateOnly>();
        foreach (var op in decision.Operations)
        {
            if (!string.Equals(op.Operation, "upsert", StringComparison.Ordinal) || op.Workout is null)
            {
                continue;
            }

            upsertedDates.Add(op.Date);

            var replacedAuto = activeExisting
                .Where(e => e.Date == op.Date
                    && (string.Equals(e.Ownership, "auto", StringComparison.Ordinal)
                        || (string.Equals(decision.Trigger, "telegram_override", StringComparison.Ordinal)
                            && !e.IsCompleted)))
                .ToList();
            foreach (var evt in replacedAuto)
            {
                activeExisting.Remove(evt);
                MarkDeleted(evt.Id);
            }

            var compiled = compileUpserts ? IntervalsWorkoutCompiler.Compile(op.Workout) : null;
            var duration = compiled?.TotalDurationSeconds ?? WorkoutModalityValidator.ComputeTotalDurationSeconds(op.Workout);
            var estLoad = TrainingLoadProjector.EstimateWorkoutLoad(op.Workout);

            result.Add(new ReconciledScheduleWorkout
            {
                Date = op.Date,
                Tier = op.Date <= watchCutoff ? "watch_commit" : "preview",
                Ownership = string.Equals(decision.Trigger, "telegram_override", StringComparison.Ordinal) ? "user_locked" : "auto",
                Source = "upsert",
                ExistingEventId = replacedAuto.Count == 1 ? replacedAuto[0].Id : null,
                Title = op.Workout.Title.Trim(),
                SessionType = op.Workout.SessionType,
                TotalDurationSeconds = duration,
                EstimatedLoad = estLoad,
                CompiledWorkout = compiled
            });
        }

        // 3. Retain kept or untouched existing workouts
        foreach (var evt in activeExisting)
        {
            result.Add(new ReconciledScheduleWorkout
            {
                Date = evt.Date,
                Tier = evt.Date <= watchCutoff ? "watch_commit" : "preview",
                Ownership = evt.Ownership,
                Source = upsertedDates.Contains(evt.Date) ? "existing_coexisting" : "existing",
                ExistingEventId = evt.Id,
                Title = evt.Name,
                SessionType = null,
                TotalDurationSeconds = null,
                EstimatedLoad = evt.EstimatedLoad ?? 35.0,
                CompiledWorkout = null
            });
        }

        result.Sort((a, b) =>
        {
            var cmp = a.Date.CompareTo(b.Date);
            return cmp != 0 ? cmp : string.Compare(a.Title, b.Title, StringComparison.Ordinal);
        });

        return (result, deletedIds);
    }

    private static void ValidateReentryRampUp(
        List<ReconciledScheduleWorkout> reconciled,
        CoachIntelligenceDossier dossier,
        List<WorkoutValidationError> errors)
    {
        var breakCtx = dossier.StimulusProgression.BreakReentry;
        if (!breakCtx.HasRecentExtendedBreak || breakCtx.CleanBaseRunsSinceRecentBreak >= 2)
        {
            return;
        }

        var cleanBaseCount = breakCtx.CleanBaseRunsSinceRecentBreak;

        foreach (var item in reconciled)
        {
            var isQuality = item.SessionType is not null
                ? WorkoutModalityValidator.IsQualitySessionType(item.SessionType)
                : IsQualityName(item.Title);

            if (isQuality)
            {
                if (cleanBaseCount < 2)
                {
                    errors.Add(new WorkoutValidationError(
                        "reentry_requires_base_before_quality",
                        string.Create(CultureInfo.InvariantCulture, $"schedule[{item.Date:yyyy-MM-dd}]"),
                        $"Quality workout '{item.Title}' on {item.Date:yyyy-MM-dd} requires at least 2 clean post-break aerobic sessions first (currently {cleanBaseCount})."));
                }
            }
            else if (item.SessionType is null || WorkoutModalityValidator.IsAerobicSessionType(item.SessionType))
            {
                cleanBaseCount++;
            }
        }
    }

    private static void ValidateConsecutiveRunDays(
        List<ReconciledScheduleWorkout> reconciled,
        CoachIntelligenceDossier dossier,
        List<WorkoutValidationError> errors)
    {
        if (dossier.Athlete.AllowConsecutiveRunDays != false)
        {
            return;
        }

        var workoutDates = reconciled
            .Select(i => i.Date)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        if (dossier.StimulusProgression.Ledger.DaysSinceLastRun == 1)
        {
            var yesterday = dossier.ReferenceDate.AddDays(-1);
            if (!workoutDates.Contains(yesterday))
            {
                workoutDates.Insert(0, yesterday);
            }
        }

        for (var i = 1; i < workoutDates.Count; i++)
        {
            if (workoutDates[i].DayNumber - workoutDates[i - 1].DayNumber == 1)
            {
                errors.Add(new WorkoutValidationError(
                    "consecutive_run_days_not_allowed",
                    string.Create(CultureInfo.InvariantCulture, $"schedule[{workoutDates[i]:yyyy-MM-dd}]"),
                    $"Consecutive running days {workoutDates[i - 1]:yyyy-MM-dd} and {workoutDates[i]:yyyy-MM-dd} violate athlete allow_consecutive_run_days=false."));
            }
        }
    }

    private static bool IsQualityName(string title)
    {
        foreach (var keyword in QualityEventTitleKeywords)
        {
            if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
