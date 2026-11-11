# Implementation Plan: Deterministic Athlete Foresight & Forward Projection Engine

**Date:** 2026-11-11  
**Status:** Planned (Depends on resolution of Findings 5.2, 5.3, and 5.5 in [`ARCHITECTURE_REVIEW_AND_FINDINGS.md`](ARCHITECTURE_REVIEW_AND_FINDINGS.md))  
**Scope:** Deterministic, zero-LLM forward projection of training load (CTL/ATL/TSB), adaptive slot schedules, deload timing, and "What-If" trajectories over a 14- to 28-day horizon.  
**Companion Plan:** [`AGENTIC_SIMULATION_PLAN.md`](AGENTIC_SIMULATION_PLAN.md) (On-Demand LLM Stress-Testing Harness, strictly separated from athlete forecasting).

---

## 1. Problem Statement & Motivation

Currently, `generate_volume_forecast()` in [`src/analysis/forecast.py`](../src/analysis/forecast.py#L172-L235) computes a static 4-week volume projection by multiplying the trailing 3-week average distance by hardcoded factors `[1.10, 1.20, 1.30, 0.95]`.

This static calculation has three structural limitations:
1. **Disconnected from the Adaptive Planner**: It ignores the athlete's configured `weekly_slots` and `target_runs_per_week` in `athletes/<slug>/athlete.json` as well as [`choose_adaptive_workout()`](../src/planner/schedule_workout.py#L594-L720).
2. **Blind to Build-Week & Deload State**: If an athlete has already completed 2 or 3 consecutive build weeks (`consecutive_build_weeks`), the static table still predicts `"Aufbauwoche 1 (+10%)"` for next week, even though [`extract_markers.py`](../src/planner/extract_markers.py#L66-L103) will actually enforce a **Deload Week** on Monday.
3. **No Load / Form (CTL/ATL/TSB) Trajectory**: Athletes cannot see how following the upcoming plan (vs. taking recovery days) will impact their Banister Fitness ($\text{CTL}$), Fatigue ($\text{ATL}$), and Form ($\text{TSB}$) over the next 2–4 weeks.

---

## 2. Strict Separation from Agentic Simulation

| Dimension | Athlete Foresight Engine (`FORESIGHT_ENGINE_PLAN.md`) | Agentic Scenario Simulator (`AGENTIC_SIMULATION_PLAN.md`) |
| :--- | :--- | :--- |
| **Primary Goal** | Provide the athlete with accurate 14–28 day training, load (CTL/ATL/TSB), and deload projections. | Stress-test the LLM Coach (`ai_coach.py`, `telegram_coach.py`) across crisis scenarios to uncover bugs. |
| **LLM Usage** | **Zero LLM calls** (100% deterministic Python math, $< 50\text{ ms}$ runtime). | **Real LLM calls** (`agy` / `gemini`) on every simulated day. |
| **Execution Context** | Runs automatically during `./run dashboard` and via `./run plan --foresight`. | Runs manually on demand via `./run simulate --scenario <name>`. |
| **Wellness Assumptions** | Assumes stable baseline recovery (or explicit deterministic "What-If" trajectories). | Injects synthetic illnesses, RHR spikes, cramps, overpacing, and Telegram messages. |

---

## 3. Foresight Engine Architecture (`src/analysis/foresight.py`)

The Foresight Engine operates as a pure, in-memory forward state machine starting from the athlete's current state on `ref_date` ($t_0$) and stepping day by day through $t_0 + 1 \dots t_0 + H$ (default $H = 28$ days).

```mermaid
flowchart LR
    subgraph Initial State t_0
        ACT[intervals_activities.json]
        WEL[intervals_wellness.json]
        CAL[calendar_events.json]
        PROF[athlete.json]
    end

    subgraph Daily Forward Step t = t_0 + 1 .. t_0 + H
        MARK[extract_planning_markers<br/>in-memory state at day t] --> PLAN[choose_adaptive_workout<br/>for active slot on day t]
        PLAN --> SYNTH[Synthesize Expected Activity<br/>Duration, Zone & Estimated Load]
        SYNTH --> EWMA[Update Banister EWMA<br/>CTL_t, ATL_t, TSB_t]
    end

    ACT & WEL & CAL & PROF --> MARK
    EWMA -->|t = t + 1| MARK
    EWMA --> OUT[Foresight Payload:<br/>Weekly Summary, Daily Schedule,<br/>CTL/ATL/TSB Curves & Deload Dates]
```

### 3.1 Synthetic Load Estimation per Workout Archetype
To project $\text{CTL}_t$, $\text{ATL}_t$, and $\text{TSB}_t$ forward without arbitrary guesses, each workout template scheduled by `choose_adaptive_workout()` maps to a deterministic expected training load (`icu_training_load`) and distance derived from the athlete's historical efficiency in `intervals_activities.json` (with profile-based fallbacks):

- **`easy_run` (45m Z2)**: Expected duration $45\text{ min}$, estimated load $\approx 38\text{–}45\text{ TSS}$, estimated distance from athlete's median Z2 pace.
- **`Deload Run` / `recovery_run` (30–35m Z1/Z2)**: Expected duration $30\text{–}35\text{ min}$, estimated load $\approx 22\text{–}28\text{ TSS}$.
- **`long_run` / Slot 3 Base Anchor (50–75m Z2)**: Expected duration $50\text{–}75\text{ min}$, estimated load $\approx 50\text{–}68\text{ TSS}$.
- **`intervals` / `threshold_intervals` (Slot 1 / Slot 2 Quality)**: Expected duration $42\text{–}46\text{ min}$, estimated load $\approx 52\text{–}62\text{ TSS}$.

### 3.2 Day-by-Day Banister EWMA Projection
For each future day $t$:
$$\text{CTL}_t = \text{CTL}_{t-1} \cdot e^{-1/42} + \text{Load}_t \cdot \left(1 - e^{-1/42}\right)$$
$$\text{ATL}_t = \text{ATL}_{t-1} \cdot e^{-1/7} + \text{Load}_t \cdot \left(1 - e^{-1/7}\right)$$
$$\text{TSB}_t = \text{CTL}_t - \text{ATL}_t$$

---

## 4. Multi-Trajectory "What-If" Projections

The Foresight Engine computes three parallel trajectories over the upcoming 14–28 days so the athlete can compare outcomes:

1. **Trajectory A — `plan_compliant` (Adaptive Baseline)**:
   - Assumes the athlete completes each slot workout selected by `choose_adaptive_workout()` on their configured `weekly_slots` days while maintaining baseline sleep and resting HR.
   - Automatically reflects upcoming **Deload Weeks** as soon as `consecutive_build_weeks` reaches `3`, and shows when quality sessions resume afterward.
2. **Trajectory B — `recovery_week_first` (What-If: 7 Days Easy / Rest)**:
   - Simulates taking the next 7 days strictly as rest or easy Z1/Z2 recovery before resuming normal slots.
   - Quantifies how quickly $\text{TSB}$ rebounds into the fresh zone ($> +5$) and how little $\text{CTL}$ is lost over a single recovery week.
3. **Trajectory C — `ambitious_build` (What-If: Maximum Slot Utilization)**:
   - Simulates executing all quality-eligible slots at full stimulus to highlight when $\text{TSB}$ would drop below the fatigue guardrail ($\text{TSB} < -10$) and force an automatic downgrade.

---

## 5. Planned Integration & Data Contracts

### 5.1 Contract Addition in [`src/core/contracts.py`](../src/core/contracts.py)
Define `ForesightProjectionContract` (`TypedDict`) and `validate_foresight_projection()`:
- `reference_date`: ISO date string (`YYYY-MM-DD`)
- `horizon_days`: Integer (`14` or `28`)
- `current_state`: `{ "ctl": float, "atl": float, "tsb": float, "consecutive_build_weeks": int }`
- `next_deload_week_iso`: ISO week string (e.g., `"2026-W47"`) showing exactly when the next automatic deload triggers
- `trajectories`: Dictionary containing daily and weekly series for `plan_compliant`, `recovery_week_first`, and `ambitious_build`

### 5.2 CLI & Dashboard Integration
1. **CLI Inspection (`./run plan --foresight [--days 14|28]`)**:
   - Prints a structured terminal table of upcoming scheduled slot workouts, weekly projected km/load, real `consecutive_build_weeks` transitions, and end-of-week `CTL / ATL / TSB` values.
2. **Replacement of Static Volume Forecast in [`src/analysis/forecast.py`](../src/analysis/forecast.py)**:
   - Replace the static `[1.10, 1.20, 1.30, 0.95]` multiplier loop in `generate_volume_forecast()` with the state-aware `plan_compliant` weekly aggregation from `src/analysis/foresight.py` so the HTML dashboard (`dist/<dashboard_slug>/index.html`) always displays the true upcoming build/deload sequence.

---

## 6. Verification Strategy (BDD)

Because the Foresight Engine is 100% deterministic, it will be covered by standard offline BDD scenarios in `tests/foresight.feature` and `tests/test_foresight.py` (executed during `./run test`):
- **Scenario 1**: Athlete with `consecutive_build_weeks == 2` sees 1 Build Week followed immediately by a Deload Week in week 2 of the foresight projection (rather than 3 more build weeks).
- **Scenario 2**: Athlete currently in an acute illness/health-warning state sees conservative recovery/re-entry sessions in week 1 of the projection before quality slots resume.
- **Scenario 3**: Banister `CTL`, `ATL`, and `TSB` projections match exact exponential decay/impulse math across rest and training days.
