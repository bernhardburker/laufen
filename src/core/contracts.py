"""Executable data contracts and schema validators for Laufen artifacts.

Provides zero-dependency TypedDict definitions and runtime validation helpers
for all persistent JSON structures and inter-module payloads:
- Run reviews (`run_reviews.json` and `evaluate_run_deep()` output, schema_version: 2)
- Coach cycle memory (`coach_history.json`)
- Athlete profile configuration (`athlete.json`)
- Adaptive planning telemetry markers (`extract_planning_markers()`)
- Intervals.icu calendar events (`calendar_events.json`)

Agents and developers MUST consult and update this module whenever changing
dictionary keys produced or consumed across `src/analysis/`, `src/planner/`,
`src/integrations/`, or `src/visualization/`.
"""

from __future__ import annotations

from typing import Any, Dict, List, Optional, Tuple, TypedDict


# ==============================================================================
# 1. Run Review Contracts (schema_version: 2)
# ==============================================================================

class ParsedIntervalContract(TypedDict, total=False):
    raw: str
    reps: int
    duration_s: int
    duration_str: str
    avg_hr: int
    role: str


class Stage1PhysiologyContract(TypedDict):
    headline: str
    summary: str
    ef: Optional[float]
    tsb_at_run: Optional[float]
    parsed_intervals: List[ParsedIntervalContract]


class Stage2ComplianceContract(TypedDict, total=False):
    planned_slot: str
    intent_source: str
    intended_type: str
    status: str
    status_label: str
    intervals_icu_compliance: Optional[float]
    target_comparison: str
    compliance_notes: str


class Stage3OptimizationItemContract(TypedDict, total=False):
    category: str
    title: str
    text: str
    actionable_advice: str
    cli_command: str


class Stage4NextRunContract(TypedDict):
    recovery_hours: int
    tsb_context: str
    next_slot: str
    next_workout_type: str
    target_hr_max: int
    duration_min: int
    directive: str
    optimization_focus: str


class RunReviewStagesContract(TypedDict):
    stage1_physiology: Stage1PhysiologyContract
    stage2_compliance: Stage2ComplianceContract
    stage3_optimizations: List[Stage3OptimizationItemContract]
    stage4_next_run: Stage4NextRunContract


class RunReviewEvaluationV2Contract(TypedDict, total=False):
    """Output of `evaluate_run_deep()` in `src/analysis/run_reviewer.py`."""
    schema_version: int
    rating: str
    rating_label: str
    summary: str
    coach_tip: str
    cadence_feedback: str
    athlete_note: Optional[str]
    zone_distribution_text: str
    stages: RunReviewStagesContract


class CachedRunReviewRecordContract(TypedDict, total=False):
    """Per-activity entry stored in `athletes/<slug>/run_reviews.json`."""
    id: str
    date: str
    name: str
    distance_km: float
    moving_time_s: int
    moving_time_str: str
    pace_str: str
    avg_hr: float
    max_hr: int
    cadence_spm: float
    elevation_gain_m: float
    training_load: float
    icu_ctl: Optional[float]
    icu_atl: Optional[float]
    tsb_at_run: Optional[float]
    aerobic_ef: Optional[float]
    z1_z2_pct: float
    zone_breakdown: Dict[str, Any]
    intervals: List[str]
    intensity_class: str
    review: RunReviewEvaluationV2Contract


# ==============================================================================
# 2. Coach Cycle Memory Contract (`coach_history.json`)
# ==============================================================================

class CoachCycleEntryContract(TypedDict, total=False):
    """Single coaching cycle record inside `athletes/<slug>/coach_history.json`."""
    id: str
    date: str
    recommendation: str
    target_type: str
    target_hr_max: Optional[int]
    status: str  # "offen" | "erfüllt" | "teilweise" | "verfehlt"
    actual: str
    effect: str
    evaluated_run_ids: List[str]


# ==============================================================================
# 3. Athlete Profile Contract (`athlete.json`)
# ==============================================================================

class WeeklySlotContract(TypedDict, total=False):
    slot: int
    name: str
    role: str  # "quality" | "flexible" | "base_anchor"
    allowed_days: List[str]
    allowed_types: List[str]
    preferred_type: str
    default_type: str
    workout_type: str
    description: str
    target: str
    requires_base_anchor: bool


class AthleteHeartRateContract(TypedDict, total=False):
    max_hr: int
    resting_hr: int
    lthr: int
    zones: Dict[str, Dict[str, Any]]
    icu_hr_zones: List[int]
    hr_zone_names: List[str]


class AthleteBodyContract(TypedDict, total=False):
    id: str
    name: str
    display_name: str
    classification: str
    training_mode: str
    training_model_name: str
    target_runs_per_week: int
    allow_consecutive_run_days: bool
    weekly_slots: List[WeeklySlotContract]
    heart_rate: AthleteHeartRateContract


class AthleteProfileContract(TypedDict):
    athlete: AthleteBodyContract


# ==============================================================================
# 4. Planning Markers Contract (`extract_planning_markers()`)
# ==============================================================================

class ReadinessMarkersContract(TypedDict, total=False):
    ctl: float
    atl: float
    tsb: float
    resting_hr_latest: Optional[int]
    resting_hr_7d_avg: Optional[float]
    hrv_latest: Optional[float]
    sleep_hours_last_night: float
    sleep_score_last_night: float


class PeriodizationMarkersContract(TypedDict, total=False):
    target_day_name: str
    slot_id: int
    slot_name: str
    role: str
    allowed_types: List[str]
    preferred_type: str
    slot_purpose: str


class GuardrailsContract(TypedDict):
    quality_discouraged: bool
    fatigue_warning: bool
    rest_day_recommended: bool
    volume_spike_alert: bool
    slot_allows_threshold: bool
    active_warnings: List[str]


class PlanningMarkersContract(TypedDict):
    reference_date: str
    target_date: str
    athlete: Dict[str, Any]
    readiness: ReadinessMarkersContract
    history: Dict[str, Any]
    periodization: PeriodizationMarkersContract
    guardrails: GuardrailsContract


# ==============================================================================
# 5. Runtime Contract Validators
# ==============================================================================

def validate_run_review_evaluation(evaluation: Dict[str, Any]) -> Tuple[bool, List[str]]:
    """Validates an `evaluate_run_deep()` dictionary against `RunReviewEvaluationV2Contract`."""
    errors: List[str] = []
    if not isinstance(evaluation, dict):
        return False, ["Evaluation must be a dictionary."]

    if evaluation.get("schema_version") != 2:
        errors.append(f"Expected schema_version == 2, got {evaluation.get('schema_version')!r}.")

    for top_key in ("rating", "rating_label", "summary", "coach_tip", "cadence_feedback", "zone_distribution_text", "stages"):
        if top_key not in evaluation:
            errors.append(f"Missing top-level evaluation key '{top_key}'.")

    stages = evaluation.get("stages")
    if not isinstance(stages, dict):
        errors.append("'stages' must be a dictionary.")
        return len(errors) == 0, errors

    for stage_key in ("stage1_physiology", "stage2_compliance", "stage3_optimizations", "stage4_next_run"):
        if stage_key not in stages:
            errors.append(f"Missing stage key 'stages.{stage_key}'.")

    s1 = stages.get("stage1_physiology")
    if isinstance(s1, dict):
        for k in ("headline", "summary", "parsed_intervals"):
            if k not in s1:
                errors.append(f"Missing key 'stages.stage1_physiology.{k}'.")

    s2 = stages.get("stage2_compliance")
    if isinstance(s2, dict):
        for k in ("planned_slot", "intent_source", "intended_type", "status", "status_label", "target_comparison"):
            if k not in s2:
                errors.append(f"Missing key 'stages.stage2_compliance.{k}'.")

    s3 = stages.get("stage3_optimizations")
    if not isinstance(s3, list) or len(s3) == 0:
        errors.append("'stages.stage3_optimizations' must be a non-empty list.")
    else:
        for idx, item in enumerate(s3):
            if not isinstance(item, dict):
                errors.append(f"Optimization item [{idx}] must be a dict.")
                continue
            for k in ("category", "title", "text"):
                if k not in item:
                    errors.append(f"Missing key 'stages.stage3_optimizations[{idx}].{k}'.")

    s4 = stages.get("stage4_next_run")
    if isinstance(s4, dict):
        for k in ("recovery_hours", "tsb_context", "next_slot", "next_workout_type", "target_hr_max", "duration_min", "directive", "optimization_focus"):
            if k not in s4:
                errors.append(f"Missing key 'stages.stage4_next_run.{k}'.")

    return len(errors) == 0, errors


def validate_coach_history(history: Any) -> Tuple[bool, List[str]]:
    """Validates a `coach_history.json` list against `CoachCycleEntryContract`."""
    errors: List[str] = []
    if not isinstance(history, list):
        return False, ["coach_history must be a list of cycle entries."]

    valid_statuses = {"offen", "erfüllt", "teilweise", "verfehlt"}
    for idx, entry in enumerate(history):
        if not isinstance(entry, dict):
            errors.append(f"Cycle entry [{idx}] must be a dictionary.")
            continue
        for k in ("id", "date", "recommendation", "status"):
            if k not in entry:
                errors.append(f"Cycle entry [{idx}] missing required key '{k}'.")
        status = entry.get("status")
        if status and status not in valid_statuses:
            errors.append(f"Cycle entry [{idx}] has invalid status '{status}'.")
        if status != "offen" and "evaluated_run_ids" not in entry:
            errors.append(f"Closed cycle entry [{idx}] (status='{status}') missing 'evaluated_run_ids'.")
        if "evaluated_run_ids" in entry and not isinstance(entry["evaluated_run_ids"], list):
            errors.append(f"Cycle entry [{idx}] 'evaluated_run_ids' must be a list.")

    return len(errors) == 0, errors


def validate_athlete_profile(profile: Any) -> Tuple[bool, List[str]]:
    """Validates an `athlete.json` dictionary against `AthleteProfileContract`."""
    errors: List[str] = []
    if not isinstance(profile, dict) or "athlete" not in profile:
        return False, ["Profile must be a dictionary containing top-level 'athlete' key."]

    athlete = profile["athlete"]
    if not isinstance(athlete, dict):
        return False, ["'athlete' value must be a dictionary."]

    for k in ("id", "name", "heart_rate"):
        if k not in athlete:
            errors.append(f"Missing key 'athlete.{k}'.")

    hr = athlete.get("heart_rate")
    if isinstance(hr, dict):
        for k in ("max_hr", "resting_hr", "lthr", "zones"):
            if k not in hr:
                errors.append(f"Missing key 'athlete.heart_rate.{k}'.")

    slots = athlete.get("weekly_slots")
    if slots is not None:
        if not isinstance(slots, list):
            errors.append("'athlete.weekly_slots' must be a list.")
        else:
            for idx, slot in enumerate(slots):
                if not isinstance(slot, dict):
                    errors.append(f"Slot [{idx}] must be a dictionary.")
                    continue
                for k in ("slot", "name", "role", "allowed_days"):
                    if k not in slot:
                        errors.append(f"Slot [{idx}] missing key '{k}'.")

    return len(errors) == 0, errors


def validate_planning_markers(markers: Any) -> Tuple[bool, List[str]]:
    """Validates an `extract_planning_markers()` dictionary against `PlanningMarkersContract`."""
    errors: List[str] = []
    if not isinstance(markers, dict):
        return False, ["Planning markers must be a dictionary."]

    for top_key in ("reference_date", "target_date", "athlete", "readiness", "history", "periodization", "guardrails"):
        if top_key not in markers:
            errors.append(f"Missing top-level marker key '{top_key}'.")

    readiness = markers.get("readiness")
    if isinstance(readiness, dict):
        for k in ("ctl", "atl", "tsb", "sleep_hours_last_night", "sleep_score_last_night"):
            if k not in readiness:
                errors.append(f"Missing key 'readiness.{k}'.")

    periodization = markers.get("periodization")
    if isinstance(periodization, dict):
        for k in ("target_day_name", "slot_id", "slot_name", "allowed_types", "preferred_type"):
            if k not in periodization:
                errors.append(f"Missing key 'periodization.{k}'.")

    guardrails = markers.get("guardrails")
    if isinstance(guardrails, dict):
        for k in ("quality_discouraged", "fatigue_warning", "rest_day_recommended", "volume_spike_alert", "slot_allows_threshold", "active_warnings"):
            if k not in guardrails:
                errors.append(f"Missing key 'guardrails.{k}'.")

    return len(errors) == 0, errors
