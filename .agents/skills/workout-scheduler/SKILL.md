---
name: workout-scheduler
description: >-
  Schedule structured running workouts to Intervals.icu calendar and automatically sync them
  to Garmin watches (or Coros, Wahoo, Suunto). Use when the user requests creating or scheduling
  running workouts, intervals, easy runs, tempo runs, or custom training sessions.
---

# Workout Scheduler for Intervals.icu & Garmin

Create, parameterize, and schedule structured running workouts directly onto the Intervals.icu calendar. Intervals.icu automatically pushes scheduled workouts for the upcoming 7 days to Garmin Connect, which then syncs them to Garmin watches over Bluetooth/Wi-Fi.

---

## Interactive Planning & Consultation Workflow

When the user asks to schedule, plan, or create a workout (or asks for training advice), follow this interactive, data-driven workflow:

### Step 1: Recent Training & Gap Analysis
Before proposing or finalizing a workout, inspect the athlete's recent training history:
1. **Sync / Check Recent Data**:
   - Check `data/intervals_activities.json`. If missing or older than a few days, fetch recent activities:
     ```bash
     python3 src/integrations/download_intervals.py --oldest <date-30-days-ago>
     ```
   - Run `python3 src/analysis/recent_runs.py` (shows recent 10 runs by default) to inspect individual runs (date, distance, pace, HR, load).
   - Run `python3 src/analysis/trends.py --json` to get weekly volume, average pace, average HR, and `z1_z2_pct`.
2. **Identify Training Balance & Gaps**:
   - **Aerobic Base (80/20 Rule)**: Check `z1_z2_pct`. If `< 70-80%`, the runner is spending too much time in the "grey zone" (Zone 3/4) and lacks low-intensity Zone 2 base building.
   - **Fatigue & Timing**: When was the last run? Was it hard (high HR, threshold tempo)? Does the athlete need a recovery day or an easy run?
   - **Missing Stimuli**: Check which workout archetypes have been neglected over the last 2–3 weeks:
     - *VO2max / Speed Intervals* (short reps e.g. 10x 1m, 400m–800m)
     - *Threshold / Schwellenlauf* (sustained tempo e.g. 20–30m Z4, 3x 10m)
     - *Long Run* (extended duration e.g. > 60–75m in Z2)
     - *Recovery / Easy Run* (low HR, strictly Z1/Z2)
3. **Calibrate Target Modality (Pace vs. Heart Rate)**:
   - **Strict Separation Principle**: Never mix Pace and Heart Rate targets in the same workout or estimate target paces for heart-rate guided runs:
     - **Zone- / HR-guided Sessions (Easy Run, Recovery, Long Run, Warmup/Cooldown)**: Guided **exclusively by Heart Rate** (e.g. `Z2 HR` or `Z1-Z2 HR`). **Never prescribe, estimate, or suggest a target pace** (e.g. do NOT say "ca. 7:00 /km"). Pace is purely an outcome and varies widely with fatigue, weather, terrain, and individual cardiac drift. Prescribing a pace alongside an HR target leads to overpacing and ruins the aerobic stimulus. The athlete must run as slowly as needed (or walk) to keep heart rate in the target zone.
     - **Short Intervals & Speed Reps (< 2 min)**: Guided **exclusively by Pace** (e.g. `05:10-05:25/km Pace`). Heart rate suffers from cardiac lag during short intervals and cannot respond quickly enough. Calibrate short interval pace ~30–45 s/km faster than threshold pace.
     - **Threshold / Long Intervals (e.g. 1km reps or 3x 10m)**: Choose EITHER a defined threshold pace corridor (~15–25 s/km faster than threshold pace for 5k/10k pace) OR threshold heart rate (`Z4 HR`), never both.

### Step 2: Interactive Dialogue with Athlete
Engage with the user to co-create the best training session:
1. **Brief Observation**: State what they have recently done and what stimulus is currently missing (e.g., *"Deine letzten Läufe waren fast alle im Schwellenbereich (Z3/Z4). Dir fehlt aktuell Grundlagenausdauer in Zone 2..."*).
2. **Propose Calibrated Workout Options**:
   - **For HR-guided sessions (Zone 2, Easy, Recovery)**: Propose target zones strictly as HR (`Z2 HR` or `Z1-Z2 HR`, e.g. `< 148 bpm`). **DO NOT mention or suggest target paces**. Make clear that pace is completely secondary and will adjust naturally.
   - **For Pace-guided sessions (Short Intervals < 2 min, VO2max)**: Propose specific target pace corridors (e.g. `05:10-05:25/km Pace`) rather than heart rate, explaining how the Garmin speedometer gauge and vibration alerts will guide them.
3. **Confirm & Adjust**: Let the athlete confirm or refine the duration, target zone/pace, or date.

### Step 3: Schedule the Workout
Once agreed, schedule the workout via `src/planner/schedule_workout.py`.

---

## Command Reference

The runner script is located at:
`src/planner/schedule_workout.py`

### Common Workouts

#### 1. Easy Run / Grundlagenlauf (Zone 2)
```bash
python3 src/planner/schedule_workout.py \
  --type easy_run \
  --duration 45m \
  --date tomorrow
```

#### 2. Intervals with Pace Targets (e.g. 10x 1m)
```bash
python3 src/planner/schedule_workout.py \
  --type intervals \
  --reps 10 \
  --work 1m \
  --recovery 30s \
  --target "05:10-05:25/km Pace" \
  --warmup 10m \
  --cooldown 10m \
  --date tomorrow
```

#### 3. Intervals with Heart Rate Targets (e.g. 4x 1km)
```bash
python3 src/planner/schedule_workout.py \
  --type intervals \
  --reps 4 \
  --work 1km \
  --recovery 2m \
  --target "Z4 HR" \
  --warmup 10m \
  --cooldown 10m \
  --date tomorrow
```

#### 4. Tempo Run / Schwellenlauf
```bash
python3 src/planner/schedule_workout.py \
  --type tempo_run \
  --duration 20m \
  --target "Z4 HR" \
  --warmup 8m \
  --cooldown 10m \
  --date tomorrow
```

#### 5. Custom Raw Text Workout (Intervals.icu DSL)
```bash
python3 src/planner/schedule_workout.py \
  --name "10x 1m Intervals" \
  --raw-text "Warmup\n- 10m Z1-Z2 HR\n\n10x\n- 1m 05:10-05:25/km Pace 'Schnell & sauber laufen'\n- 30s Recovery 'Locker traben'\n\nCooldown\n- 10m Z1 HR" \
  --date tomorrow
```

---

## Target Formatting & Garmin Watch Display

* **Pace Corridor**: `- 1m 05:10-05:25/km Pace` (Garmin shows target speedometer gauge with vibration alerts outside corridor).
* **Single Pace**: `- 1m 05:15/km Pace` (Garmin creates automatic ±5-10 s/km tolerance window).
* **Heart Rate Zone**: `- 10m Z2 HR` or `- 1m Z4 HR` (best for Warmup, Cooldown, and Easy Runs).
* **Active Recovery vs Rest**:
  - `- 30s Recovery`: Active jogging recovery (Garmin continues pace/step tracking).
  - `- 30s Rest`: Standing/walking pause (Garmin suppresses pace alarms and shows Pause).
* **Open-ended Steps**: `- 10m Z1-Z2 HR press lap` (step continues until runner presses the lap button).
* **Step Prompts / Cues**: Add quoted text after step, e.g. `- 1m 05:15/km Pace "Schnell laufen"`.

> [!IMPORTANT]
> **Strict Separation: Never Mix or Prescribe Pace for HR-Based Runs!**
> - When a workout is guided by **Heart Rate** (`Z2 HR`, `Z1-Z2 HR`, Easy Run, Recovery): **DO NOT provide, estimate, or suggest a target pace** in proposals or workout steps. Prescribing a pace alongside an HR target leads to overpacing and conflicts (e.g., running at 7:00 /km may easily exceed Zone 2). The athlete must regulate speed exclusively by HR, walking if necessary.
> - When a workout is guided by **Pace** (Short Intervals, VO2max): Prescribe **strictly Pace corridors** due to cardiac lag. Do not set HR targets for short work reps.
> - Garmin watches display either the Pace speedometer gauge OR the Heart Rate zone gauge. They cannot display both simultaneously for the same step.

---

## Prerequisites

* `INTERVALS_API_KEY` must be set in the shell or in `.env`.
* In Intervals.icu settings (*Settings $\rightarrow$ Garmin*), **"Upload planned workouts"** must be checked for automatic Garmin sync.
