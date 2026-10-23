#!/usr/bin/env python3
"""BDD Step definitions and test driver for AI Running Coach with Abstracted LLM Pipeline."""

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

from src.analysis.ai_coach import (
    build_coach_prompt,
    parse_coach_response,
    run_ai_coach,
)
from src.analysis.coach_history import load_coach_history
from src.analysis.run_reviewer import load_run_reviews
from src.llm import MockLLMClient

# 1. Bind feature scenarios
scenarios("ai_coach.feature")


# 2. Test Driver
class AICoachDriver:
    def __init__(self, tmp_path: Path):
        self.tmp_path = tmp_path
        self.activities_file = tmp_path / "activities.json"
        self.athlete_file = tmp_path / "athlete.json"
        self.history_file = tmp_path / "coach_history.json"
        self.reviews_file = tmp_path / "run_reviews.json"
        self.output_json = tmp_path / "ai_coach_summary.json"
        self.output_md = tmp_path / "ai_coach_commentary.md"

        self.activities: List[Dict[str, Any]] = []
        self.athlete_profile: Dict[str, Any] = {
            "athlete": {
                "name": "Berni",
                "display_name": "Berni",
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
        self.llm_client: Optional[MockLLMClient] = None
        self.prompt: str = ""
        self.execution_result: Optional[Dict[str, Any]] = None
        self.raw_llm_output: str = ""
        self.parsed_response: Optional[Dict[str, Any]] = None

    def setup_empty_activities(self) -> None:
        self.activities = []
        self.activities_file.write_text("[]", encoding="utf-8")

    def setup_activities_dataset(self) -> None:
        self.activities = [
            {
                "id": "run-101",
                "type": "Run",
                "name": "Lockerer Z2 Dauerlauf",
                "start_date_local": "2026-10-02T17:30:00",
                "distance": 7000.0,
                "moving_time": 2520,  # 6:00/km
                "average_heartrate": 142,
                "icu_training_load": 48,
                "icu_hr_zone_times": [300, 1800, 300, 120, 0, 0, 0],
            },
            {
                "id": "run-102",
                "type": "Run",
                "name": "Schwellenreiz 5k",
                "start_date_local": "2026-10-05T09:00:00",
                "distance": 5000.0,
                "moving_time": 1500,  # 5:00/km
                "average_heartrate": 165,
                "icu_training_load": 55,
                "icu_hr_zone_times": [100, 200, 400, 800, 0, 0, 0],
            },
        ]
        self.activities_file.write_text(
            json.dumps(self.activities, ensure_ascii=False), encoding="utf-8"
        )

    def setup_athlete(self, name: str, z2_max: int = 148) -> None:
        self.athlete_profile["athlete"]["name"] = name
        self.athlete_profile["athlete"]["display_name"] = name
        self.athlete_profile["athlete"]["heart_rate"]["zones"]["z2_aerobic"]["max"] = z2_max
        self.athlete_file.write_text(
            json.dumps(self.athlete_profile, ensure_ascii=False), encoding="utf-8"
        )

    def setup_active_coach_memory(self, open_date: str) -> None:
        self.history = [
            {
                "id": "cycle-001",
                "date": open_date,
                "recommendation": "Laufe 2x 45min strikt unter 148 bpm zur aeroben Stabilisierung.",
                "status": "offen",
                "evaluated_run_ids": [],
            }
        ]
        self.history_file.write_text(
            json.dumps(self.history, ensure_ascii=False, indent=2), encoding="utf-8"
        )

    def setup_mock_llm_valid_response(self) -> None:
        response_dict = {
            "status_title": "Solider Grundlagenaufbau",
            "status_badge": "Basis stabil",
            "status_level": "info",
            "coach_commentary": "### Status\nGuter Dauerlauf in Zone 2 mit disziplinierter Herzfrequenz.",
            "history_evaluation": {
                "compliance_status": "erfüllt",
                "actual_summary": "Zwei solide Einheiten absolviert, eine aerob, eine an der Schwelle.",
                "effect_analysis": "Aerobes Fundament gepflegt, TSB gut balanciert.",
                "next_recommendation": "Ein weiterer Z2-Lauf am Wochenende bis max 148 bpm.",
                "next_workout_type": "easy_run",
                "next_target_hr": 148,
                "next_duration_min": 50,
            },
            "insights": [
                {
                    "title": "Aerobe Disziplin",
                    "type": "success",
                    "badge": "Z2 stabil",
                    "text": "Der überwiegende Teil der Einheiten lag im Zielpulsbereich.",
                }
            ],
            "recommendations": [
                {
                    "title": "Grundlagenlauf am Wochenende",
                    "tag": "Priorität 1: Z2",
                    "tag_class": "tag-amber",
                    "text": "Strikte HF-Steuerung unter 148 bpm.",
                }
            ],
        }
        self.llm_client = MockLLMClient(responses=response_dict)

    def setup_mock_llm_malformed_response(self) -> None:
        self.llm_client = MockLLMClient(responses="DAS IST KEIN GUELTIGES JSON!")

    def setup_mock_llm_failure(self, error_message: str) -> None:
        self.llm_client = MockLLMClient(should_fail=True, error_message=error_message)

    def generate_prompt(self) -> None:
        self.prompt = build_coach_prompt(
            activities_file=self.activities_file,
            athlete_file=self.athlete_file,
            history_file=self.history_file,
            reviews_file=self.reviews_file,
        )

    def execute_engine(self) -> None:
        self.execution_result = run_ai_coach(
            activities_file=self.activities_file,
            athlete_file=self.athlete_file,
            history_file=self.history_file,
            reviews_file=self.reviews_file,
            output_json=self.output_json,
            output_md=self.output_md,
            llm_client=self.llm_client,
            fallback_on_error=True,
            update_reviews_first=True,
        )

    def setup_raw_wrapped_output(self) -> None:
        self.raw_llm_output = """```json
{
  "status_title": "Disziplinierter Grundlagenlauf",
  "status_badge": "Optimal",
  "status_level": "success",
  "coach_commentary": "Hervorragend gelaufen.",
  "insights": [],
  "recommendations": []
}
```"""

    def parse_raw_output(self) -> None:
        self.parsed_response = parse_coach_response(self.raw_llm_output)

    def assert_prompt_contains(self, text: str) -> None:
        assert text in self.prompt, f"Expected '{text}' in prompt:\n{self.prompt}"

    def assert_execution_status(self, expected_status: str) -> None:
        assert self.execution_result is not None
        actual_status = self.execution_result.get("status")
        assert actual_status == expected_status, (
            f"Expected status '{expected_status}', got '{actual_status}'. Result: {self.execution_result}"
        )

    def assert_summary_json_contains(self, key_title: str) -> None:
        assert self.output_json.exists(), f"Output JSON {self.output_json} was not created"
        data = json.loads(self.output_json.read_text(encoding="utf-8"))
        assert data.get("status_title") == key_title

    def assert_commentary_md_contains(self, snippet: str) -> None:
        assert self.output_md.exists(), f"Output MD {self.output_md} was not created"
        content = self.output_md.read_text(encoding="utf-8")
        assert snippet in content

    def assert_coach_history_updated(self) -> None:
        history = load_coach_history(self.history_file)
        # Should now have closed entry and new open entry
        assert len(history) >= 2
        last_entry = history[-1]
        assert last_entry.get("status") == "offen"
        assert "148 bpm" in last_entry.get("recommendation", "") or "Z2" in last_entry.get("recommendation", "")

    def assert_open_cycle_remains_open(self) -> None:
        history = load_coach_history(self.history_file)
        assert len(history) == 1
        assert history[0].get("status") == "offen"

    def assert_error_mentions(self, text: str) -> None:
        assert self.execution_result is not None
        err = self.execution_result.get("error") or ""
        assert text.lower() in err.lower()

    def assert_heuristic_reviews_updated(self) -> None:
        reviews = load_run_reviews(self.reviews_file)
        assert len(reviews) >= 1
        assert "run-101" in reviews

    def assert_parsed_response_valid(self, expected_title: str) -> None:
        assert self.parsed_response is not None
        assert self.parsed_response.get("status_title") == expected_title


# 3. Fixture
@pytest.fixture
def driver(tmp_path: Path) -> AICoachDriver:
    return AICoachDriver(tmp_path)


# 4. Step Definitions
@given("an empty activities dataset")
def given_empty_activities(driver: AICoachDriver):
    driver.setup_empty_activities()


@given("an activities dataset with multiple running activities")
def given_activities_dataset(driver: AICoachDriver):
    driver.setup_activities_dataset()


@given(parsers.parse('an athlete profile for "{name}"'))
def given_athlete_profile(driver: AICoachDriver, name: str):
    driver.setup_athlete(name)


@given(parsers.parse('an athlete profile for "{name}" with max Z2 heart rate {z2_max:d}'))
def given_athlete_profile_z2(driver: AICoachDriver, name: str, z2_max: int):
    driver.setup_athlete(name, z2_max=z2_max)


@given(parsers.parse('an active coach memory with an open recommendation from "{open_date}"'))
def given_active_coach_memory(driver: AICoachDriver, open_date: str):
    driver.setup_active_coach_memory(open_date)


@given("a mock LLM client providing a valid structured coaching response")
def given_mock_llm_valid(driver: AICoachDriver):
    driver.setup_mock_llm_valid_response()


@given("a mock LLM client providing unparseable malformed text")
def given_mock_llm_malformed(driver: AICoachDriver):
    driver.setup_mock_llm_malformed_response()


@given(parsers.parse('a mock LLM client configured to fail with error "{error_message}"'))
def given_mock_llm_failing(driver: AICoachDriver, error_message: str):
    driver.setup_mock_llm_failure(error_message)


@given("a raw LLM output wrapped in markdown json fences")
def given_raw_output_fenced(driver: AICoachDriver):
    driver.setup_raw_wrapped_output()


@when("the AI coach prompt is generated")
def when_generate_prompt(driver: AICoachDriver):
    driver.generate_prompt()


@when("the AI coach engine is executed")
def when_execute_engine(driver: AICoachDriver):
    driver.execute_engine()


@when("the raw response is parsed by the coach engine")
def when_parse_raw_response(driver: AICoachDriver):
    driver.parse_raw_output()


@then(parsers.parse('the prompt contains "{text}"'))
def then_prompt_contains(driver: AICoachDriver, text: str):
    driver.assert_prompt_contains(text)


@then(parsers.parse('the prompt addresses athlete "{name}"'))
def then_prompt_addresses(driver: AICoachDriver, name: str):
    driver.assert_prompt_contains(name)


@then(parsers.parse('the prompt contains athlete display name "{name}"'))
def then_prompt_display_name(driver: AICoachDriver, name: str):
    driver.assert_prompt_contains(name)


@then("the prompt contains the previous recommendation text")
def then_prompt_prev_rec(driver: AICoachDriver):
    driver.assert_prompt_contains("strikt unter 148 bpm")


@then(parsers.parse('the prompt contains heart rate threshold "{hr_val}"'))
def then_prompt_hr_val(driver: AICoachDriver, hr_val: str):
    driver.assert_prompt_contains(hr_val)


@then("the prompt asks for valid JSON output")
def then_prompt_asks_json(driver: AICoachDriver):
    driver.assert_prompt_contains("validen JSON-Objekt")


@then(parsers.parse('the execution status is "{expected_status}"'))
def then_execution_status(driver: AICoachDriver, expected_status: str):
    driver.assert_execution_status(expected_status)


@then(parsers.parse('the structured summary JSON file is created and contains status title "{key_title}"'))
def then_summary_json_created(driver: AICoachDriver, key_title: str):
    driver.assert_summary_json_contains(key_title)


@then(parsers.parse('the extracted commentary Markdown file is created and contains "{snippet}"'))
def then_commentary_created(driver: AICoachDriver, snippet: str):
    driver.assert_commentary_md_contains(snippet)


@then("the coach history is updated with the new cycle recommendation")
def then_history_updated(driver: AICoachDriver):
    driver.assert_coach_history_updated()


@then("the open cycle in coach history remains open")
def then_cycle_remains_open(driver: AICoachDriver):
    driver.assert_open_cycle_remains_open()


@then(parsers.parse('the error details mention "{text}"'))
def then_error_details_mention(driver: AICoachDriver, text: str):
    driver.assert_error_mentions(text)


@then("heuristic per-run reviews are updated in the cache")
def then_heuristic_reviews_updated(driver: AICoachDriver):
    driver.assert_heuristic_reviews_updated()


@then(parsers.parse('the parsed object is valid and contains status title "{expected_title}"'))
def then_parsed_object_valid(driver: AICoachDriver, expected_title: str):
    driver.assert_parsed_response_valid(expected_title)
