---
name: workout-scheduler
description: >-
  Schedule structured running workouts to Intervals.icu calendar and automatically sync them
  to Garmin watches (or Coros, Wahoo, Suunto). Use when the user requests creating or scheduling
  running workouts, intervals, easy runs, tempo runs, or custom training sessions.
---

# Workout Scheduler for Intervals.icu & Garmin

Create, parameterize, and schedule structured running workouts directly onto the Intervals.icu calendar. Intervals.icu automatically pushes scheduled workouts for the upcoming 7 days to Garmin Connect, which then syncs them to Garmin watches over Bluetooth/Wi-Fi.

## How It Works

1. User requests a workout in natural language (e.g. *"Mach mir ein Intervalltraining für morgen: 10 min aufwärmen locker, 4 mal 1000m schnell und dann 10 min auslaufen"*).
2. The agent translates the request into a call to `src/planner/schedule_workout.py`.
3. The script posts the workout to Intervals.icu REST API (`POST /api/v1/athlete/0/events`).
4. Intervals.icu converts the workout into the Garmin FIT format and syncs it to Garmin Connect.

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

#### 2. Intervals / Wiederholungen
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

#### 3. Tempo Run / Schwellenlauf
```bash
python3 src/planner/schedule_workout.py \
  --type tempo_run \
  --duration 20m \
  --target "Z4 HR" \
  --warmup 8m \
  --cooldown 10m \
  --date tomorrow
```

#### 4. Custom Raw Text Workout
```bash
python3 src/planner/schedule_workout.py \
  --name "Pyramid Intervals" \
  --raw-text "Warmup\n- 10m Z1 HR\n\n- 1m Z5 HR\n- 1m Recovery Z1 HR\n- 2m Z5 HR\n- 2m Recovery Z1 HR\n- 3m Z5 HR\n- 2m Recovery Z1 HR\n- 2m Z5 HR\n- 1m Recovery Z1 HR\n- 1m Z5 HR\n\nCooldown\n- 10m Z1 HR" \
  --date 2026-10-06
```

---

## Important Options

* `--dry-run`: Always use this if the user wants to preview the workout before committing to the calendar.
* `--date`: Accepts `today`, `tomorrow`, or `YYYY-MM-DD`.
* `--time`: Defaults to `07:00:00`.
* `--target`: Supports HR zones (`Z2 HR`, `Z4 HR`), pace (`05:00/km Pace`), or percentages (`75% HR`). Note: Do not mix pace and heart rate targets in the same workout on Garmin.

---

## Prerequisites

* `INTERVALS_API_KEY` must be set in the shell or in `.env`.
* In Intervals.icu settings (*Settings $\rightarrow$ Garmin*), **"Upload planned workouts"** must be checked for automatic Garmin sync.
