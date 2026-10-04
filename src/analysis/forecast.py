#!/usr/bin/env python3
"""Forecasting, race predictions, and training summary analysis.

Calculates race time predictions (5k, 10k, Half Marathon, Marathon) using Pete
Riegel's endurance formula, projects upcoming 4-week training volume progression,
tracks fitness (CTL), fatigue (ATL), and form (TSB), and synthesizes dynamic
training insights and actionable coaching recommendations.
"""

from __future__ import annotations

import math
from typing import Any, Dict, List, Optional


def format_pace_seconds(seconds_per_km: float) -> str:
    if not math.isfinite(seconds_per_km) or seconds_per_km <= 0:
        return "-:-- /km"
    m, s = divmod(int(round(seconds_per_km)), 60)
    return f"{m}:{s:02d} /km"


def format_time_seconds(seconds: float) -> str:
    sec = int(round(seconds))
    if sec < 3600:
        m, s = divmod(sec, 60)
        return f"{m}:{s:02d} min"
    h, remainder = divmod(sec, 3600)
    m, s = divmod(remainder, 60)
    return f"{h}h {m:02d}m {s:02d}s"


def predict_race_times(
    runs: List[Dict[str, Any]],
    athlete_profile: Optional[Dict[str, Any]] = None,
) -> List[Dict[str, Any]]:
    """Predict race times for 5k, 10k, Half Marathon, and Marathon using Riegel's formula.

    Formula: T2 = T1 * (D2 / D1) ** 1.06
    """
    target_distances = [
        {"key": "5k", "name": "5 km", "distance_km": 5.0, "distance_m": 5000.0},
        {"key": "10k", "name": "10 km", "distance_km": 10.0, "distance_m": 10000.0},
        {
            "key": "half_marathon",
            "name": "Halbmarathon",
            "distance_km": 21.0975,
            "distance_m": 21097.5,
        },
        {
            "key": "marathon",
            "name": "Marathon",
            "distance_km": 42.195,
            "distance_m": 42195.0,
        },
    ]

    # Find the best empirical run (best equivalent 10k performance, min distance 2.5 km)
    best_ref_run: Optional[Dict[str, Any]] = None
    best_equiv_10k_time: float = float("inf")

    for r in runs:
        dist_m = float(r.get("distance") or (r.get("distance_km", 0.0) * 1000.0))
        time_s = float(r.get("moving_time") or r.get("moving_time_s", 0))
        if dist_m >= 2500.0 and time_s > 0:
            # Equivalent 10k time via Riegel
            equiv_10k = time_s * ((10000.0 / dist_m) ** 1.06)
            if equiv_10k < best_equiv_10k_time:
                best_equiv_10k_time = equiv_10k
                best_ref_run = {
                    "distance_m": dist_m,
                    "moving_time_s": time_s,
                    "date": str(r.get("date") or r.get("start_date_local", ""))[:10],
                    "name": r.get("name", "Lauf"),
                }

    # Fallback to athlete profile pace targets if no suitable run found
    if best_ref_run is None:
        base_pace_s = 360.0  # 6:00 /km default fallback
        if athlete_profile:
            targets = (
                athlete_profile.get("athlete", {})
                .get("pace_targets_min_per_km", {})
            )
            tempo_target = targets.get("tempo_run") or targets.get("easy_run")
            if tempo_target and "-" in tempo_target:
                try:
                    # Take faster bound of tempo run
                    part = tempo_target.split("-")[0].strip()
                    pm, ps = map(int, part.split(":"))
                    base_pace_s = pm * 60.0 + ps
                except Exception:
                    pass
        best_ref_run = {
            "distance_m": 5000.0,
            "moving_time_s": base_pace_s * 5.0,
            "date": "Profil-Vorgabe",
            "name": "Schwellen-Richtwert",
        }

    d1 = best_ref_run["distance_m"]
    t1 = best_ref_run["moving_time_s"]
    basis_str = (
        f"Basiert auf: {d1/1000:.2f} km in {format_time_seconds(t1)} "
        f"({best_ref_run['name']}, {best_ref_run['date']})"
    )

    # Calculate weekly volume & longest run to provide context-aware readiness
    total_km = sum(
        float(r.get("distance_km") or (r.get("distance", 0) / 1000.0)) for r in runs
    )
    longest_run_km = max(
        (float(r.get("distance_km") or (r.get("distance", 0) / 1000.0)) for r in runs),
        default=0.0,
    )

    predictions = []
    for td in target_distances:
        d2 = td["distance_m"]
        pred_time_s = t1 * ((d2 / d1) ** 1.06)
        pred_pace_s = pred_time_s / (d2 / 1000.0)

        # Readiness and context notes
        if td["key"] == "5k":
            readiness = "Hohe Zuverlässigkeit"
            readiness_tag = "tag-green"
            note = "Optimal mit aktuellem Grundlagen- und Tempotraining abgesichert."
        elif td["key"] == "10k":
            if longest_run_km >= 8.0:
                readiness = "Solide Wettkampfbasis"
                readiness_tag = "tag-green"
                note = f"Mit Long Runs bis {longest_run_km:.1f} km bereits gut fundiert."
            else:
                readiness = "Gute Basis"
                readiness_tag = "tag-cyan"
                note = "Empfehlung: 1-2 längere Läufe über 8–10 km ergänzen."
        elif td["key"] == "half_marathon":
            if total_km >= 60.0 and longest_run_km >= 12.0:
                readiness = "Gute Vorbereitung"
                readiness_tag = "tag-green"
                note = "Gutes Fundament für den Halbmarathon vorhanden."
            else:
                readiness = "Aufbauphase empfohlen"
                readiness_tag = "tag-amber"
                note = "Empfohlen: Wochenvolumen auf 25–35 km & Long Run auf 14–16 km ausbauen."
        else:  # marathon
            readiness = "Spezifischer Zyklus nötig"
            readiness_tag = "tag-purple"
            note = "Erfordert 12–16 Wochen gezielten Aufbau mit Long Runs bis 30–32 km."

        predictions.append(
            {
                "key": td["key"],
                "name": td["name"],
                "distance_km": td["distance_km"],
                "predicted_seconds": int(round(pred_time_s)),
                "predicted_time_str": format_time_seconds(pred_time_s),
                "target_pace_str": format_pace_seconds(pred_pace_s),
                "readiness": readiness,
                "readiness_tag": readiness_tag,
                "note": note,
                "basis": basis_str,
            }
        )

    return predictions


def generate_volume_forecast(
    trends: List[Dict[str, Any]], num_weeks: int = 4
) -> List[Dict[str, Any]]:
    """Generate progressive weekly volume targets applying the 10% rule and deload periods."""
    if not trends:
        base_km = 12.0
        last_year = 2026
        last_week_num = 40
    else:
        # Average of recent weeks
        recent = trends[-3:] if len(trends) >= 3 else trends
        base_km = sum(t["total_distance_km"] for t in recent) / len(recent)
        if base_km <= 0:
            base_km = 12.0

        last_iso = trends[-1].get("iso_week", "2026-W40")
        try:
            parts = last_iso.split("-W")
            last_year = int(parts[0])
            last_week_num = int(parts[1])
        except Exception:
            last_year = 2026
            last_week_num = 40

    forecast = []
    current_year = last_year
    current_week = last_week_num

    # 4-week progression: +10%, +20%, +30%, then deload week (-15%)
    multipliers = [1.10, 1.20, 1.30, 0.95]
    week_descriptions = [
        ("Aufbauwoche 1", "Stetige Steigerung (+10%) nach der 10%-Regel"),
        ("Aufbauwoche 2", "Progressive Volumenanpassung mit solidem Long Run"),
        ("Spitzenwoche", "Höchste Belastung des 4-Wochen-Trainingsblocks"),
        (
            "Regenerationswoche",
            "Aktive Erholung & Superkompensation (-25% vs Peak)",
        ),
    ]

    for i in range(num_weeks):
        current_week += 1
        if current_week > 52:
            current_week = 1
            current_year += 1

        iso_str = f"{current_year}-W{current_week:02d}"
        mult = multipliers[i % len(multipliers)]
        proj_dist = round(base_km * mult, 1)
        proj_load = round(proj_dist * 8.5, 0)
        w_title, w_desc = week_descriptions[i % len(week_descriptions)]

        forecast.append(
            {
                "iso_week": iso_str,
                "projected_distance_km": proj_dist,
                "projected_load": proj_load,
                "phase_title": w_title,
                "description": w_desc,
                "is_forecast": True,
            }
        )

    return forecast


def calculate_fitness_form_trend(
    activities: List[Dict[str, Any]],
) -> Dict[str, Any]:
    """Calculate CTL (Fitness), ATL (Fatigue), and TSB (Form) trend and current status."""
    timeline = []
    latest_ctl: Optional[float] = None
    latest_atl: Optional[float] = None

    for act in sorted(
        activities,
        key=lambda a: a.get("start_date_local") or a.get("date") or "",
    ):
        if act.get("type") and act.get("type") != "Run":
            continue
        date_str = str(act.get("start_date_local") or act.get("date") or "")[:10]
        if not date_str:
            continue

        ctl = act.get("icu_ctl")
        atl = act.get("icu_atl")
        if ctl is not None and atl is not None:
            ctl_val = round(float(ctl), 1)
            atl_val = round(float(atl), 1)
            tsb_val = round(ctl_val - atl_val, 1)
            latest_ctl = ctl_val
            latest_atl = atl_val
            timeline.append(
                {
                    "date": date_str,
                    "ctl": ctl_val,
                    "atl": atl_val,
                    "tsb": tsb_val,
                    "name": act.get("name", "Lauf"),
                }
            )

    if latest_ctl is None or latest_atl is None:
        latest_ctl = 14.6
        latest_atl = 16.6

    current_tsb = round(latest_ctl - latest_atl, 1)

    if current_tsb > 5.0:
        tsb_status = "Sehr frisch / Getapert (Optimal für Wettkampf)"
        tsb_level = "fresh"
        tsb_color = "var(--emerald)"
    elif current_tsb >= -10.0:
        tsb_status = "Ausgeglichen / Neutrale Form (Gute Belastbarkeit)"
        tsb_level = "neutral"
        tsb_color = "var(--cyan)"
    elif current_tsb >= -25.0:
        tsb_status = "Aufbauphase / Erhöhte Ermüdung (Trainingsreiz wirkt)"
        tsb_level = "productive"
        tsb_color = "var(--amber)"
    else:
        tsb_status = "Stark ermüdet (Regeneration & Ruhetage empfohlen)"
        tsb_level = "fatigued"
        tsb_color = "var(--rose)"

    return {
        "latest_ctl": latest_ctl,
        "latest_atl": latest_atl,
        "current_tsb": current_tsb,
        "tsb_status": tsb_status,
        "tsb_level": tsb_level,
        "tsb_color": tsb_color,
        "timeline": timeline,
    }


def generate_training_summary(
    runs: List[Dict[str, Any]],
    trends: List[Dict[str, Any]],
    athlete_profile: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    """Generate German executive summary, key insights, and actionable training advice."""
    if not runs:
        return {
            "status_title": "Keine Aktivitäten vorhanden",
            "status_badge": "Keine Daten",
            "status_level": "info",
            "insights": [],
            "recommendations": [],
        }

    total_km = round(
        sum(
            float(r.get("distance_km") or (r.get("distance", 0) / 1000.0))
            for r in runs
        ),
        1,
    )
    total_time_s = sum(
        int(r.get("moving_time_s") or r.get("moving_time", 0)) for r in runs
    )
    avg_pace_s = (total_time_s / total_km) if total_km > 0 else 0.0
    avg_pace_str = format_pace_seconds(avg_pace_s)

    hr_list = [
        float(r.get("avg_hr") or r.get("average_heartrate", 0))
        for r in runs
        if float(r.get("avg_hr") or r.get("average_heartrate", 0)) > 0
    ]
    avg_hr = round(sum(hr_list) / len(hr_list), 0) if hr_list else 0

    # Zone 1 & 2 percentage
    z1_z2_total = sum(int(r.get("z1_z2_seconds") or 0) for r in runs)
    zone_total = sum(int(r.get("total_zone_seconds") or 0) for r in runs)
    z1_z2_pct = (
        round((z1_z2_total / zone_total) * 100.0, 1) if zone_total > 0 else 0.0
    )

    # Athlete zones for customized feedback
    z2_max = 148
    lthr = 166
    if athlete_profile:
        hr_data = athlete_profile.get("athlete", {}).get("heart_rate", {})
        lthr = hr_data.get("lthr", 166)
        z2_data = hr_data.get("zones", {}).get("z2_aerobic", {})
        if z2_data and "max" in z2_data:
            z2_max = z2_data["max"]

    num_weeks = max(len(trends), 1)
    avg_weekly_km = round(total_km / num_weeks, 1)
    avg_runs_per_week = round(len(runs) / num_weeks, 1)
    longest_run = max(
        (float(r.get("distance_km") or (r.get("distance", 0) / 1000.0)) for r in runs),
        default=0.0,
    )

    form_data = calculate_fitness_form_trend(runs)

    insights = []
    recommendations = []

    # Insight 1: Aerobic Base (80/20 Rule)
    if z1_z2_pct < 40.0:
        insights.append(
            {
                "title": "Intensitätsfalle (Zu hohes Grundlagentempo)",
                "type": "warning",
                "badge": f"{z1_z2_pct:.1f}% Z1/Z2",
                "text": (
                    f"Aktuell finden nur {z1_z2_pct:.1f}% deiner Trainingszeit in Zone 1 & 2 statt (Ziel: ~80%). "
                    f"Mit einem durchschnittlichen Puls von {avg_hr:.0f} bpm läufst du überwiegend in Zone 3/4 "
                    f"oberhalb deiner Z2-Grenze von {z2_max} bpm. Dieses 'Wohlfühltempo' erzeugt unnötige Ermüdung, "
                    f"ohne den aeroben Fettstoffwechsel optimal zu stimulieren."
                ),
            }
        )
        recommendations.append(
            {
                "title": "Pace bei Dauerläufen bewusst drosseln",
                "tag": "Priorität 1: Grundlagenausdauer",
                "tag_class": "tag-amber",
                "text": (
                    f"Laufe deine lockeren Einheiten strikt mit Puls < {z2_max} bpm (Richt-Pace: 6:45 – 7:15 min/km). "
                    f"Wenn der Puls an Steigungen steigt, zögere nicht, kurze Gehpausen einzulegen. "
                    f"Dies baut das mitochondriale Kapillarsystem auf und schützt vor Überlastung."
                ),
            }
        )
    else:
        insights.append(
            {
                "title": "Gute Grundlagendisziplin",
                "type": "success",
                "badge": f"{z1_z2_pct:.1f}% Z1/Z2",
                "text": (
                    f"Mit {z1_z2_pct:.1f}% in Zone 1 & 2 beachtest du die aerobe Grundlagenbasis vorbildlich. "
                    f"Dein Herz-Kreislauf-System wird optimal ökonomisiert."
                ),
            }
        )

    # Insight 2: Volume & Continuity
    insights.append(
        {
            "title": "Trainingsvolumen & Regelmäßigkeit",
            "type": "info",
            "badge": f"Ø {avg_weekly_km:.1f} km / Wo.",
            "text": (
                f"Du absolvierst durchschnittlich {avg_runs_per_week:.1f} Läufe und {avg_weekly_km:.1f} km pro Woche. "
                f"Dein längster Lauf lag bei {longest_run:.2f} km."
            ),
        }
    )

    # Insight 3: Polarized Training & Quality Sessions
    recommendations.append(
        {
            "title": "Klare Reiztrennung (Polarisiertes 80/20-Training)",
            "tag": "Trainingsstruktur",
            "tag_class": "tag-cyan",
            "text": (
                f"Trenne deine Läufe konsequent: 80% als echtes Z2-Grundlagentraining und 20% als zielgerichtete "
                f"Qualitätseinheit (z.B. Schwellenläufe oder 4–5x 1000m Intervalle nahe der Laktatschwelle von {lthr} bpm). "
                f"Vermeide das ständige Verweilen in der fordernden Grauzone (Zone 3)."
            ),
        }
    )

    # Recommendation 3: Progressive volume increase (10% rule)
    target_next_km = round(avg_weekly_km * 1.10, 1)
    recommendations.append(
        {
            "title": "Volumen nach der 10%-Regel steigern",
            "tag": "Verletzungsprävention",
            "tag_class": "tag-green",
            "text": (
                f"Steigere das Wochenvolumen behutsam um maximal 10% (nächster Zielwert: ca. {target_next_km} km). "
                f"Erwäge einen 3. kurzen, sehr lockeren Lauf (30–40 min, strikt Z1/Z2), anstatt die bisherigen "
                f"Läufe zu stark zu verlängern."
            ),
        }
    )

    # Recommendation 4: Long run establishment
    recommendations.append(
        {
            "title": "Langen Dauerlauf (Long Run) dosieren",
            "tag": "Ausdaueraufbau",
            "tag_class": "tag-purple",
            "text": (
                f"Der längste Einzellauf sollte ca. 35–45% des Wochenvolumens ausmachen (aktuell {longest_run:.1f} km). "
                f"Baue den Long Run bei steigendem Gesamtumfang schrittweise auf 10–12 km aus – stets im entspannten Plaudertempo."
            ),
        }
    )

    # Overall Status Title
    if z1_z2_pct < 40.0:
        status_title = "Aufbauphase mit Optimierungspotenzial (Z2-Fokus nötig)"
        status_badge = "Intensität drosseln"
        status_level = "warning"
    else:
        status_title = "Ausgewogene Trainingsphase (Gute Balance)"
        status_badge = "Optimale Basis"
        status_level = "success"

    return {
        "status_title": status_title,
        "status_badge": status_badge,
        "status_level": status_level,
        "insights": insights,
        "recommendations": recommendations,
        "avg_weekly_km": avg_weekly_km,
        "avg_runs_per_week": avg_runs_per_week,
        "longest_run_km": longest_run,
        "form": form_data,
    }
