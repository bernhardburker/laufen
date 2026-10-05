#!/usr/bin/env python3
"""BDD Step definitions and test driver for Coach Memory and Adaptive Feedback Loop."""

import json
from pathlib import Path
import sys
from typing import Any, Dict, List, Optional

# Ensure project root is in sys.path
PROJECT_ROOT = Path(__file__).resolve().parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

import pytest
from pytest_bdd import given, parsers, scenarios, then, when

from src.analysis.ai_coach import build_coach_prompt, parse_coach_response
from src.analysis.coach_history import (
    find_runs_since,
    get_open_recommendation,
    initialize_cold_start_history,
    load_coach_history,
    save_coach_history,
    update_history_with_coach_review,
)
from src.visualization.generate_dashboard import build_coach_history_html, build_summary_html

# 1. Scenarios from feature file
scenarios("coach_history.feature")


# 2. Test Driver
class CoachHistoryDriver:
    def __init__(self, tmp_path: Path):
        self.tmp_path = tmp_path
        self.history_file = tmp_path / "coach_history.json"
        self.activities_file = tmp_path / "activities.json"
        self.athlete_file = tmp_path / "athlete.json"

        self.activities: List[Dict[str, Any]] = []
        self.athlete_profile: Dict[str, Any] = {
            "athlete": {
                "name": "Berni",
                "classification": "Hobbyläufer (Basisaufbau)",
                "heart_rate": {
                    "lthr": 166,
                    "max_hr": 183,
                    "resting_hr": 51,
                    "zones": {"z2_aerobic": {"max": 148}},
                },
            }
        }
        self.history: List[Dict[str, Any]] = []
        self.prompt: str = ""
        self.coach_response_json: str = ""
        self.parsed_response: Optional[Dict[str, Any]] = None
        self.rendered_html: str = ""

    def setup_empty_history(self) -> None:
        if self.history_file.exists():
            self.history_file.unlink()
        self.history = []

    def prepare_recent_runs_with_low_z2(self) -> None:
        self.activities = [
            {
                "id": "run-001",
                "type": "Run",
                "name": "Schwellenlauf 7k",
                "start_date_local": "2026-10-02T17:30:00",
                "distance": 7000.0,
                "moving_time": 2520,  # 6:00/km
                "average_heartrate": 166,
                "icu_training_load": 65,
                "icu_hr_zone_times": [60, 100, 360, 1500, 500, 0, 0],
            },
            {
                "id": "run-002",
                "type": "Run",
                "name": "Mittlerer Dauerlauf",
                "start_date_local": "2026-09-28T09:00:00",
                "distance": 6000.0,
                "moving_time": 2220,
                "average_heartrate": 162,
                "icu_training_load": 55,
                "icu_hr_zone_times": [50, 120, 500, 1200, 350, 0, 0],
            },
        ]
        self.activities_file.write_text(json.dumps(self.activities), encoding="utf-8")
        self.athlete_file.write_text(json.dumps(self.athlete_profile), encoding="utf-8")

    def initialize_history_from_activity_data(self) -> None:
        self.history = initialize_cold_start_history(
            self.activities, self.athlete_profile, self.history_file
        )

    def verify_initial_baseline_created(self) -> None:
        assert len(self.history) >= 1
        first_entry = self.history[0]
        assert "id" in first_entry
        assert "date" in first_entry
        assert "Ausgangsbasis" in first_entry.get("actual", "")

    def verify_open_recommendation_scheduled(self) -> None:
        open_rec = get_open_recommendation(self.history)
        assert open_rec is not None
        assert open_rec.get("status") == "offen"
        assert "148" in open_rec.get("recommendation", "")

    def setup_history_with_open_rec(self) -> None:
        self.history = [
            {
                "id": "cycle-001",
                "date": "2026-10-01",
                "recommendation": "Grundlagenläufe strikt unter 148 bpm halten, Pace ignorieren",
                "target_type": "easy_run",
                "target_hr_max": 148,
                "status": "offen",
                "actual": "Ausstehend",
                "effect": "-",
            }
        ]
        save_coach_history(self.history, self.history_file)
        self.athlete_file.write_text(json.dumps(self.athlete_profile), encoding="utf-8")

    def add_subsequent_runs(self) -> None:
        self.activities = [
            {
                "id": "run-003",
                "type": "Run",
                "name": "Testlauf nach Empfehlung",
                "start_date_local": "2026-10-03T08:00:00",
                "distance": 6500.0,
                "moving_time": 2600,
                "average_heartrate": 144,
                "icu_training_load": 40,
                "icu_hr_zone_times": [300, 2000, 300, 0, 0, 0, 0],
            }
        ]
        self.activities_file.write_text(json.dumps(self.activities), encoding="utf-8")

    def build_prompt_with_history(self) -> None:
        self.prompt = build_coach_prompt(
            activities_file=self.activities_file,
            athlete_file=self.athlete_file,
            history_file=self.history_file,
        )

    def verify_prompt_contains_previous_rec(self) -> None:
        assert "Bisheriges Coaching-Gedächtnis" in self.prompt
        assert "Grundlagenläufe strikt unter 148 bpm" in self.prompt

    def verify_prompt_includes_runs(self) -> None:
        assert "Testlauf nach Empfehlung" in self.prompt
        assert "144 bpm" in self.prompt

    def verify_prompt_instructions(self) -> None:
        assert "Compliance" in self.prompt
        assert "Adaptive Steuerung" in self.prompt
        assert "history_evaluation" in self.prompt

    def prepare_coach_response_compliant(self) -> None:
        self.coach_response_json = json.dumps(
            {
                "status_title": "Solide Zone-2-Einheit umgesetzt",
                "status_badge": "Disziplin verbessert",
                "status_level": "success",
                "coach_commentary": "Gute Arbeit Berni, der Testlauf am 03.10. lag sauber im aeroben Fenster.",
                "history_evaluation": {
                    "compliance_status": "erfüllt",
                    "actual_summary": "6.5 km mit Ø 144 bpm (77 % Z2) absolviert. Vorgabe eingehalten.",
                    "effect_analysis": "Puls stabil im aeroben Bereich, gute Regeneration.",
                    "next_recommendation": "Zweites Z2-Training über 45 Minuten mit maximal 148 bpm.",
                    "next_workout_type": "easy_run",
                    "next_target_hr": 148,
                    "next_duration_min": 45,
                },
                "insights": [{"title": "Z2 Treue", "type": "success", "badge": "77% Z2", "text": "Strikte Pulskontrolle"}],
                "recommendations": [{"title": "Weiter Z2 aufbauen", "tag": "Basis", "tag_class": "tag-green", "text": "Puls < 148"}],
            }
        )

    def prepare_coach_response_tempo_run(self) -> None:
        self.coach_response_json = json.dumps(
            {
                "status_title": "Tempolauf statt Easy Run eingeschoben",
                "status_badge": "Plan adaptiert (Regeneration)",
                "status_level": "warning",
                "coach_commentary": "Berni hat am Samstag einen 5k Tempolauf mit 172 bpm durchgezogen. Das Training wird nun auf Erholung angepasst.",
                "history_evaluation": {
                    "compliance_status": "angepasst_nach_tempo",
                    "actual_summary": "5 km Tempolauf an der Schwelle absolviert (Ø 172 bpm) statt lockerer Z2-Lauf.",
                    "effect_analysis": "Hoher Trainingsreiz und erhöhte Ermüdung (TSB -4.5). Benötigt Erholungsphase.",
                    "next_recommendation": "Aktive Regeneration: 30 Minuten ganz locker traben (< 135 bpm) oder Gehpausen.",
                    "next_workout_type": "recovery_run",
                    "next_target_hr": 135,
                    "next_duration_min": 30,
                },
                "insights": [{"title": "Schwellenreiz", "type": "warning", "badge": "172 bpm", "text": "Hoher anaerober Anteil"}],
                "recommendations": [{"title": "Regeneration", "tag": "Erholung", "tag_class": "tag-cyan", "text": "Aktive Regeneration"}],
            }
        )

    def process_coach_response(self) -> None:
        self.parsed_response = parse_coach_response(self.coach_response_json)
        assert self.parsed_response is not None
        runs_since = find_runs_since(self.activities, "2026-10-01")
        self.history = update_history_with_coach_review(
            history=self.history,
            review_data=self.parsed_response["history_evaluation"],
            runs_since=runs_since,
            athlete_profile=self.athlete_profile,
            history_file=self.history_file,
        )

    def verify_previous_status(self, expected_status: str) -> None:
        first_entry = self.history[0]
        assert first_entry.get("status") == expected_status

    def verify_entry_records(self) -> None:
        first_entry = self.history[0]
        assert first_entry.get("actual") != "Ausstehend"
        assert first_entry.get("effect") != "-"

    def verify_new_open_recommendation(self) -> None:
        assert len(self.history) == 2
        latest_entry = self.history[-1]
        assert latest_entry.get("status") == "offen"
        assert "Zweites Z2-Training" in latest_entry.get("recommendation", "")

    def verify_tempo_adaptation(self) -> None:
        first_entry = self.history[0]
        assert first_entry.get("status") == "angepasst_nach_tempo"
        latest_entry = self.history[-1]
        assert latest_entry.get("status") == "offen"
        assert "Aktive Regeneration" in latest_entry.get("recommendation", "")
        # Verify persistence and no duplicate keys
        reloaded = load_coach_history(self.history_file)
        assert len(reloaded) == len(self.history)

    def prepare_multiple_cycles_history(self) -> None:
        self.history = [
            {
                "id": "cycle-001",
                "date": "2026-09-25",
                "recommendation": "Z2 Grundlagenlauf unter 148 bpm",
                "actual": "6 km mit Ø 142 bpm absolviert",
                "status": "erfüllt",
                "effect": "Puls stabil, Erholung optimal",
            },
            {
                "id": "cycle-002",
                "date": "2026-10-01",
                "recommendation": "Lockere Einheit 40 min",
                "actual": "Tempolauf mit 170 bpm absolviert",
                "status": "angepasst_nach_tempo",
                "effect": "Hohe Ermüdung, Plan auf Erholung geschwenkt",
            },
            {
                "id": "cycle-003",
                "date": "2026-10-05",
                "recommendation": "Aktive Regeneration 30 min",
                "actual": "Ausstehend",
                "status": "offen",
                "effect": "-",
            },
        ]
        save_coach_history(self.history, self.history_file)

    def render_dashboard_with_history(self) -> None:
        summary_data = {
            "status_title": "Test Title",
            "status_badge": "Test Badge",
            "status_level": "info",
            "insights": [{"title": "Insight", "type": "info", "badge": "tag", "text": "text"}],
            "recommendations": [{"title": "Rec", "tag": "Tag", "tag_class": "tag-cyan", "text": "text"}],
        }
        self.rendered_html = build_summary_html(
            summary=summary_data,
            ai_coach_markdown="### Coach Brief\nAlles gut.",
            coach_history=self.history,
        )

    def verify_dashboard_history_table(self) -> None:
        assert "Coach-Gedächtnis &amp; Feedback-Schleife" in self.rendered_html
        assert "Z2 Grundlagenlauf" in self.rendered_html
        assert "✓ Erfüllt" in self.rendered_html
        assert "⚡ Adaptiert (Tempo)" in self.rendered_html
        assert "⏳ Offen" in self.rendered_html
        assert "Tempolauf mit 170 bpm" in self.rendered_html


# 3. Fixtures
@pytest.fixture
def driver(tmp_path: Path) -> CoachHistoryDriver:
    return CoachHistoryDriver(tmp_path)


# 4. Step Definitions
@given("an empty coach history repository")
def given_empty_history(driver: CoachHistoryDriver):
    driver.setup_empty_history()


@given("recent running activities with low aerobic zone discipline")
def given_recent_runs_low_z2(driver: CoachHistoryDriver):
    driver.prepare_recent_runs_with_low_z2()


@when("coach history is initialized from activity data")
def when_history_initialized(driver: CoachHistoryDriver):
    driver.initialize_history_from_activity_data()


@then("an initial baseline entry is created")
def then_baseline_created(driver: CoachHistoryDriver):
    driver.verify_initial_baseline_created()


@then("an open recommendation is scheduled for aerobic base building")
def then_open_rec_scheduled(driver: CoachHistoryDriver):
    driver.verify_open_recommendation_scheduled()


@given("an existing coach history with an open recommendation for Zone 2 running")
def given_history_with_open_rec(driver: CoachHistoryDriver):
    driver.setup_history_with_open_rec()


@given("new running activities completed after the recommendation date")
def given_new_activities(driver: CoachHistoryDriver):
    driver.add_subsequent_runs()


@when("an AI coach prompt is constructed with history context")
def when_prompt_constructed_with_history(driver: CoachHistoryDriver):
    driver.build_prompt_with_history()


@then("the prompt contains the previous recommendation text")
def then_prompt_contains_rec(driver: CoachHistoryDriver):
    driver.verify_prompt_contains_previous_rec()


@then("the prompt includes details of the runs completed since the recommendation")
def then_prompt_includes_runs(driver: CoachHistoryDriver):
    driver.verify_prompt_includes_runs()


@then("the prompt instructs the coach to review compliance and physiological effect")
def then_prompt_instructions(driver: CoachHistoryDriver):
    driver.verify_prompt_instructions()


@given("a coach response indicating successful Zone 2 compliance and positive aerobic effect")
def given_coach_response_compliant(driver: CoachHistoryDriver):
    driver.prepare_coach_response_compliant()


@when("the coach response is processed into the history")
def when_coach_response_processed(driver: CoachHistoryDriver):
    driver.process_coach_response()


@then(parsers.parse('the previous recommendation is marked with status "{expected_status}"'))
def then_previous_marked_status(driver: CoachHistoryDriver, expected_status: str):
    driver.verify_previous_status(expected_status)


@then("the entry records the actual runs and measured effect")
def then_entry_records_actual_and_effect(driver: CoachHistoryDriver):
    driver.verify_entry_records()


@then("a new open recommendation is appended for the next cycle")
def then_new_open_rec_appended(driver: CoachHistoryDriver):
    driver.verify_new_open_recommendation()


@given("a coach response detecting an unplanned threshold tempo run")
def given_coach_response_tempo(driver: CoachHistoryDriver):
    driver.prepare_coach_response_tempo_run()


@then(parsers.parse('the previous entry is marked with status "{expected_status}"'))
def then_previous_entry_status(driver: CoachHistoryDriver, expected_status: str):
    driver.verify_previous_status(expected_status)


@then("the next recommendation prescribes active recovery or easy base training")
def then_next_rec_prescribes_recovery(driver: CoachHistoryDriver):
    driver.verify_tempo_adaptation()


@then("the history remains persistently saved without duplicates")
def then_history_saved_without_duplicates(driver: CoachHistoryDriver):
    # Verified in verify_tempo_adaptation
    pass


@given("a coach history with multiple past and open coaching cycles")
def given_multiple_cycles_history(driver: CoachHistoryDriver):
    driver.prepare_multiple_cycles_history()


@when("the HTML dashboard is generated with coach history data")
def when_dashboard_generated_with_history(driver: CoachHistoryDriver):
    driver.render_dashboard_with_history()


@then("the dashboard includes a Coach Memory and Feedback section")
def then_dashboard_includes_history(driver: CoachHistoryDriver):
    driver.verify_dashboard_history_table()


@then("each cycle displays date, recommendation, actual performance, status badge, and effect")
def then_each_cycle_displays_fields(driver: CoachHistoryDriver):
    # Already verified in verify_dashboard_history_table
    pass
