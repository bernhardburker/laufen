# Architecture & Implementation Plan: Adaptive Workout Synthesis & Discovery

**Document Version:** 1.0.0  
**Target Architecture:** Laufen Management Hub (AI Coach, Intervals.icu DSL, Workout Planner, CLI Runner)  
**Status:** Approved for Implementation  
**Language:** English (per project guidelines)

---

## 1. Executive Summary & Vision

### The Challenge: Beyond Hardcoded Workouts
Traditional running apps and static scripts rely on rigid, hardcoded workout menus (e.g., only offering `8x 1m` or `3x 8m`). If an athlete encounters physiological friction during an archetype—such as the massive post-tempo cardiac drift and sympathetic lock experienced during continuous or long threshold blocks—a static script has no mechanism to adapt. It either repeats the failed workout or falls back to an unstructured jog.

### The Goal: Autonomous Workout Synthesis
The goal of this subsystem is to enable the **AI Coach to autonomously synthesize and propose novel, alternative training sessions** that the runner and codebase did not previously define, grounded in:
1. **Objective Physiological Signals:** Detecting exact failure modes (cardiac drift, lactate pooling, pace decay, excessive EPOC, poor heart rate recovery).
2. **Exercise Science Reasoning:** Leveraging modern endurance paradigms (e.g., Norwegian Double-Threshold, Olbrecht lactate shuttling, Canova block training, Seiler polarized micro-intervals).
3. **Universal DSL Output:** Generating valid Intervals.icu / Garmin workout scripts directly compatible with Garmin watches.
4. **Deterministic Safety Validation:** Python verifies structural validity, load caps, and zone limits before pushing workouts to the calendar.

---

## 2. Physiological Triggers for Workout Adaptation

The system does not change workouts randomly; adaptations are triggered deterministically by observed physiological responses in recent runs:

```
┌───────────────────────────────────────────────────────────────────────────────────┐
│                      OBSERVED PHYSIOLOGICAL RESPONSE                              │
└────────────────────────────────────────┬──────────────────────────────────────────┘
                                         │
        ┌────────────────────────────────┼────────────────────────────────┐
        ▼                                ▼                                ▼
  Cardiac Drift &                  Early Acidosis &                 Aerobic Decoupling
  Post-Effort Sympathetic Lock     Pace Overshooting                in Long Base Runs
  (HR trapped > 160 bpm)           (HR explodes into Z5 in < 3m)    (HR drifts > 5% in Z2)
        │                                │                                │
        ▼                                ▼                                ▼
  ADAPTATION PATTERN:              ADAPTATION PATTERN:              ADAPTATION PATTERN:
  Micro-Threshold Intervals        Over-Under / Lactate Shuttle     Run-Walk-Run (Jeffing)
  (e.g., 5x 4m Z4 w/ 90s pause)    (e.g., 6x 2m Z4 + 1m Z2)         (e.g., 9m run + 1m walk)
```

### Trigger 1: Severe Cardiac Drift / Delayed HRR (`delayed_recovery`)
* **Symptom:** In a continuous tempo or $3 \times 8\text{ min}$ threshold session, heart rate in recovery or cooldown stays elevated in Zone 3 ($> 160\text{ bpm}$) and fails to drop by $\ge 20\text{ bpm}$ within 60 seconds.
* **Diagnosis:** Autonomic hyper-arousal and elevated core temperature; continuous blocks exceed the athlete's current capillary clearance capacity.
* **Autonomous Prescription:** Switch to **Micro-Threshold Intervals** ($5 \times 4\text{ min}$ or $6 \times 3\text{ min}$ at threshold pace with $90\text{s}$ walk/jog recoveries). Net time in Zone 4 remains high ($20\text{–}24\text{ min}$), but periodic pauses purge lactate and prevent cardiovascular runaway.

### Trigger 2: Pace Overshooting / Premature Acidosis (`pace_instability`)
* **Symptom:** Pace fluctuates wildly; the runner begins an interval too fast (chasing lagging HR), spikes into Zone 5 ($> 178\text{ bpm}$), and then fades dramatically in the second half.
* **Diagnosis:** Lack of internal pace calibration; runner is reactive to delayed heart rate feedback.
* **Autonomous Prescription:** Switch to **Pace-Locked Progressive Intervals** or **Lactate Shuttle / Over-Under sessions** ($2\text{m}$ at threshold pace $+ 1\text{m}$ at aerobic base pace). Teaches steady neuromuscular output and active metabolic clearance.

### Trigger 3: Aerobic Fatigue in Base Runs (`aerobic_decoupling`)
* **Symptom:** In a $50\text{ min}$ Zone 2 run, pace drops steadily while HR rises above $158\text{ bpm}$ after minute 35.
* **Diagnosis:** Mitochondrial fatigue, glycogen depletion, or thermal stress in slow-twitch fibers.
* **Autonomous Prescription:** Introduce structured **Run-Walk-Run intervals (Jeffing)**: e.g., $4\text{ reps of } (9\text{ min Z2 run} + 1\text{ min power walk})$. Maintains low sympathetic tone, prevents cardiac drift, and extends durable time-on-feet.

---

## 3. The Universal Workout DSL Grammar

Intervals.icu and Garmin share a compact, human-readable Domain Specific Language (DSL). The AI Coach synthesizes valid workout text using this grammar:

### Structural Grammar
```text
# Title & Pacing Directives
Warmup
- <duration|distance> <target> "<cue>"

<reps>x
- <duration|distance> <target> "<cue>"
- <duration|distance> <target> "<cue>"

Cooldown
- <duration|distance> <target> "<cue>"
```

### Target Modality Rules
* **HR Targets:** Used for Warmup, Cooldown, Easy Runs, and Long Runs:
  * Syntax: `Z1 HR`, `Z2 HR`, `Z1-Z2 HR`, `Z4 HR`.
  * *Pace must NEVER be prescribed for HR runs.*
* **Pace Targets:** Used for Threshold blocks, Strides, and Speed intervals:
  * Syntax: `05:45/km Pace`, `05:30-05:45/km Pace`.
  * *Protects against cardiac lag during the first 2–3 minutes.*
* **Recovery Targets:**
  * Syntax: `Z1 HR` or text prompts like `"Locker traben oder zügig gehen"`.

---

## 4. End-to-End Autonomous Generation Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                          1. RUN ANALYSIS & DETECTION                        │
│  - Python calculates: drift_pct, hrr_60s, zone_seconds, compliance          │
│  - Sets diagnostic flags: delayed_recovery, pace_instability, etc.          │
└──────────────────────────────────────┬──────────────────────────────────────┘
                                       │
                                       ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                          2. LLM WORKOUT SYNTHESIS                           │
│  - Prompt receives: athlete profile, current fitness, observed friction     │
│  - LLM applies exercise science principles to create alternative workout    │
│  - Emits: JSON payload with workout metadata, rationale, and raw DSL text   │
└──────────────────────────────────────┬──────────────────────────────────────┘
                                       │
                                       ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                    3. DETERMINISTIC PYTHON VALIDATION                       │
│  - Parser checks: valid DSL syntax, duration bounds (20m - 75m)             │
│  - Safety check: estimated TSS/load <= 120% of weekly slot budget           │
│  - Zone cap check: no unsafe Z5/Z6 prescription for base-building athlete    │
└──────────────────────────────────────┬──────────────────────────────────────┘
                                       │
                                       ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                      4. PERSISTENCE & USER PRESENTATION                     │
│  - Save synthesized workout to workouts/intervals/synthesis_<id>.txt        │
│  - Display as alternative choice in ./run today and HTML Dashboard         │
│  - Optionally schedule to Intervals.icu calendar on user confirmation       │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## 5. Concrete Alternative Workout Catalog

These calibrated alternative archetypes serve as reference baselines for both the coach engine and manual scheduling:

### Archetype A: Continuous Threshold Run (`20m Z4`)
* **File:** `workouts/intervals/05_tempo_run_20m_z4.txt`
* **Purpose:** Classic threshold tempo for steady-state lactate tolerance.
* **Target:** $10\text{–}15\text{ min}$ Warmup $\rightarrow$ $20\text{ min}$ continuous at $Z4$ ($169\text{–}174\text{ bpm}$) $\rightarrow$ $10\text{ min}$ Cooldown.
* **When to use:** High athlete freshness, minimal previous drift, stable pacing discipline.

### Archetype B: Norwegian Micro-Threshold (`5x 4m`)
* **File:** `workouts/intervals/06_micro_threshold_5x4m.txt`
* **Purpose:** Controlled threshold accumulation with minimal cardiac drift.
* **Target:** $10\text{ min}$ Warmup $\rightarrow$ $5 \times (4\text{ min } @ 05:45\text{/km Pace} + 90\text{s recovery walk/jog}) \rightarrow 10\text{ min}$ Cooldown.
* **When to use:** Following runs with high post-tempo drift or when $3 \times 8\text{ min}$ causes excessive fatigue.

### Archetype C: Lactate Shuttle / Over-Under (`6x [2m Z4 + 1m Z2]`)
* **File:** `workouts/intervals/07_lactate_shuttle_over_under.txt`
* **Purpose:** Teaches the aerobic system to actively clear and utilize lactate during running.
* **Target:** $10\text{ min}$ Warmup $\rightarrow$ $6 \times (2\text{ min } @ 05:40\text{/km} + 1\text{ min } @ 07:15\text{/km}) \rightarrow 10\text{ min}$ Cooldown.
* **When to use:** Mid-season sharpening and overcoming pace stagnation.

---

## 6. Implementation Roadmap

1. **Phase 1 (Artifacts & Templates):**
   * Create standard alternative templates in `workouts/intervals/` (`05_tempo_run_20m_z4.txt`, `06_micro_threshold_5x4m.txt`).
2. **Phase 2 (LLM Prompt & Schema Integration):**
   * Update `src/analysis/ai_coach.py` prompt schema with `alternative_workout_recommendation` object containing `name`, `rationale`, and `dsl_text`.
3. **Phase 3 (Validation & CLI Hook):**
   * Add validation helper in `src/planner/schedule_workout.py` to verify DSL syntax and schedule dynamically synthesized workouts.
