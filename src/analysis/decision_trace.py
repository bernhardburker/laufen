#!/usr/bin/env python3
"""decision_trace.py

Structured Decision Tracing & Coaching Criteria Inspector for Laufen.
Provides:
1. Deterministic evaluation of all coaching criteria, guardrails, and rejected
   alternatives (`evaluate_workout_decision`).
2. Persistent JSONL audit logging (`append_decision_trace`, `load_decision_traces`)
   in `athletes/<slug>/decision_trace.jsonl`.
3. Terminal & JSON CLI inspection (`./run trace`) showing what the Macro AI Coach
   currently thinks (`coach_history.json` / `ai_coach_summary.json`), how each
   criterion is evaluated today, which alternatives were rejected, and historical
   logged decisions.
"""

from __future__ import annotations

import argparse
import datetime
import json
from pathlib import Path
import sys
from typing import Any, Dict, List, Optional

PROJECT_ROOT = Path(__file__).resolve().parent.parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from src.planner.extract_markers import extract_planning_markers, load_json_file


def evaluate_workout_decision(
    markers: Dict[str, Any],
    days_since_last_run: Optional[int] = None,
    is_rest_day: bool = False,
    rest_day_reason: Optional[str] = None,
    trigger: str = "adaptive_plan",
    timestamp: Optional[str] = None,
) -> Dict[str, Any]:
    """Evaluates all planning criteria from extracted markers and explains the resulting decision."""
    now_iso = timestamp or datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    ref_date_str = str(markers.get("reference_date") or datetime.date.today().isoformat())
    target_date_str = str(markers.get("target_date") or ref_date_str)

    readiness = markers.get("readiness") or {}
    history = markers.get("history") or {}
    periodization = markers.get("periodization") or {}
    coach_ctx = markers.get("coach_context") or {}
    guardrails = markers.get("guardrails") or {}

    days_since = (
        days_since_last_run
        if days_since_last_run is not None
        else int(history.get("days_since_last_run", 999))
    )
    days_since_quality = int(history.get("days_since_last_quality", 999))
    last_run = history.get("last_run") or {}
    slot_id = int(periodization.get("slot_id", 2))
    slot_name = str(periodization.get("slot_name") or f"Slot {slot_id}")
    target_day_name = str(periodization.get("target_day_name") or "").capitalize()

    # Build step-by-step criteria evaluation list
    criteria: List[Dict[str, Any]] = []

    # Criterion 1: Inactivity / Re-entry break (>= 10 days)
    long_break = days_since >= 10
    criteria.append({
        "criterion": "1. Trainingspause / Wiedereinstieg (< 10 Tage)",
        "passed": not long_break,
        "status": "override" if long_break else "ok",
        "value": days_since,
        "detail": (
            f"Pause von {days_since} Tagen (>= 10 Tage) -> Sanfter Wiedereinstieg (30m Z2) erzwungen."
            if long_break
            else f"Letzter Lauf vor {days_since} Tag(en) ({last_run.get('date') or 'k.A.'})."
        ),
    })

    # Criterion 2: Health, Wellness & Muscular Complaint Check
    health_warn = bool(guardrails.get("health_warning", False))
    health_details: List[str] = []
    if last_run.get("has_muscular_complaint"):
        note_snip = str(last_run.get("athlete_note") or "")[:90]
        health_details.append(f"Beschwerden im letzten Lauf ({last_run.get('date')}): '{note_snip}'")
    if readiness.get("injury"):
        health_details.append(f"Verletzung: {readiness.get('injury')}")
    if readiness.get("illness_comment"):
        health_details.append(f"Unwohlsein/Krankheit: {readiness.get('illness_comment')}")
    if readiness.get("elevated_rhr"):
        health_details.append(
            f"Erhöhter Ruhepuls: {readiness.get('resting_hr_today')} bpm (+{readiness.get('rhr_delta')} bpm ggü. Schnitt {readiness.get('resting_hr_baseline')})"
        )
    criteria.append({
        "criterion": "2. Gesundheit, Muskulatur & Wellness-Check",
        "passed": not health_warn,
        "status": "override" if health_warn else "ok",
        "value": not health_warn,
        "detail": (
            " | ".join(health_details) + " -> Keine harten Intervalle/Schwellenläufe."
            if health_warn
            else f"Unauffällig (Ruhepuls {readiness.get('resting_hr_today')} bpm, Baseline {readiness.get('resting_hr_baseline')} bpm, keine Beschwerden)."
        ),
    })

    # Criterion 3: Acute Fatigue & Sleep Guardrail
    fatigue_warn = bool(guardrails.get("fatigue_warning", False))
    volume_spike = bool(guardrails.get("volume_spike_alert", False))
    tsb = readiness.get("tsb", 0.0)
    sleep_h = readiness.get("sleep_hours_last_night", 0.0)
    sleep_score = readiness.get("sleep_score_last_night", 0.0)
    criteria.append({
        "criterion": "3. Ermüdung (TSB), Schlaf & Vorbelastung",
        "passed": not (fatigue_warn or volume_spike),
        "status": "override" if (fatigue_warn or volume_spike) else "ok",
        "value": {"tsb": tsb, "sleep_hours": sleep_h, "sleep_score": sleep_score},
        "detail": (
            f"Ermüdungs-/Umfangsschutz aktiv (TSB {tsb:+.1f}, Schlaf {sleep_h}h / Score {sleep_score:.0f}, letzter Lauf {last_run.get('duration_min', 0):.0f}m)."
            if (fatigue_warn or volume_spike)
            else f"Belastbar (TSB {tsb:+.1f}, Schlaf {sleep_h}h / Score {sleep_score:.0f})."
        ),
    })

    # Criterion 4: Quality Spacing (>= 3 days & TSB >= -8.0)
    qual_discouraged = bool(guardrails.get("quality_discouraged", False))
    criteria.append({
        "criterion": "4. Qualitäts-Abstand (>= 3 Tage nach Tempo/Intervall)",
        "passed": not qual_discouraged,
        "status": "override" if qual_discouraged else "ok",
        "value": days_since_quality,
        "detail": (
            f"Qualitätsreiz gesperrt: Letzter Qualitätslauf vor {days_since_quality} Tag(en) oder TSB {tsb:+.1f} < -8.0."
            if qual_discouraged
            else f"Ausreichend Abstand: Letzter Qualitätsreiz vor {days_since_quality} Tag(en)."
        ),
    })

    # Criterion 5: Mandatory Base Anchor Check (e.g. Weekend Slot 3)
    base_missing = bool(guardrails.get("base_anchor_missing", False))
    missed_days = history.get("missed_anchor_days") or []
    criteria.append({
        "criterion": "5. Aerober Basis-Anker im Vorzyklus (Slot 3 / Wochenende)",
        "passed": not base_missing,
        "status": "override" if base_missing else "ok",
        "value": not base_missing,
        "detail": (
            f"Wochenend-/Grundlagenlauf ({', '.join(missed_days) or 'Slot 3'}) ist ausgefallen -> Z2-Grundlage wird vor Qualitätsreizen nachgeholt."
            if base_missing
            else "Aerober Basis-Anker erfüllt oder für diesen Slot nicht vorausgesetzt."
        ),
    })

    # Criterion 6: Open Macro AI Coach Directive (`coach_history.json`)
    coach_override = bool(guardrails.get("coach_directive_override", False))
    open_cid = coach_ctx.get("open_cycle_id")
    open_type = coach_ctx.get("target_type")
    open_rec_text = coach_ctx.get("recommendation") or ""
    criteria.append({
        "criterion": "6. Offene Makro-Coach-Vorgabe (coach_history.json)",
        "passed": not coach_override,
        "status": "override" if coach_override else "ok",
        "value": {"cycle_id": open_cid, "target_type": open_type},
        "detail": (
            f"Offene Vorgabe {open_cid} ({coach_ctx.get('open_cycle_date')}) fordert '{open_type}' (< {coach_ctx.get('target_hr_max') or 158} bpm) und wurde noch nicht absolviert."
            if coach_override
            else (
                f"Offene Vorgabe {open_cid} ('{open_type}') steht nicht im Widerspruch oder ist bereits erledigt."
                if open_cid
                else "Keine offene Basis-Restriktion in coach_history.json."
            )
        ),
    })

    # Criterion 7: Target Weekday Periodization Slot
    criteria.append({
        "criterion": f"7. Wochentags-Periodisierung ({target_date_str}, {target_day_name})",
        "passed": True,
        "status": "ok",
        "value": {"slot_id": slot_id, "slot_name": slot_name, "role": periodization.get("role")},
        "detail": f"Zugeordnet zu {slot_name} (Rolle: {periodization.get('role', 'flexible')}, Bevorzugt: {periodization.get('preferred_type', 'easy_run')}).",
    })

    # Determine default slot workout (what the slot would schedule without guardrails)
    slot_allows_threshold = bool(guardrails.get("slot_allows_threshold", False))
    if slot_id == 1:
        default_slot_workout = ("Intervall- & Kadenztraining (5x 1m Speed)", "intervals")
    elif slot_id == 2 and ("tempo_run" in (periodization.get("allowed_types") or [])):
        default_slot_workout = ("Kontrollierter Schwellenlauf (3x 8m Z4)", "threshold_intervals")
    else:
        default_slot_workout = ("Aerober Anker & Grundlagenlauf (45m Z2)", "easy_run")

    rejected_alternatives: List[Dict[str, str]] = []
    active_warnings: List[str] = list(guardrails.get("active_warnings") or [])

    # Resolve final workout selection matching choose_adaptive_workout()
    if long_break:
        selected_name = "Easy Run - Sanfter Wiedereinstieg (30m)"
        selected_type = "easy_run"
        decision_source = "guardrail:long_inactivity_break"
        reasoning_summary = (
            f"Nach {days_since} Tagen Pause (>= 10 Tage) wird zum Schutz von Sehnen und Kreislauf "
            "ein sanfter 30-minütiger Wiedereinstieg in Zone 2 gewählt."
        )
        if default_slot_workout[1] != "easy_run":
            rejected_alternatives.append({
                "workout": default_slot_workout[0],
                "reason": f"Verworfen wegen {days_since} Tagen Trainingspause.",
            })
    elif volume_spike or (fatigue_warn and qual_discouraged) or (health_warn and fatigue_warn):
        selected_name = "Easy Run - Aktive Erholung & Basis (35m Z2)"
        selected_type = "easy_run"
        decision_source = "guardrail:acute_fatigue_or_volume_spike"
        reasoning_summary = (
            "Wegen hoher Vorbelastung oder akuter Ermüdung wird die Einheit auf 35 Minuten "
            "aktive Erholung in Zone 2 verkürzt."
        )
        if default_slot_workout[1] != "easy_run":
            rejected_alternatives.append({
                "workout": default_slot_workout[0],
                "reason": "; ".join(active_warnings) or "Akute Ermüdung / hohe Vorbelastung.",
            })
    elif qual_discouraged or fatigue_warn or health_warn or base_missing or coach_override:
        selected_name = "Aerober Anker & Grundlagenlauf (45m Z2)"
        selected_type = "easy_run"
        active_reasons: List[str] = []
        if health_warn:
            active_reasons.append("Muskel-/Gesundheitsschutz (Beschwerden beim Vorlauf oder Wellness-Warnung)")
        if base_missing:
            active_reasons.append(f"ausgefallener Wochenend-Grundlagenlauf ({', '.join(missed_days) or 'Slot 3'})")
        if coach_override:
            active_reasons.append(f"offene Coach-Vorgabe {open_cid} ('{open_type}')")
        if qual_discouraged:
            active_reasons.append(f"Qualitätsabstand ({days_since_quality}d) / TSB ({tsb:+.1f})")
        if fatigue_warn:
            active_reasons.append(f"Schlaf/Ermüdung ({sleep_h}h, Score {sleep_score:.0f})")

        if base_missing or coach_override or health_warn:
            decision_source = "guardrail:base_anchor_or_coach_directive"
        else:
            decision_source = "guardrail:fatigue_or_quality_spacing"

        reasoning_summary = (
            f"Obwohl {target_day_name} regulär in {slot_name} fällt, wird kein Tempo-/Intervalltraining "
            f"angesetzt. Ausschlaggebend: {'; '.join(active_reasons)}. "
            "Stattdessen wird das aerobe Fundament mit 45 Minuten ruhig in Zone 2 gesichert."
        )
        if default_slot_workout[1] != "easy_run":
            rejected_alternatives.append({
                "workout": default_slot_workout[0],
                "reason": "; ".join(active_reasons),
            })
    elif slot_id == 1:
        selected_name = "Intervall- & Kadenztraining (5x 1m Speed)"
        selected_type = "intervals"
        decision_source = "slot_1_quality"
        reasoning_summary = (
            f"Alle Leitplanken sind im grünen Bereich (Basis-Anker erfüllt, {days_since_quality} Tage seit letzter Qualität, "
            f"TSB {tsb:+.1f}, keine offenen Basis-Direktiven). {slot_name} gibt das Intervall- & Kadenztraining frei."
        )
    elif slot_id == 2 and slot_allows_threshold:
        selected_name = "Kontrollierter Schwellenlauf (3x 8m Z4)"
        selected_type = "threshold_intervals"
        decision_source = "slot_2_threshold"
        reasoning_summary = (
            f"Hohe Frische (TSB {tsb:+.1f}) und intaktes Grundlagenfundament erlauben in {slot_name} "
            "den kontrollierten Schwellenlauf (3x 8m Z4)."
        )
    else:
        selected_name = "Aerober Anker & Grundlagenlauf (45m Z2)"
        selected_type = "easy_run"
        decision_source = f"slot_{slot_id}_base_anchor"
        reasoning_summary = (
            f"Planmäßiger Grundlagenlauf in {slot_name} (45m Zone 2) zur Pflege der aeroben Ausdauer."
        )

    if is_rest_day and rest_day_reason:
        reasoning_summary = f"[Heute Ruhetag: {rest_day_reason} -> Planung gilt für {target_date_str}] " + reasoning_summary

    return {
        "timestamp": now_iso,
        "trigger": trigger,
        "reference_date": ref_date_str,
        "target_date": target_date_str,
        "is_rest_day": is_rest_day,
        "rest_day_reason": rest_day_reason,
        "decision_source": decision_source,
        "selected_workout_name": selected_name,
        "selected_workout_type": selected_type,
        "rejected_alternatives": rejected_alternatives,
        "inputs": {
            "days_since_last_run": days_since,
            "days_since_last_quality": days_since_quality,
            "days_since_last_base_run": history.get("days_since_last_base_run"),
            "base_anchor_completed": history.get("base_anchor_completed"),
            "missed_anchor_days": missed_days,
            "last_run_date": last_run.get("date"),
            "last_run_name": last_run.get("name"),
            "last_run_complaint": last_run.get("has_muscular_complaint", False),
            "tsb": tsb,
            "ctl": readiness.get("ctl"),
            "atl": readiness.get("atl"),
            "sleep_hours": sleep_h,
            "sleep_score": sleep_score,
            "resting_hr": readiness.get("resting_hr_today"),
            "resting_hr_baseline": readiness.get("resting_hr_baseline"),
            "slot_id": slot_id,
            "slot_name": slot_name,
            "open_coach_cycle_id": open_cid,
            "open_coach_target_type": open_type,
            "open_coach_recommendation": open_rec_text,
        },
        "criteria_evaluated": criteria,
        "active_warnings": active_warnings,
        "reasoning_summary": reasoning_summary,
    }


def append_decision_trace(trace_entry: Dict[str, Any], trace_file: Path) -> None:
    """Appends a structured decision trace record as a single JSON line to trace_file."""
    try:
        trace_file.parent.mkdir(parents=True, exist_ok=True)
        with open(trace_file, "a", encoding="utf-8") as f:
            f.write(json.dumps(trace_entry, ensure_ascii=False) + "\n")
    except Exception:
        pass


def load_decision_traces(trace_file: Optional[Path], limit: int = 5) -> List[Dict[str, Any]]:
    """Loads the most recent `limit` decision trace entries from a JSONL file."""
    if not trace_file or not trace_file.is_file():
        return []
    entries: List[Dict[str, Any]] = []
    try:
        with open(trace_file, "r", encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    entries.append(json.loads(line))
                except Exception:
                    continue
    except Exception:
        return []
    return entries[-limit:] if limit > 0 else entries


def format_decision_trace_report(
    live_trace: Dict[str, Any],
    ai_summary: Optional[Dict[str, Any]] = None,
    logged_traces: Optional[List[Dict[str, Any]]] = None,
    history_only: bool = False,
) -> str:
    """Formats a human-readable CLI report showing Coach thinking, live criteria, and logged traces."""
    lines: List[str] = []
    lines.append("=" * 75)
    lines.append("  COACH DECISION TRACE & KRITERIEN-ANALYSE")
    lines.append("=" * 75)

    if not history_only:
        # 1. Macro AI Coach State (What the Coach thinks)
        lines.append("\n[1] WAS DENKT DER MAKRO-COACH? (coach_history.json & ai_coach_summary.json)")
        lines.append("-" * 75)
        inputs = live_trace.get("inputs") or {}
        if ai_summary and isinstance(ai_summary, dict):
            status_title = ai_summary.get("status_title", "-")
            status_badge = ai_summary.get("status_badge", "-")
            hist_eval = ai_summary.get("history_evaluation") or {}
            lines.append(f"  Coach-Status       : {status_title} [{status_badge}]")
            if hist_eval.get("actual_summary"):
                lines.append(f"  Rückblick (IST)    : {hist_eval.get('actual_summary')}")
            if hist_eval.get("effect_analysis"):
                lines.append(f"  Physiol. Effekt    : {hist_eval.get('effect_analysis')}")
            if hist_eval.get("next_recommendation"):
                lines.append(
                    f"  Nächste Direktive  : [{hist_eval.get('next_workout_type', '-')}, "
                    f"max {hist_eval.get('next_target_hr', '-')} bpm] {hist_eval.get('next_recommendation')}"
                )
        elif inputs.get("open_coach_cycle_id"):
            lines.append(
                f"  Offener Zyklus     : {inputs.get('open_coach_cycle_id')} "
                f"(Ziel-Typ: {inputs.get('open_coach_target_type')})"
            )
            lines.append(f"  Vorgabe            : {inputs.get('open_coach_recommendation')}")
        else:
            lines.append("  Keine gespeicherte Makro-Coach-Zusammenfassung gefunden.")

        # 2. Live Criteria Evaluation
        ref_d = live_trace.get("reference_date")
        tgt_d = live_trace.get("target_date")
        lines.append(f"\n[2] ENTSCHEIDUNGS-KRITERIEN (Stichtag: {ref_d} -> Planungsziel: {tgt_d})")
        lines.append("-" * 75)
        for c in live_trace.get("criteria_evaluated") or []:
            status = c.get("status", "ok")
            badge = "[OK]     " if status == "ok" else "[EINGRIFF]"
            lines.append(f"  {badge} {c.get('criterion')}")
            lines.append(f"             -> {c.get('detail')}")

        # 3. Rejected Alternatives & Selected Workout
        lines.append("\n[3] RESULTIERENDE PLANUNGS-ENTSCHEIDUNG")
        lines.append("-" * 75)
        rejected = live_trace.get("rejected_alternatives") or []
        if rejected:
            for rej in rejected:
                lines.append(f"  ❌ Verworfen       : {rej.get('workout')}")
                lines.append(f"     Grund           : {rej.get('reason')}")
        lines.append(
            f"  ✅ Gewähltes Plan  : {live_trace.get('selected_workout_name')} "
            f"(Typ: {live_trace.get('selected_workout_type')}, Quelle: {live_trace.get('decision_source')})"
        )
        lines.append(f"  💡 Begründung      : {live_trace.get('reasoning_summary')}")

    # 4. Historical Trace Log
    lines.append("\n[4] HISTORISCHES ENTSCHEIDUNGS-LOG (decision_trace.jsonl)")
    lines.append("-" * 75)
    traces = logged_traces or []
    if not traces:
        lines.append("  Noch keine historischen Trace-Einträge in decision_trace.jsonl vorhanden.")
    else:
        for entry in traces:
            ts = str(entry.get("timestamp") or "")[:19].replace("T", " ")
            trig = entry.get("trigger", "plan")
            tgt = entry.get("target_date", "-")
            w_name = entry.get("selected_workout_name", "-")
            src = entry.get("decision_source", "-")
            reason = entry.get("reasoning_summary", "")
            lines.append(f"  • [{ts}] Trigger: {trig} | Ziel: {tgt}")
            lines.append(f"    Entscheidung: {w_name} ({src})")
            if reason:
                lines.append(f"    Begründung  : {reason}")

    lines.append("=" * 75)
    return "\n".join(lines)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Inspect Coach thinking, evaluated decision criteria, and historical decision traces."
    )
    parser.add_argument("--activities-file", help="Path to intervals_activities.json")
    parser.add_argument("--wellness-file", help="Path to intervals_wellness.json")
    parser.add_argument("--athlete-config", help="Path to athlete.json")
    parser.add_argument("--history-file", help="Path to coach_history.json")
    parser.add_argument("--ai-summary-file", help="Path to ai_coach_summary.json")
    parser.add_argument("--trace-file", help="Path to decision_trace.jsonl")
    parser.add_argument("--ref-date", help="Reference date (YYYY-MM-DD, defaults to today)")
    parser.add_argument("--target-date", help="Target planning date (YYYY-MM-DD, defaults to ref-date)")
    parser.add_argument("--limit", type=int, default=5, help="Number of historical trace entries to show")
    parser.add_argument("--history-only", action="store_true", help="Only display logged entries from decision_trace.jsonl")
    parser.add_argument("--json", action="store_true", help="Output structured JSON")
    args = parser.parse_args()

    ref_d = datetime.date.fromisoformat(args.ref_date) if args.ref_date else datetime.date.today()
    target_d = datetime.date.fromisoformat(args.target_date) if args.target_date else ref_d

    act_path = Path(args.activities_file) if args.activities_file else (PROJECT_ROOT / "athletes" / "berni" / "intervals_activities.json")
    well_path = Path(args.wellness_file) if args.wellness_file else (act_path.parent / "intervals_wellness.json")
    prof_path = Path(args.athlete_config) if args.athlete_config else (act_path.parent / "athlete.json")
    hist_path = Path(args.history_file) if args.history_file else (act_path.parent / "coach_history.json")
    sum_path = Path(args.ai_summary_file) if args.ai_summary_file else (act_path.parent / "ai_coach_summary.json")
    trace_path = Path(args.trace_file) if args.trace_file else (act_path.parent / "decision_trace.jsonl")

    markers = extract_planning_markers(
        activities_file=act_path,
        wellness_file=well_path,
        profile_file=prof_path,
        history_file=hist_path,
        ref_date=ref_d,
        target_date=target_d,
        project_root=PROJECT_ROOT,
    )
    live_trace = evaluate_workout_decision(
        markers=markers,
        trigger="cli_inspect",
    )
    ai_summary = load_json_file(sum_path) if sum_path.is_file() else None
    logged_traces = load_decision_traces(trace_path, limit=args.limit)

    if args.json:
        output = {
            "live_evaluation": live_trace,
            "macro_coach_summary": ai_summary,
            "logged_traces": logged_traces,
        }
        print(json.dumps(output, indent=2, ensure_ascii=False))
        return

    report = format_decision_trace_report(
        live_trace=live_trace,
        ai_summary=ai_summary,
        logged_traces=logged_traces,
        history_only=args.history_only,
    )
    print(report)


if __name__ == "__main__":
    main()
