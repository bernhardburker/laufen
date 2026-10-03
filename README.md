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

## 2. Kernziele

- **Trainingskoordination & Periodisierung**: Zentrale Definition von Makro-, Meso- und Mikrozyklen (z. B. 80/20, polarisiert oder pyramidal).
- **Automatisierte Analyse & Trenderkennung**:
  - **Fitness & Ermüdung**: Tracking von **CTL** (Chronic Training Load / Fitness), **ATL** (Acute Training Load / Ermüdung) und **TSB** (Training Stress Balance / Form).
  - **Aerobe Entkopplung (Decoupling / Pw:HR)**: Erkennung von kardiovaskulärem Drift bei Grundlageneinheiten.
  - **Zonenanalyse**: Verteilung der Trainingszeit über Herzfrequenz- und Pace-Zonen.
  - **Leistungskurven**: Bestzeiten- und Pace-Dauer-Profile über Zeiträume.
- **Workout-Erstellung & Synchronisation**:
  - Skriptbasierte Generierung strukturierter Trainings (Intervallläufe, Schwellenläufe, Tempoläufe, Regenerationsläufe).
  - Direkter Upload in Intervals.icu und/oder Garmin Connect zur Ausführung auf der Uhr.

---

## 3. Architektur & Verzeichnisstruktur

```text
laufen/
├── .env.example              # Template für API-Keys & Zugangsdaten
├── .gitignore                # Ausschluss von FIT-Dateien, Tokens & Cache
├── README.md                 # Projektdokumentation
├── config/                   # Lokale Konfigurationen (Sportlerprofil, Schwellenwerte)
│   └── athlete.json          # Zonen, LTHR, MaxHR, Schwellen-Pace (CSS/TP)
├── data/                     # Lokaler Datenspeicher (nicht im Git getrackt)
│   ├── raw/                  # FIT/GPX/JSON-Rohdateien
│   └── cache/                # Gecachte API-Responses & aggregierte Auswertungen
├── workouts/                 # Strukturierte Trainingsvorlagen
│   ├── intervals/            # Textbasierte Workouts für Intervals.icu
│   └── garmin/               # JSON/FIT-Dateien für Garmin Connect
└── src/                      # Skripte und Analyse-Module
    ├── integrations/         # API-Clients (Garmin, Intervals, Strava)
    ├── analysis/             # Metrikberechnungen, Trends, HF-Drift, Zonenverteilung
    └── planner/              # Workout-Builder & Planungslogik
```

---

## 4. Wichtige Metriken & Trainingsmodelle

### Belastungssteuerung (Bannister / Coggan Modell)
- **CTL (Chronic Training Load - Fitness)**: Exponentiell gewichteter Schnitt der Trainingsbelastung (~42 Tage).
- **ATL (Acute Training Load - Ermüdung)**: Exponentiell gewichteter Schnitt der Belastung (~7 Tage).
- **TSB (Training Stress Balance - Form)**: $TSB = CTL - ATL$.
  - $> +15$: Frische / Transition (Gefahr von Deconditioning bei zu langem Verbleib)
  - $+5$ bis $+15$: Optimales Wettkampffenster (Peaking)
  - $-10$ bis $+5$: Neutral / produktives Training
  - $-30$ bis $-10$: Hohe Trainingsbelastung (Aufbauphase)
  - $< -30$: Überlastungsrisiko

### Physiologische Schwellen & Zonen
- **Z1 (Aktive Erholung)**: $< 75\%$ LTHR (Lactate Threshold Heart Rate)
- **Z2 (Grundlagenausdauer 1 / Aerob)**: $75\% - 89\%$ LTHR
- **Z3 (Tempo / Grundlagenausdauer 2)**: $90\% - 94\%$ LTHR
- **Z4 (Schwelle / LTHR)**: $95\% - 99\%$ LTHR
- **Z5 (VO2max / Anaerob)**: $> 100\%$ LTHR

---

## 5. API-Schnittstellen im Überblick

### Intervals.icu REST API
- **Dokumentation**: [Intervals.icu API Docs](https://intervals.icu/api/v1/docs)
- **Authentifizierung**: HTTP Basic Auth mit Username `API_KEY` und dem persönlichen API-Key aus den Intervals-Einstellungen.
- **Endpoints**:
  - `GET /api/v1/athlete/{id}/activities`: Abruf von Trainings inkl. TSS, HR, Pace, Decoupling.
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

## 6. Setup & Schnellstart

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
export INTERVALS_API_KEY=$(pass show api/intervals.icu) # bzw. entsprechender pass-Pfad

# Option B: Konfiguration über .env-Datei (z. B. auf Workstations ohne pass)
cp .env.example .env
# .env mit Garmin-, Strava- und ggf. Intervals-Zugangsdaten befüllen
```

---

## 7. Sicherheitshinweise

- Die `.env`-Datei und Token-Verzeichnisse (`garmin_tokens/`, `tokens.json`) dürfen **niemals** im Git-Repository committet werden.
- Alle sensiblen Keys werden ausschließlich über Umgebungsvariablen geladen.
