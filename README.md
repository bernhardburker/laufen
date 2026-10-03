# Lauf- & Trainingsmanagement-Hub

Zentrales Repository zur Koordination, Analyse und Erstellung von Lauftrainings unter Anbindung von **Garmin Connect**, **Strava** und **Intervals.icu**.

---

## 1. Systemübersicht & Plattform-Rollen

| Plattform | Primäre Rolle | Relevante Schnittstellen / Protokolle |
| :--- | :--- | :--- |
| **Garmin Uhr & Garmin Connect** | Hardware-Messung (HR, Pace, GPS, Kadenz, Dynamik) & Workout-Ausführung | `garminconnect` Python API, FIT-File Download/Upload, Connect IQ |
| **Intervals.icu** | Belastungssteuerung, Trainingsplanung & Kalender (CTL/ATL/TSB) | Intervals.icu REST API, Workout-Textformat ("Warmup 10m 65% HR") |
| **Strava** | Social Platform, Segmente, Langzeit-Übersicht | Strava v3 REST API (OAuth2) |

---

## 2. Kernfokus & Ziele (Die Basics)

- **Herzfrequenz-Steuerung**:
  - Verständnis der Zonen (insbesondere **Zone 2** für den aeroben Aufbau).
  - Kontrolle der Belastung: Laufen nach Puls statt Überpacen.
- **Pace & Tempogefühl**:
  - Einordnung von Geschwindigkeiten (min/km).
  - Verhältnis von Herzfrequenz zu Pace beobachten (gleiche Pace bei niedrigerem Puls = Fitnessgewinn).
- **Trainingskoordination & Wochenstruktur**:
  - Sinnvolle Aufteilung der Einheiten über die Woche (Lauf- vs. Ruhetage).
  - Balance zwischen leichten Grundlageneinheiten und gezielten Reizen.
  - Schrittweise Steigerung des Gesamtvolumens (Faustregel: max. ~10% pro Woche).
- **Trainings erstellen & auf die Garmin-Uhr übertragen**:
  - Strukturierte Workouts per Skript/Text definieren (z. B. Einlaufen, Intervalle nach Pace/Puls, Auslaufen).
  - Sync an Garmin Connect / Intervals.icu, damit die Uhr beim Laufen Orientierung gibt (Puls-/Pace-Alarme).

---

## 3. Die Lauf-Grundlagen: Herzfrequenz & Pace

### Herzfrequenz-Zonen
Die Einteilung erfolgt meist über die maximale Herzfrequenz ($HF_{max}$) oder die Schwellenherzfrequenz ($LTHR$):

| Zone | Bezeichnung | Intensität | Zweck / Gefühl |
| :--- | :--- | :--- | :--- |
| **Z1** | Regeneration | $< 65\% HF_{max}$ | Sehr leicht, fördert Durchblutung & Erholung |
| **Z2** | Grundlagenausdauer 1 | $65\% - 75\% HF_{max}$ | **Das Fundament (60–80% des Trainings)**; lockeres Sprechen problemlos möglich ("Talk Test") |
| **Z3** | Grundlagenausdauer 2 / Tempo | $75\% - 85\% HF_{max}$ | Zügig; Sprechen nur noch in kurzen Sätzen |
| **Z4** | Schwellenbereich (Threshold) | $85\% - 92\% HF_{max}$ | "Angenehm hart"; anaerobe Schwelle, fordert mentale Härte |
| **Z5** | Maximalbereich / VO2max | $> 92\% HF_{max}$ | All-out, kurze Intervalle, Zielsprints |

### Die wichtigsten Trainingsarten
- **Lockerer Dauerlauf (Easy Run / Z2)**: Bildet Kapillaren, stärkt Sehnen und den Fettstoffwechsel. Der häufigste Fehler ist, diese Läufe zu schnell zu laufen.
- **Langer Lauf (Long Run)**: Ein Z2-Lauf mit längerer Dauer, um Ausdauer und Ermüdungswiderstand aufzubauen.
- **Intervalltraining**: Wechsel aus Belastungsphasen (z. B. 400m bis 1000m in Z4/Z5 oder Ziel-Pace) und Erholungsphasen (Gehen/Traben).
- **Tempolauf / Schwellenlauf**: Kontinuierlicher Lauf über 15–30 Minuten an der Schwellenintensität (Z3/Z4).
- **Regenerationslauf (Recovery Run)**: Sehr kurz und langsam (Z1/untere Z2) am Tag nach harten Einheiten.

### Trainingskoordination & Wochenbeispiel (3 Läufe/Woche)
- **Dienstag**: Lockerer Grundlagenlauf (Z2, 35–45 min)
- **Donnerstag**: Intervall- oder Schwellentraining (z. B. 10 min Einlaufen, 4x 3 min zügig / 2 min Trab, 10 min Auslaufen)
- **Sonntag**: Langer Grundlagenlauf (Z2, 50–70 min)
- *Montag, Mittwoch, Freitag, Samstag*: Regeneration, Spaziergänge oder leichtes Stabi-/Krafttraining

---

## 4. Workouts erstellen & auf die Uhr bringen

### Prinzip der strukturierten Workouts
Statt "einfach loszulaufen" können Einheiten vorgeplant werden:
1. **Warmup**: z. B. 10–15 Minuten Zone 1–2
2. **Hauptteil**:
   - *Entweder* Dauerlauf mit Zielpuls-Bereich (z. B. 45 min in Z2: 130–145 bpm)
   - *Oder* Wiederholungsblöcke (z. B. 5x 800m mit Pace 5:15 min/km, dazwischen 2 min Gehen/Traben)
3. **Cooldown**: 5–10 Minuten lockeres Auslaufen

### Warum das hilft
- Die **Garmin-Uhr** warnt per Ton/Vibration, wenn man zu schnell/zu langsam ist oder der Puls die Zielzone verlässt.
- Verhindert das typische "Zu-schnell-Beginnen" bei Grundlagenläufen.
- Automatische Rundenzeiten für Intervalle ohne manuelles Stoppen.

---

## 5. Projektstruktur & Werkzeuge

```text
laufen/
├── .env.example              # Vorlage für Zugangsdaten
├── .gitignore                # Ausschluss von FIT-Dateien, Tokens & Cache
├── README.md                 # Projektdokumentation
├── config/                   # Persönliche Werte (MaxHF, Ruhepuls, Zonen)
│   └── athlete.json          # Zonen, LTHR, HF-Bereiche, Pace-Ziele
├── workouts/                 # Strukturierte Trainingsvorlagen
│   ├── intervals/            # Textbasierte Workouts (Intervals.icu Syntax)
│   └── garmin/               # JSON-Workouts für Garmin Connect
└── src/                      # Skripte für Analyse & Sync
    ├── integrations/         # Schnittstellen (Garmin, Intervals, Strava)
    ├── analysis/             # Einfache Auswertungen (Pace vs. HF, Zonen-Zeiten)
    └── planner/              # Workout-Erstellung & Upload
```

---

## 6. API-Schnittstellen im Überblick

### Intervals.icu REST API
- **Dokumentation**: [Intervals.icu API Docs](https://intervals.icu/api/v1/docs)
- **Authentifizierung**: HTTP Basic Auth mit Username `API_KEY` und dem persönlichen API-Key aus den Intervals-Einstellungen.
- **Endpoints**:
  - `GET /api/v1/athlete/{id}/activities`: Abruf von Trainings inkl. HR, Pace, Zonenverteilung.
  - `POST /api/v1/athlete/{id}/events`: Hochladen geplanter Workouts direkt in den Kalender.

### Garmin Connect API
- **Library**: `garminconnect` (Python)
- **Authentifizierung**: Login via E-Mail/Passwort mit Session-Token-Caching (`garmin_tokens/`).
- **Funktionen**:
  - Download von Original-Aktivitäten (`.fit`-Format).
  - Upload von Trainings (`upload_workout`) direkt auf die Garmin-Uhr.

### Strava API
- **Dokumentation**: [Strava Developers](https://developers.strava.com/)
- **Authentifizierung**: OAuth2 mit Refresh-Token-Workflow.
- **Endpoints**:
  - `GET /api/v3/athlete/activities`: Abgleich von Segmentzeiten, Social Kudo-Metadaten und Laufschuh-Tracking (Gear).

---

## 7. Setup & Schnellstart

### 1. Repository klonen & Virtual Environment aufsetzen
```bash
git clone <repository-url>
cd laufen
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
```

### 2. Credentials & Umgebungsvariablen hinterlegen
Das Projekt prüft Umgebungsvariablen in folgender Reihenfolge:
1. **System-/Shell-Umgebungsvariable** (z. B. geladen via `pass` in `~/.bashrc` oder `~/.zshrc`).
2. **Lokale `.env`-Datei** im Projektverzeichnis.

```bash
# Option A: Falls INTERVALS_API_KEY via pass verwaltet wird
export INTERVALS_API_KEY=$(pass show api/intervals.icu)

### 3. Aktivitäten von Intervals.icu abrufen
```bash
# Lädt standardmäßig alle historischen Aktivitäten nach data/intervals_activities.json herunter
python3 src/integrations/download_intervals.py

# Optional: Nur Einheiten ab einem bestimmten Datum
python3 src/integrations/download_intervals.py --oldest 2026-09-01

# Optional: Jede Aktivität zusätzlich als separate JSON-Datei unter data/activities/ ablegen
python3 src/integrations/download_intervals.py --save-individual
```

### 4. Wöchentliche Trends & Zonen-Disziplin auswerten
```bash
# Wöchentliche Zusammenfassung (Distanz, Pace, HF, Z1-Z2-Anteil, Aerobe Effizienz)
python3 src/analysis/trends.py

# Optional: Als maschinenlesbares JSON ausgeben
python3 src/analysis/trends.py --json
```

### 5. Workouts erstellen & terminieren (Intervals.icu & Garmin)
```bash
# Easy Run (z. B. 45 min Zone 2 für morgen)
python3 src/planner/schedule_workout.py --type easy_run --duration 45m --date tomorrow

# Intervalltraining (4x 1.000m schnell, 2 min Trabpause)
python3 src/planner/schedule_workout.py --type intervals --reps 4 --work 1km --recovery 2m --target "Z4 HR" --date tomorrow

# Schwellenlauf (8 min Warmup, 20 min Schwelle, 10 min Auslaufen)
python3 src/planner/schedule_workout.py --type tempo_run --warmup 8m --duration 20m --cooldown 10m --target "Z4 HR" --date tomorrow

# Vorschau ohne Upload (Dry-Run)
python3 src/planner/schedule_workout.py --dry-run --type intervals --reps 5 --work 1km --recovery 2m30s --target "Z4 HR" --date tomorrow
```

### 6. Tests ausführen
```bash
pytest tests/
```

---

## 8. Sicherheitshinweise

- Die `.env`-Datei und Token-Verzeichnisse (`garmin_tokens/`, `tokens.json`) dürfen **niemals** im Git-Repository committet werden.
- Alle sensiblen Keys werden ausschließlich über Umgebungsvariablen geladen.

---

## 9. Anhang: Erweiterte Metriken (Späterer Ausblick)

> [!NOTE]
> Sobald die Routine und die Grundlagen sitzen, können tiefergehende Modelle hinzugenommen werden:
> - **CTL / ATL / TSB**: Langfristige Form- und Belastungssteuerung (Chronic Training Load vs. Acute Training Load).
> - **Aerobe Entkopplung ($Pw:HR$)**: Prüfung, ob bei konstantem Tempo der Puls nach 45+ Minuten wegläuft (kardiovaskulärer Drift).
> - **VO2max**: Geschätztes maximales Sauerstoffaufnahmevermögen.
