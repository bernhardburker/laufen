import http.server
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
from typing import Optional

import pytest
from pytest_bdd import given, parsers, scenarios, then, when

# 1. Bind feature scenarios
scenarios("sync_athlete.feature")


# 2. Mock HTTP Server
class MockAthleteHandler(http.server.BaseHTTPRequestHandler):
    athlete_data = {
        "id": "i9999",
        "name": "TestRunner",
        "icu_resting_hr": 48,
        "sportSettings": [
            {
                "types": ["Run"],
                "lthr": 165,
                "max_hr": 182,
                "hr_zones": [138, 147, 155, 164, 168, 173, 182],
                "hr_zone_names": [
                    "Recovery",
                    "Aerobic",
                    "Tempo",
                    "SubThreshold",
                    "SuperThreshold",
                    "Aerobic Capacity",
                    "Anaerobic",
                ],
            }
        ],
    }

    def do_GET(self):
        auth_header = self.headers.get("Authorization", "")
        if not auth_header.startswith("Basic "):
            self.send_response(401)
            self.end_headers()
            self.wfile.write(b'{"error": "Unauthorized"}')
            return

        if "/athlete/" in self.path:
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps(self.athlete_data).encode("utf-8"))
        else:
            self.send_response(404)
            self.end_headers()

    def log_message(self, format, *args):
        pass


# 3. Test Driver
class SyncAthleteDriver:
    def __init__(self, workspace_root: Path, tmp_path: Path):
        self.workspace_root = workspace_root
        self.tmp_path = tmp_path
        self.output_file = tmp_path / "athlete.json"
        self.env: dict[str, str] = os.environ.copy()
        self.env["DOTENV_PATH"] = str(tmp_path / "nonexistent.env")
        self.process_result: Optional[subprocess.CompletedProcess] = None
        self.mock_server: Optional[http.server.HTTPServer] = None
        self.mock_server_thread: Optional[threading.Thread] = None
        self.mock_server_port: Optional[int] = None

    def unset_api_key(self) -> None:
        self.env.pop("INTERVALS_API_KEY", None)

    def set_api_key(self, key: str = "mock_key") -> None:
        self.env["INTERVALS_API_KEY"] = key

    def start_mock_api(self) -> None:
        self.mock_server = http.server.HTTPServer(("127.0.0.1", 0), MockAthleteHandler)
        self.mock_server_port = self.mock_server.server_address[1]
        self.mock_server_thread = threading.Thread(target=self.mock_server.serve_forever, daemon=True)
        self.mock_server_thread.start()

    def stop_mock_api(self) -> None:
        if self.mock_server:
            self.mock_server.shutdown()
            self.mock_server.server_close()

    def run_sync(self, use_mock: bool = False) -> None:
        cmd = [
            sys.executable,
            str(self.workspace_root / "src" / "integrations" / "sync_athlete.py"),
            "--output",
            str(self.output_file),
        ]
        if use_mock and self.mock_server_port:
            cmd.extend(["--api-base", f"http://127.0.0.1:{self.mock_server_port}"])

        self.process_result = subprocess.run(
            cmd,
            cwd=self.workspace_root,
            env=self.env,
            capture_output=True,
            text=True,
        )

    def assert_exit_code(self, expected_code: int) -> None:
        assert self.process_result is not None
        assert self.process_result.returncode == expected_code, (
            f"Expected {expected_code}, got {self.process_result.returncode}.\n"
            f"STDOUT:\n{self.process_result.stdout}\n"
            f"STDERR:\n{self.process_result.stderr}"
        )

    def assert_exit_code_non_zero(self) -> None:
        assert self.process_result is not None
        assert self.process_result.returncode != 0, f"Expected non-zero exit code: {self.process_result.stdout}"

    def assert_output_contains(self, text: str) -> None:
        assert self.process_result is not None
        combined = (self.process_result.stdout or "") + (self.process_result.stderr or "")
        assert text in combined, f"Expected '{text}' in output:\n{combined}"

    def assert_athlete_config_created(self) -> None:
        assert self.output_file.is_file(), f"Output file not created at {self.output_file}"

    def assert_athlete_config_contains_zones(self) -> None:
        with open(self.output_file, "r", encoding="utf-8") as f:
            data = json.load(f)
        ath = data.get("athlete", {})
        assert ath.get("name") == "TestRunner"
        assert ath.get("id") == "i9999"
        hr = ath.get("heart_rate", {})
        assert hr.get("resting_hr") == 48
        assert hr.get("lthr") == 165
        assert hr.get("max_hr") == 182
        assert len(hr.get("zones", {})) >= 5


# 4. Pytest Fixture
@pytest.fixture
def driver(tmp_path: Path) -> SyncAthleteDriver:
    workspace_root = Path(__file__).resolve().parent.parent
    drv = SyncAthleteDriver(workspace_root, tmp_path)
    yield drv
    drv.stop_mock_api()


# 5. Step Definitions
@given("INTERVALS_API_KEY is not configured")
def given_no_api_key(driver: SyncAthleteDriver):
    driver.unset_api_key()


@given("INTERVALS_API_KEY is configured")
def given_api_key_configured(driver: SyncAthleteDriver):
    driver.set_api_key()


@given("a mock Intervals.icu API server is running with athlete profile data")
def given_mock_api(driver: SyncAthleteDriver):
    driver.start_mock_api()


@when("the athlete profile sync script is executed")
def when_sync_executed(driver: SyncAthleteDriver):
    driver.run_sync(use_mock=False)


@when("the athlete profile sync script is executed against the mock API")
def when_sync_against_mock(driver: SyncAthleteDriver):
    driver.run_sync(use_mock=True)


@then("the process exits with code 0")
def then_exits_0(driver: SyncAthleteDriver):
    driver.assert_exit_code(0)


@then("the process exits with an error")
def then_exits_error(driver: SyncAthleteDriver):
    driver.assert_exit_code_non_zero()


@then(parsers.parse('the error output mentions "{keyword}"'))
def then_error_mentions(driver: SyncAthleteDriver, keyword: str):
    driver.assert_output_contains(keyword)


@then("the athlete configuration JSON file is created")
def then_config_created(driver: SyncAthleteDriver):
    driver.assert_athlete_config_created()


@then("the athlete configuration contains the calibrated heart rate zones")
def then_config_contains_zones(driver: SyncAthleteDriver):
    driver.assert_athlete_config_contains_zones()


@then("the stdout report displays the athlete name and heart rate summary")
def then_stdout_displays_summary(driver: SyncAthleteDriver):
    driver.assert_output_contains("TestRunner")
    driver.assert_output_contains("LTHR")
