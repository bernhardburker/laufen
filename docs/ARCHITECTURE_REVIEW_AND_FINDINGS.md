# Architecture Review, Bug Audit & Test Coverage Findings

**Date:** 2026-10-31  
**Target Commit:** `9185903` (`main`)  
**Scope:** Full design review of `/home/berni/laufen` across all integrations, planning engines, analysis modules, Telegram bot daemon, dashboard generator, agent bundle, CI/CD workflows, and BDD test suite.  
**Policy Note:** Per user instructions, **no production code or test files were modified**. All findings, defects, test gaps, agreed architectural decisions, and open questions are documented here for seamless continuation in the next session.

---

## 1. Confirmed Bugs & Concrete Code Defects (To Fix in Next Session)

### 1.1 [RESOLVED - BUG] Duplicate Morning Briefings & 07:00 Post-Run Review Burst ("3-Messages Bug")
- **Location:** [`.github/workflows/deploy-pages.yml` (lines 179–197)](file:///home/berni/laufen/.github/workflows/deploy-pages.yml#L179-L197) + [`src/integrations/telegram_bot.py` (lines 441–576, 1215–1225, 1623–1725, 1780–1785, 1935–1955)](file:///home/berni/laufen/src/integrations/telegram_bot.py)
- **Resolution:**
  1. **Removed Briefing Step in CI:** Removed `./run briefing --all` from [`.github/workflows/deploy-pages.yml`](file:///home/berni/laufen/.github/workflows/deploy-pages.yml). The Telegram Bot Daemon on Burkerserver is now the sole authority for morning briefings.
  2. **Morning Briefing Idempotency:** Added an idempotency check (`state.get("last_briefing_date") == today_iso`) to [`send_daily_briefing_for_athlete()`](file:///home/berni/laufen/src/integrations/telegram_bot.py) with `--force` override support, preventing duplicate briefings on the same day across daemon and CLI invocations.
  3. **Periodic Activity Polling:** Updated `check_and_send_due_post_run_reviews()` in `run_bot_daemon` to poll Intervals.icu periodically (every 10 minutes) for new activities so runs are reviewed promptly when completed and uploaded.
  4. **Recency & Age Protection:** Added run age/date filtering (`is_recent = run_date == today or age <= 6h`) and historical cold-start initialization to prevent yesterday's runs from firing delayed post-run reviews during the morning 07:00 briefing check.
  5. **Automated BDD Tests:** Added 3 BDD scenarios in [`tests/telegram_bot.feature`](file:///home/berni/laufen/tests/telegram_bot.feature) and step definitions in [`tests/test_telegram_bot.py`](file:///home/berni/laufen/tests/test_telegram_bot.py) verifying briefing idempotency, filtering of yesterday's runs, and periodic activity polling for today's runs. All 139 tests pass.

---

### 1.2 [RESOLVED - BUG] Post-Run Review Telegram Message Reads Wrong Dictionary Keys (`telegram_bot.py` vs. `run_reviewer.py`)
- **Location:** [`format_post_run_review_message()`](file:///home/berni/laufen/src/integrations/telegram_bot.py#L288-L370) vs. [`evaluate_run_deep()`](file:///home/berni/laufen/src/analysis/run_reviewer.py#L801-L816)
- **Resolution:**
  1. **Aligned Schema-v2 Nesting:** Updated [`format_post_run_review_message()`](file:///home/berni/laufen/src/integrations/telegram_bot.py) to extract stages from `evaluation.get("stages", {})` (`stage1_physiology`, `stage2_compliance`, `stage3_optimizations`, `stage4_next_run`) and `evaluation.get("coach_tip")`, eliminating the empty-dictionary fallbacks.
  2. **Aligned Field Names Across Stages:**
     - Stage 2: Correctly reads `planned_slot`, `intended_type`, `target_comparison`, `compliance_notes`, and `status_label`.
     - Stage 3: Formats optimization titles & texts (`f"{title}: {text}"` or fallback) alongside `actionable_advice`.
     - Stage 4: Reads `recovery_hours`, `next_slot`, `next_workout_type`, and `directive`. The `"Erholungsfenster: ca. X Stunden"` line is now reliably rendered.
  3. **Automated BDD Tests:** Updated [`tests/telegram_bot.feature`](file:///home/berni/laufen/tests/telegram_bot.feature) and [`tests/test_telegram_bot.py`](file:///home/berni/laufen/tests/test_telegram_bot.py) to assert actual dynamic values (`Erholungsfenster: ca. 24 Stunden`, `Coach-Beobachtung & Hebel`, `Slot 2`) and added a dedicated scenario verifying dynamic cramp detection, 48h recovery window, and recovery run recommendations. All 140 tests pass.

---

### 1.3 [OPEN - BUG] Evening Preview Passes `ref_date=tomorrow` with `clean_past=True` to `sync_adaptive_plan` (`telegram_bot.py` & `schedule_workout.py`)
- **Location:** [`send_evening_preview_for_athlete()`](file:///home/berni/laufen/src/integrations/telegram_bot.py#L622-L645) calling [`sync_adaptive_plan()`](file:///home/berni/laufen/src/planner/schedule_workout.py#L739-L958)
- **Symptom:**
  1. If the evening preview runs at `20:30` and the athlete had a scheduled workout for **today** that was not completed, `sync_adaptive_plan` treats `ref_date` (`tomorrow`) as `today_iso`, so Step 1 (`ev_date < today_iso and not is_paired`, [line 802](file:///home/berni/laufen/src/planner/schedule_workout.py#L802)) **deletes today's workout** at 20:30.
  2. If the athlete **did** complete a run today (`ref_date = today`), passing `ref_date = tomorrow` to `sync_adaptive_plan` causes [`extract_planning_markers(reference_date=tomorrow)`](file:///home/berni/laufen/src/planner/schedule_workout.py#L817-L824) to compute `days_since_last_run = (tomorrow - today).days == 1`. Then [`ran_yesterday = (days_since_last_run == 1)`](file:///home/berni/laufen/src/planner/schedule_workout.py#L828) triggers `is_rest_day = True` for `tomorrow` and schedules the next workout for `tomorrow + 1` (the day after tomorrow), while also writing a `"🛋️ Ruhetag (Coach)"` note on `tomorrow`.

---

### 1.4 [OPEN - BUG] AI Coach LLM Execution Always Skipped in GitHub Actions (`deploy-pages.yml` + `run-ai-coach.sh`)
- **Location:** [`.github/workflows/deploy-pages.yml` (lines 93–126)](file:///home/berni/laufen/.github/workflows/deploy-pages.yml#L93-L126) and [`.github/scripts/run-ai-coach.sh` (lines 53–71)](file:///home/berni/laufen/.github/scripts/run-ai-coach.sh#L53-L71)
- **Symptom:** During scheduled or dispatched runs of `deploy-pages.yml`, the Macro AI Coach (`ai_coach.py`) is **never executed** for existing athletes whose `ai_coach_summary.json` and `ai_coach_commentary.md` are already committed in Git, even when new runs have been downloaded.
- **Root Cause:**
  1. Line 98/100 of `deploy-pages.yml` runs `./run review --all`. This executes [`src/analysis/run_reviewer.py`](file:///home/berni/laufen/src/analysis/run_reviewer.py#L975-L982), which calls `update_run_reviews()` and writes `"schema_version": 2` entries for **all** new runs into `athletes/<slug>/run_reviews.json`.
  2. Immediately afterward on line 119, `deploy-pages.yml` runs `./.github/scripts/run-ai-coach.sh`.
  3. `run-ai-coach.sh` (lines 53–71) checks `find_unreviewed_runs(runs, reviews)` against `run_reviews.json`. Because `./run review` just populated `run_reviews.json` with `schema_version: 2` for every run, `UNREVIEWED_COUNT` is always `0`!
  4. Since `athletes/berni/ai_coach_summary.json` and `athletes/berni/ai_coach_commentary.md` are tracked in Git (`[ -f "$SUMMARY_FILE" ] && [ -f "$COMMENTARY_FILE" ]`), `run-ai-coach.sh` prints `"No new unreviewed runs found (0 new runs)... Skipping redundant LLM execution"` and exits `0` without ever calling `ai_coach.py`!

---

### 1.5 [OPEN - BUG] `./run dashboard` and `./run preview` Omit `--athlete "$ATHLETE_CONFIG_PATH"` (`run`)
- **Location:** [`run` (lines 239–248 and 258–265)](file:///home/berni/laufen/run#L239-L265)
- **Symptom:** When generating a dashboard for any non-default athlete (e.g., `./run dashboard --athlete berni-test` or `./run dashboard --all`), `generate_dashboard.py` loads heart rate zones, weekly slots, and pace targets from the legacy root `config/athlete.json` (`berni`) instead of `athletes/<slug>/athlete.json`.
- **Root Cause:** `run` omits `--athlete "$ATHLETE_CONFIG_PATH"`, so [`generate_dashboard.py` (line 715)](file:///home/berni/laufen/src/visualization/generate_dashboard.py#L715) defaults `--athlete` to `PROJECT_ROOT / "config" / "athlete.json"`.

---

### 1.6 [OPEN - BUG] `sync_adaptive_plan` Truncates or Wipes `calendar_events.json` (`schedule_workout.py`)
- **Location:** [`sync_adaptive_plan()`](file:///home/berni/laufen/src/planner/schedule_workout.py#L769-L780) and [lines 893–898, 941–946](file:///home/berni/laufen/src/planner/schedule_workout.py#L893-L946)
- **Symptom:**
  1. In `./run sync`, [`download_events.py`](file:///home/berni/laufen/src/integrations/download_events.py#L140-L154) downloads calendar events from `-7` days to **`+14` days** into `calendar_events.json`.
  2. Next, `./run sync` runs `schedule_workout.py --adaptive-plan --events-file "$CALENDAR_EVENTS_PATH"`.
  3. `sync_adaptive_plan` fetches events only up to **`+7` days** and overwrites `calendar_events.json` with `remaining_events`, discarding all events between `+8` and `+14` days (or wiping `calendar_events.json` to `[]` if run in `dry_run` mode).

---

### 1.7 [OPEN - BUG] `extract_planning_markers` Hardcodes `z4_subthreshold` Key Lookup (`extract_markers.py`)
- **Location:** [`extract_planning_markers()`](file:///home/berni/laufen/src/planner/extract_markers.py#L221-L227)
- **Symptom:** `z4_sub = zones.get("z4_subthreshold") or {}` only looks up `"z4_subthreshold"`, falling back to hardcoded `[169, 177]` whenever the key is named `"z4_threshold"` (e.g. standard 5-zone profiles synced via `sync_athlete.py`).

---

## 2. Structural & Architectural Issues

### 2.1 Two Divergent Planning / Recommendation Engines with Rigid `if/else` Logic
- Currently, [`schedule_workout.py`](file:///home/berni/laufen/src/planner/schedule_workout.py#L618-L736) (`choose_adaptive_workout`) and [`daily_recommendation.py`](file:///home/berni/laufen/src/analysis/daily_recommendation.py#L661-L993) (`generate_daily_recommendation`) maintain separate procedural `if/else` trees.
- In `daily_recommendation.py`, lines 705–817 check `if calendar_events is not None:` and unconditionally return `"Heute: Ruhetag (Laut Kalender)"` whenever today has no calendar workout, bypassing all downstream logic (lines 819–993).
- See **Section 4.2** below for the agreed architectural redesign.

### 2.2 Extensive Boilerplate Duplication Across Modules
Despite [`src/core/config.py`](file:///home/berni/laufen/src/core/config.py) existing to centralize configuration and `.env` loading, almost no module imports it:
- `load_env_file()` is copy-pasted across **10 files** (`src/core/config.py`, `download_intervals.py`, `download_wellness.py`, `download_events.py`, `sync_athlete.py`, `upload_activities.py`, `seed_athlete.py`, `onboard_athlete.py`, `manage_note.py`, `schedule_workout.py`).
- `load_athlete_profile()` is duplicated across **5 files** (`src/core/config.py`, `daily_recommendation.py`, `run_reviewer.py`, `ai_coach.py`, `generate_dashboard.py`).
- `format_duration()` and `format_pace()` are implemented separately in 5 files (taking `m/s` in `download_intervals.py` and `recent_runs.py`, vs. `sec/km` in `trends.py`, `forecast.py`, and `generate_dashboard.py`).
- `is_quality_run()` / `is_quality_workout()` is duplicated in `extract_markers.py` and `daily_recommendation.py`.

### 2.3 Legacy Single-Tenant Agent Tools (`agent/tools/*.py`)
- [`agent/tools/get_activities.py`](file:///home/berni/laufen/agent/tools/get_activities.py#L40), [`agent/tools/get_trends.py`](file:///home/berni/laufen/agent/tools/get_trends.py#L22), and [`agent/tools/get_upcoming_workouts.py`](file:///home/berni/laufen/agent/tools/get_upcoming_workouts.py#L18-L20) hardcode `REPO_ROOT / "data" / "intervals_activities.json"` instead of using `src/athletes.py`.
- [`agent/manifest.yaml` (line 61)](file:///home/berni/laufen/agent/manifest.yaml#L61) omits `micro_threshold` and `progressive_threshold` from the `schedule_workout` tool enum.

### 2.4 CI/CD & Deployment Inconsistencies
1. **Case-Sensitive Path Mismatch on Burkerserver (`deploy-agent.yml` vs. `deploy-burkerserver.yml`)**:
   - `deploy-burkerserver.yml` and `deploy-pages.yml` use `~/dockerContainer/laufen` (uppercase `C`), whereas [`deploy-agent.yml` (line 60)](file:///home/berni/laufen/.github/workflows/deploy-agent.yml#L60) uses `$HOME/dockercontainer/agents/laufen` (lowercase `c`).
2. **Docker Compose Bot Command (`deploy/docker-compose.bot.yml`)**:
   - [`deploy/docker-compose.bot.yml` (line 19)](file:///home/berni/laufen/deploy/docker-compose.bot.yml#L19) runs `["python3", "src/integrations/telegram_bot.py"]` without `--scheduler`.
3. **`./run help` Missing Commands**:
   - [`show_help()` in `run`](file:///home/berni/laufen/run#L25-L60) does not list `events` (`sync-events`), `evening` (`evening-preview`), or `post-run-review`.

---

## 3. Missing Tests & Test Coverage Gaps

| Area / Module | Missing Test Coverage | Status |
| :--- | :--- | :--- |
| **[`src/integrations/manage_note.py`](file:///home/berni/laufen/src/integrations/manage_note.py) (`./run note`)** | No dedicated `.feature` / `test_*.py` file. CLI flags (`--id`, `--date`, `--time`, `--overwrite`, `--dry-run`), 180-minute time-proximity matching, note deduplication, and `PUT /api/v1/activity/{id}` in `tests/drivers.py` (`UnifiedIntervalsHandler`) are untested via `./run note`. | `[OPEN]` |
| **Post-Run Review Content in Telegram ([`telegram_bot.py`](file:///home/berni/laufen/src/integrations/telegram_bot.py#L288-L370))** | Dynamic content assertion added in `tests/telegram_bot.feature` verifying Stage 2 compliance, Stage 3 optimizations, and Stage 4 recovery hours (24h/48h) / next slot. | `[RESOLVED]` |
| **Evening Preview Edge Cases ([`telegram_bot.py`](file:///home/berni/laufen/src/integrations/telegram_bot.py#L622-L702))** | Needs scenarios when an uncompleted workout exists for today and verifying exact target date (`tomorrow` vs `tomorrow + 1`) when the athlete ran today. | `[OPEN]` |
| **Multi-Athlete Dashboard Profile Resolution (`./run dashboard --athlete <slug>`)** | Needs a CLI runner test invoking `./run dashboard --athlete <slug>` to verify tenant `athlete.json` is passed to `generate_dashboard.py`. | `[OPEN]` |
| **[`src/integrations/onboard_athlete.py`](file:///home/berni/laufen/src/integrations/onboard_athlete.py) (`./run add-athlete`, `./run athletes`)** | No BDD test executing `onboard_athlete.py` CLI (`cmd_onboard`, `cmd_list`, `--skip-sync`, error handling). | `[OPEN]` |
| **[`agent/tools/*.py`](file:///home/berni/laufen/agent/tools/)** | Zero tests for `get_activities.py` (15-min cache TTL), `get_trends.py`, or `get_upcoming_workouts.py`. | `[OPEN]` |
| **Interactive Note Picker & Cancel Callbacks ([`telegram_bot.py`](file:///home/berni/laufen/src/integrations/telegram_bot.py#L1217-L1308))** | Missing tests for `note_pick_other`, `note_select:<id>`, `note_cancel`, `/evening`, and `/review` commands. | `[OPEN]` |
| **CI Pipeline Script ([`.github/scripts/run-ai-coach.sh`](file:///home/berni/laufen/.github/scripts/run-ai-coach.sh))** | Needs a test running `./run review` followed by `run-ai-coach.sh` to ensure new runs still trigger Macro AI Coach evaluation. | `[OPEN]` |

---

## 4. Agreed Architecture & Design Decisions (Clarified on 2026-10-31)

### 4.1 [RESOLVED & IMPLEMENTED] Telegram Morning Briefing & Post-Run Review Scheduling
1. **Single Authority for Morning Briefing (`laufen-telegram-bot.service`):**
   - Removed the `./run briefing --all` step from [`.github/workflows/deploy-pages.yml`](file:///home/berni/laufen/.github/workflows/deploy-pages.yml).
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
   - Remove the hard short-circuit in [`daily_recommendation.py` (lines 705–817)](file:///home/berni/laufen/src/analysis/daily_recommendation.py#L705-L817) that blindly returns `"Ruhetag (Laut Kalender)"` or unconditionally overrides evaluation whenever `calendar_events.json` exists.
   - Instead, pass existing calendar entries (scheduled workouts, coach notes, rest day notes, races) as **part of the planning context** so the Coach weighs what is currently on the calendar alongside recovery, readiness, and training history (neither blindly preferring nor ignoring the calendar).
4. **Rest Day Policy Between Runs (Configurable):**
   - Default to requiring at least 1 rest day between runs (`allow_consecutive_run_days: false` in `athlete.json`, matching current athlete preference of not running two days in a row), while keeping it configurable in `athlete.json` for future flexibility.

---

## 5. Remaining Open Question for Next Session

### 5.1 [OPEN - QUESTION] Role of `docs/UNIFIED_COACH_SPEC.md` vs. 4-Stage `run_reviewer.py` + `ai_coach.py`
- **Context:**
  - Currently, [`src/analysis/run_reviewer.py`](file:///home/berni/laufen/src/analysis/run_reviewer.py) (`schema_version: 2`, per [`docs/plans/4_stage_run_review_and_adaptive_feedback.md`](file:///home/berni/laufen/docs/plans/4_stage_run_review_and_adaptive_feedback.md)) deterministically generates the 4-stage per-run evaluation (`stage1_physiology`, `stage2_compliance`, `stage3_optimizations`, `stage4_next_run`), while [`src/analysis/ai_coach.py`](file:///home/berni/laufen/src/analysis/ai_coach.py) uses the LLM (`agy`) to generate the macro coaching letter (`ai_coach_commentary.md`), summary (`ai_coach_summary.json`), and cycle updates (`coach_history.json`).
  - Meanwhile, [`docs/UNIFIED_COACH_SPEC.md`](file:///home/berni/laufen/docs/UNIFIED_COACH_SPEC.md) describes an older/alternative `schema_version: "1.1"` where a single LLM call writes both `run_reviews.json` and `macro_coach`.
  - Because `run-ai-coach.sh` checks `find_unreviewed_runs()` in `run_reviews.json` (which `./run review` already populates), `run-ai-coach.sh` currently skips `ai_coach.py` in CI (Bug 1.4).
- **Decision Needed in Next Session:**
  - Confirm whether to keep the **4-stage per-run review (`run_reviewer.py`)** + **LLM Macro/Cycle Coach (`ai_coach.py`)** split (fixing `run-ai-coach.sh` to check `coach_history.json` for new runs since the last cycle evaluation instead of `run_reviews.json`), or whether the LLM should also overwrite/augment per-run reviews in `run_reviews.json` per `UNIFIED_COACH_SPEC.md`.
