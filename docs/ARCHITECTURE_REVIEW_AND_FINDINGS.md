# Architecture Review, Bug Audit & Test Coverage Findings

**Date:** 2026-10-31  
**Target Commit:** `9185903` (`main`)  
**Scope:** Full design review of the `laufen` repository across all integrations, planning engines, analysis modules, Telegram bot daemon, dashboard generator, agent bundle, CI/CD workflows, and BDD test suite.  
**Policy Note:** Per user instructions, **no production code or test files were modified** during the initial audit. All findings, defects, test gaps, agreed architectural decisions, and open questions are documented here.

---

## 1. Confirmed Bugs & Concrete Code Defects

### 1.1 [RESOLVED - BUG] Duplicate Morning Briefings & 07:00 Post-Run Review Burst ("3-Messages Bug")
- **Location:** [`.github/workflows/deploy-pages.yml` (lines 179–197)](../.github/workflows/deploy-pages.yml#L179-L197) + [`src/integrations/telegram_bot.py`](../src/integrations/telegram_bot.py)
- **Resolution:**
  1. **Removed Briefing Step in CI:** Removed `./run briefing --all` from [`.github/workflows/deploy-pages.yml`](../.github/workflows/deploy-pages.yml). The Telegram Bot Daemon on Burkerserver is now the sole authority for morning briefings.
  2. **Morning Briefing Idempotency:** Added an idempotency check (`state.get("last_briefing_date") == today_iso`) to [`send_daily_briefing_for_athlete()`](../src/integrations/telegram_bot.py) with `--force` override support, preventing duplicate briefings on the same day across daemon and CLI invocations.
  3. **Periodic Activity Polling:** Updated `check_and_send_due_post_run_reviews()` in `run_bot_daemon` to poll Intervals.icu periodically (every 10 minutes) for new activities so runs are reviewed promptly when completed and uploaded.
  4. **Recency & Age Protection:** Added run age/date filtering (`is_recent = run_date == today or age <= 6h`) and historical cold-start initialization to prevent yesterday's runs from firing delayed post-run reviews during the morning 07:00 briefing check.
  5. **Automated BDD Tests:** Added 3 BDD scenarios in [`tests/telegram_bot.feature`](../tests/telegram_bot.feature) and step definitions in [`tests/test_telegram_bot.py`](../tests/test_telegram_bot.py) verifying briefing idempotency, filtering of yesterday's runs, and periodic activity polling for today's runs.

---

### 1.2 [RESOLVED - BUG] Post-Run Review Telegram Message Reads Wrong Dictionary Keys (`telegram_bot.py` vs. `run_reviewer.py`)
- **Location:** [`format_post_run_review_message()`](../src/integrations/telegram_bot.py#L288-L370) vs. [`evaluate_run_deep()`](../src/analysis/run_reviewer.py#L801-L816)
- **Resolution:**
  1. **Aligned Schema-v2 Nesting:** Updated [`format_post_run_review_message()`](../src/integrations/telegram_bot.py) to extract stages from `evaluation.get("stages", {})` (`stage1_physiology`, `stage2_compliance`, `stage3_optimizations`, `stage4_next_run`) and `evaluation.get("coach_tip")`, eliminating the empty-dictionary fallbacks.
  2. **Aligned Field Names Across Stages:**
     - Stage 2: Correctly reads `planned_slot`, `intended_type`, `target_comparison`, `compliance_notes`, and `status_label`.
     - Stage 3: Formats optimization titles & texts (`f"{title}: {text}"` or fallback) alongside `actionable_advice`.
     - Stage 4: Reads `recovery_hours`, `next_slot`, `next_workout_type`, and `directive`. The `"Erholungsfenster: ca. X Stunden"` line is now reliably rendered.
  3. **Automated BDD Tests:** Updated [`tests/telegram_bot.feature`](../tests/telegram_bot.feature) and [`tests/test_telegram_bot.py`](../tests/test_telegram_bot.py) to assert actual dynamic values (`Erholungsfenster: ca. 24 Stunden`, `Coach-Beobachtung & Hebel`, `Slot 2`) and added a dedicated scenario verifying dynamic cramp detection, 48h recovery window, and recovery run recommendations.

---

### 1.3 [RESOLVED - BUG] Evening Preview Passes `ref_date=tomorrow` with `clean_past=True` to `sync_adaptive_plan` (`telegram_bot.py` & `schedule_workout.py`)
- **Location:** [`send_evening_preview_for_athlete()`](../src/integrations/telegram_bot.py#L622-L710) calling [`sync_adaptive_plan()`](../src/planner/schedule_workout.py#L715-L880)
- **Resolution:**
  1. **Strict Reference Date (`ref_date = today`):** Updated [`send_evening_preview_for_athlete()`](../src/integrations/telegram_bot.py) to always pass actual today (`today`) and `evening_preview=True` to [`sync_adaptive_plan()`](../src/planner/schedule_workout.py).
  2. **Dedicated Tomorrow Evaluation:** In `sync_adaptive_plan()`, added `is_rest_day_tomorrow = ran_today or tomorrow_has_rest_note` alongside `is_rest_day` (today). In evening preview mode:
     - If athlete ran today (`ran_today == True`): Tomorrow is announced as rest day (`is_rest_day_tomorrow = True`), any uncompleted workout on tomorrow is removed, and next training is scheduled for day after tomorrow (`today + 2`).
     - If athlete did not run today (`ran_today == False`): Tomorrow is a training day (`is_rest_day_tomorrow = False`) and next training is scheduled for tomorrow (`today + 1`).
  3. **Preserve Ongoing Today Workouts:** Removed same-day deletion of uncompleted workouts on `today_iso`. Uncompleted workouts from today remain in place throughout the evening and are only pruned on the next day by the normal `ev_date < today_iso` morning sweep.
  4. **Target Day Marker Spacing:** Enhanced `get_run_history_markers()` in [`extract_markers.py`](../src/planner/extract_markers.py) to provide `target_days_since_last_run` and `target_days_since_last_quality` when `target_date` is specified.
  5. **Automated BDD Tests:** Added scenario `Evening preview preserves uncompleted workout of today and schedules tomorrow` in [`tests/telegram_bot.feature`](../tests/telegram_bot.feature) and step definitions in [`tests/test_telegram_bot.py`](../tests/test_telegram_bot.py). All 146 tests pass.

---

### 1.4 [OPEN - BUG] AI Coach LLM Execution Always Skipped in GitHub Actions (`deploy-pages.yml` + `run-ai-coach.sh`)
- **Location:** [`.github/workflows/deploy-pages.yml` (lines 93–126)](../.github/workflows/deploy-pages.yml#L93-L126) and [`.github/scripts/run-ai-coach.sh` (lines 53–71)](../.github/scripts/run-ai-coach.sh#L53-L71)
- **Symptom:** During scheduled or dispatched runs of `deploy-pages.yml`, the Macro AI Coach (`ai_coach.py`) is **never executed** for existing athletes whose `ai_coach_summary.json` and `ai_coach_commentary.md` are already committed in Git, even when new runs have been downloaded.
- **Root Cause:**
  1. Line 98/100 of `deploy-pages.yml` runs `./run review --all`. This executes [`src/analysis/run_reviewer.py`](../src/analysis/run_reviewer.py#L975-L982), which calls `update_run_reviews()` and writes `"schema_version": 2` entries for **all** new runs into `athletes/<slug>/run_reviews.json`.
  2. Immediately afterward on line 119, `deploy-pages.yml` runs `./.github/scripts/run-ai-coach.sh`.
  3. `run-ai-coach.sh` (lines 53–71) checks `find_unreviewed_runs(runs, reviews)` against `run_reviews.json`. Because `./run review` just populated `run_reviews.json` with `schema_version: 2` for every run, `UNREVIEWED_COUNT` is always `0`!
  4. Since `athletes/berni/ai_coach_summary.json` and `athletes/berni/ai_coach_commentary.md` are tracked in Git (`[ -f "$SUMMARY_FILE" ] && [ -f "$COMMENTARY_FILE" ]`), `run-ai-coach.sh` prints `"No new unreviewed runs found (0 new runs)... Skipping redundant LLM execution"` and exits `0` without ever calling `ai_coach.py`!

---

### 1.5 [OPEN - BUG] `./run dashboard` and `./run preview` Omit `--athlete "$ATHLETE_CONFIG_PATH"` (`run`)
- **Location:** [`run` (lines 239–248 and 258–265)](../run#L239-L265)
- **Symptom:** When generating a dashboard for any non-default athlete (e.g., `./run dashboard --athlete berni-test` or `./run dashboard --all`), `generate_dashboard.py` loads heart rate zones, weekly slots, and pace targets from the legacy root `config/athlete.json` (`berni`) instead of `athletes/<slug>/athlete.json`.
- **Root Cause:** `run` omits `--athlete "$ATHLETE_CONFIG_PATH"`, so [`generate_dashboard.py` (line 715)](../src/visualization/generate_dashboard.py#L715) defaults `--athlete` to `PROJECT_ROOT / "config" / "athlete.json"`.

---

### 1.6 [OPEN - BUG] `sync_adaptive_plan` Truncates or Wipes `calendar_events.json` (`schedule_workout.py`)
- **Location:** [`sync_adaptive_plan()`](../src/planner/schedule_workout.py#L769-L780) and [lines 893–898, 941–946](../src/planner/schedule_workout.py#L893-L946)
- **Symptom:**
  1. In `./run sync`, [`download_events.py`](../src/integrations/download_events.py#L140-L154) downloads calendar events from `-7` days to **`+14` days** into `calendar_events.json`.
  2. Next, `./run sync` runs `schedule_workout.py --adaptive-plan --events-file "$CALENDAR_EVENTS_PATH"`.
  3. `sync_adaptive_plan` fetches events only up to **`+7` days** and overwrites `calendar_events.json` with `remaining_events`, discarding all events between `+8` and `+14` days (or wiping `calendar_events.json` to `[]` if run in `dry_run` mode).

---

### 1.7 [OPEN - BUG] `extract_planning_markers` Hardcodes `z4_subthreshold` Key Lookup (`extract_markers.py`)
- **Location:** [`extract_planning_markers()`](../src/planner/extract_markers.py#L221-L227)
- **Symptom:** `z4_sub = zones.get("z4_subthreshold") or {}` only looks up `"z4_subthreshold"`, falling back to hardcoded `[169, 177]` whenever the key is named `"z4_threshold"` (e.g. standard 5-zone profiles synced via `sync_athlete.py`).

---

## 2. Structural & Architectural Issues

### 2.1 Two Divergent Planning / Recommendation Engines with Rigid `if/else` Logic
- Currently, [`schedule_workout.py`](../src/planner/schedule_workout.py#L618-L736) (`choose_adaptive_workout`) and [`daily_recommendation.py`](../src/analysis/daily_recommendation.py#L661-L993) (`generate_daily_recommendation`) maintain separate procedural `if/else` trees.
- In `daily_recommendation.py`, lines 705–817 check `if calendar_events is not None:` and unconditionally return `"Heute: Ruhetag (Laut Kalender)"` whenever today has no calendar workout, bypassing all downstream logic (lines 819–993).
- See **Section 4.2** below for the agreed architectural redesign.

### 2.2 Extensive Boilerplate Duplication Across Modules
Despite [`src/core/config.py`](../src/core/config.py) existing to centralize configuration and `.env` loading, almost no module imports it:
- `load_env_file()` is copy-pasted across **10 files** (`src/core/config.py`, `download_intervals.py`, `download_wellness.py`, `download_events.py`, `sync_athlete.py`, `upload_activities.py`, `seed_athlete.py`, `onboard_athlete.py`, `manage_note.py`, `schedule_workout.py`).
- `load_athlete_profile()` is duplicated across **5 files** (`src/core/config.py`, `daily_recommendation.py`, `run_reviewer.py`, `ai_coach.py`, `generate_dashboard.py`).
- `format_duration()` and `format_pace()` are implemented separately in 5 files (taking `m/s` in `download_intervals.py` and `recent_runs.py`, vs. `sec/km` in `trends.py`, `forecast.py`, and `generate_dashboard.py`).
- `is_quality_run()` / `is_quality_workout()` is duplicated in `extract_markers.py` and `daily_recommendation.py`.

### 2.3 Legacy Single-Tenant Agent Tools (`agent/tools/*.py`)
- [`agent/tools/get_activities.py`](../agent/tools/get_activities.py#L40), [`agent/tools/get_trends.py`](../agent/tools/get_trends.py#L22), and [`agent/tools/get_upcoming_workouts.py`](../agent/tools/get_upcoming_workouts.py#L18-L20) hardcode `REPO_ROOT / "data" / "intervals_activities.json"` instead of using `src/athletes.py`.
- [`agent/manifest.yaml` (line 61)](../agent/manifest.yaml#L61) omits `micro_threshold` and `progressive_threshold` from the `schedule_workout` tool enum.

### 2.4 CI/CD & Deployment Inconsistencies
1. **Case-Sensitive Path Mismatch on Burkerserver (`deploy-agent.yml` vs. `deploy-burkerserver.yml`)**:
   - `deploy-burkerserver.yml` and `deploy-pages.yml` use `~/dockerContainer/laufen` (uppercase `C`), whereas [`deploy-agent.yml` (line 60)](../.github/workflows/deploy-agent.yml#L60) uses `$HOME/dockercontainer/agents/laufen` (lowercase `c`).
2. **Docker Compose Bot Command (`deploy/docker-compose.bot.yml`)**:
   - [`deploy/docker-compose.bot.yml` (line 19)](../deploy/docker-compose.bot.yml#L19) runs `["python3", "src/integrations/telegram_bot.py"]` without `--scheduler`.
3. **`./run help` Missing Commands**:
   - `[RESOLVED]` Added `events`, `evening`, and `post-run-review` to [`show_help()` in `run`](../run#L31-L86).

---

## 3. Missing Tests & Test Coverage Gaps

| Area / Module | Missing Test Coverage | Status |
| :--- | :--- | :--- |
| **[`src/integrations/manage_note.py`](../src/integrations/manage_note.py) (`./run note`)** | No dedicated `.feature` / `test_*.py` file. CLI flags (`--id`, `--date`, `--time`, `--overwrite`, `--dry-run`), 180-minute time-proximity matching, note deduplication, and `PUT /api/v1/activity/{id}` in `tests/drivers.py` (`UnifiedIntervalsHandler`) are untested via `./run note`. | `[OPEN]` |
| **Post-Run Review Content in Telegram ([`telegram_bot.py`](../src/integrations/telegram_bot.py#L288-L370))** | Dynamic content assertion added in `tests/telegram_bot.feature` verifying Stage 2 compliance, Stage 3 optimizations, and Stage 4 recovery hours (24h/48h) / next slot. | `[RESOLVED]` |
| **Evening Preview Edge Cases ([`telegram_bot.py`](../src/integrations/telegram_bot.py#L622-L710))** | Scenarios added in `tests/telegram_bot.feature` verifying uncompleted workouts of today are preserved and rest days / next workouts for tomorrow / day after tomorrow are scheduled accurately. | `[RESOLVED]` |
| **Multi-Athlete Dashboard Profile Resolution (`./run dashboard --athlete <slug>`)** | Needs a CLI runner test invoking `./run dashboard --athlete <slug>` to verify tenant `athlete.json` is passed to `generate_dashboard.py`. | `[OPEN]` |
| **[`src/integrations/onboard_athlete.py`](../src/integrations/onboard_athlete.py) (`./run add-athlete`, `./run athletes`)** | No BDD test executing `onboard_athlete.py` CLI (`cmd_onboard`, `cmd_list`, `--skip-sync`, error handling). | `[OPEN]` |
| **[`agent/tools/*.py`](../agent/tools/)** | Zero tests for `get_activities.py` (15-min cache TTL), `get_trends.py`, or `get_upcoming_workouts.py`. | `[OPEN]` |
| **Interactive Note Picker & Cancel Callbacks ([`telegram_bot.py`](../src/integrations/telegram_bot.py#L1217-L1308))** | Missing tests for `note_pick_other`, `note_select:<id>`, `note_cancel`, `/evening`, and `/review` commands. | `[OPEN]` |
| **CI Pipeline Script ([`.github/scripts/run-ai-coach.sh`](../.github/scripts/run-ai-coach.sh))** | Needs a test running `./run review` followed by `run-ai-coach.sh` to ensure new runs still trigger Macro AI Coach evaluation. | `[OPEN]` |

---

## 4. Agreed Architecture & Design Decisions (Clarified on 2026-10-31)

### 4.1 [RESOLVED & IMPLEMENTED] Telegram Morning Briefing & Post-Run Review Scheduling
1. **Single Authority for Morning Briefing (`laufen-telegram-bot.service`):**
   - Removed the `./run briefing --all` step from [`.github/workflows/deploy-pages.yml`](../.github/workflows/deploy-pages.yml).
   - The Telegram Bot Daemon (`--scheduler`) is the sole trigger for the morning briefing, sending it once per day at the athlete's configured `briefing_time` (default `07:00`).
   - Added an idempotency check (`last_briefing_date == today_iso`) inside `send_daily_briefing_for_athlete()` (`./run briefing`) so manual or script invocations also never double-send unless `--force` is specified.
2. **Periodic Activity Polling for Immediate Post-Run Reviews:**
   - Updated `check_and_send_due_post_run_reviews()` in the Telegram daemon to poll Intervals.icu periodically (every 10 minutes) for new activities, so the 4-stage post-run review is sent shortly after a run is uploaded rather than waiting until 07:00 the next morning.
   - Guarded against cold-start spam: only auto-notifies for runs completed on `today` (or within 6 hours) and initializes `notified_activity_ids` with existing historical IDs on first run.
3. **Align Crontab Timing Before Briefing:**
   - Recommended crontab schedule for the daily `deploy-pages` cron job is `06:45` (instead of `07:00`) so that data sync, AI Coach analysis, and dashboard generation are already completed and copied to `~/dockerContainer/laufen/athletes/` before the `07:00` morning briefing runs.

---

### 4.2 [DECIDED] Unified Planning Engine: Telemetry Counts & Context Instead of Rigid `if/else`
1. **Unify `schedule_workout.py` and `daily_recommendation.py`:**
   - Consolidate the two separate planning engines into a single unified pipeline.
2. **Quantitative Counts & Benchmarks Instead of Hardcoded `if/else` Decisions:**
   - Do **not** encode rigid procedural `if/else` coaching branches in Python for deload weeks or base-anchor catch-up.
   - Instead, Python (`extract_markers.py`) deterministically computes **objective counts and continuous benchmarks** and provides them to the Coach:
     - `consecutive_build_weeks` / `weeks_since_last_deload` (number of hard/build weeks in sequence, weekly volume & load progression)
     - `days_since_last_base_anchor` / `base_anchors_in_last_14d` (status and recency of long Zone 2 base runs)
     - `days_since_last_run` & `days_since_last_quality`
     - `readiness` (TSB, CTL, ATL, sleep hours/score, RHR vs. baseline, HRV)
     - `coach_directive` (active open cycle from `coach_history.json`)
   - The Coach evaluates these counts holistically in context to decide the appropriate stimulus.
3. **Calendar Events as Context (Neither Blindly Prioritized Nor Ignored):**
   - Remove the hard short-circuit in [`daily_recommendation.py` (lines 705–817)](../src/analysis/daily_recommendation.py#L705-L817) that blindly returns `"Ruhetag (Laut Kalender)"` or unconditionally overrides evaluation whenever `calendar_events.json` exists.
   - Instead, pass existing calendar entries (scheduled workouts, coach notes, rest day notes, races) as **part of the planning context** so the Coach weighs what is currently on the calendar alongside recovery, readiness, and training history (neither blindly preferring nor ignoring the calendar).
4. **Rest Day Policy & Weekly Slots (Per-Athlete Configuration):**
   - Weekly periodization slots (`weekly_slots`), run frequency (`target_runs_per_week`), and rest day policy (`allow_consecutive_run_days`) are strictly per-athlete settings in `athletes/<slug>/athlete.json`.

---

## 5. Documentation & Specification Consolidation (Resolved on 2026-10-31)

### 5.1 [RESOLVED] Unified Domain Specification & Archival of Superseded Drafts
- **Resolution:**
  - Created [`docs/SYSTEM_SPEC.md`](SYSTEM_SPEC.md) as the single authoritative Ground Truth for coaching DNA, physiological formulas, 24-hour operational lifecycles, and SOLL vs. IST architecture.
  - Created [`src/core/contracts.py`](../src/core/contracts.py) with executable `TypedDict` definitions and runtime validators for `run_reviews.json` (`schema_version: 2`), `coach_history.json`, `athlete.json`, and planning markers.
  - Archived historical plans and superseded drafts under [`docs/archive/`](archive/README.md) (including [`docs/archive/plans/4_stage_run_review_and_adaptive_feedback.md`](archive/plans/4_stage_run_review_and_adaptive_feedback.md) and [`docs/archive/UNIFIED_COACH_SPEC.md`](archive/UNIFIED_COACH_SPEC.md)).
  - Added automated drift-prevention BDD tests in [`tests/documentation_integrity.feature`](../tests/documentation_integrity.feature) and [`tests/test_documentation_integrity.py`](../tests/test_documentation_integrity.py).
