---
name: workout-scheduler
description: >-
  Schedule structured running workouts to Intervals.icu calendar and automatically sync them
  to Garmin watches (or Coros, Wahoo, Suunto). Use when the user requests creating or scheduling
  running workouts, intervals, easy runs, tempo runs, or custom training sessions.
---

# Workout Scheduler for Intervals.icu & Garmin

Create, parameterize, and schedule structured running workouts directly onto the Intervals.icu calendar. Intervals.icu automatically pushes scheduled workouts for the upcoming 7 days to Garmin Connect, which then syncs them to Garmin watches over Bluetooth/Wi-Fi.

For full physiological rules, modality invariants, and lifecycle context, see **[`docs/SYSTEM_SPEC.md`](../../../docs/SYSTEM_SPEC.md)**.

---

## Interactive Planning & Consultation Workflow

When the user asks to schedule, plan, or create a workout (or asks for training advice), follow this interactive, data-driven workflow:

### Step 0: Identify Target Athlete (Multi-Tenant Hub)
Check configured athletes via `./run athletes` or [`athletes/registry.json`](../../../athletes/registry.json).
- If the user specifies an athlete (e.g. "für Markus" or "für Berni Test"), use that athlete slug.
- If multiple athletes exist and none was specified, either clarify or default to the `default_athlete` in `athletes/registry.json`.
- Always pass `--athlete <slug>` to `./run` commands when targeting a specific runner.

### Step 1: Recent Training & Gap Analysis
Before proposing or finalizing a workout, inspect the athlete's recent training history and objective markers:
1. **Sync / Check Recent Data & Markers**:
   - Inspect physiological readiness and planning markers:
     ```bash
     ./run plan --markers [--athlete <slug>] --json
     ```
   - Check recent runs and weekly trends:
     ```bash
     ./run runs [--athlete <slug>]
     ./run trends [--athlete <slug>] --json
     ```
2. **Identify Training Balance & Gaps**:
   - **Aerobic Base (De-Dogmatized 80/20 Macro Compass)**: Check `z1_z2_pct` across multiple weeks. For low-to-moderate volume runners (e.g. 2–3 runs/week), 80/20 is a rolling multi-week/monthly macro compass rather than a rigid micro pass/fail filter on a 2-run week. Ensure that dedicated base runs (`easy_run`, `long_run`) remain strictly in Zone 1/2 (`< z2_max` bpm from `athlete.json`, walking on hills if needed).
   - **Fatigue, Timing & Athlete Preferences**: Check `days_since_last_run`, `days_since_last_quality`, and the athlete's configured `weekly_slots` and `allow_consecutive_run_days` in `athletes/<slug>/athlete.json`.
   - **Supported Workout Templates ([`workouts/templates/`](../../../workouts/templates/))**:
     - `easy_run`: Aerobic base in `Z2 HR`
     - `recovery_run`: Active regeneration in `Z1 HR`
     - `long_run`: Extended weekend endurance anchor in `Z2 HR`
     - `intervals`: Short speed / VO2max reps (`10x 1m` Pace corridor or `4x 1km` `Z4 HR`)
     - `fartlek`: Speed-play surges with easy jogging recovery
     - `tempo_run`: Continuous threshold block (`20m Z4 HR`)
     - `threshold_intervals`: Cruise intervals at threshold (`3x 8m Z4 HR`)
     - `micro_threshold`: Controlled short threshold blocks (`5x 4m Z4 HR`)
     - `progressive_threshold`: Progressive `Z2 HR` $\rightarrow$ `Z3 HR` $\rightarrow$ `Z4 HR` transition
3. **Calibrate Target Modality (Pace vs. Heart Rate)**:
   - **Strict Separation Principle**: Never mix Pace and Heart Rate targets in the same workout step or estimate target paces for heart-rate guided runs:
     - **Zone- / HR-guided Sessions (`easy_run`, `recovery_run`, `long_run`, Warmup/Cooldown)**: Guided **exclusively by Heart Rate** (e.g. `Z2 HR` or `Z1-Z2 HR`). **Never prescribe, estimate, or suggest a target pace** (e.g. do NOT say "ca. 7:00 /km"). Pace is purely an outcome and varies widely with fatigue, weather, terrain, and cardiac drift. Encourage brisk walking breaks (`Gehpausen`) on inclines to protect biomechanics and keep HR in zone.
     - **Short Intervals & Speed Reps (< 2 min)**: Guided **exclusively by Pace** (e.g. `05:10-05:25/km Pace`). Heart rate suffers from ~30–60s cardiac lag during short intervals and cannot respond quickly enough.
     - **Threshold / Long Intervals (e.g. `3x 8m` or `5x 4m`)**: Choose EITHER a defined threshold pace corridor OR threshold heart rate (`Z4 HR`), never both simultaneously.

### Step 2: Interactive Dialogue with Athlete
Engage with the user to co-create the best training session:
1. **Brief Observation**: State what they have recently done and what stimulus is currently appropriate.
2. **Propose Calibrated Workout Options**:
   - **For HR-guided sessions (Zone 2, Easy, Recovery, Long Run)**: Propose target zones strictly as HR (`Z2 HR` or `Z1-Z2 HR`). **DO NOT mention or suggest target paces**.
   - **For Pace-guided sessions (Short Intervals < 2 min, VO2max)**: Propose specific target pace corridors (e.g. `05:10-05:25/km Pace`) rather than heart rate.
3. **Confirm & Adjust**: Let the athlete confirm or refine the duration, target zone/pace, or date.

### Step 3: Schedule the Workout
Once agreed, schedule the workout via `./run plan` (which invokes [`src/planner/schedule_workout.py`](../../../src/planner/schedule_workout.py) with resolved multi-tenant paths).

---

## Command Reference (`./run plan`)

### Common Workouts

#### 1. Easy Run / Grundlagenlauf (Zone 2)
```bash
./run plan --athlete berni \
  --type easy_run \
  --duration 45m \
  --date tomorrow
```

#### 2. Intervals with Pace Targets (e.g. 10x 1m)
```bash
./run plan --athlete berni \
  --type intervals \
  --reps 10 \
  --work 1m \
  --recovery 30s \
  --target "05:10-05:25/km Pace" \
  --warmup 10m \
  --cooldown 10m \
  --date tomorrow
```

#### 3. Threshold / Micro-Threshold Intervals with Heart Rate Targets
```bash
./run plan --athlete berni \
  --type threshold_intervals \
  --reps 3 \
  --work 8m \
  --recovery 2m \
  --target "Z4 HR" \
  --warmup 10m \
  --cooldown 10m \
  --date tomorrow
```

#### 4. Tempo Run / Schwellenlauf
```bash
./run plan --athlete berni \
  --type tempo_run \
  --duration 20m \
  --target "Z4 HR" \
  --warmup 10m \
  --cooldown 10m \
  --date tomorrow
```

#### 5. Custom Raw Text Workout (Intervals.icu DSL)
```bash
./run plan --athlete berni \
  --name "10x 1m Intervals" \
  --raw-text "Warmup\n- 10m Z1-Z2 HR\n\n10x\n- 1m 05:10-05:25/km Pace 'Schnell & sauber laufen'\n- 30s Recovery 'Locker traben'\n\nCooldown\n- 10m Z1 HR" \
  --date tomorrow
```

---

## Target Formatting & Garmin Watch Display

* **Pace Corridor**: `- 1m 05:10-05:25/km Pace` (Garmin shows target speedometer gauge with vibration alerts outside corridor).
* **Single Pace**: `- 1m 05:15/km Pace` (Garmin creates automatic ±5–10 s/km tolerance window).
* **Heart Rate Zone**: `- 10m Z2 HR` or `- 8m Z4 HR` (best for Warmup, Cooldown, Easy Runs, and Threshold blocks).
* **Active Recovery vs Rest**:
  - `- 30s Recovery`: Active jogging recovery (Garmin continues pace/step tracking).
  - `- 30s Rest`: Standing/walking pause (Garmin suppresses pace alarms and shows Pause).
* **Open-ended Steps**: `- 10m Z1-Z2 HR press lap` (step continues until runner presses the lap button).
* **Step Prompts / Cues**: Add quoted text after step, e.g. `- 1m 05:15/km Pace "Schnell laufen"`.

> [!IMPORTANT]
> **Strict Separation: Never Mix or Prescribe Pace for HR-Based Runs!**
> - When a workout is guided by **Heart Rate** (`Z2 HR`, `Z1-Z2 HR`, Easy Run, Recovery, Long Run): **DO NOT provide, estimate, or suggest a target pace** in proposals or workout steps. The athlete must regulate speed exclusively by HR, walking if necessary.
> - When a workout is guided by **Pace** (Short Intervals < 2 min, VO2max): Prescribe **strictly Pace corridors** due to cardiac lag. Do not set HR targets for short work reps.
> - Garmin watches display either the Pace speedometer gauge OR the Heart Rate zone gauge. They cannot display both simultaneously for the same step.

---

## Prerequisites

* `INTERVALS_API_KEY` must be set in the shell or in `.env`.
* In Intervals.icu settings (*Settings $\rightarrow$ Garmin*), **"Upload planned workouts"** must be checked for automatic Garmin sync.
