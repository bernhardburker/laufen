using System.Globalization;
using System.Text.RegularExpressions;

namespace Laufen.Domain;

public sealed record WorkoutValidationError(string Code, string Field, string Message);

public sealed record WorkoutValidationResult(bool IsValid, IReadOnlyList<WorkoutValidationError> Errors);

/// <summary>
/// Deterministic Coaching DNA modality gatekeeper and dossier-aware safety validator.
/// Enforces <c>SYSTEM_SPEC.md</c> §3.1 HR-vs-Pace separation and §1.2 medical/calendar invariants.
/// </summary>
public static partial class WorkoutModalityValidator
{
    private const int MinWorkoutDurationSeconds = 900;
    private const int MaxWorkoutDurationSeconds = 9000;
    private const int MinStepDurationSeconds = 10;
    private const int ShortIntervalHrThresholdSeconds = 120;
    private const int MinPaceSecPerKm = 180;
    private const int MaxPaceSecPerKm = 600;
    private const int MaxPaceSpreadSeconds = 45;

    private static readonly HashSet<string> AerobicSessionTypes = new(
        ["easy_run", "recovery_run", "long_run"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> QualitySessionTypes = new(
        ["intervals", "fartlek", "tempo_run", "threshold_intervals", "micro_threshold", "progressive_threshold"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> AllowedActions = new(
        ["keep_existing", "rest_day", "single_workout", "dual_option"],
        StringComparer.Ordinal);

    [GeneratedRegex(@"\b\d{1,2}:\d{2}\s*(?:/km|min/km)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PaceLeakPattern();

    public static bool IsAerobicSessionType(string? sessionType) =>
        sessionType is not null && AerobicSessionTypes.Contains(sessionType);

    public static bool IsQualitySessionType(string? sessionType) =>
        sessionType is not null && QualitySessionTypes.Contains(sessionType);

    public static int ComputeTotalDurationSeconds(WorkoutDefinition workout)
    {
        ArgumentNullException.ThrowIfNull(workout);

        var total = 0;
        if (workout.Steps is null)
        {
            return 0;
        }

        foreach (var step in workout.Steps)
        {
            if (string.Equals(step.Kind, "repeat", StringComparison.Ordinal))
            {
                var reps = step.Reps ?? 0;
                var work = step.Work?.DurationSeconds ?? 0;
                var rec = step.Recovery?.DurationSeconds ?? 0;
                total += reps * (work + rec);
            }
            else
            {
                total += step.DurationSeconds ?? 0;
            }
        }

        return total;
    }

    public static WorkoutValidationResult ValidateWorkout(WorkoutDefinition workout)
    {
        ArgumentNullException.ThrowIfNull(workout);
        var errors = new List<WorkoutValidationError>();
        ValidateWorkoutInternal(workout, prefix: string.Empty, errors);
        return new WorkoutValidationResult(errors.Count == 0, errors);
    }

    public static WorkoutValidationResult ValidateDecision(
        CoachPlanDecision decision,
        CoachIntelligenceDossier? dossier = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var errors = new List<WorkoutValidationError>();

        if (!AllowedActions.Contains(decision.Action))
        {
            errors.Add(new WorkoutValidationError(
                "invalid_decision_action",
                "action",
                $"Action '{decision.Action}' is invalid. Expected keep_existing, rest_day, single_workout, or dual_option."));
        }

        ValidateRationale(decision.Rationale, errors);
        ValidateCompass(decision.UpdatedCompass, errors);
        ValidateActionPayloads(decision, errors);

        if (decision.PrimaryWorkout is not null)
        {
            ValidateWorkoutInternal(decision.PrimaryWorkout, "primary_workout.", errors);
        }

        if (decision.SecondaryWorkout is not null)
        {
            ValidateWorkoutInternal(decision.SecondaryWorkout, "secondary_workout.", errors);
        }

        if (string.Equals(decision.Action, "dual_option", StringComparison.Ordinal)
            && decision.PrimaryWorkout is not null
            && decision.SecondaryWorkout is not null)
        {
            var primaryIsQuality = IsQualitySessionType(decision.PrimaryWorkout.SessionType);
            var primaryIsBase = IsAerobicSessionType(decision.PrimaryWorkout.SessionType);
            var secondaryIsQuality = IsQualitySessionType(decision.SecondaryWorkout.SessionType);
            var secondaryIsBase = IsAerobicSessionType(decision.SecondaryWorkout.SessionType);

            if (!((primaryIsQuality && secondaryIsBase) || (primaryIsBase && secondaryIsQuality)))
            {
                errors.Add(new WorkoutValidationError(
                    "dual_option_must_pair_quality_with_base",
                    "secondary_workout.session_type",
                    "Dual option must pair one quality workout with one aerobic base/recovery workout."));
            }
        }

        if (dossier is not null)
        {
            ValidateDossierSafetyGates(decision, dossier, errors);
        }

        return new WorkoutValidationResult(errors.Count == 0, errors);
    }

    internal static void ValidateRationale(CoachDecisionRationale? rationale, List<WorkoutValidationError> errors)
    {
        if (rationale is null)
        {
            errors.Add(new WorkoutValidationError("missing_rationale", "rationale", "Decision rationale is required."));
            return;
        }

        if (rationale.Observations is null || rationale.Observations.Count == 0 || rationale.Observations.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add(new WorkoutValidationError("missing_rationale_observations", "rationale.observations", "At least one non-empty observation is required."));
        }

        if (string.IsNullOrWhiteSpace(rationale.TradeoffWeighed))
        {
            errors.Add(new WorkoutValidationError("missing_rationale_field", "rationale.tradeoff_weighed", "Tradeoff weighed must be non-empty."));
        }

        if (string.IsNullOrWhiteSpace(rationale.ProgressionIntent))
        {
            errors.Add(new WorkoutValidationError("missing_rationale_field", "rationale.progression_intent", "Progression intent must be non-empty."));
        }

        if (string.IsNullOrWhiteSpace(rationale.RejectedAlternative))
        {
            errors.Add(new WorkoutValidationError("missing_rationale_field", "rationale.rejected_alternative", "Rejected alternative must be non-empty."));
        }
    }

    internal static void ValidateCompass(BlockCompassSnapshot? compass, List<WorkoutValidationError> errors)
    {
        if (compass is null)
        {
            errors.Add(new WorkoutValidationError("missing_updated_compass", "updated_compass", "Updated BlockCompassSnapshot is required."));
            return;
        }

        if (string.IsNullOrWhiteSpace(compass.CurrentBlockFocus))
        {
            errors.Add(new WorkoutValidationError("missing_compass_field", "updated_compass.current_block_focus", "Current block focus must be non-empty."));
        }

        if (string.IsNullOrWhiteSpace(compass.BaseMilestone))
        {
            errors.Add(new WorkoutValidationError("missing_compass_field", "updated_compass.base_milestone", "Base milestone must be non-empty."));
        }

        if (string.IsNullOrWhiteSpace(compass.QualityMilestone))
        {
            errors.Add(new WorkoutValidationError("missing_compass_field", "updated_compass.quality_milestone", "Quality milestone must be non-empty."));
        }

        if (compass.ObservedAthletePatterns is null || compass.ObservedAthletePatterns.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add(new WorkoutValidationError("invalid_compass_patterns", "updated_compass.observed_athlete_patterns", "Observed athlete patterns cannot contain empty items."));
        }

        if (string.IsNullOrWhiteSpace(compass.LastDecisionSummary))
        {
            errors.Add(new WorkoutValidationError("missing_compass_field", "updated_compass.last_decision_summary", "Last decision summary must be non-empty."));
        }
    }

    private static void ValidateActionPayloads(CoachPlanDecision decision, List<WorkoutValidationError> errors)
    {
        switch (decision.Action)
        {
            case "keep_existing":
                if (string.IsNullOrWhiteSpace(decision.KeepExistingReason))
                {
                    errors.Add(new WorkoutValidationError("missing_keep_existing_reason", "keep_existing_reason", "keep_existing action requires keep_existing_reason."));
                }
                if (decision.PrimaryWorkout is not null)
                {
                    errors.Add(new WorkoutValidationError("unexpected_primary_workout", "primary_workout", "keep_existing action must not define primary_workout."));
                }
                if (decision.SecondaryWorkout is not null)
                {
                    errors.Add(new WorkoutValidationError("unexpected_secondary_workout", "secondary_workout", "keep_existing action must not define secondary_workout."));
                }
                if (!string.IsNullOrWhiteSpace(decision.RestDayReason))
                {
                    errors.Add(new WorkoutValidationError("unexpected_rest_day_reason", "rest_day_reason", "keep_existing action must not define rest_day_reason."));
                }
                break;

            case "rest_day":
                if (string.IsNullOrWhiteSpace(decision.RestDayReason))
                {
                    errors.Add(new WorkoutValidationError("missing_rest_day_reason", "rest_day_reason", "rest_day action requires rest_day_reason."));
                }
                if (decision.PrimaryWorkout is not null)
                {
                    errors.Add(new WorkoutValidationError("unexpected_primary_workout", "primary_workout", "rest_day action must not define primary_workout."));
                }
                if (decision.SecondaryWorkout is not null)
                {
                    errors.Add(new WorkoutValidationError("unexpected_secondary_workout", "secondary_workout", "rest_day action must not define secondary_workout."));
                }
                if (!string.IsNullOrWhiteSpace(decision.KeepExistingReason))
                {
                    errors.Add(new WorkoutValidationError("unexpected_keep_existing_reason", "keep_existing_reason", "rest_day action must not define keep_existing_reason."));
                }
                break;

            case "single_workout":
                if (decision.PrimaryWorkout is null)
                {
                    errors.Add(new WorkoutValidationError("missing_primary_workout", "primary_workout", "single_workout action requires primary_workout."));
                }
                if (decision.SecondaryWorkout is not null)
                {
                    errors.Add(new WorkoutValidationError("unexpected_secondary_workout", "secondary_workout", "single_workout action must not define secondary_workout."));
                }
                if (!string.IsNullOrWhiteSpace(decision.RestDayReason) || !string.IsNullOrWhiteSpace(decision.KeepExistingReason))
                {
                    errors.Add(new WorkoutValidationError("unexpected_reason_field", "action", "single_workout action must not define rest_day_reason or keep_existing_reason."));
                }
                break;

            case "dual_option":
                if (decision.PrimaryWorkout is null)
                {
                    errors.Add(new WorkoutValidationError("missing_primary_workout", "primary_workout", "dual_option action requires primary_workout."));
                }
                if (decision.SecondaryWorkout is null)
                {
                    errors.Add(new WorkoutValidationError("missing_secondary_workout", "secondary_workout", "dual_option action requires secondary_workout."));
                }
                if (!string.IsNullOrWhiteSpace(decision.RestDayReason) || !string.IsNullOrWhiteSpace(decision.KeepExistingReason))
                {
                    errors.Add(new WorkoutValidationError("unexpected_reason_field", "action", "dual_option action must not define rest_day_reason or keep_existing_reason."));
                }
                break;
        }
    }

    private static void ValidateDossierSafetyGates(
        CoachPlanDecision decision,
        CoachIntelligenceDossier dossier,
        List<WorkoutValidationError> errors)
    {
        if (dossier.Readiness.MedicalRedFlags.Count > 0)
        {
            if (decision.PrimaryWorkout is not null
                && (IsQualitySessionType(decision.PrimaryWorkout.SessionType)
                    || string.Equals(decision.PrimaryWorkout.SessionType, "long_run", StringComparison.Ordinal)))
            {
                errors.Add(new WorkoutValidationError(
                    "medical_red_flag_blocks_intensity",
                    "primary_workout.session_type",
                    $"Active medical red flag blocks '{decision.PrimaryWorkout.SessionType}'; prescribe rest_day or recovery/easy_run."));
            }

            if (decision.SecondaryWorkout is not null
                && (IsQualitySessionType(decision.SecondaryWorkout.SessionType)
                    || string.Equals(decision.SecondaryWorkout.SessionType, "long_run", StringComparison.Ordinal)))
            {
                errors.Add(new WorkoutValidationError(
                    "medical_red_flag_blocks_intensity",
                    "secondary_workout.session_type",
                    $"Active medical red flag blocks '{decision.SecondaryWorkout.SessionType}'."));
            }
        }

        if (decision.TargetDate == dossier.TargetDate
            && dossier.Calendar.HasCompletedRunOnTargetDate
            && decision.Action is "single_workout" or "dual_option")
        {
            errors.Add(new WorkoutValidationError(
                "completed_run_on_target_date",
                "target_date",
                $"Target date {decision.TargetDate:yyyy-MM-dd} already has a completed run; cannot schedule '{decision.Action}'."));
        }

        if (string.Equals(decision.Action, "keep_existing", StringComparison.Ordinal)
            && decision.TargetDate == dossier.TargetDate
            && !dossier.Calendar.HasUncompletedPlannedWorkoutOnTargetDate
            && !dossier.Calendar.HasCompletedRunOnTargetDate)
        {
            errors.Add(new WorkoutValidationError(
                "keep_existing_without_scheduled_event",
                "action",
                $"Target date {decision.TargetDate:yyyy-MM-dd} has no scheduled workout to keep."));
        }
    }

    internal static void ValidateWorkoutInternal(
        WorkoutDefinition workout,
        string prefix,
        List<WorkoutValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(workout.Title))
        {
            errors.Add(new WorkoutValidationError("missing_workout_title", $"{prefix}title", "Workout title must be non-empty."));
        }

        var isAerobic = IsAerobicSessionType(workout.SessionType);
        var isQuality = IsQualitySessionType(workout.SessionType);
        if (!isAerobic && !isQuality)
        {
            errors.Add(new WorkoutValidationError(
                "invalid_session_type",
                $"{prefix}session_type",
                $"Unsupported session_type '{workout.SessionType}'."));
        }

        if (isAerobic)
        {
            CheckAerobicTextForPaceLeak(workout.Title, $"{prefix}title", errors);
            CheckAerobicTextForPaceLeak(workout.CoachingNotes, $"{prefix}coaching_notes", errors);
        }

        if (workout.Steps is null || workout.Steps.Count == 0)
        {
            errors.Add(new WorkoutValidationError("missing_workout_steps", $"{prefix}steps", "Workout must contain at least one step."));
            return;
        }

        for (var i = 0; i < workout.Steps.Count; i++)
        {
            var step = workout.Steps[i];
            var stepField = string.Create(CultureInfo.InvariantCulture, $"{prefix}steps[{i}]");
            ValidateStep(step, workout.SessionType, isAerobic, stepField, errors);
        }

        var totalSeconds = ComputeTotalDurationSeconds(workout);
        if (totalSeconds < MinWorkoutDurationSeconds || totalSeconds > MaxWorkoutDurationSeconds)
        {
            errors.Add(new WorkoutValidationError(
                "workout_duration_out_of_bounds",
                $"{prefix}steps",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Total workout duration {totalSeconds}s is outside allowed bounds [{MinWorkoutDurationSeconds}s, {MaxWorkoutDurationSeconds}s].")));
        }
    }

    private static void ValidateStep(
        WorkoutStepNode step,
        string sessionType,
        bool isAerobicSession,
        string stepField,
        List<WorkoutValidationError> errors)
    {
        if (isAerobicSession)
        {
            CheckAerobicTextForPaceLeak(step.Cue, $"{stepField}.cue", errors);
        }

        switch (step.Kind)
        {
            case "warmup" or "steady" or "cooldown":
                ValidateScalarStep(step, sessionType, isAerobicSession, stepField, errors);
                break;

            case "repeat":
                ValidateRepeatStep(step, sessionType, isAerobicSession, stepField, errors);
                break;

            default:
                errors.Add(new WorkoutValidationError(
                    "invalid_step_kind",
                    $"{stepField}.kind",
                    $"Unsupported step kind '{step.Kind}'. Expected warmup, steady, repeat, or cooldown."));
                break;
        }
    }

    private static void ValidateScalarStep(
        WorkoutStepNode step,
        string sessionType,
        bool isAerobicSession,
        string stepField,
        List<WorkoutValidationError> errors)
    {
        if (step.Reps.HasValue || step.Work is not null || step.Recovery is not null)
        {
            errors.Add(new WorkoutValidationError(
                "unexpected_repeat_fields",
                stepField,
                $"Step kind '{step.Kind}' must not define reps, work, or recovery."));
        }

        if (step.DurationSeconds is null or < MinStepDurationSeconds)
        {
            errors.Add(new WorkoutValidationError(
                "invalid_step_duration",
                $"{stepField}.duration_seconds",
                $"Step kind '{step.Kind}' requires duration_seconds >= {MinStepDurationSeconds}."));
        }

        if (step.Target is null)
        {
            errors.Add(new WorkoutValidationError(
                "missing_step_target",
                $"{stepField}.target",
                $"Step kind '{step.Kind}' requires a target."));
            return;
        }

        if (!ValidateTarget(step.Target, $"{stepField}.target", errors))
        {
            return;
        }

        if (step.Kind is "warmup" or "cooldown")
        {
            if (!IsHrZoneWithin(step.Target, 2))
            {
                errors.Add(new WorkoutValidationError(
                    "warmup_cooldown_must_be_low_hr",
                    $"{stepField}.target",
                    $"{step.Kind} steps must use hr_zone <= Z2 HR."));
            }
        }

        if (isAerobicSession)
        {
            var maxZone = string.Equals(sessionType, "recovery_run", StringComparison.Ordinal) ? 1 : 2;
            if (!IsHrZoneWithin(step.Target, maxZone))
            {
                errors.Add(new WorkoutValidationError(
                    "aerobic_session_forbids_non_hr_or_high_zone",
                    $"{stepField}.target",
                    $"Session type '{sessionType}' must use hr_zone <= Z{maxZone} HR."));
            }
        }
    }

    private static void ValidateRepeatStep(
        WorkoutStepNode step,
        string sessionType,
        bool isAerobicSession,
        string stepField,
        List<WorkoutValidationError> errors)
    {
        if (step.DurationSeconds.HasValue || step.Target is not null)
        {
            errors.Add(new WorkoutValidationError(
                "unexpected_steady_fields",
                stepField,
                "Repeat step must not define top-level duration_seconds or target."));
        }

        if (step.Reps is null or < 2 or > 20)
        {
            errors.Add(new WorkoutValidationError(
                "invalid_repeat_reps",
                $"{stepField}.reps",
                "Repeat step requires reps in [2, 20]."));
        }

        if (step.Work is null || step.Work.DurationSeconds < MinStepDurationSeconds)
        {
            errors.Add(new WorkoutValidationError(
                "missing_or_invalid_repeat_leg",
                $"{stepField}.work",
                $"Repeat work leg is required with duration_seconds >= {MinStepDurationSeconds}."));
        }
        else
        {
            if (isAerobicSession)
            {
                CheckAerobicTextForPaceLeak(step.Work.Cue, $"{stepField}.work.cue", errors);
            }

            if (ValidateTarget(step.Work.Target, $"{stepField}.work.target", errors))
            {
                if (step.Work.DurationSeconds < ShortIntervalHrThresholdSeconds
                    && string.Equals(step.Work.Target.Modality, "hr_zone", StringComparison.Ordinal))
                {
                    errors.Add(new WorkoutValidationError(
                        "short_interval_forbids_hr_target",
                        $"{stepField}.work.target",
                        $"Short intervals (< {ShortIntervalHrThresholdSeconds}s) cannot use hr_zone targets due to cardiac lag; use pace_range or rpe."));
                }

                if (isAerobicSession)
                {
                    var maxZone = string.Equals(sessionType, "recovery_run", StringComparison.Ordinal) ? 1 : 2;
                    if (!IsHrZoneWithin(step.Work.Target, maxZone))
                    {
                        errors.Add(new WorkoutValidationError(
                            "aerobic_session_forbids_non_hr_or_high_zone",
                            $"{stepField}.work.target",
                            $"Session type '{sessionType}' must use hr_zone <= Z{maxZone} HR."));
                    }
                }
            }
        }

        if (step.Recovery is null || step.Recovery.DurationSeconds < MinStepDurationSeconds)
        {
            errors.Add(new WorkoutValidationError(
                "missing_or_invalid_repeat_leg",
                $"{stepField}.recovery",
                $"Repeat recovery leg is required with duration_seconds >= {MinStepDurationSeconds}."));
        }
        else
        {
            if (isAerobicSession)
            {
                CheckAerobicTextForPaceLeak(step.Recovery.Cue, $"{stepField}.recovery.cue", errors);
            }

            if (ValidateTarget(step.Recovery.Target, $"{stepField}.recovery.target", errors))
            {
                if (string.Equals(step.Recovery.Target.Modality, "hr_zone", StringComparison.Ordinal)
                    && !IsHrZoneWithin(step.Recovery.Target, 2))
                {
                    errors.Add(new WorkoutValidationError(
                        "recovery_leg_exceeds_z2",
                        $"{stepField}.recovery.target",
                        "Recovery interval HR target must not exceed Z2 HR."));
                }

                if (isAerobicSession)
                {
                    var maxZone = string.Equals(sessionType, "recovery_run", StringComparison.Ordinal) ? 1 : 2;
                    if (!IsHrZoneWithin(step.Recovery.Target, maxZone))
                    {
                        errors.Add(new WorkoutValidationError(
                            "aerobic_session_forbids_non_hr_or_high_zone",
                            $"{stepField}.recovery.target",
                            $"Session type '{sessionType}' must use hr_zone <= Z{maxZone} HR."));
                    }
                }
            }
        }
    }

    private static bool ValidateTarget(
        WorkoutTargetSpec target,
        string field,
        List<WorkoutValidationError> errors)
    {
        var hasHr = target.HrZoneStart.HasValue || target.HrZoneEnd.HasValue;
        var hasPace = target.PaceFastSecPerKm.HasValue || target.PaceSlowSecPerKm.HasValue;
        var hasRpe = target.Rpe.HasValue;

        var populatedModalities = (hasHr ? 1 : 0) + (hasPace ? 1 : 0) + (hasRpe ? 1 : 0);
        if (populatedModalities > 1)
        {
            errors.Add(new WorkoutValidationError(
                "mixed_modality_target",
                field,
                "Target cannot combine HR, Pace, and RPE properties in a single step."));
            return false;
        }

        switch (target.Modality)
        {
            case "hr_zone":
                if (hasPace || hasRpe)
                {
                    errors.Add(new WorkoutValidationError("mixed_modality_target", field, "hr_zone target cannot define pace or RPE fields."));
                    return false;
                }
                if (target.HrZoneStart is null or < 1 or > 5)
                {
                    errors.Add(new WorkoutValidationError("invalid_hr_zone", field, "hr_zone_start must be in [1, 5]."));
                    return false;
                }
                if (target.HrZoneEnd.HasValue && (target.HrZoneEnd.Value <= target.HrZoneStart.Value || target.HrZoneEnd.Value > 5))
                {
                    errors.Add(new WorkoutValidationError("invalid_hr_zone", field, "hr_zone_end must be > hr_zone_start and <= 5."));
                    return false;
                }
                return true;

            case "pace_range":
                if (hasHr || hasRpe)
                {
                    errors.Add(new WorkoutValidationError("mixed_modality_target", field, "pace_range target cannot define HR or RPE fields."));
                    return false;
                }
                if (target.PaceFastSecPerKm is null or < MinPaceSecPerKm or > MaxPaceSecPerKm
                    || target.PaceSlowSecPerKm is null or < MinPaceSecPerKm or > MaxPaceSecPerKm
                    || target.PaceSlowSecPerKm.Value < target.PaceFastSecPerKm.Value
                    || target.PaceSlowSecPerKm.Value - target.PaceFastSecPerKm.Value > MaxPaceSpreadSeconds)
                {
                    errors.Add(new WorkoutValidationError(
                        "invalid_pace_range",
                        field,
                        $"pace_range requires fast/slow in [{MinPaceSecPerKm}, {MaxPaceSecPerKm}] s/km with slow >= fast and spread <= {MaxPaceSpreadSeconds}s."));
                    return false;
                }
                return true;

            case "rpe":
                if (hasHr || hasPace)
                {
                    errors.Add(new WorkoutValidationError("mixed_modality_target", field, "rpe target cannot define HR or pace fields."));
                    return false;
                }
                if (target.Rpe is null or < 1 or > 10)
                {
                    errors.Add(new WorkoutValidationError("invalid_rpe_target", field, "rpe must be in [1, 10]."));
                    return false;
                }
                return true;

            default:
                errors.Add(new WorkoutValidationError(
                    "invalid_target_modality",
                    $"{field}.modality",
                    $"Unsupported target modality '{target.Modality}'. Expected hr_zone, pace_range, or rpe."));
                return false;
        }
    }

    private static bool IsHrZoneWithin(WorkoutTargetSpec target, int maxAllowedZone)
    {
        if (!string.Equals(target.Modality, "hr_zone", StringComparison.Ordinal) || !target.HrZoneStart.HasValue)
        {
            return false;
        }

        var upperZone = target.HrZoneEnd ?? target.HrZoneStart.Value;
        return upperZone <= maxAllowedZone;
    }

    private static void CheckAerobicTextForPaceLeak(
        string? text,
        string field,
        List<WorkoutValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var match = PaceLeakPattern().Match(text);
        if (match.Success)
        {
            errors.Add(new WorkoutValidationError(
                "aerobic_session_pace_leak_in_text",
                field,
                $"Aerobic/recovery session text must not mention target paces ('{match.Value}')."));
        }
    }
}
