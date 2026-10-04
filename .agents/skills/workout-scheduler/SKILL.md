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
   - Run `python3 src/analysis/trends.py --json` to get weekly volume, average pace, average HR, and `z1_z2_pct`.
2. **Identify Training Balance & Gaps**:
   - **Aerobic Base (80/20 Rule)**: Check `z1_z2_pct`. If `< 70-80%`, the runner is spending too much time in the "grey zone" (Zone 3/4) and lacks low-intensity Zone 2 base building.
   - **Fatigue & Timing**: When was the last run? Was it hard (high HR, threshold tempo)? Does the athlete need a recovery day or an easy run?
   - **Missing Stimuli**: Check which workout archetypes have been neglected over the last 2–3 weeks:
     - *VO2max / Speed Intervals* (short reps e.g. 10x 1m, 400m–800m)
     - *Threshold / Schwellenlauf* (sustained tempo e.g. 20–30m Z4, 3x 10m)
     - *Long Run* (extended duration e.g. > 60–75m in Z2)
     - *Recovery / Easy Run* (low HR, strictly Z1/Z2)
3. **Calibrate Individual Pace Corridors**:
   - Calibrate target paces using actual continuous run performances:
     - **Short Intervals (e.g. 1m reps)**: ~30–45 s/km faster than threshold pace.
     - **Long Intervals (e.g. 1km reps)**: ~15–25 s/km faster than threshold pace (5k race pace).
     - **Threshold / Tempo**: Sustainable 1-hour race pace (near LTHR).
     - **Easy / Recovery**: 60–90 s/km slower than threshold pace, keeping HR strictly in Z1/Z2.

### Step 2: Interactive Dialogue with Athlete
Engage with the user to co-create the best training session:
1. **Brief Observation**: State what they have recently done and what stimulus is currently missing (e.g., *"Deine letzten Läufe waren fast alle im Schwellenbereich (Z3/Z4). Dir fehlt aktuell Grundlagenausdauer in Zone 2, aber wenn du Intervalle laufen willst, passen 10x 1m ideal..."*).
2. **Propose Calibrated Workout Options**:
   - Suggest specific target pace corridors (e.g., `05:10-05:25/km Pace`) rather than just raw bpm, explaining how the Garmin watch gauge and vibration alerts will guide them.
   - For short intervals (< 2 min), always recommend **Pace targets** over Heart Rate due to cardiac lag. For easy runs and warmup/cooldown, recommend **HR targets** (Z1/Z2 HR).
3. **Confirm & Adjust**: Let the athlete confirm or refine the pace, duration, or date.

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
> Do not mix Pace targets and Heart Rate targets in the same work intervals on Garmin watches, as Garmin displays either the Pace gauge or the HR gauge.

---

## Prerequisites

* `INTERVALS_API_KEY` must be set in the shell or in `.env`.
* In Intervals.icu settings (*Settings $\rightarrow$ Garmin*), **"Upload planned workouts"** must be checked for automatic Garmin sync.
