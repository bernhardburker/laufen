# Laufen System & Domain Specification (Authoritative Ground Truth)

> [!IMPORTANT]
> **Purpose for Agentic Development:**
> This document is the authoritative **Single Source of Truth (Ground Truth)** for domain rules, coaching philosophy, operational lifecycles, data contracts, and target architecture (SOLL vs. IST).
> When a user makes a request or proposes a change, agents MUST compare the request against this specification:
> - If a request aligns with these invariants, execute it cleanly.
> - If a request contradicts a core invariant or physiological guardrail (e.g., prescribing target paces for a Zone 2 HR run, or hardcoding one athlete's personal preferences into global code), **explicitly highlight the conflict** and ask whether to override or adapt the invariant.

---

## 1. Core System Invariants

1. **Zero External Runtime Dependencies**:
   - All production modules in [`src/`](../src/) and [`agent/`](../agent/) use **exclusively the Python 3 standard library** (`urllib.request`, `json`, `datetime`, `pathlib`, `re`, `csv`, `typing`).
   - External packages (`pytest`, `pytest-bdd`) are restricted strictly to `.venv` for offline/staging test execution.
2. **Deterministic Telemetry First, Cognitive Synthesis Second**:
   - Python deterministically computes all physiological metrics (7-zone HR distribution, Aerobic Efficiency Factor, Banister CTL/ATL/TSB, interval splits, and readiness counts).
   - The LLM ([`src/llm/client.py`](../src/llm/client.py)) interprets pre-computed facts and phrases human coaching guidance; it never calculates raw telemetry or invents numeric thresholds.
3. **Multi-Tenant & Configuration Isolation via [`src/athletes.py`](../src/athletes.py)**:
   - [`athletes/registry.json`](../athletes/registry.json) maps each athlete slug (`berni`, `berni-test`, etc.) to an isolated directory `athletes/<slug>/` and an unguessable dashboard output path `dist/<dashboard_slug>/index.html`.
   - **No Hardcoded Athlete Preferences in Code**: Heart rate zones, weekly run frequency, consecutive-day recovery rules, and weekly periodization slots are **athlete-specific configurations** stored in `athletes/<slug>/athlete.json`. Engine code must never hardcode one athlete's personal HR thresholds or weekday preferences for other tenants.
   - Live API E2E tests (`./run test:e2e`) and synthetic data seeding (`./run seed`) are **strictly restricted** to staging tenant `berni-test` (`i771119`). Production tenants are protected by hard safety checks in [`src/integrations/seed_athlete.py`](../src/integrations/seed_athlete.py).
4. **Intervals.icu as Calendar Single Source of Truth**:
   - Planned workouts (`category: "WORKOUT"`) and coach rest-day markers (`category: "NOTE"`, `"🛋️ Ruhetag (Coach)"`) are persisted directly to the Intervals.icu calendar via [`src/planner/schedule_workout.py`](../src/planner/schedule_workout.py).
   - Intervals.icu automatically syncs scheduled workouts for the upcoming 7 days to Garmin Connect and GPS watches.
5. **Strict BDD Test Driver Pattern & Relative Documentation Paths**:
   - Every code modification requires automated Gherkin BDD scenarios (`tests/*.feature`) bound to thin step definitions and encapsulated Test Drivers (`tests/test_*.py`, [`tests/drivers.py`](../tests/drivers.py)) per [`tests/README.md`](../tests/README.md).
   - All documentation links in the repository must use **relative paths** (never machine-specific `file://` or `/home/...` paths).

---

## 2. Coaching DNA & Physiological Rules

### 2.1 Strict Modality Separation: Heart Rate vs. Pace

Garmin watches display either a **Heart Rate Zone Gauge** OR a **Pace Speedometer Gauge** per workout step—never both simultaneously. Mixing HR and Pace targets creates physiological conflicts:

| Session Archetype | Primary Control Modality | Rule & Coaching Rationale |
| :--- | :--- | :--- |
| **Easy Run, Recovery Run, Long Run, Warmup, Cooldown** | **Heart Rate Only** (`Z1 HR`, `Z2 HR`, `Z1-Z2 HR`) | **Never prescribe, estimate, or suggest a target pace** (e.g., never write *"ca. 7:00 /km"*). Pace in Zone 2 is purely an outcome of daily readiness, heat, elevation, and cardiac drift. Prescribing pace causes overpacing into Zone 3. |
| **Short Intervals & Speed Reps (< 2 min)** | **Pace Corridor Only** (`05:10-05:25/km Pace`) | **Never use Heart Rate targets for short reps.** Due to physiological **cardiac lag** (~30–60s), heart rate cannot respond quickly enough during 30s–90s intervals. |
| **Threshold / Sustained Tempo ($\ge 3\text{–}4\text{ min}$)** | **Either** `Z4 HR` **or** Pace Corridor | Choose one modality per workout (`Z4 HR` for sub-threshold/threshold effort on undulating terrain; Pace corridor for flat track/road threshold reps). Never combine both in the same step. |

### 2.2 Run-Walk Biomechanics & Elevation Handling
- **No "Slow-Jogging Shuffling"**: When heart rate drifts toward the athlete's configured upper Zone 2 ceiling (`heart_rate.zones.z2_aerobic.max` in `athlete.json`) on inclines or late in a run, the athlete should **switch proactively to brisk walking (`Gehpause`)** until HR drops by ~8–10 bpm, rather than shuffling at an unnatural, biomechanically stressful micro-stride.
- **Cadence Economy**: Target natural running cadence around `158–165 spm` during running phases instead of artificially throttling stride frequency.

### 2.3 Intensity Distribution (De-Dogmatized 80/20 Macro Compass)
- **80/20 Intensity Distribution (`z1_z2_pct`)**:
  - For low-to-moderate volume runners (e.g., 2–3 sessions/week), 80/20 is a **rolling multi-week / monthly macro compass**, not a rigid single-run or single-week pass/fail filter.
  - A 2-run week consisting of 1 Interval session + 1 Easy Run naturally yields ~65–70% Z1/Z2 time due to cardiac lag and afterburn in recovery jogs—this is physiologically normal as long as dedicated base runs stay strictly in Z1/Z2.

### 2.4 Athlete-Specific Periodization & Slot Configuration (`athlete.json`)
Weekly training structure is **not hardcoded globally**; every athlete defines their own preferences, physiological thresholds, and weekly slot model in `athletes/<slug>/athlete.json`:
- **Frequency & Recovery Preferences**:
  - `target_runs_per_week`: Target number of weekly sessions (e.g., `2`, `3`, `4`, or `5`).
  - `allow_consecutive_run_days`: Boolean controlling whether back-to-back running days are permitted (`false` enforces at least 1 rest day between runs; `true` allows consecutive running days for higher-frequency athletes).
  - `training_mode`: Athlete-chosen periodization model (e.g., `flexi_hybrid`, `pyramidal_hybrid`, `polarized`).
- **Configurable `weekly_slots`**:
  - Each athlete profile defines an array of `weekly_slots` tailored to their personal schedule and goals.
  - Each slot specifies:
    - `slot`: Numeric slot identifier (`1..N`)
    - `name` & `description`: Human-readable label and coaching intent
    - `role`: Functional role in the training week (e.g., `quality`, `flexible`, `base_anchor`)
    - `allowed_days`: List of weekdays (`monday`..`sunday`) on which this slot applies
    - `allowed_types` & `preferred_type`: Permitted workout archetypes from `workouts/templates/`
- **Heart Rate Thresholds (`heart_rate`)**:
  - `max_hr`, `resting_hr`, `lthr`, and `zones` (`z1_recovery`, `z2_aerobic`, `z3_tempo`, `z4_subthreshold` / `z4_threshold`, `z5_vo2max`) are synced per athlete via `./run sync-profile --athlete <slug>`.

### 2.5 Deterministic Physiological Formulas
- **7-Zone Heart Rate Mapping** ([`src/analysis/trends.py`](../src/analysis/trends.py), [`src/analysis/run_reviewer.py`](../src/analysis/run_reviewer.py)):
  - Maps `icu_hr_zone_times` (`Z1` Recovery, `Z2` Aerobic, `Z3` Tempo, `Z4` Sub-Threshold/Threshold, `Z5` VO2max, `Z6` Anaerobic, `Z7` Neuromuscular).
  - $\text{Z1/Z2 \%} = \frac{t_{Z1} + t_{Z2}}{\sum_{i=1}^{7} t_{Zi}} \times 100$.
- **Aerobic Efficiency Factor (AEF)**:
  $$\text{AEF} = \frac{\text{Speed (m/min)}}{\text{Average Heart Rate (bpm)}}$$
  Tracked across all runs and specifically filtered for pure Z2 base runs (`z1_z2_pct >= 80%`) to measure aerobic progression independent of pace.
- **Banister Impulse-Response Form** ([`src/analysis/forecast.py`](../src/analysis/forecast.py)):
  - Chronic Training Load ($\text{CTL}$, 42-day exponentially weighted moving average of `icu_training_load`).
  - Acute Training Load ($\text{ATL}$, 7-day EWMA).
  - Training Stress Balance ($\text{TSB} = \text{CTL} - \text{ATL}$).
- **Riegel Race Prediction** ([`src/analysis/forecast.py`](../src/analysis/forecast.py)):
  $$T_2 = T_1 \cdot \left(\frac{D_2}{D_1}\right)^k$$
  where the fatigue exponent $k$ is dynamically adjusted based on the athlete's weekly aerobic volume and long-run endurance base.

---

## 3. Data Contracts & Multi-Tenant Storage Layout

All inter-module JSON schemas and runtime validators are defined in **[`src/core/contracts.py`](../src/core/contracts.py)**.

### 3.1 Per-Athlete File Map (`athletes/<slug>/`)

| File | Producer(s) | Consumer(s) | Contract in `src/core/contracts.py` |
| :--- | :--- | :--- | :--- |
| `athlete.json` | [`sync_athlete.py`](../src/integrations/sync_athlete.py), [`onboard_athlete.py`](../src/integrations/onboard_athlete.py) | All planner, analysis & dashboard modules | [`AthleteProfileContract`](../src/core/contracts.py) (`validate_athlete_profile`) |
| `intervals_activities.json` | [`download_intervals.py`](../src/integrations/download_intervals.py), [`manage_note.py`](../src/integrations/manage_note.py) | `trends.py`, `run_reviewer.py`, `extract_markers.py`, `generate_dashboard.py` | Raw Intervals.icu Activity list |
| `intervals_wellness.json` | [`download_wellness.py`](../src/integrations/download_wellness.py) | `extract_markers.py`, `telegram_bot.py`, `generate_dashboard.py` | Raw Intervals.icu Wellness list |
| `calendar_events.json` | [`download_events.py`](../src/integrations/download_events.py), [`schedule_workout.py`](../src/planner/schedule_workout.py) | `daily_recommendation.py`, `telegram_bot.py`, `generate_dashboard.py` | Intervals.icu Event list |
| `run_reviews.json` | [`run_reviewer.py`](../src/analysis/run_reviewer.py) (`schema_version: 2`) | `ai_coach.py`, `telegram_bot.py`, `generate_dashboard.py` | [`CachedRunReviewRecordContract`](../src/core/contracts.py) / [`RunReviewEvaluationV2Contract`](../src/core/contracts.py) (`validate_run_review_evaluation`) |
| `coach_history.json` | [`coach_history.py`](../src/analysis/coach_history.py), [`ai_coach.py`](../src/analysis/ai_coach.py) | `run_reviewer.py`, `daily_recommendation.py`, `generate_dashboard.py` | [`CoachCycleEntryContract`](../src/core/contracts.py) (`validate_coach_history`) |
| `ai_coach_summary.json` & `ai_coach_commentary.md` | [`ai_coach.py`](../src/analysis/ai_coach.py) | `generate_dashboard.py` | Structured LLM macro summary & Markdown letter |
| `telegram_state.json` | [`telegram_bot.py`](../src/integrations/telegram_bot.py) | `telegram_bot.py` | Tracks `last_briefing_date`, `last_evening_date`, `notified_activity_ids`, and pending note state |

> [!WARNING]
> **Legacy Path Precaution (`data/` and `config/athlete.json`):**
> Root `data/` and `config/athlete.json` exist strictly as a legacy single-tenant fallback when `athletes/registry.json` is absent (e.g., in isolated unit test `tmp_path` fixtures). In the repository workspace, **always** pass `--athlete <slug>` (or invoke via `./run <cmd>`) so paths resolve to `athletes/<slug>/`.

---

## 4. 24-Hour Operational Lifecycle & State Machine

```mermaid
sequenceDiagram
    participant Cron as CI / Crontab (06:45)
    participant ICU as Intervals.icu API
    participant Repo as Athletes State (athletes/<slug>/)
    participant Daemon as Telegram Bot Daemon (--scheduler)
    participant User as Athlete (Telegram & Garmin Watch)

    Cron->>ICU: 06:45 Sync profile, wellness, activities, calendar events
    Cron->>Repo: Run 4-stage review (./run review) & Macro AI Coach (./run coach)
    Cron->>Repo: Build & deploy dashboards (GitHub Pages + Burkerserver)
    Daemon->>ICU: Morning Briefing (sync latest sleep/HRV & adaptive plan)
    Daemon->>User: Send Morning Briefing + Inline Action Keyboard
    User-->>Daemon: Optional callback ("📉 Lockerer", "⏱️ 35 Min", "🛋️ Ruhetag") or free-text
    Daemon->>ICU: Update/override today's calendar workout -> pushes to Garmin Watch
    User->>ICU: Completes Run on Garmin Watch -> auto-uploads to Intervals.icu
    Daemon->>ICU: Every 10m poll activities (check_and_send_due_post_run_reviews)
    Daemon->>User: Send 4-Stage Post-Run Review (for runs completed today / <= 6h old)
    User-->>Daemon: Optional subjective feedback (e.g., "Wadenkrämpfe ab km 4")
    Daemon->>ICU: Attach note via manage_note.py & re-evaluate 4-stage review
    Daemon->>User: Evening Preview for tomorrow's planned session
```

### Key Lifecycle Invariants
1. **Morning Briefing (`briefing_time`)**:
   - Sent by the Telegram bot daemon (`--scheduler`) once per day with idempotency protection (`state["last_briefing_date"] == today_iso`).
2. **Immediate Post-Run Reviews**:
   - Polled every 10 minutes for runs completed today ($\le 6\text{ hours}$ old) with cold-start deduplication.
3. **Evening Preview (`evening_briefing_time`)**:
   - Previews tomorrow: rest day if the athlete ran today, otherwise training day.
   - Workouts for today are never deleted in the evening.

---

## 5. Workout DSL & Templates Reference

Structured workout templates live in [`workouts/templates/`](../workouts/templates/) and are rendered by [`src/planner/schedule_workout.py`](../src/planner/schedule_workout.py):

| Template Name (`--type`) | Template File | Target Modality | Primary Use Case |
| :--- | :--- | :--- | :--- |
| `easy_run` | [`easy_run.txt`](../workouts/templates/easy_run.txt) | `Z2 HR` (Warmup `Z1-Z2 HR`, Cooldown `Z1 HR`) | Standard aerobic base session |
| `recovery_run` | [`recovery_run.txt`](../workouts/templates/recovery_run.txt) | `Z1 HR` | Active regeneration after hard load or mild fatigue |
| `long_run` | [`long_run.txt`](../workouts/templates/long_run.txt) | `Z2 HR` | Extended endurance base anchor ($\ge 60\text{ min}$) |
| `intervals` | [`intervals.txt`](../workouts/templates/intervals.txt) | Pace Corridor (`05:10-05:25/km Pace`) or `Z4 HR` | Short speed / VO2max repetitions (`10x 1m`) |
| `fartlek` | [`fartlek.txt`](../workouts/templates/fartlek.txt) | Pace Corridor / RPE surges | Playful speed-play surges with easy jogging recovery |
| `tempo_run` | [`tempo_run.txt`](../workouts/templates/tempo_run.txt) | `Z4 HR` | Continuous threshold block (e.g., `20m Z4 HR`) |
| `threshold_intervals` | [`threshold_intervals.txt`](../workouts/templates/threshold_intervals.txt) | `Z4 HR` | Cruise intervals at sub-threshold/threshold (`3x 8m Z4 HR`) |
| `micro_threshold` | [`micro_threshold.txt`](../workouts/templates/micro_threshold.txt) | `Z4 HR` | Short threshold blocks (`5x 4m Z4 HR` with `90s` recovery) to limit lactate accumulation |
| `progressive_threshold` | [`progressive_threshold.txt`](../workouts/templates/progressive_threshold.txt) | `Z2 HR` $\rightarrow$ `Z3 HR` $\rightarrow$ `Z4 HR` | Progressive aerobic-to-threshold transition run |

---

## 6. Target Architecture vs. Current Implementation (SOLL vs. IST)

To prevent agents from confusing legacy implementation details with agreed target design, note the following active architectural transitions tracked in [`docs/ARCHITECTURE_REVIEW_AND_FINDINGS.md`](ARCHITECTURE_REVIEW_AND_FINDINGS.md):

| Component | Current Implementation (IST) | Agreed Target Architecture (SOLL) |
| :--- | :--- | :--- |
| **Planning & Daily Recommendation Engines** | Split across [`schedule_workout.py`](../src/planner/schedule_workout.py) (`choose_adaptive_workout`) and [`daily_recommendation.py`](../src/analysis/daily_recommendation.py) (`generate_daily_recommendation`) with procedural `if/else` trees (hardcoded 3:1 deload week, weekend catch-up branches). | **Single Unified Planning Pipeline:** [`extract_markers.py`](../src/planner/extract_markers.py) computes objective telemetry counts (`consecutive_build_weeks`, `days_since_last_base_anchor`, `days_since_last_run`, `days_since_last_quality`, `readiness`), and the Coach evaluates them holistically without rigid `if/else` trees. |
| **Athlete Slot & Zone Resolution** | Some helpers in [`run_reviewer.py`](../src/analysis/run_reviewer.py) (`resolve_next_slot`) still contain fallback weekday assumptions (`monday/tuesday -> Slot 1`) or fallback HR numbers (`158` / `178`). | **100% Profile-Driven Resolution:** All slot roles, allowed weekdays, run frequency, and HR thresholds must be resolved dynamically from `athletes/<slug>/athlete.json`. |
| **Calendar Event Handling in Daily Recommendation** | `daily_recommendation.py` short-circuits when `calendar_events.json` is present, immediately returning `"Ruhetag (Laut Kalender)"` if today has no scheduled event. | **Calendar as Context:** Existing calendar entries (planned workouts, rest notes, races) are passed as context alongside readiness and training history—neither blindly short-circuiting nor ignored. |
| **Per-Run Review vs. Macro AI Coach** | Two-stage split: deterministic 4-stage review in [`run_reviewer.py`](../src/analysis/run_reviewer.py) (`schema_version: 2`) + LLM macro/cycle evaluation in [`ai_coach.py`](../src/analysis/ai_coach.py). | Keep this clean separation, and fix [`.github/scripts/run-ai-coach.sh`](../.github/scripts/run-ai-coach.sh) (Bug 1.4) to check `coach_history.json` (`evaluated_run_ids`) rather than `run_reviews.json`. |
| **Open Code Defects (Bugs 1.2–1.7)** | Documented in [`docs/ARCHITECTURE_REVIEW_AND_FINDINGS.md` §1](ARCHITECTURE_REVIEW_AND_FINDINGS.md). | Scheduled for resolution with accompanying BDD tests. |

---

## 7. Documentation Drift Prevention (Automated Verification)

Documentation in this repository is protected against drift by automated BDD tests in [`tests/documentation_integrity.feature`](../tests/documentation_integrity.feature) and [`tests/test_documentation_integrity.py`](../tests/test_documentation_integrity.py), executed on every `./run test`:

1. **Relative Link & Path Integrity**: Every relative file link across [`GEMINI.md`](../GEMINI.md), [`README.md`](../README.md), [`docs/SYSTEM_SPEC.md`](SYSTEM_SPEC.md), [`docs/ARCHITECTURE_REVIEW_AND_FINDINGS.md`](ARCHITECTURE_REVIEW_AND_FINDINGS.md), [`tests/README.md`](../tests/README.md), and [`.agents/skills/workout-scheduler/SKILL.md`](../.agents/skills/workout-scheduler/SKILL.md) is verified to exist on disk, and **absolute filesystem paths (`file://`, `/home/...`, `/mnt/...`) are rejected**.
2. **CLI Runner Subcommand Parity**: Every subcommand implemented in [`./run`](../run) is verified to appear in `./run help` (`show_help()`) and [`README.md`](../README.md).
3. **Workout Template Parity**: Every workout DSL template in [`workouts/templates/*.txt`](../workouts/templates/) is verified to be documented in [`docs/SYSTEM_SPEC.md`](SYSTEM_SPEC.md) and [`.agents/skills/workout-scheduler/SKILL.md`](../.agents/skills/workout-scheduler/SKILL.md).
4. **Executable Schema Contract Parity**: Outputs of `evaluate_run_deep()`, `extract_planning_markers()`, `load_coach_history()`, and `athletes/berni/athlete.json` are validated against [`src/core/contracts.py`](../src/core/contracts.py).
