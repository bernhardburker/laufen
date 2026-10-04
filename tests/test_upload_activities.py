import csv
import http.server
import json
import os
import subprocess
import sys
import threading
from pathlib import Path

import pytest
from pytest_bdd import given, parsers, scenarios, then, when

# 1. Bind scenarios from feature file
scenarios("upload_activities.feature")


# 2. Mock HTTP Server for Intervals.icu Upload API
class MockIntervalsUploadHandler(http.server.BaseHTTPRequestHandler):
    received_uploads: list[dict] = []

    def do_POST(self):
        auth_header = self.headers.get("Authorization", "")
        if not auth_header.startswith("Basic "):
            self.send_response(401)
            self.end_headers()
            self.wfile.write(b'{"error": "Unauthorized"}')
            return

        content_length = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(content_length)

        MockIntervalsUploadHandler.received_uploads.append({
            "path": self.path,
            "size": len(body),
        })

        self.send_response(201)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(json.dumps({"id": f"i{len(MockIntervalsUploadHandler.received_uploads)}"}).encode("utf-8"))

    def log_message(self, format, *args):
        pass


# 3. Test Driver
class UploadActivitiesDriver:
    def __init__(self, workspace_root: Path, tmp_path: Path):
        self.workspace_root = workspace_root
        self.tmp_path = tmp_path
        self.env: dict[str, str] = os.environ.copy()
        self.process_result: subprocess.CompletedProcess | None = None
        self.mock_server: http.server.HTTPServer | None = None
        self.mock_server_thread: threading.Thread | None = None
        self.mock_server_port: int | None = None
        self.export_dir = tmp_path / "strava_export"
        self.intervals_json = tmp_path / "intervals_activities.json"

        MockIntervalsUploadHandler.received_uploads = []

    def set_api_key(self, key: str = "test_key_123") -> None:
        self.env["INTERVALS_API_KEY"] = key

    def unset_api_key(self) -> None:
        self.env.pop("INTERVALS_API_KEY", None)

    def start_mock_api(self) -> None:
        MockIntervalsUploadHandler.received_uploads = []
        self.mock_server = http.server.HTTPServer(("127.0.0.1", 0), MockIntervalsUploadHandler)
        self.mock_server_port = self.mock_server.server_address[1]
        self.mock_server_thread = threading.Thread(target=self.mock_server.serve_forever, daemon=True)
        self.mock_server_thread.start()

    def stop_mock_api(self) -> None:
        if self.mock_server:
            self.mock_server.shutdown()
            self.mock_server.server_close()

    def create_mock_export(self, count: int = 2) -> None:
        self.export_dir.mkdir(parents=True, exist_ok=True)
        acts_dir = self.export_dir / "activities"
        acts_dir.mkdir(parents=True, exist_ok=True)

        csv_path = self.export_dir / "activities.csv"
        rows = []
        for i in range(1, count + 1):
            filename = f"activities/act_{i}.tcx.gz"
            (self.export_dir / filename).write_bytes(b"dummy_tcx_gz_content")
            rows.append({
                "Aktivitäts-ID": f"1000{i}",
                "Aktivitätsdatum": f"0{i}.08.2025, 17:00:00",
                "Name der Aktivität": f"Test Run {i}",
                "Aktivitätsart": "Lauf",
                "Dateiname": filename,
                "Distanz": "5000.0",
            })

        with open(csv_path, "w", encoding="utf-8", newline="") as f:
            writer = csv.DictWriter(f, fieldnames=["Aktivitäts-ID", "Aktivitätsdatum", "Name der Aktivität", "Aktivitätsart", "Dateiname", "Distanz"])
            writer.writeheader()
            writer.writerows(rows)

        # Empty intervals_activities.json
        self.intervals_json.write_text("[]", encoding="utf-8")

    def run_upload(self, extra_args: list[str] | None = None) -> None:
        cmd = [
            sys.executable,
            str(self.workspace_root / "src" / "integrations" / "upload_activities.py"),
            "--export-dir", str(self.export_dir),
            "--intervals-json", str(self.intervals_json),
        ]
        if extra_args:
            cmd.extend(extra_args)

        self.process_result = subprocess.run(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            env=self.env,
        )

    def assert_exit_code(self, expected_code: int) -> None:
        assert self.process_result is not None
        assert self.process_result.returncode == expected_code

    def assert_stdout_contains(self, text: str) -> None:
        assert self.process_result is not None
        assert text in self.process_result.stdout

    def assert_stderr_contains(self, text: str) -> None:
        assert self.process_result is not None
        assert text in self.process_result.stderr or text in self.process_result.stdout

    def assert_mock_received_count(self, expected_count: int) -> None:
        assert len(MockIntervalsUploadHandler.received_uploads) == expected_count


# 4. Pytest Fixtures
@pytest.fixture
def driver(tmp_path: Path) -> UploadActivitiesDriver:
    workspace_root = Path(__file__).resolve().parent.parent
    drv = UploadActivitiesDriver(workspace_root, tmp_path)
    yield drv
    drv.stop_mock_api()


# 5. Step Definitions
@given("INTERVALS_API_KEY is configured")
def given_api_key(driver: UploadActivitiesDriver) -> None:
    driver.set_api_key()


@given("a mock Intervals.icu upload API server is running")
def given_mock_upload_server(driver: UploadActivitiesDriver) -> None:
    driver.start_mock_api()


@given("a valid Strava export directory with 2 activity files")
def given_valid_export(driver: UploadActivitiesDriver) -> None:
    driver.create_mock_export(count=2)


@when("the upload script is executed with non-existent export directory")
def when_execute_non_existent(driver: UploadActivitiesDriver) -> None:
    driver.export_dir = driver.tmp_path / "does_not_exist"
    driver.run_upload()


@when("the upload script is executed with dry-run flag")
def when_execute_dry_run(driver: UploadActivitiesDriver) -> None:
    driver.run_upload(["--dry-run"])


@when("the upload script is executed against the mock API")
def when_execute_against_mock(driver: UploadActivitiesDriver) -> None:
    mock_url = f"http://127.0.0.1:{driver.mock_server_port}/api/v1"
    driver.run_upload(["--api-base", mock_url])


@then("the process exits with code 0")
def then_exit_zero(driver: UploadActivitiesDriver) -> None:
    driver.assert_exit_code(0)


@then("the process exits with an error")
def then_exit_error(driver: UploadActivitiesDriver) -> None:
    assert driver.process_result is not None
    assert driver.process_result.returncode != 0


@then(parsers.parse('the error output mentions "{text}"'))
def then_error_mentions(driver: UploadActivitiesDriver, text: str) -> None:
    driver.assert_stderr_contains(text)


@then(parsers.parse('the stdout mentions "{text}"'))
def then_stdout_mentions(driver: UploadActivitiesDriver, text: str) -> None:
    driver.assert_stdout_contains(text)


@then("the stdout lists 2 activities to upload")
def then_stdout_lists_two(driver: UploadActivitiesDriver) -> None:
    driver.assert_stdout_contains("[01/02]")
    driver.assert_stdout_contains("[02/02]")


@then("the mock API received 2 uploaded files")
def then_mock_received_two(driver: UploadActivitiesDriver) -> None:
    driver.assert_mock_received_count(2)
