# Iterative C# / .NET Migration & Discovery Plan

**Date:** 2026-11-14  
**Status:** Active (Discovery-Driven / Iterative)  
**Authoritative Domain Reference:** [`SYSTEM_SPEC.md`](SYSTEM_SPEC.md)  
**Existing Python Contracts:** [`../src/core/contracts.py`](../src/core/contracts.py)

---

## 1. Guiding Principles

1. **Zero Modification to the Existing Python Project (Additive / Shadow & Test-Only Mode)**:
   - All existing Python modules in `src/`, BDD tests in `tests/`, the `./run` CLI runner, and the Python production deployment (`~/dockerContainer/laufen`) remain untouched and active until the very end of the migration.
   - All C# / **.NET 11 (`net11.0`)** work is added strictly on top and isolated inside `dotnet/` (deployed independently to `~/dockerContainer/laufen-dotnet`), with new capabilities enabled only selectively for testing and comparison.
2. **Intentionally Low Upfront Detail (Emergent Architecture)**:
   - We do **not** pre-specify all future layers, classes, or services upfront. Translating the existing Python structure 1:1 into a rigid master plan would carry over hidden coupling and domain flaws.
   - Instead, we proceed **one small step at a time**, evaluate what we uncover against real athlete data (`athletes/<slug>/*.json`), and decide the next step based on those findings.
3. **Separation by Responsibility**:
   - Code is strictly separated by task and responsibility (Domain Models & Rules vs. Data Access / Infrastructure vs. Application Use Cases vs. Presentation / CLI).
4. **Discovery Through Strict Typing & Differential Checks**:
   - Every step uses C#'s type system, strict JSON deserialization, and side-by-side comparison against the Python output to expose silent fallbacks (e.g., `.get(..., 0)` masking `null`), schema drift, and conflicting business rules.

---

## 2. Iterative Workflow Loop

Each iteration follows a four-step feedback cycle before we commit to the next scope:

```mermaid
flowchart LR
    S1["1. Pick Small Slice\n(Single Task / Boundary)"] --> S2["2. Model Cleanly in C#\n(Strict Types & Responsibility)"]
    S2 --> S3["3. Run Against Real Data\n(athletes/<slug>/*.json)"]
    S3 --> S4["4. Evaluate & Decide Next Step\n(Log Weaknesses Found)"]
    S4 --> S1
```

### Loop Execution Constraints (Prompt & Edit Hygiene)

1. **Batch Schema Inspection First (`jq`)**: Before writing or editing C# domain contracts, run **one** combined `jq` query across all target tenant files (`athletes/berni/`, `athletes/berni-test/`) to extract all keys, types, 100%-present vs. optional fields, and timestamp formats (`DateOnly` vs. `DateTime` vs. `DateTimeOffset`). Do not discover optional fields through repeated `dotnet test` edit-fail cycles.
2. **Single-Pass Code & Test Authoring**: Author the domain model, loader, and tests in one complete pass based on the upfront schema inspection. Never hardcode volatile live-synced values (e.g., `resting_hr`, `max_hr`, zone counts) in contract tests—use physiological range assertions (`Is.InRange(...)`).
3. **Prefix-Matchable Commands Only**: Never use inline environment-variable prefixes (`VAR=val ...`), shell redirects (`>`), `cp`/`sed` file writes, or chained operators (`&&`, `;`, `||`) in `run_command`. Use native `edit_file`/`write_file` for file edits and clean single-command invocations for tests.
4. **Single Documentation Update per Loop**: Update Section 3 and Section 4 (`W-xx`) **once** at the end of a completed loop—never incrementally mid-loop.

---

## 3. High-Level Horizon (Uncommitted Direction)

Only **Step 1** is defined for immediate execution. Subsequent steps are rough placeholders and will be shaped after evaluating Step 1:

- **Step 1 (Completed) — Workspace Skeleton & Real-Data Contract Inspection**:
  - Created the .NET 11 solution in `dotnet/Laufen.sln` with four projects separated by responsibility: `Laufen.Domain`, `Laufen.Infrastructure`, `Laufen.Cli`, and `Laufen.Tests`.
  - Loaded and strictly deserialized all core JSON files across `athletes/berni/` and `athletes/berni-test/` (`registry.json`, `athlete.json`, `intervals_activities.json`, `intervals_wellness.json`, `run_reviews.json`, `coach_history.json`).
  - Documented all schema mismatches, unexpected `null` fields, and structural weaknesses uncovered by strict typing (`W-01`–`W-09`).
- **Step 2 (Completed) — Pure Physiological & Trend Calculations (`report` / Weekly Metrics)**:
  - Implemented pure domain calculations in `Laufen.Domain` (`PhysiologicalCalculator`, `RunPhysiologicalMetrics`, `WeeklyTrendSummary`, `FitnessFormSummary`, `AthleteReportSnapshot`) for AEF, weekly volume & zone distribution, and Banister CTL/ATL/TSB with exponential rest-day decay.
  - Added differential tests in `dotnet/tests/Laufen.Tests/PhysiologicalMetricsDifferentialTests.cs` verifying exact parity against Python (`trends.py`, `recent_runs.py`, `forecast.py`) across `berni` and `berni-test`.
  - Documented base-run threshold drift, per-run rounding propagation, hardcoded CTL/ATL fallback, and activity-filter asymmetry (`W-10`–`W-13`).
- **Step 3 (Completed) — Readiness Markers & Guardrails (`extract_markers`)**:
  - Implemented pure domain marker and guardrail evaluation in `Laufen.Domain` (`PlanningMarkersCalculator`, `PlanningMarkersSnapshot`, `ReadinessMarkers`, `RunHistoryMarkers`, `PeriodizationMarkers`, `CoachContextMarkers`, `CalendarContextMarkers`, `GuardrailEvaluation`).
  - Added differential tests in `dotnet/tests/Laufen.Tests/PlanningMarkersDifferentialTests.cs` verifying parity against Python `extract_markers.py` across `berni` (`2026-11-11`, `2026-11-10`) and `berni-test` (`2026-11-11`).
  - Documented hardcoded `target_runs_per_week`, ISO-week-53 boundary bug, `injury: 0.0` string-coercion bug, and unmodeled synthetic fields (`W-15`–`W-18`).
- **Step 4 (Completed) — `calendar_events.json` Strict Contract & Calendar Context**:
  - Modeled `CalendarEventEntry`, `WorkoutDoc`, `WorkoutStep`, `WorkoutStepTarget`, and `WorkoutZoneTime` in `Laufen.Domain` with `StrictJsonLoader.LoadCalendarEvents` in `Laufen.Infrastructure`.
  - Added `PlanningMarkersCalculator.GetCalendarContext` and differential tests in `dotnet/tests/Laufen.Tests/CalendarEventContractTests.cs` against Python `extract_markers.get_calendar_context` (`W-19`).
- **Later Candidates**:
  - Vertical `Laufen.Cli` slice (`report` / `markers` commands).
  - Workout planning (`schedule_workout` / `daily_recommendation`).
  - Post-run review (`run_reviewer`) & AI Coach (`ai_coach`).
  - External API clients (Intervals.icu, Telegram, LLM).

---

## 4. Discovered Weaknesses & Architectural Log

*This section is updated after each step with concrete flaws discovered in the existing data contracts or Python implementation.*

| ID | Discovered In Step | Affected Python / Data Area | Description of Weakness | Resolution in .NET |
| :--- | :--- | :--- | :--- | :--- |
| W-01 | Step 1 (`registry.json` & `athlete.json`) | `athletes/berni/athlete.json`, `athletes/berni-test/athlete.json` vs. [`../src/core/contracts.py`](../src/core/contracts.py) | `AthleteHeartRateContract` declares `icu_hr_zones` and `hr_zone_names`, whereas real `athlete.json` files store `raw_hr_zones` and `raw_zone_names`. | Mapped exact JSON keys (`raw_hr_zones`, `raw_zone_names`) in `HeartRateProfile` (`Laufen.Domain`) and enforced `JsonUnmappedMemberHandling.Disallow` in `StrictJsonLoader`. |
| W-02 | Step 1 (`registry.json` & `athlete.json`) | `athletes/berni/athlete.json` vs. [`../src/core/contracts.py`](../src/core/contracts.py) | `AthleteBodyContract` omits `pace_targets_min_per_km`, and `WeeklySlotContract` omits `target_hr_range`, `name_display`, `target_hr_max`, `duration_min`, and `is_mandatory_base` present in real `weekly_slots`. | Modeled all observed properties explicitly in `AthleteProfile` and `WeeklySlot` with `required` vs. nullable types (`T?`). |
| W-03 | Step 1 (`registry.json` & `athlete.json`) | `athletes/berni-test/athlete.json` vs. `athletes/berni/athlete.json` | Tenant profiles diverge structurally: `berni-test` lacks `weekly_slots`, `classification`, `training_mode`, `training_model_name`, and `target_runs_per_week`, while `berni` lacks `display_name`. | Marked tenant-optional fields nullable in `AthleteProfile` (`WeeklySlots?`, `DisplayName?`, etc.) so missing periodization config is explicit rather than silently defaulted. |
| W-04 | Step 1 (`registry.json` & `athlete.json`) | `athletes/registry.json` vs. [`../src/core/contracts.py`](../src/core/contracts.py) | `registry.json` has no `TypedDict` contract in `contracts.py`, and `berni-test` omits `telegram_chat_id` and `briefing_enabled`. | Added `AthleteRegistry` and `AthleteRegistryEntry` in `Laufen.Domain` with nullable `TelegramChatId` and `BriefingEnabled`. |
| W-05 | Step 1 (`coach_history.json`) | `athletes/berni/coach_history.json`, `athletes/berni-test/coach_history.json` vs. [`../src/core/contracts.py`](../src/core/contracts.py) | Open coaching cycles (`status: "offen"`) omit `evaluated_run_ids`, and `cycle-011` (`berni`) uses undocumented status `"angepasst_nach_tempo"` outside the contract comment (`"offen" \| "erfüllt" \| "teilweise" \| "verfehlt"`). | Modeled `CoachHistoryEntry` with nullable `EvaluatedRunIds` (`IReadOnlyList<string>?`) and `DateOnly` `Date` while keeping `Status` as `string` to avoid silent drops. |
| W-06 | Step 1 (`intervals_wellness.json`) | `athletes/berni/intervals_wellness.json`, `athletes/berni-test/intervals_wellness.json` vs. [`../src/core/contracts.py`](../src/core/contracts.py) | `intervals_wellness.json` has no `TypedDict` contract in `contracts.py`, and real Intervals.icu payloads exhibit cross-tenant nullability divergence across 47 daily fields: `ctl`, `atl`, `rampRate`, `ctlLoad`, `atlLoad`, `sportInfo`, `restingHR`, `steps`, and `floorsClimbed` are always populated in `berni` but nullable in `berni-test`, whereas `soreness`, `fatigue`, `stress`, and `comments` are populated in `berni-test` and `null` in `berni`. | Modeled all 47 fields and nested `WellnessSportInfo` explicitly in `IntervalsWellnessEntry` with strict `DateOnly` `Id`, `DateTimeOffset` `Updated`, and `T?` for all tenant-nullable metrics. |
| W-07 | Step 1 (`run_reviews.json`) | `athletes/berni/run_reviews.json`, `athletes/berni-test/run_reviews.json` vs. [`../src/core/contracts.py`](../src/core/contracts.py) | `CachedRunReviewRecordContract` omits `analyzed_at` (`DateTimeOffset`) and top-level `athlete_note` (`string?`), and declares `date: str` without noting it is a local `YYYY-MM-DDTHH:MM:SS` timestamp rather than `DateOnly`. `Stage1PhysiologyContract.parsed_intervals` and `zone_breakdown` are untyped (`Dict[str, Any]`). Cross-tenant & item divergence: `berni-test` omits `compliance_notes` (`stage2_compliance`), `actionable_advice` (`stage3_optimizations`), and top-level `athlete_note`; `cli_command` (`stage3_optimizations`) and `optimization_focus` (`stage4_next_run`) are also absent on older/minimal entries. | Modeled the full 4-stage `RunReviewRecord` hierarchy (`ZoneBreakdownEntry`, `ParsedReviewInterval`, `Stage1Physiology`–`Stage4NextRun`) in `Laufen.Domain` with `DateTime` `Date`, `DateTimeOffset` `AnalyzedAt`, and nullable tenant-optional fields. |
| W-08 | Step 1 (`intervals_activities.json`) | `athletes/berni/intervals_activities.json`, `athletes/berni-test/intervals_activities.json` vs. [`../src/core/contracts.py`](../src/core/contracts.py) | `intervals_activities.json` has no `TypedDict` contract in `contracts.py` (184 raw Intervals.icu fields per activity in `berni`, `[]` in `berni-test`). Only 4 fields (`id`, `icu_athlete_id`, `source`, `start_date_local`) are guaranteed across all records because Strava-only stubs omit `type`, `name`, `moving_time`, `icu_hr_zones`, etc. Includes stringified `strava_id`, integer `paired_event_id`, local `DateTime` `start_date_local` without offset, and nested `icu_hrr` object. | Modeled all 184 fields and `IntervalsHeartRateRecovery` explicitly in `IntervalsActivityEntry` (`Laufen.Domain`) with strict `JsonUnmappedMemberHandling.Disallow`, only the 4 guaranteed fields `required`, and `JsonElement?` for currently all-null fields. |
| W-09 | Step 1 (`athlete.json` live sync drift) | `athletes/berni/athlete.json` vs. `dotnet/tests/Laufen.Tests/AthleteContractLoadingTests.cs` | Live `sync-profile` updates physiological baselines and zone counts in `athletes/berni/athlete.json` (`resting_hr` `46 -> 48`, `max_hr` `204 -> 192`, `zones` / `raw_hr_zones` `7 -> 5`). Hardcoding exact values in contract tests causes false failures after profile syncs. | Replaced exact HR and zone-count assertions on live-synced `berni` data with physiological range assertions (`Is.InRange(...)`). |
| W-10 | Step 2 (`trends.py` vs. `SYSTEM_SPEC.md`) | `src/analysis/trends.py` vs. [`SYSTEM_SPEC.md`](SYSTEM_SPEC.md) | `aggregate_weekly_trends` classifies pure base runs using `z1_z2 >= 0.70`, whereas `SYSTEM_SPEC.md` specifies `z1_z2_pct >= 80%`. It also collapses `icu_hr_zone_times` into 5 buckets (`Z5+ = sum(zone_times[4:])`) despite the spec's 7-zone mapping. | Matched Python's `0.70` threshold and 5-bucket aggregation in `PhysiologicalCalculator` for differential parity while isolating the constants so they can be aligned cleanly after Python cutover. |
| W-11 | Step 2 (`trends.py` rounding propagation) | `src/analysis/trends.py` (`calculate_run_metrics` & `aggregate_weekly_trends`) | `calculate_run_metrics` rounds `distance_km` to 2 decimals per run, and `aggregate_weekly_trends` sums those pre-rounded values for `total_distance_km` and `avg_pace_s` instead of summing raw meters. | Preserved exact Python rounding order in `PhysiologicalCalculator.AggregateWeeklyTrends` for differential parity and flagged it for cleanup post-cutover. |
| W-12 | Step 2 (`forecast.py` hardcoded CTL/ATL fallback) | `src/analysis/forecast.py` (`calculate_fitness_form_trend`) | When no activity has `icu_ctl`/`icu_atl` (e.g., `berni-test` with `[]` activities), `calculate_fitness_form_trend` falls back to hardcoded `latest_ctl = 14.6` and `latest_atl = 16.6`, leaking another athlete's baseline into empty tenants if no newer wellness entry overrides it. | Kept parity in `PhysiologicalCalculator.CalculateFitnessFormTrend` for differential verification and flagged the hardcoded baseline for removal in the pure .NET cutover. |
| W-13 | Step 2 (`forecast.py` vs. `trends.py` activity filter) | `src/analysis/forecast.py` vs. `src/analysis/trends.py` | `trends.py` and `recent_runs.py` require `type == "Run"`, `distance > 0`, and `moving_time > 0`, whereas `calculate_fitness_form_trend` only skips entries when `type` is truthy and `!= "Run"`, allowing `type == null` Strava stubs if they carry `icu_ctl`/`icu_atl`. | Replicated Python's exact filter predicate in `CalculateFitnessFormTrend` for differential parity while keeping `FilterValidRuns` strict for run metrics and weekly trends. |
| W-14 | Step 2 (`athlete.json` zone key drift) | `athletes/berni/athlete.json` vs. `src/analysis/forecast.py` | Live `sync-profile` names zone dictionary keys `"z1_zone_1"`..`"z5_zone_5"` instead of `"z2_aerobic"`, causing `forecast.py` (`hr_data.get("zones", {}).get("z2_aerobic", {})`) to miss the synced Z2 ceiling and fall back to hardcoded `148` bpm. | Resolved Z2 max in `PhysiologicalCalculator.CreateReportSnapshot` by `Zone == 2` (`HeartRateZone.Zone`) instead of dictionary key name. |
| W-15 | Step 3 (`extract_markers.py` hardcoded `target_runs_per_week`) | `src/planner/extract_markers.py` (`get_run_history_markers`) | `get_run_history_markers` ignores `athlete_profile["athlete"]["target_runs_per_week"]` and hardcodes `3`, violating `SYSTEM_SPEC.md` 1.1 #3 (no hardcoded athlete preferences). | `PlanningMarkersCalculator.GetRunHistoryMarkers` reads `athleteProfile?.TargetRunsPerWeek ?? 3`. |
| W-16 | Step 3 (`extract_markers.py` ISO-week-53 bug) | `src/planner/extract_markers.py` (`get_run_history_markers`) | Previous ISO week is computed via `current_week - 1 if current_week > 1 else 52`, which skips week 53 when `ref_date` falls in ISO week 1 after a 53-week ISO year (e.g. `2026-W53`). | `PlanningMarkersCalculator.GetRunHistoryMarkers` uses `ISOWeek.GetYear(refDate.AddDays(-7))` and `ISOWeek.GetWeekOfYear(refDate.AddDays(-7))`. |
| W-17 | Step 3 (`extract_markers.py` `injury` coercion & CTL fallback drift) | `src/planner/extract_markers.py` (`get_readiness_markers`) | Checks `injury` via string coercion (`str(raw_inj) not in ("none", "null", "0", "false")`), so numeric `0.0` evaluates as `"0.0"` and triggers a false injury warning; also uses hardcoded `ctl=13.0 / atl=15.0` fallback instead of `forecast.py`'s `14.6 / 16.6`. | `PlanningMarkersCalculator.GetReadinessMarkers` checks numeric `rec.Injury is > 0` cleanly. |
| W-18 | Step 3 (`extract_markers.py` unmodeled synthetic keys) | `src/planner/extract_markers.py` vs. `IntervalsActivityEntry` / `AthleteProfile` | Checks `run.get("workout_type")`, `run.get("athlete_note")`, and `slot.get("is_base_anchor")`, none of which exist in real Intervals.icu `intervals_activities.json` or `athlete.json` payloads. | Used the strictly modeled contract properties (`Name`, `Description`, `Role`, `IsMandatoryBase`) in `PlanningMarkersCalculator`. |
| W-19 | Step 4 (`calendar_events.json`) | `athletes/berni/calendar_events.json`, `athletes/berni-test/` vs. [`../src/core/contracts.py`](../src/core/contracts.py) | `calendar_events.json` has no `TypedDict` contract in `contracts.py` (62 top-level fields + recursive `workout_doc.steps` in `berni`), and `athletes/berni-test/calendar_events.json` is absent on disk. | Modeled all 62 fields and recursive `WorkoutDoc`/`WorkoutStep` hierarchy in `CalendarEventEntry` (`Laufen.Domain`), added `StrictJsonLoader.LoadCalendarEvents`, and verified `GetCalendarContext` against Python. |
