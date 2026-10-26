# Spezifikation: Marker-basierte adaptive Trainingsplanung (4-Phasen-Modell)

**Dokument:** `docs/plans/marker_basierte_trainingsplanung_spezifikation.md`  
**Status:** Vollständig umgesetzt & verifiziert (Erledigt am 26.10.2026)  
**Bezug:** Ergänzt [4_phasen_laufanalyse_spezifikation.md](file:///home/berni/laufen/docs/plans/4_phasen_laufanalyse_spezifikation.md) & löst die starre `if/else`-Entscheidungslogik in `src/planner/schedule_workout.py` ab.

---

## 1. Problemstellung & Paradigmenwechsel

### Das Problem (Status Quo)
Bislang enthielt das Skript `schedule_workout.py` eine naive, prozedurale Verzweigung:
```python
# VERALTETE LOGIK (Problembehaftet)
if last_was_quality:
    return "Aerober Anker & Grundlagenlauf (45m Z2)"
else:
    return "Kontrollierter Schwellenlauf (3x 8m Z4)"
```
**Die Schwachstellen:**
1. **Blindheit gegenüber Vorbelastung:** Betrachtet nur $n-1$. War der Lauf vor 4 Tagen eine harte Schwelle und gestern ein 9-km-Dauerlauf, feuert das Skript sofort die nächste Schwelle ab.
2. **Ignorieren von Regeneration & Wellness:** TSB (-6.8), Schlafmangel (5h 44m) und HRV fließen nicht in die Workout-Auswahl ein.
3. **Missachtung der Slot-Architektur:** Slot 1 (Wochenanfang = kurze Intervalle/Kadenz) wird mit Slot 2 (Wochenmitte = Schwelle) verwechselt.

### Das neue Paradigma: Engine liefert Fakten, Agent fällt Urteil
* **Python-Code (Engine / CLI):** Sammelt deterministisch Daten, berechnet physiologische Kennzahlen und exportiert ein strukturiertes **Marker-Objekt** (JSON). Keine Trainingsentscheidungen im Python-Code!
* **KI-Agent (Coach / LLM):** Liest die Marker, versteht die sportwissenschaftlichen Zusammenhänge, wägt Kontext ab und entscheidet über den konkreten Trainingsreiz.

```
┌─────────────────────────────────────────────────────────────────────────────┐
│             DAS 4-PHASEN-MODELL DER TRAININGSPLANUNG                        │
├─────────────────────────────────────────────────────────────────────────────┤
│  Phase 1: Marker- & Telemetrie-Extraktion (Python Engine)                   │
│  - days_since_last_run, days_since_last_quality, last_quality_type          │
│  - TSB, ATL, CTL, Schlaf-Score, RHR-Trend, Wochentag & Ziel-Slot            │
│                                                                             │
│  Phase 2: Physiologische Leitplanken & Guardrails (Constraints)             │
│  - Mindestabstand Qualitätsreize (z. B. < 48h -> quality_discouraged)       │
│  - Ermüdungswarnungen (TSB < -10, Schlaf < 6h -> high_fatigue_warning)      │
│                                                                             │
│  Phase 3: Kognitive Entscheidung (Agentic Coach)                            │
│  - Wählt Archetyp: Ruhetag, Z2-Dauerlauf, Kadenz-Fahrtspiel oder Schwelle   │
│  - Definiert individuelle Parameter (Dauer, Intervalldauer, Pausen, HF)    │
│  - Begründet die Wahl transparent anhand der Marker                         │
│                                                                             │
│  Phase 4: DSL-Synthese & Kalender-Sync (Intervals.icu / Garmin)             │
│  - Erzeugt das strukturierte Workout in der Intervals.icu Text-DSL          │
│  - Schreibt/aktualisiert den Kalendereintrag & dokumentiert den Beschluss   │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Detaillierte Spezifikation der 4 Phasen

### Phase 1: Marker- & Telemetrie-Extraktion (Python Engine)
Die Python-Engine (`./run plan --markers` oder `src/planner/extract_markers.py`) generiert ein neutrales JSON-Objekt.

#### Schema des Marker-Payloads:
```json
{
  "reference_date": "2026-10-26",
  "target_date": "2026-10-27",
  "athlete": {
    "classification": "Hobbyläufer (Basisaufbau)",
    "z2_max_hr": 158,
    "z4_range": [169, 177],
    "lthr": 178
  },
  "readiness": {
    "tsb": -6.8,
    "atl": 20.5,
    "ctl": 13.7,
    "sleep_hours_last_night": 5.73,
    "sleep_score_last_night": 70,
    "resting_hr_today": 49,
    "resting_hr_baseline": 48.3,
    "hrv_today": 67,
    "days_since_last_run": 1
  },
  "history": {
    "days_since_last_quality": 3,
    "last_run": {
      "date": "2026-10-25",
      "type": "easy_run",
      "name": "Gerasdorf bei Wien - Aerober Anker",
      "distance_km": 9.1,
      "duration_min": 66.5,
      "avg_hr": 152,
      "load": 66,
      "z1_z2_pct": 96.6
    },
    "last_quality_run": {
      "date": "2026-10-23",
      "type": "tempo_run",
      "name": "Wien - Schwellenlauf",
      "distance_km": 7.15,
      "z4_minutes": 13.8,
      "load": 58
    },
    "weekly_runs_completed": 0,
    "target_runs_per_week": 3,
    "last_week_total_km": 20.93,
    "last_week_z1_z2_pct": 82.1
  },
  "periodization": {
    "target_day_name": "tuesday",
    "slot_id": 1,
    "slot_name": "Slot 1 (Wochenanfang)",
    "allowed_types": ["intervals", "tempo_run", "easy_run"],
    "preferred_type": "intervals",
    "slot_purpose": "Neuromuskulärer Speed, Schrittfrequenz (>165 spm) & Hügel"
  }
}
```

---

### Phase 2: Physiologische Leitplanken & Guardrails
Die Engine trifft keine Wahl, rechnet aber **Status-Flags** aus, die der Agent zwingend berücksichtigen muss:

| Guardrail-Flag | Bedingung | Auswirkung auf Agenten-Entscheidung |
| :--- | :--- | :--- |
| `quality_discouraged` | `days_since_last_quality < 3` ODER `tsb < -8` | Keine langen Schwellenblöcke (Z4). Maximal kurze Z2-Einheit oder Ruhetag. |
| `fatigue_warning` | `sleep_hours < 6.0` ODER `sleep_score < 72` | Intensitätsdeckel. Keine maximalen VO2max-Reize; Fokus auf Erhalt. |
| `rest_day_recommended` | `days_since_last_run == 0` | Pflicht-Erholungstag zwischen Trainingseinheiten. |
| `volume_spike_alert` | `last_run_duration > 60m` am Vortag | Folgetag maximal 35–40m locker oder Ruhetag. |

---

### Phase 3: Kognitive Entscheidung (Agentic Coach)
Der Agent erhält den Prompt mit dem Marker-Payload und entscheidet nach folgender sportwissenschaftlicher Systematik:

1. **Prüfung der Regenerationsampel (TSB & Schlaf):**
   * *Beispiel heute:* TSB -6.8, Schlaf 5h 44m, gestriger Lauf 9.1 km $\rightarrow$ **Gelbe Ampel**.
2. **Abgleich mit dem Ziel-Slot:**
   * Dienstag ist Slot 1. Wenn Qualität gelaufen werden soll, dann **nicht** Schwelle (da Freitag bereits Schwelle war und TSB negativ ist), sondern kurze, neuromuskuläre Kadenzen (5x 1 Min.) oder ein lockerer Z2-Dauerlauf.
3. **Auswahl & Parametrisierung:**
   * Der Agent legt fest:
     * `archetype`: z. B. `easy_run` (35 Min. @ Z2) ODER `cadence_intervals` (10m Z2 + 5x 1m flott + 10m Z1).
     * `target_hr`: `< 158 bpm`
     * `rationale`: Begründung für den Athleten.

---

### Phase 4: DSL-Synthese & Kalender-Sync (Intervals.icu / Garmin)
Sobald der Agent den Workout-Typ und die Parameter bestimmt hat, ruft er das Werkzeug zur Generierung des DSL-Codes auf:

```text
Warmup
- 10m Z1-Z2 HR

5x
- 1m 05:15/km Pace
- 2m Recovery Z1 HR

Cooldown
- 10m Z1 HR
```
* Das Workout wird via Intervals.icu API (`POST /api/v1/athlete/{id}/events`) in den Kalender geschrieben.
* Der Eintrag wird sofort automatisch auf die Garmin-Uhr synchronisiert.
* Ein Eintrag im `coach_history.json` dokumentiert die Entscheidung und die zugrundeliegenden Marker.

---

## 3. Gegenüberstellung: Alt vs. Neu am aktuellen Beispiel (26./27.10.2026)

| Dimension | Altes Skript (`schedule_workout.py`) | Neues Marker-basiertes 4-Phasen-Modell |
| :--- | :--- | :--- |
| **Logik** | `if not last_quality: schedule_3x8m_z4` | `Agent evaluiert TSB (-6.8), Schlaf (5h44m), Last Quality (vor 3d = Schwelle)` |
| **Workout für Di, 27.10.** | ❌ **3x 8m Z4 Schwellenlauf** (2. Schwelle in 4 Tagen!) |  **Lockerer 35m Z2-Lauf** ODER **5x 1m Kadenz-Spiel** (oder Ruhetag) |
| **Schlaf/Erholung** | Völlig ignoriert | Direkt als Dämpfungsfaktor berücksichtigt |
| **Periodisierung** | Wirft Z4 in Slot 1 | Hält Slot 1 frei von Z4-Dauerreizen |

---

## 4. Umsetzungsstatus & Verifikation (Abgeschlossen)

- [x] **Review & Freigabe** dieser Spezifikation durch den Benutzer.
- [x] **Engine umgesetzt:**
  - Implementierung von `src/planner/extract_markers.py` und `--markers` / `--json` Flags in `./run plan`.
  - Entfernung der primitiven `if/else`-Entscheidungslogik in `choose_adaptive_workout()`.
- [x] **Guardrails & Periodisierung:**
  - `quality_discouraged`, `fatigue_warning`, `volume_spike_alert` und `slot_allows_threshold` aktiv integriert.
- [x] **BDD-Tests (`tests/marker_planning.feature` & `tests/test_marker_planning.py`):**
  - Alle 4 neuen Szenarien erfolgreich implementiert und verifiziert (gesamte Testsuite: 96/96 Tests bestanden).
