# Spezifikation: Die 4 Phasen der Lauf-Bewertung & Feedback-Schleife

**Dokument:** `docs/plans/4_phasen_laufanalyse_spezifikation.md`  
**Status:** Definiert & Freigegeben zur Implementierung  
**Bezug:** Ergänzt [4_stage_run_review_and_adaptive_feedback.md](file:///home/berni/laufen/docs/plans/4_stage_run_review_and_adaptive_feedback.md)

---

## Übersicht des 4-Phasen-Modells

Jeder absolvierte Lauf wird künftig nach genau diesen **vier aufeinander aufbauenden Phasen** ausgewertet:

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                    DIE 4 PHASEN DER LAUF-ANALYSE                            │
├─────────────────────────────────────────────────────────────────────────────┤
│  Phase 1: Lauf-Bewertung (Objektive Physiologie)                            │
│  - Reine Fakten: Distanz, Dauer, Pace, Ø/Max Puls, Zonenverteilung (Z1–Z7)  │
│  - Physiologische Reaktion: Kardiovaskulärer Drift, Sympathikustonus, EF    │
│                                                                             │
│  Phase 2: Plan-Erfüllung (Soll vs. Ist Abgleich)                            │
│  - Welcher Slot / welches Training war geplant? (Name, Slot, paired_event)  │
│  - Status: Erfüllt, Teilweise erfüllt oder Abweichend                       │
│  - Bewertung des Hauptreizes (z. B. Z4-Zeit bei Schwelle statt Z3-Kritik)   │
│                                                                             │
│  Phase 3: Vergleich & Optimierungspotenzial (Actionable Insights)           │
│  - Was lief gut, wo gab es Reibungspunkte?                                  │
│  - Konkrete Hebel: Pacing am Intervallstart, Gehpausen bei Puls-Drift       │
│  - Alternative Trainingsvorschläge bei Überlastung/Drift (z. B. 5x 4m)      │
│                                                                             │
│  Phase 4: Nächster Lauf (Ausblick & Regenerationssteuerung)                 │
│  - Erholungsbedarf: Ruhezeit (24–48h) basierend auf Load & TSB              │
│  - Konkrete Vorgabe für die nächste chronologische Einheit (z. B. Slot 3)   │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Detail-Spezifikation der 4 Phasen

### Phase 1: Lauf-Bewertung (Physiologische Daten)
* **Ziel:** Eine völlig neutrale, faktenbasierte Bestandsaufnahme der körperlichen Beanspruchung.
* **Inhalte:**
  * **Kennzahlen:** Distanz, Netto-Laufzeit, Durchschnitts-Pace, Höhenmeter, Training Load.
  * **Herzfrequenz & Zonen:** Durchschnitts- und Maximalpuls, sekundengenaue Aufteilung über alle Zonen ($Z1$ bis $Z7$).
  * **Physiologischer Befund:** Analyse des autonomen Nervensystems (Sympathikusaktivität), kardiovaskulärer Drift (Entkopplung von Pace und Puls) sowie Schrittfrequenz (Kadenz).

### Phase 2: Plan-Erfüllung (Soll vs. Ist)
* **Ziel:** Prüfung, ob die Einheit ihren beabsichtigten Trainingszweck erfüllt hat.
* **Ermittlung des Plans:**
  1. Verknüpfter Kalendereintrag (`paired_event_id` aus Intervals.icu).
  2. Wochentags-Slot aus `config/athlete.json` (Slot 1 Tempo, Slot 2 Flexibel, Slot 3 Basis).
  3. Semantischer Abgleich über Titel/Keywords (`Schwellenlauf`, `Intervalle`, `Easy Run`).
* **Erfolgsmaßstab:**
  * **Schwellenlauf / Intervalle:** Maßgeblich ist die effektive Reizzeit in Zone 4 ($\ge 12\text{ min}$). Längere Verweildauer in Zone 3 beim Auslaufen oder Traben wird als *physiologischer Nachbrenneffekt* gewertet und nicht als Disziplinfehler abgestraft.
  * **Grundlagenlauf (Z2):** Maßgeblich ist das Einhalten der Obergrenze ($< 158\text{ bpm}$) und $\ge 80\%$ Z1/Z2-Anteil.

### Phase 3: Vergleich & Optimierungspotenzial
* **Ziel:** Konstruktive, direkt umsetzbare Tipps für die nächste Durchführung.
* **Standard-Hebel:**
  * **Pacing bei Intervallbeginn:** Die ersten 2 Minuten starr nach Ziel-Pace laufen und nicht auf den trägen Puls warten.
  * **Erholungstechnik:** Bei verharrendem Puls über $160\text{ bpm}$ konsequent 60–90 Sekunden strammes Gehen („Power Walk“) einlegen, um den Parasympathikus zu aktivieren.
  * **Alternative Trainingsformate:** Vorschlag modularer Alternativen (z. B. $5 \times 4\text{ min}$ Norwegian Micro-Threshold statt $3 \times 8\text{ min}$), falls lange Blöcke noch zu viel thermische Drift erzeugen.

### Phase 4: Nächster Lauf (Ausblick & Regeneration)
* **Ziel:** Nahtloser Übergang in die Wochensteuerung und Schutz vor Übertraining.
* **Inhalte:**
  * **Erholungsfenster:** Klare Stundenangabe (z. B. 48h nach Schwellenreiz, 24h nach lockerem Z2-Lauf).
  * **Folge-Slot:** Konkrete Vorgabe für die nächste Einheit laut Wochenplan (z. B. *„Sonntag: Slot 3 – 50m Z2 Long Run < 158 bpm, Pace ignorieren“*).

---

## Konkretes Beispiel: Wien - Schwellenlauf (7 km)

```markdown
### 1. Lauf-Bewertung (Physiologische Daten)
* **Kennzahlen:** 7,15 km in 49:56 min (Ø Pace: 6:59 min/km) | Training Load: 58
* **Puls & Zonen:** Ø 159 bpm | Max 176 bpm
  * Zone 1 & 2 (Aerob): 24:50 min (49,7 %)
  * Zone 3 (Tempo/Drift): 11:22 min (22,7 %)
  * Zone 4 (Schwelle): 13:47 min (27,6 %)
* **Physiologischer Befund:** Effektiver Schwellenreiz mit knapp 14 Minuten in Zone 4 nahe LTHR (178 bpm). Starker kardiovaskulärer Drift und verzögerte Herzfrequenz-Erholung: Der Puls verharrte nach den Tempoblöcken auch bei 7:30 min/km und in Gehpausen bei 160+ bpm. Schrittfrequenz: Ø 153 spm.

### 2. Plan-Erfüllung (Soll vs. Ist)
* **Geplanter Slot:** Slot 2 (Wochenmitte) – Schwellenoption (3x 8m Z4).
* **Status:** Erfüllt.
* **Abgleich:** Das Hauptziel (mindestens 12–15 Min wirksamer Z4-Reiz zur Anhebung der Laktatschwelle) wurde mit 13:47 min voll erreicht. Der anschließende Z3-Anteil ist die physiologische Folge des Reizes und kein Disziplinfehler.

### 3. Vergleich & Optimierungspotenzial
* **Pacing:** Nicht auf den Puls warten! Die ersten 2 Minuten starr auf Ziel-Pace (~05:50/km) anlaufen; der Puls zieht nach 2–3 Minuten von selbst in Z4 nach.
* **Erholung:** Bleibt der Puls nach dem Tempo oben, 2:30 min zügig gehen statt verkrampft langsam traben.
* **Alternative Formate:** Falls 3x 8m am Stück zu viel Drift erzeugt, bieten sich 5x 4m Schwellenintervalle (mit 90s Pause) als elegantere Alternative mit gleicher Netto-Reizzeit an.

### 4. Nächster Lauf (Ausblick & Regeneration)
* **Erholungsstatus:** Deutliche Beanspruchung der Glykogenspeicher (TSB: -2.9). Nächste 48 Stunden keine Tempoeinheiten.
* **Nächste Einheit:** Slot 3 am Wochenende – Aerober Anker & Long Run (45–50 min strikt in Zone 2 < 158 bpm).
```

---

## Datenmodell im Review-Cache (`data/run_reviews.json`)

```json
{
  "id": "i200612231",
  "review": {
    "schema_version": 2,
    "rating": "intensiv",
    "rating_label": "Gezielter Schwellenlauf (Plan erfüllt)",
    "summary": "Solider Schwellenreiz mit 13m 47s in Zone 4...",
    "coach_tip": "...",
    "phases": {
      "phase1_physiology": {
        "distance_km": 7.15,
        "moving_time_s": 2996,
        "avg_hr": 159.0,
        "max_hr": 176,
        "z4_seconds": 827,
        "findings": "Effektiver Schwellenreiz, ausgeprägter Nachbrenneffekt..."
      },
      "phase2_compliance": {
        "planned_slot": "Slot 2 (Schwellenlauf)",
        "status": "erfüllt",
        "target_comparison": "13m 47s in Z4 (Ziel: >= 12m). Z3-Anteile waren Drift, kein Fehler."
      },
      "phase3_optimizations": [
        "Intervallstart nach Zielpace steuern",
        "Gehpausen bei Puls-Drift nutzen",
        "Alternative: 5x 4m Micro-Threshold"
      ],
      "phase4_next_run": {
        "recovery_hours": 48,
        "next_slot": "Slot 3 (Aerober Anker)",
        "directive": "50m Z2 Dauerlauf (< 158 bpm)"
      }
    }
  }
}
```
