# Architecture & Implementation Plan: 4-Stage Run Review & Adaptive Feedback

**Document Version:** 1.1.0  
**Target Architecture:** Laufen Management Hub (CLI Runner, Intervals.icu Sync, Dashboard, AI Coach, Workout Planner)  
**Status:** Approved for Implementation  
**Language:** English (per project guidelines)

---

## 1. Context & Motivation

### The Challenge
Previously, single-run evaluations (`src/analysis/run_reviewer.py`) operated strictly in isolation using rigid zone percentage rules (e.g., classifying any run with $< 60\%$ Zone 1/2 and $< 30\%$ Zone 4 as a "zügiger Dauerlauf / Zone 3 run"). This produced three major issues:
1. **Context Blindness & Warmup/Cooldown Dilution:** When the athlete executed an intended quality workout (such as a 50-minute session with `10m warmup` + `3x 8m threshold` + `6m recovery` + `10m cooldown`), physiological cardiac lag (~2–2.5 minutes per interval to cross into Zone 4) plus warmup/cooldown time naturally capped Zone 4 duration at ~13–16 minutes ($\approx 26\text{–}32\%$ of total moving time). Simultaneously, post-effort cardiovascular drift and sympathetic nervous system linger kept heart rate in Zone 3 during recovery phases. For example, run `i200612231` (*"Wien - Schwellenlauf (7 km)"*, `13m 47s` / `27.6%` in Z4, `11m 22s` in Z3) fell just below the rigid `30%` Z4 cutoff and was misclassified as an unspecific Zone 3 run.
2. **Disconnected Evaluation Layers:** The Macro Coach (`src/analysis/ai_coach.py` / `data/coach_history.json`) and Intervals.icu (`compliance: 100.0`) tracked weekly slots and rated such sessions as **compliant (`erfüllt`)**, whereas the per-run reviewer displayed critical Zone 3 drift feedback.
3. **Flexible Slot Ambiguity:** In `config/athlete.json`, mid-week Slot 2 (`role: "flexible"`) permits both an Easy Base Run ($< 158\text{ bpm}$) and a Controlled Threshold Run ($168\text{–}174\text{ bpm}$). Evaluating a run without resolving which option the athlete intended leads to false compliance ratings.

### The Solution: 4-Stage Evaluation Model
A unified, backward-compatible 4-part feedback framework separating pure physiological metrics from multi-signal training compliance, followed by data-triggered coaching optimizations and point-in-time post-run recovery guidance.

---

## 2. The 4-Stage Review Framework

```
                  Activity Data (Intervals.icu) + Athlete Config + Coach History
                                               │
                                               ▼
┌──────────────────────────────────────────────────────────────────────────────────────┐
│                            4-STAGE RUN REVIEW PIPELINE                               │
│                                                                                      │
│  Stage 1: Physiological Run Review (Lauf-Bewertung)                                  │
│  - Objective data: distance, time, pace, GAP, avg/max HR, Z1–Z7 breakdown            │
│  - Point-in-time load & fatigue: training_load, icu_ctl, icu_atl, tsb_at_run         │
│  - Parsed interval_summary (work vs. recovery blocks), cadence, efficiency (EF)      │
│                                                                                      │
│  Stage 2: Target vs. Actual Compliance (Plan-Erfüllung)                              │
│  - Multi-signal intent resolution: Activity name/pairing -> Coach History ->         │
│    Weekly Slot lookup (with flexible-slot physiological disambiguation)              │
│  - Absolute & relative compliance rules dynamically loaded from config/athlete.json  │
│  - Status: Erfüllt (Compliant), Teilweise (Partial), Abweichend (Divergent)          │
│                                                                                      │
│  Stage 3: Comparison & Actionable Optimizations (Vergleich & Optimierung)            │
│  - Data-triggered pacing rules (initial 2m by target pace to bridge cardiac lag)     │
│  - Recovery protocols (60–90s brisk walking when pause HR stays trapped near Z2_max) │
│  - Modular workout discovery (e.g. 5x 4m micro-threshold via ./run plan CLI)         │
│                                                                                      │
│  Stage 4: Post-Run Recovery & Next Slot Guidance (Nächster Lauf)                     │
│  - Point-in-time recovery window (24–48h) based on load, intensity & tsb_at_run      │
│  - Concrete directive for the next chronological weekly slot (DRY with daily engine) │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 3. Detailed Stage Specifications

### Stage 1: Physiological Run Review (`Lauf-Bewertung`)
* **Focus:** Pure, objective physiology—independent of whether the run matched a scheduled workout.
* **Core Metrics (extracted in `extract_deep_run_metrics()`):**
  * Distance, moving duration, average pace, and Grade Adjusted Pace (`gap`).
  * Heart rate: Average HR, Max HR, and full 7-zone distribution ($Z1$ through $Z7$ in seconds and percentages).
  * Point-in-time load & form from activity record: `training_load` (`icu_training_load`), `icu_ctl`, `icu_atl`, and `tsb_at_run = round(icu_ctl - icu_atl, 1)`, plus subjective `icu_rpe` and `feel` when present.
  * Running dynamics: Average cadence (spm), elevation gain/loss, and aerobic efficiency factor ($EF = \text{speed (m/min)} / \text{avg HR}$).
  * **Structured Interval Parsing (`parsed_intervals`):** Parse `interval_summary` strings (e.g., `"3x 5m59s 168bpm"`, `"2x 8m13s 157bpm"`) via regex into structured blocks (`reps`, `duration_s`, `avg_hr`, `role: "work" | "recovery" | "warmup_cooldown"`).
* **Updated Intensity Classification (`classify_run_intensity()`):**
  * Replace rigid percentage-only threshold cutoffs with combined absolute/relative thresholds to account for warmup, cooldown, and cardiac lag:
    * `schwellen_intervall`: `z5_pct >= 10.0` or (`z4_seconds + z5_seconds >= 900` and `avg_hr >= lthr - 5`).
    * `schwellenlauf`: `z4_seconds >= 720` ($\ge 12\text{ min}$ in Zone 4) OR (`z4_pct >= 25.0` and `z3_seconds + z4_seconds >= 1200`).
    * `aerobe_basis`: `z1_z2_pct >= 80.0` and `avg_hr <= z2_max`.
    * `ueberwiegend_aerob`: `z1_z2_pct >= 60.0`.
    * `tempo_z3`: `z3_pct >= 35.0` and `z4_seconds < 720`.

### Stage 2: Planned vs. Actual Compliance (`Plan-Erfüllung`)
* **Dynamic Threshold Loading:** All HR and pace boundaries are read dynamically from `config/athlete.json` (`z1_recovery.max`, `z2_aerobic.max`, `z4_subthreshold.min/max`, `lthr`, `weekly_slots`, `pace_targets_min_per_km`) rather than hardcoded constants.
* **Multi-Signal Intent Identification (`resolve_run_intent()`):**
  Because `download_intervals.py` fetches `/activities` (which includes `paired_event_id` and `compliance`, but not the full `/events` calendar payload), intent is resolved deterministically across four signals:
  1. **Explicit Activity Semantics & Pairing:**
     * Keyword detection on `run["name"]`:
       * Quality/Threshold keywords: `schwelle`, `threshold`, `tempo`, `intervall`, `interval`, `speed`, `fartlek`, `fahrtspiel`.
       * Base/Recovery keywords: `easy`, `grundlage`, `base`, `long run`, `anker`, `rekom`, `recovery`.
     * Presence of `paired_event_id` and Intervals.icu `compliance` score.
  2. **Coach History Context (`data/coach_history.json`):**
     * Lookup whether the run ID was evaluated in a `coach_history.json` cycle or matches the active open recommendation (`target_type`, `target_hr_max`).
  3. **Weekly Slot Lookup (`config/athlete.json` `weekly_slots`):**
     * Match activity weekday against `allowed_days` (Slot 1 Quality, Slot 2 Flexible, Slot 3 Base Anchor).
  4. **Flexible Slot Disambiguation (`role == "flexible"`):**
     * When a run falls on a flexible slot (e.g., Slot 2 Wed–Fri) without explicit name keywords, inspect physiological execution: if `z4_seconds >= 600` (10+ min in Z4) or `parsed_intervals` contains work blocks at $\ge Z4_{\min} - 2\text{ bpm}$, resolve intent to **Slot 2A: Threshold/Quality**. Otherwise, resolve intent to **Slot 2B: Aerobic Base**.
* **Compliance Matrix:**
  * **Intended Threshold / Quality Session:**
    * **Target:** $\ge 12\text{ min}$ (`720s`) in Zone 4+ ($Z4_{\min}\text{–}Z4_{\max}$, e.g. $169\text{–}177\text{ bpm}$ or slot `target_hr_range` $168\text{–}174\text{ bpm}$) OR structured work intervals totaling $\ge 15\text{ min}$ at $\ge 168\text{ bpm}$.
    * **Status:**
      * `erfüllt` (Compliant): Target Zone 4 duration/work blocks achieved. Time spent in Zone 3 (`z3_seconds`) is explicitly labeled as **warmup transit & post-interval sympathetic drift**, not a pacing error.
      * `teilweise` (Partial): $6\text{–}11\text{ min}$ (`360–719s`) in Zone 4+ or premature fade into Zone 3.
      * `abweichend` (Divergent): $< 6\text{ min}$ in Zone 4+ (e.g., athlete spontaneously ran an easy Zone 2 base session instead; rated neutrally per coaching guidelines: *"Temporeiz entfiel, Beine blieben frisch"*).
  * **Intended Aerobic Base / Long Run:**
    * **Target:** $\ge 80\%$ in Zone 1/2 and `avg_hr <= z2_max` ($158\text{ bpm}$).
    * **Status:**
      * `erfüllt` (Compliant): `z1_z2_pct >= 80.0` and `avg_hr <= z2_max`.
      * `teilweise` (Partial): `z1_z2_pct` between $60.0\%$ and $79.9\%$, or `avg_hr <= z2_max + 3` with moderate Z3 drift on hills/late run.
      * `abweichend` (Divergent): Unplanned high-intensity excursion (`z1_z2_pct < 60.0%` or `avg_hr > z2_max + 3`); rated neutrally with adaptive recovery consequence.

### Stage 3: Comparison & Actionable Optimizations (`Vergleich & Optimierung`)
Generated via deterministic triggers from `metrics` and `parsed_intervals`:
* **Trigger 1 — Cardiac Lag & Initial Interval Pacing:**
  * *Condition:* Quality/Threshold run where work intervals average $165\text{–}168\text{ bpm}$ (just below $Z4_{\min}$) while `max_hr >= 173` and `z3_seconds >= 480`.
  * *Optimization:* Guide the runner to start the first 2 minutes of each interval strictly by target pace (`pace_targets_min_per_km.tempo_run`, e.g., `05:45–06:00/km`) rather than accelerating to force a lagging heart rate up.
* **Trigger 2 — Incomplete Pause Recovery (Sympathetic Linger):**
  * *Condition:* Quality/Threshold run where non-work / recovery intervals in `parsed_intervals` remain at $\ge z2_{\max} - 3\text{ bpm}$ (e.g., $155\text{–}160\text{ bpm}$) or `z3_seconds >= 600`.
  * *Optimization:* Prescribe $60\text{–}90\text{s}$ of brisk power walking at the start of each recovery pause until HR drops below $Z1_{\max}$ ($150\text{ bpm}$) before resuming a light jog.
* **Trigger 3 — Modular Alternative Workout Discovery:**
  * *Condition:* Sustained threshold blocks ($3 \times 8\text{ min}$ or $20\text{ min}$ continuous) exhibit high Z3 drift (`z3_seconds >= 0.75 * z4_seconds`).
  * *Optimization:* Suggest alternative threshold formats that keep lactate steady while breaking sympathetic drift, including direct CLI commands:
    * **Norwegian Micro-Threshold:** $5 \times 4\text{ min}$ @ Z4 with $90\text{s}$ brisk walk/jog recovery (`./run plan --type micro_threshold --date <next_quality_date>`).
    * **Progressive Threshold:** $15\text{ min}$ Z2 $\rightarrow$ $10\text{ min}$ Z3 $\rightarrow$ $10\text{ min}$ Z4 (`./run plan --type progressive_threshold --date <next_quality_date>`).
* **Trigger 4 — Base Run Drift / Cadence Economy:**
  * *Condition:* Base run with `z3_seconds > 300` or `cadence_spm < 155`.
  * *Optimization:* Early walking breaks on inclines before HR crosses $z2_{\max} - 3\text{ bpm}$ ($155\text{ bpm}$) and shortening stride length toward $158\text{–}165\text{ spm}$.

### Stage 4: Post-Run Recovery & Next Slot Guidance (`Nächster Lauf`)
* **Architectural Scope (Cached Post-Run Directive vs. Live Daily Recommendation):**
  * Because `data/run_reviews.json` caches each run's review upon completion, Stage 4 is framed as **Immediate Post-Run Guidance** (computed relative to the run's completion timestamp and `tsb_at_run`), while `src/analysis/daily_recommendation.py` (`./run today`) continues to compute live day-of-execution status.
* **Recovery Window Calculation:**
  * Quality / Threshold run (`intensity_class` in `schwellenlauf`, `schwellen_intervall`, `tempo_z3` or `training_load >= 55`): `recovery_hours = 48` (1 full rest day + next run strictly aerobic Z1/Z2).
  * Aerobic Base run (`aerobe_basis`, `training_load < 55`): `recovery_hours = 24`.
* **Next Slot Resolution:**
  * Identify the next chronological slot in `config/athlete.json` (`weekly_slots`) after `run_date`.
  * Apply stimulus guardrails consistent with `daily_recommendation.py`: after a quality stimulus (e.g., Slot 2A Threshold on Friday), the upcoming Slot 3 (Weekend Base Anchor) is locked strictly to Easy Base ($50\text{ min}$ $< 158\text{ bpm}$, pace ignored).

---

## 4. Technical Implementation Steps

### Phase 1: Modular Workout Library & Planner Integration (`workouts/` & `src/planner/schedule_workout.py`)
1. **Static Reference Workouts (`workouts/intervals/`):**
   * `workouts/intervals/04_threshold_intervals_3x8m.txt` (Implemented)
   * `workouts/intervals/05_tempo_run_20m.txt`
   * `workouts/intervals/06_micro_threshold_5x4m.txt`
   * `workouts/intervals/07_progressive_threshold_35m.txt`
2. **Parameterized Planner Templates (`workouts/templates/` & `src/planner/schedule_workout.py`):**
   * Add `workouts/templates/micro_threshold.txt` ($5 \times 4\text{ min}$ Z4 / $90\text{s}$ Z1 recovery) and `workouts/templates/progressive_threshold.txt`.
   * Register `micro_threshold` and `progressive_threshold` in `schedule_workout.py` (`--type` choices and `build_workout_payload()`) so Stage 3 suggestions are directly executable via `./run plan`.

### Phase 2: Engine & Cache Schema Upgrade (`src/analysis/run_reviewer.py`)
1. **Backward-Compatible Schema (`schema_version: 2`):**
   * Keep all existing top-level keys inside `reviews[run_id]["review"]` (`rating`, `rating_label`, `summary`, `coach_tip`, `cadence_feedback`, `zone_distribution_text`) so `src/analysis/ai_coach.py` and legacy consumers continue to work seamlessly.
   * Populate those top-level keys from the new 4-stage evaluation (e.g., a compliant threshold run receives `rating: "intensiv"`, `rating_label: "Gezielter Schwellenlauf (Plan erfüllt)"` instead of `"Zügiger Dauerlauf (Zone 3)"`).
   * Add `"schema_version": 2` and `"stages"` inside `review`:
     ```json
     {
       "schema_version": 2,
       "rating": "intensiv",
       "rating_label": "Gezielter Schwellenlauf (Plan erfüllt)",
       "summary": "...",
       "coach_tip": "...",
       "cadence_feedback": "...",
       "zone_distribution_text": "Z1: 6m 18s | Z2: 18m 32s | Z3: 11m 22s | Z4: 13m 47s",
       "stages": {
         "stage1_physiology": {
           "headline": "Intensiver Schwellenreiz mit 13m 47s in Zone 4",
           "summary": "...",
           "ef": 1.498,
           "tsb_at_run": -2.9,
           "parsed_intervals": [ ... ]
         },
         "stage2_compliance": {
           "planned_slot": "Slot 2 (Wochenmitte – Schwellen-Option)",
           "intent_source": "name+slot",
           "intended_type": "threshold",
           "status": "erfüllt",
           "status_label": "Erfüllt",
           "intervals_icu_compliance": 100.0,
           "target_comparison": "13m 47s in Zone 4 (Ziel: >= 12m @ 168–174 bpm). Die 11m 22s in Zone 3 resultieren aus dem Anlauf-Lag und dem Nachbrenneffekt in den Trabpausen."
         },
         "stage3_optimizations": [
           {
             "category": "pacing",
             "title": "Intervall-Einstieg über Ziel-Pace steuern",
             "text": "..."
           },
           {
             "category": "recovery",
             "title": "Gehpausen zur Pulsabsenkung nutzen",
             "text": "..."
           },
           {
             "category": "alternative_workout",
             "title": "Alternative: 5x 4m Micro-Threshold",
             "text": "...",
             "cli_command": "./run plan --type micro_threshold --date tomorrow"
           }
         ],
         "stage4_next_run": {
           "recovery_hours": 48,
           "tsb_context": "TSB -2.9 (CTL 12.7 / ATL 15.6)",
           "next_slot": "Slot 3 (Wochenende – Aerober Anker)",
           "next_workout_type": "easy_run",
           "target_hr_max": 158,
           "duration_min": 50,
           "directive": "Mindestens 24–48h keine intensiven Spitzen. Nächste Einheit: 50 Min lockerer Grundlagenlauf strikt < 158 bpm."
         }
       }
     }
     ```
2. **Automatic Cache Upgrade in `find_unreviewed_runs()`:**
   * Treat any cached review where `review.get("schema_version") != 2` as unreviewed so running `./run review` or `./run coach` automatically upgrades existing cached runs (including `i200612231`) to the 4-stage model.

### Phase 3: CLI Output, AI Coach Prompt & Dashboard Visualization
1. **CLI Runner (`./run review` / `src/analysis/run_reviewer.py`):**
   * Print a clear 4-stage terminal breakdown for newly reviewed (or `--recent N`) runs.
2. **Macro Coach Integration (`src/analysis/ai_coach.py`):**
   * Enrich `build_coach_prompt()` to include `stage2_compliance.status` and `planned_slot` from cached reviews so the LLM Coach and Per-Run Reviewer share identical compliance context.
3. **Dashboard (`templates/dashboard.html` & `src/visualization/generate_dashboard.py`):**
   * Upgrade the expandable `<details class="run-review-details">` in `build_table_rows()` to render a structured 4-card grid (`1. Lauf-Bewertung`, `2. Plan-Erfüllung` with status badge, `3. Vergleich & Optimierung` bullet list, `4. Nächster Lauf & Erholung`), while gracefully falling back to single-card rendering if a legacy review object lacks `stages`.

### Phase 4: Automated Testing & Verification (BDD)
* Add declarative Gherkin scenarios in `tests/run_reviewer.feature` and step definitions/driver methods in `tests/test_run_reviewer.py`:
  * Scenario 1: Planned threshold session with warmup/cooldown and post-interval Zone 3 drift (`13m 47s` Z4, `49.7%` Z1/Z2) is classified as a compliant threshold run (`status: "erfüllt"`, `rating: "intensiv"`).
  * Scenario 2: Flexible mid-week slot disambiguates between an intended Easy Base run with excessive Z3 drift (`status: "teilweise"`) vs. an intended Threshold session (`status: "erfüllt"`).
  * Scenario 3: Automatic migration of `schema_version < 2` cache entries into the 4-stage review structure without breaking legacy fields.
* Execute full test suite via `./run test`.
