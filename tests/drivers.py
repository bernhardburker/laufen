"""Unified test drivers and shared mock servers for BDD testing.

Provides:
- MockIntervalsServer: In-process multi-threaded HTTP server simulating
  the full Intervals.icu REST API (athletes, activities, wellness, events).
- LiveIntervalsDriver: Encapsulates live API calls against the staging
  athlete account (berni-test) for end-to-end verification.
"""

from __future__ import annotations

import base64
import http.server
import json
from pathlib import Path
import threading
from typing import Any, Dict, List, Optional
import urllib.error
import urllib.request


class UnifiedIntervalsHandler(http.server.BaseHTTPRequestHandler):
    """HTTP handler mimicking the complete Intervals.icu REST API v1."""

    @property
    def mock_server(self) -> MockIntervalsServer:
        return getattr(self.server, "mock_instance")

    def log_message(self, format: str, *args: Any) -> None:
        """Suppress default HTTP server stderr output during test runs."""
        pass

    def _send_json(self, status: int, payload: Any) -> None:
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(json.dumps(payload).encode("utf-8"))

    def _read_body_json(self) -> Any:
        length = int(self.headers.get("Content-Length", 0))
        if length <= 0:
            return {}
        raw = self.rfile.read(length).decode("utf-8")
        try:
            return json.loads(raw)
        except Exception:
            return {}

    def _is_authorized(self) -> bool:
        auth_header = self.headers.get("Authorization", "")
        return auth_header.startswith("Basic ")

    def do_GET(self) -> None:
        if not self._is_authorized():
            self._send_json(401, {"error": "Unauthorized"})
            return

        server = self.mock_server

        # 1. Athlete profile: /api/v1/athlete/{id}
        if "/athlete/" in self.path and not any(
            sub in self.path for sub in ("/activities", "/wellness", "/events")
        ):
            parts = self.path.split("/athlete/")[1].split("?")[0].strip("/").split("/")
            athlete_id = parts[0]
            profile = server.athletes.get(athlete_id, server.default_profile)
            self._send_json(200, profile)
            return

        # 2. Activities: /api/v1/athlete/{id}/activities
        if "/activities" in self.path:
            self._send_json(200, server.activities)
            return

        # 3. Wellness: /api/v1/athlete/{id}/wellness
        if "/wellness" in self.path:
            self._send_json(200, server.wellness)
            return

        # 4. Events: /api/v1/athlete/{id}/events
        if "/events" in self.path:
            self._send_json(200, list(server.events.values()))
            return

        self._send_json(404, {"error": "Not Found", "path": self.path})

    def do_POST(self) -> None:
        if not self._is_authorized():
            self._send_json(401, {"error": "Unauthorized"})
            return

        server = self.mock_server
        body = self._read_body_json()

        # 1. Activities: /api/v1/athlete/{id}/activities
        if "/activities" in self.path:
            act_id = f"act_{len(server.activities) + 1}"
            record = {**body, "id": act_id}
            server.activities.append(record)
            self._send_json(201, record)
            return

        # 2. Events: /api/v1/athlete/{id}/events
        if "/events" in self.path:
            event_id = body.get("id") or (max(server.events.keys()) + 1 if server.events else 1001)
            event_record = {**body, "id": event_id}
            server.events[event_id] = event_record
            self._send_json(200, event_record)
            return

        # 3. Wellness: /api/v1/athlete/{id}/wellness
        if "/wellness" in self.path:
            server.wellness.append(body)
            self._send_json(200, body)
            return

        self._send_json(404, {"error": "Not Found", "path": self.path})

    def do_PUT(self) -> None:
        if not self._is_authorized():
            self._send_json(401, {"error": "Unauthorized"})
            return

        server = self.mock_server
        body = self._read_body_json()

        # Wellness PUT: /api/v1/athlete/{id}/wellness/{date}
        if "/wellness" in self.path:
            date_key = self.path.split("/wellness/")[-1].split("?")[0].strip("/")
            existing = [w for w in server.wellness if w.get("id") != date_key]
            record = {**body, "id": date_key}
            existing.append(record)
            server.wellness = existing
            self._send_json(200, record)
            return

        # Events PUT: /api/v1/athlete/{id}/events/{id}
        if "/events" in self.path:
            event_id_str = self.path.split("/events/")[-1].split("?")[0].strip("/")
            try:
                event_id = int(event_id_str)
            except ValueError:
                event_id = event_id_str  # type: ignore
            server.events[event_id] = {**body, "id": event_id}
            self._send_json(200, server.events[event_id])
            return

        self._send_json(404, {"error": "Not Found", "path": self.path})

    def do_DELETE(self) -> None:
        if not self._is_authorized():
            self._send_json(401, {"error": "Unauthorized"})
            return

        server = self.mock_server

        if "/events/" in self.path:
            event_id_str = self.path.split("/events/")[-1].split("?")[0].strip("/")
            try:
                event_id = int(event_id_str)
            except ValueError:
                event_id = event_id_str  # type: ignore

            if event_id in server.events:
                del server.events[event_id]
                self._send_json(200, {"status": "deleted"})
            else:
                self._send_json(404, {"error": "Event not found"})
            return

        self._send_json(404, {"error": "Not Found", "path": self.path})


class MockIntervalsServer:
    """Manages an in-process mock HTTP server for Intervals.icu testing."""

    def __init__(self, port: int = 0) -> None:
        self.server = http.server.HTTPServer(("127.0.0.1", port), UnifiedIntervalsHandler)
        self.server.mock_instance = self  # type: ignore
        self.port = self.server.server_address[1]
        self.base_url = f"http://127.0.0.1:{self.port}/api/v1"

        # Mock database state
        self.default_profile: Dict[str, Any] = {
            "id": "i771119",
            "name": "Berni Test Burker",
            "resting_hr": 55,
            "max_hr": 202,
            "lthr": 183,
            "icu_hr_zones": [154, 163, 172, 182, 187, 192, 202],
        }
        self.athletes: Dict[str, Dict[str, Any]] = {}
        self.activities: List[Dict[str, Any]] = []
        self.wellness: List[Dict[str, Any]] = []
        self.events: Dict[Any, Dict[str, Any]] = {}

        self._thread: Optional[threading.Thread] = None

    def start(self) -> None:
        self._thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self._thread.start()

    def stop(self) -> None:
        if self.server:
            self.server.shutdown()
            self.server.server_close()
        if self._thread and self._thread.is_alive():
            self._thread.join(timeout=2)


class LiveIntervalsDriver:
    """Driver executing real HTTP operations against Intervals.icu for staging athlete."""

    def __init__(
        self,
        api_key: str,
        athlete_id: str = "i771119",
        api_base_url: str = "https://intervals.icu/api/v1",
    ) -> None:
        self.api_key = api_key
        self.athlete_id = athlete_id
        self.api_base_url = api_base_url.rstrip("/")
        self.created_event_ids: List[Any] = []

    def _get_headers(self) -> Dict[str, str]:
        auth_string = f"API_KEY:{self.api_key}"
        auth_header = "Basic " + base64.b64encode(auth_string.encode("utf-8")).decode("ascii")
        return {
            "Authorization": auth_header,
            "Content-Type": "application/json",
            "User-Agent": "Mozilla/5.0 (compatible; LaufenTestDriver/1.0)",
            "Accept": "application/json",
        }

    def get_athlete_profile(self) -> Dict[str, Any]:
        url = f"{self.api_base_url}/athlete/{self.athlete_id}"
        req = urllib.request.Request(url, headers=self._get_headers(), method="GET")
        with urllib.request.urlopen(req, timeout=30) as resp:
            return json.loads(resp.read().decode("utf-8"))

    def get_wellness(self, oldest: str, newest: str) -> List[Dict[str, Any]]:
        url = f"{self.api_base_url}/athlete/{self.athlete_id}/wellness?oldest={oldest}&newest={newest}"
        req = urllib.request.Request(url, headers=self._get_headers(), method="GET")
        with urllib.request.urlopen(req, timeout=30) as resp:
            data = json.loads(resp.read().decode("utf-8"))
            if isinstance(data, list):
                return data
            return [data] if isinstance(data, dict) else []

    def create_workout_event(self, event_payload: Dict[str, Any]) -> Dict[str, Any]:
        url = f"{self.api_base_url}/athlete/{self.athlete_id}/events"
        data_bytes = json.dumps(event_payload).encode("utf-8")
        req = urllib.request.Request(url, data=data_bytes, headers=self._get_headers(), method="POST")
        with urllib.request.urlopen(req, timeout=30) as resp:
            res = json.loads(resp.read().decode("utf-8"))
            if "id" in res:
                self.created_event_ids.append(res["id"])
            return res

    def get_events(self, oldest: str, newest: str) -> List[Dict[str, Any]]:
        url = f"{self.api_base_url}/athlete/{self.athlete_id}/events?oldest={oldest}&newest={newest}"
        req = urllib.request.Request(url, headers=self._get_headers(), method="GET")
        with urllib.request.urlopen(req, timeout=30) as resp:
            return json.loads(resp.read().decode("utf-8"))

    def delete_event(self, event_id: Any) -> bool:
        url = f"{self.api_base_url}/athlete/{self.athlete_id}/events/{event_id}"
        req = urllib.request.Request(url, headers=self._get_headers(), method="DELETE")
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                if event_id in self.created_event_ids:
                    self.created_event_ids.remove(event_id)
                return resp.status in (200, 204)
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return True
            return False

    def cleanup_created_events(self) -> None:
        """Removes all events registered during the driver session."""
        for event_id in list(self.created_event_ids):
            try:
                self.delete_event(event_id)
            except Exception:
                pass
