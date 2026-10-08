import json
import subprocess
import sys
from pathlib import Path
from typing import Any, Dict, List

import pytest
from pytest_bdd import given, parsers, scenarios, then, when

# 1. Bind Gherkin scenarios
scenarios("generate_dashboard.feature")


# 2. Test Driver
class DashboardGeneratorDriver:
    def __init__(self, workspace_root: Path, tmp_path: Path):
        self.workspace_root = workspace_root
        self.tmp_path = tmp_path
        self.input_file = tmp_path / "test_activities.json"
        self.output_file = tmp_path / "index.html"
        self.template_file = workspace_root / "templates" / "dashboard.html"
        self.process_result: subprocess.CompletedProcess | None = None

    def write_dataset(self, data: List[Dict[str, Any]]) -> None:
        with open(self.input_file, "w", encoding="utf-8") as f:
            json.dump(data, f)

    def prepare_dataset_with_runs(self) -> None:
        activities = [
            {
                "id": "run-001",
                "type": "Run",
                "name": "Morning Easy Run",
                "start_date_local": "2026-09-02T07:00:00",
                "distance": 6000.0,
                "moving_time": 2160,  # 36m (6:00/km)
                "average_heartrate": 140,
                "max_heartrate": 152,
                "icu_training_load": 45,
                "icu_hr_zone_times": [600, 1200, 300, 60, 0, 0, 0],
            },
            {
                "id": "run-002",
                "type": "Run",
                "name": "Sunday Long Run",
                "start_date_local": "2026-09-06T08:30:00",
                "distance": 10000.0,
                "moving_time": 3600,  # 60m (6:00/km)
                "average_heartrate": 145,
                "max_heartrate": 158,
                "icu_training_load": 75,
                "icu_hr_zone_times": [600, 2400, 500, 100, 0, 0, 0],
            },
            {
                "id": "run-003",
                "type": "Run",
                "name": "Interval 4x1000m",
                "start_date_local": "2026-09-09T18:00:00",
                "distance": 8000.0,
                "moving_time": 2560,  # ~5:20/km
                "average_heartrate": 160,
                "max_heartrate": 178,
                "icu_training_load": 65,
                "icu_hr_zone_times": [200, 300, 1000, 800, 260, 0, 0],
            },
        ]
        self.write_dataset(activities)

    def prepare_empty_dataset(self) -> None:
        activities = [
            {"id": "ride-001", "type": "Ride", "distance": 25000.0, "moving_time": 3600},
            {"id": "walk-001", "type": "Walk", "distance": 3000.0, "moving_time": 2400},
        ]
        self.write_dataset(activities)

    def remove_input_file(self) -> None:
        if self.input_file.exists():
            self.input_file.unlink()

    def run_generator(
        self,
        template_path: Path | None = None,
        input_path: Path | None = None,
        extra_args: List[str] | None = None,
    ) -> None:
        target_template = template_path if template_path is not None else self.template_file
        target_input = input_path if input_path is not None else self.input_file

        cmd = [
            sys.executable,
            str(self.workspace_root / "src" / "visualization" / "generate_dashboard.py"),
            "--input",
            str(target_input),
            "--template",
            str(target_template),
            "--output",
            str(self.output_file),
        ]
        if extra_args:
            cmd.extend(extra_args)

        self.process_result = subprocess.run(
            cmd,
            cwd=self.workspace_root,
            capture_output=True,
            text=True,
        )

    def assert_exit_code(self, expected_code: int) -> None:
        assert self.process_result is not None
        assert self.process_result.returncode == expected_code, (
            f"Expected exit code {expected_code}, got {self.process_result.returncode}.\n"
            f"STDOUT:\n{self.process_result.stdout}\n"
            f"STDERR:\n{self.process_result.stderr}"
        )

    def assert_exit_code_non_zero(self) -> None:
        assert self.process_result is not None
        assert self.process_result.returncode != 0, (
            f"Expected non-zero exit code, got 0.\nSTDOUT:\n{self.process_result.stdout}"
        )

    def assert_output_contains(self, text: str) -> None:
        assert self.process_result is not None
        combined = (self.process_result.stdout or "") + (self.process_result.stderr or "")
        assert text.lower() in combined.lower(), f"Expected '{text}' in output:\n{combined}"

    def assert_output_html_exists(self) -> None:
        assert self.output_file.exists(), f"Output HTML file does not exist: {self.output_file}"
        assert self.output_file.stat().st_size > 0, "Output HTML file is empty"

    def assert_html_contains_kpis(self) -> None:
        content = self.output_file.read_text(encoding="utf-8")
        # 6km + 10km + 8km = 24.00 km total
        assert "24.0" in content, f"Expected total distance '24.0' in HTML:\n{content[:500]}"
        assert "3 runs" in content.lower() or "3" in content
        assert "Morning Easy Run" in content
        assert "Sunday Long Run" in content

    def assert_html_embeds_data(self) -> None:
        content = self.output_file.read_text(encoding="utf-8")
        assert "iso_week" in content
        assert "total_distance_km" in content
        assert "run-001" in content

    def assert_html_displays_empty_state(self) -> None:
        content = self.output_file.read_text(encoding="utf-8")
        assert (
            "no running activities" in content.lower()
            or "0.0" in content
            or "0 runs" in content.lower()
        )

    def assert_html_contains_german_ui(self) -> None:
        content = self.output_file.read_text(encoding="utf-8")
        assert "Gesamtdistanz" in content
        assert "Ø Gesamt-Pace" in content or "Pace" in content
        assert "Grundlagen-Disziplin" in content
        assert "Letzte Aktivitäten" in content

    def assert_html_contains_summary_and_recommendations(self) -> None:
        content = self.output_file.read_text(encoding="utf-8")
        assert "Trainings-Status" in content or "Handlungsempfehlungen" in content
        assert "recommendations" in content.lower() or "handlungsempfehlungen" in content.lower()

    def assert_html_contains_forecasts(self) -> None:
        content = self.output_file.read_text(encoding="utf-8")
        assert "forecastData" in content or "Wettkampf-" in content
        assert "5 km" in content
        assert "Halbmarathon" in content
        assert "predictions" in content

    def prepare_ai_summary_file(self) -> None:
        self.ai_summary_file = self.tmp_path / "custom_ai_summary.json"
        data = {
            "status_title": "Dynamischer KI-Coach Status",
            "status_badge": "KI-Analyse aktiv",
            "status_level": "info",
            "coach_commentary": "Exklusiver Coaching-Kommentar für Berni.",
            "insights": [
                {
                    "title": "Maßgeschneiderte KI-Erkenntnis",
                    "type": "info",
                    "badge": "KI-Fokus",
                    "text": "Erkenntnis generiert via Antigravity CLI.",
                }
            ],
            "recommendations": [
                {
                    "title": "Individuelle KI-Empfehlung",
                    "tag": "KI-Tipp",
                    "tag_class": "tag-green",
                    "text": "Empfehlung generiert via Antigravity CLI.",
                }
            ],
        }
        self.ai_summary_file.write_text(json.dumps(data), encoding="utf-8")

    def run_generator_with_ai_summary(self) -> None:
        self.run_generator(extra_args=["--ai-summary", str(self.ai_summary_file)])

    def assert_html_contains_dynamic_ai_insights(self) -> None:
        content = self.output_file.read_text(encoding="utf-8")
        assert "Dynamischer KI-Coach Status" in content
        assert "Maßgeschneiderte KI-Erkenntnis" in content
        assert "Individuelle KI-Empfehlung" in content
        assert "Exklusiver Coaching-Kommentar für Berni" in content

    def prepare_dataset_with_strava_placeholders(self) -> None:
        activities = [
            {
                "id": "run-001",
                "type": "Run",
                "name": "Morning Easy Run",
                "start_date_local": "2026-09-02T07:00:00",
                "distance": 6000.0,
                "moving_time": 2160,
                "average_heartrate": 140,
                "max_heartrate": 152,
                "icu_training_load": 45,
                "icu_hr_zone_times": [600, 1200, 300, 60, 0, 0, 0],
            },
            {
                "id": "strava-stub-01",
                "type": None,
                "source": "STRAVA",
                "start_date_local": "2026-08-20T18:00:00",
            },
        ]
        self.write_dataset(activities)

    def assert_html_contains_interactive_filters(self) -> None:
        content = self.output_file.read_text(encoding="utf-8")
        assert "dateFilterButtons" in content
        assert 'data-range="all"' in content
        assert 'data-range="12w"' in content
        assert 'data-range="4w"' in content
        assert "filterStartDate" in content

    def assert_html_contains_table_controls(self) -> None:
        content = self.output_file.read_text(encoding="utf-8")
        assert "sortable" in content
        assert 'data-sort="date"' in content
        assert "pageSizeSelect" in content
        assert "tableZoneFilter" in content

    def assert_html_contains_datasource_notice(self) -> None:
        content = self.output_file.read_text(encoding="utf-8")
        assert "datasource-banner" in content
        assert "Datenquellen-Hinweis" in content
        assert "Strava" in content

    def assert_volume_chart_preserves_forecast_across_filters(self) -> None:
        content = self.output_file.read_text(encoding="utf-8")
        assert "chartWeeklyVolume" in content
        assert "stack: 'distance'" in content
        assert "showForecast" in content
        assert "currentRange !== 'custom'" in content

    def assert_html_contains_responsive_filters(self) -> None:
        content = self.output_file.read_text(encoding="utf-8")
        assert "flex-wrap: wrap" in content
        assert "@media (max-width: 768px)" in content

    def assert_html_contains_reload_controls(self) -> None:
        content = self.output_file.read_text(encoding="utf-8")
        assert "btnReloadPage" in content
        assert "fabReload" in content
        assert "pullToRefreshIndicator" in content
        assert "reloadPage" in content

    def prepare_athlete_profile_with_custom_zones(self) -> None:
        self.athlete_file = self.tmp_path / "custom_athlete.json"
        data = {
            "athlete": {
                "id": "test-runner",
                "name": "Test Runner",
                "heart_rate": {
                    "resting_hr": 50,
                    "max_hr": 204,
                    "lthr": 178,
                    "raw_hr_zones": [150, 158, 168, 177, 181, 186, 204],
                },
            }
        }
        self.athlete_file.write_text(json.dumps(data), encoding="utf-8")

    def run_generator_with_athlete_profile(self) -> None:
        self.run_generator(extra_args=["--athlete", str(self.athlete_file)])

    def assert_html_contains_calibrated_zones(self) -> None:
        content = self.output_file.read_text(encoding="utf-8")
        assert "151 – 158 bpm" in content
        assert "159 – 168 bpm" in content
        assert "169 – 177 bpm" in content
        assert "LTHR: 178 bpm" in content
        assert "apple-mobile-web-app-capable" in content
        assert "Neu laden" in content

    def prepare_dataset_with_reviews(self) -> None:
        self.prepare_dataset_with_runs()
        self.reviews_file = self.tmp_path / "test_run_reviews.json"
        data = {
            "run-001": {
                "id": "run-001",
                "cadence_spm": 158.0,
                "review": {
                    "rating": "optimal",
                    "rating_label": "Optimaler Grundlagenlauf",
                    "summary": "Exzellente aerobe Zonendisziplin.",
                    "coach_tip": "Weiter so für Fettstoffwechselaufbau.",
                },
            }
        }
        self.reviews_file.write_text(json.dumps(data), encoding="utf-8")

    def run_generator_with_reviews(self) -> None:
        self.run_generator(extra_args=["--run-reviews", str(self.reviews_file)])

    def assert_html_contains_responsive_activity_cards(self) -> None:
        content = self.output_file.read_text(encoding="utf-8")
        assert "run-row" in content
        assert "col-activity" in content
        assert "col-dist" in content
        assert "data-label=" in content
        assert "#runsTable tr.run-row" in content

    def assert_html_contains_expandable_review_details(self) -> None:
        content = self.output_file.read_text(encoding="utf-8")
        assert "run-review-details" in content
        assert "run-review-summary" in content
        assert "Optimaler Grundlagenlauf" in content
        assert "Exzellente aerobe Zonendisziplin" in content
        assert "Analyse anzeigen" in content

    def assert_html_contains_compact_kpi_grid(self) -> None:
        content = self.output_file.read_text(encoding="utf-8")
        assert ".kpi-grid" in content
        assert "repeat(2, 1fr)" in content



# 3. Fixture injecting the Driver
@pytest.fixture
def driver(tmp_path: Path) -> DashboardGeneratorDriver:
    workspace_root = Path(__file__).resolve().parent.parent
    return DashboardGeneratorDriver(workspace_root, tmp_path)


# 4. Step Definitions
@given("an activities dataset with multiple running activities")
def given_dataset_with_runs(driver: DashboardGeneratorDriver):
    driver.prepare_dataset_with_runs()


@given("an activities dataset with no running activities")
def given_dataset_without_runs(driver: DashboardGeneratorDriver):
    driver.prepare_empty_dataset()


@given("the activities dataset file does not exist")
def given_dataset_not_found(driver: DashboardGeneratorDriver):
    driver.remove_input_file()


@given("the standard dashboard template is available")
def given_template_available(driver: DashboardGeneratorDriver):
    assert driver.template_file.exists(), f"Template not found at {driver.template_file}"


@when("the dashboard generator is executed")
def when_generator_executed(driver: DashboardGeneratorDriver):
    driver.run_generator()


@when("the dashboard generator is executed with a non-existent template")
def when_generator_executed_missing_template(driver: DashboardGeneratorDriver):
    missing_template = driver.tmp_path / "non_existent_template.html"
    driver.run_generator(template_path=missing_template)


@then(parsers.parse("the process exits with code {code:d}"))
def then_process_exits_with_code(driver: DashboardGeneratorDriver, code: int):
    driver.assert_exit_code(code)


@then("the process exits with an error")
def then_process_exits_with_error(driver: DashboardGeneratorDriver):
    driver.assert_exit_code_non_zero()


@then(parsers.parse('the error output mentions "{keyword}"'))
def then_error_mentions_keyword(driver: DashboardGeneratorDriver, keyword: str):
    driver.assert_output_contains(keyword)


@then("the output HTML file is created")
def then_html_file_created(driver: DashboardGeneratorDriver):
    driver.assert_output_html_exists()


@then("the output HTML contains the rendered KPI metrics")
def then_html_contains_kpi_metrics(driver: DashboardGeneratorDriver):
    driver.assert_html_contains_kpis()


@then("the output HTML embeds the weekly trends and activities data")
def then_html_embeds_data(driver: DashboardGeneratorDriver):
    driver.assert_html_embeds_data()


@then("the output HTML displays the empty state message")
def then_html_displays_empty_state(driver: DashboardGeneratorDriver):
    driver.assert_html_displays_empty_state()


@then("the output HTML contains German navigation and KPI headers")
def then_html_contains_german_ui(driver: DashboardGeneratorDriver):
    driver.assert_html_contains_german_ui()


@then("the output HTML contains training summary and coaching recommendations")
def then_html_contains_summary(driver: DashboardGeneratorDriver):
    driver.assert_html_contains_summary_and_recommendations()


@then("the output HTML embeds race time predictions and volume forecast data")
def then_html_contains_forecasts(driver: DashboardGeneratorDriver):
    driver.assert_html_contains_forecasts()


@given("an AI coach summary JSON file with custom dynamic insights")
def given_ai_summary_file(driver: DashboardGeneratorDriver):
    driver.prepare_ai_summary_file()


@when("the dashboard generator is executed with the AI coach summary file")
def when_generator_executed_with_ai_summary(driver: DashboardGeneratorDriver):
    driver.run_generator_with_ai_summary()


@then("the output HTML contains the custom dynamic AI insights")
def then_html_contains_dynamic_ai_insights(driver: DashboardGeneratorDriver):
    driver.assert_html_contains_dynamic_ai_insights()


@given("an activities dataset with multiple running activities and strava placeholders")
def given_dataset_with_strava_placeholders(driver: DashboardGeneratorDriver):
    driver.prepare_dataset_with_strava_placeholders()


@then("the output HTML contains interactive date range filter buttons")
def then_html_contains_interactive_filters(driver: DashboardGeneratorDriver):
    driver.assert_html_contains_interactive_filters()


@then("the output HTML contains sortable table columns and pagination controls")
def then_html_contains_table_controls(driver: DashboardGeneratorDriver):
    driver.assert_html_contains_table_controls()


@then("the output HTML displays the data source transparency notice")
def then_html_displays_datasource_notice(driver: DashboardGeneratorDriver):
    driver.assert_html_contains_datasource_notice()


@then("the output HTML contains responsive layout styling for date range filters")
def then_html_contains_responsive_filters(driver: DashboardGeneratorDriver):
    driver.assert_html_contains_responsive_filters()


@then("the output HTML volume chart preserves forecast visualization across date filters")
def then_html_volume_chart_preserves_forecast(driver: DashboardGeneratorDriver):
    driver.assert_volume_chart_preserves_forecast_across_filters()


@then("the output HTML contains reload controls and mobile refresh mechanisms")
def then_html_contains_reload_controls(driver: DashboardGeneratorDriver):
    driver.assert_html_contains_reload_controls()


@given("an athlete profile with custom heart rate zones")
def given_athlete_profile_with_custom_zones(driver: DashboardGeneratorDriver):
    driver.prepare_athlete_profile_with_custom_zones()


@when("the dashboard generator is executed with the athlete profile")
def when_generator_executed_with_athlete_profile(driver: DashboardGeneratorDriver):
    driver.run_generator_with_athlete_profile()


@then("the output HTML contains the calibrated zone ranges")
def then_html_contains_calibrated_zones(driver: DashboardGeneratorDriver):
    driver.assert_html_contains_calibrated_zones()


@given("an activities dataset with multiple running activities and coach reviews")
def given_dataset_with_runs_and_reviews(driver: DashboardGeneratorDriver):
    driver.prepare_dataset_with_reviews()


@when("the dashboard generator is executed with reviews data")
def when_generator_executed_with_reviews(driver: DashboardGeneratorDriver):
    driver.run_generator_with_reviews()


@then("the output HTML contains responsive mobile styling for activity cards")
def then_html_contains_responsive_cards(driver: DashboardGeneratorDriver):
    driver.assert_html_contains_responsive_activity_cards()


@then("the output HTML contains expandable run review details")
def then_html_contains_expandable_reviews(driver: DashboardGeneratorDriver):
    driver.assert_html_contains_expandable_review_details()


@then("the output HTML contains compact two-column KPI grid for mobile")
def then_html_contains_compact_kpi(driver: DashboardGeneratorDriver):
    driver.assert_html_contains_compact_kpi_grid()



