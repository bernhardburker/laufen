"""LLM Client Abstraction Layer.

Provides a unified interface for invoking Large Language Models across multiple
providers (Antigravity CLI 'agy', Google Gemini API, and Mock/Stub testing providers).
"""

from __future__ import annotations

import abc
from dataclasses import dataclass, field
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
from typing import Any, Callable, Dict, List, Optional, Union
import urllib.error
import urllib.request


@dataclass
class LLMResult:
    """Encapsulates the outcome of an LLM generation request."""

    text: str
    success: bool
    error: Optional[str] = None
    model: Optional[str] = None
    raw_output: Optional[str] = None
    metadata: Dict[str, Any] = field(default_factory=dict)


class LLMClient(abc.ABC):
    """Abstract base class for Large Language Model clients."""

    @abc.abstractmethod
    def generate(
        self, prompt: str, system_prompt: Optional[str] = None
    ) -> LLMResult:
        """Generate a response from the LLM given a user prompt and optional system prompt."""
        raise NotImplementedError


class MockLLMClient(LLMClient):
    """Configurable mock/stub LLM client for automated testing and local debugging."""

    def __init__(
        self,
        responses: Optional[
            Union[str, Dict[str, Any], List[Union[str, Dict[str, Any]]]]
        ] = None,
        should_fail: bool = False,
        error_message: str = "Simulated LLM execution failure",
        simulate_timeout: bool = False,
        response_factory: Optional[Callable[[str], str]] = None,
        model: str = "mock-model",
    ):
        self.model = model
        self.should_fail = should_fail
        self.error_message = error_message
        self.simulate_timeout = simulate_timeout
        self.response_factory = response_factory
        self.calls: List[Dict[str, Any]] = []

        self._responses: List[str] = []
        if responses is not None:
            if isinstance(responses, list):
                for r in responses:
                    self._responses.append(
                        json.dumps(r) if isinstance(r, dict) else str(r)
                    )
            elif isinstance(responses, dict):
                self._responses.append(json.dumps(responses))
            else:
                self._responses.append(str(responses))

    def add_response(self, response: Union[str, Dict[str, Any]]) -> None:
        """Enqueue an additional mock response."""
        self._responses.append(
            json.dumps(response) if isinstance(response, dict) else str(response)
        )

    def reset(self) -> None:
        """Reset mock history and queue."""
        self.calls.clear()
        self._responses.clear()

    @property
    def last_prompt(self) -> Optional[str]:
        """Return the most recent prompt received."""
        if not self.calls:
            return None
        return self.calls[-1].get("prompt")

    def generate(
        self, prompt: str, system_prompt: Optional[str] = None
    ) -> LLMResult:
        """Execute mock generation, recording prompt and returning configured output."""
        call_entry = {
            "prompt": prompt,
            "system_prompt": system_prompt,
            "timestamp": time.time(),
        }
        self.calls.append(call_entry)

        if self.simulate_timeout:
            return LLMResult(
                text="",
                success=False,
                error="LLM request timed out after simulation",
                model=self.model,
                raw_output=None,
                metadata={"call_count": len(self.calls)},
            )

        if self.should_fail:
            return LLMResult(
                text="",
                success=False,
                error=self.error_message,
                model=self.model,
                raw_output=None,
                metadata={"call_count": len(self.calls)},
            )

        if self.response_factory:
            response_text = self.response_factory(prompt)
        elif self._responses:
            if len(self._responses) == 1:
                response_text = self._responses[0]
            else:
                response_text = self._responses.pop(0)
        else:
            default_dict = {
                "status_title": "Solider Grundlagenaufbau",
                "status_badge": "Basis stabil",
                "status_level": "info",
                "coach_commentary": "### Analyse\nSolide aerobe Einheiten im Grundlagenbereich.",
                "history_evaluation": {
                    "compliance_status": "erfüllt",
                    "actual_summary": "Absolvierte Einheiten im aeroben Grundlagenbereich.",
                    "effect_analysis": "Stabiles aerobes Fundament.",
                    "next_recommendation": "Weiterhin lockere Zone 2 Läufe.",
                    "next_workout_type": "easy_run",
                    "next_target_hr": 148,
                    "next_duration_min": 45,
                },
                "insights": [
                    {
                        "title": "Aerobe Disziplin",
                        "type": "info",
                        "badge": "Z1/Z2",
                        "text": "Überwiegend im Grundlagenbereich trainiert.",
                    }
                ],
                "recommendations": [
                    {
                        "title": "Zone 2 beibehalten",
                        "tag": "Basis",
                        "tag_class": "tag-amber",
                        "text": "Puls diszipliniert unter der aeroben Obergrenze halten.",
                    }
                ],
            }
            response_text = json.dumps(default_dict)

        return LLMResult(
            text=response_text,
            success=True,
            error=None,
            model=self.model,
            raw_output=response_text,
            metadata={"call_count": len(self.calls)},
        )


def parse_timeout_string(timeout_val: Union[str, int, float]) -> int:
    """Parse timeout representations like '3m', '180s', 180 into integer seconds."""
    if isinstance(timeout_val, (int, float)):
        return max(1, int(timeout_val))

    val_str = str(timeout_val).strip().lower()
    if val_str.endswith("m"):
        try:
            return max(1, int(val_str[:-1]) * 60)
        except ValueError:
            pass
    elif val_str.endswith("s"):
        try:
            return max(1, int(val_str[:-1]))
        except ValueError:
            pass

    try:
        return max(1, int(val_str))
    except ValueError:
        return 180


class AgyLLMClient(LLMClient):
    """Antigravity CLI ('agy') LLM client using subprocess execution."""

    def __init__(
        self,
        agy_bin: Optional[Union[str, Path]] = None,
        model: Optional[str] = None,
        timeout: Union[str, int] = "3m",
        sandbox: bool = True,
        dangerously_skip_permissions: bool = True,
        disable_slash_commands: bool = True,
        extra_args: Optional[List[str]] = None,
    ):
        self.explicit_bin = Path(agy_bin) if agy_bin else None
        self.model = model or os.getenv("AGY_MODEL", "Gemini 3.8 Flash (Medium)")
        self.timeout_str = os.getenv("AGY_TIMEOUT", str(timeout))
        self.timeout_seconds = parse_timeout_string(self.timeout_str)
        self.sandbox = sandbox
        self.dangerously_skip_permissions = dangerously_skip_permissions
        self.disable_slash_commands = disable_slash_commands
        self.extra_args = extra_args or []

    def find_binary(self) -> Optional[Path]:
        """Locate the agy executable on the host system."""
        if self.explicit_bin:
            if self.explicit_bin.is_file() and os.access(self.explicit_bin, os.X_OK):
                return self.explicit_bin
            return None

        env_bin = os.getenv("AGY_BIN")
        if env_bin:
            candidate = Path(env_bin)
            if candidate.is_file() and os.access(candidate, os.X_OK):
                return candidate

        which_path = shutil.which("agy")
        if which_path:
            return Path(which_path)

        user_bin = Path.home() / ".local" / "bin" / "agy"
        if user_bin.is_file() and os.access(user_bin, os.X_OK):
            return user_bin

        return None

    def _inline_artifact_if_present(self, raw_text: str) -> str:
        """If agy output references a local file:// artifact, read and return its content."""
        match = re.search(r"file://([^\s)]+\.(?:md|json|txt))", raw_text)
        if match:
            file_path = Path(match.group(1))
            if file_path.is_file():
                try:
                    return file_path.read_text(encoding="utf-8")
                except Exception:
                    pass
        return raw_text

    def generate(
        self, prompt: str, system_prompt: Optional[str] = None
    ) -> LLMResult:
        """Execute agy in non-interactive pipeline mode."""
        binary = self.find_binary()
        if not binary:
            return LLMResult(
                text="",
                success=False,
                error="agy CLI binary not found. Ensure agy is installed and in PATH or set AGY_BIN.",
                model=self.model,
            )

        cmd = [
            str(binary),
            "--model",
            self.model,
            "--print-timeout",
            self.timeout_str,
            "--input-format",
            "text",
            "--output-format",
            "text",
        ]

        if self.sandbox:
            cmd.append("--sandbox")
        if self.dangerously_skip_permissions:
            cmd.append("--dangerously-skip-permissions")
        if self.disable_slash_commands:
            cmd.append("--disable-slash-commands")

        cmd.extend(self.extra_args)

        full_prompt = prompt
        if system_prompt:
            full_prompt = f"System Instruction:\n{system_prompt}\n\nUser Request:\n{prompt}"

        try:
            start_time = time.time()
            proc = subprocess.run(
                cmd,
                input=full_prompt,
                capture_output=True,
                text=True,
                timeout=self.timeout_seconds,
            )
            elapsed = time.time() - start_time

            if proc.returncode != 0:
                err_msg = (
                    proc.stderr.strip()
                    or f"agy process exited with non-zero code {proc.returncode}"
                )
                return LLMResult(
                    text="",
                    success=False,
                    error=err_msg,
                    model=self.model,
                    raw_output=proc.stdout,
                    metadata={"exit_code": proc.returncode, "elapsed_s": elapsed},
                )

            raw_stdout = proc.stdout or ""
            final_text = self._inline_artifact_if_present(raw_stdout)

            # Check for minimum substance (at least 2 non-whitespace lines or valid JSON/Markdown)
            non_empty_lines = [line for line in final_text.splitlines() if line.strip()]
            if len(non_empty_lines) < 2 and "{" not in final_text:
                return LLMResult(
                    text=final_text,
                    success=False,
                    error=f"agy returned insufficient output ({len(non_empty_lines)} lines)",
                    model=self.model,
                    raw_output=raw_stdout,
                    metadata={"elapsed_s": elapsed},
                )

            return LLMResult(
                text=final_text,
                success=True,
                error=None,
                model=self.model,
                raw_output=raw_stdout,
                metadata={"elapsed_s": elapsed, "exit_code": 0},
            )

        except subprocess.TimeoutExpired:
            return LLMResult(
                text="",
                success=False,
                error=f"agy execution timed out after {self.timeout_seconds} seconds",
                model=self.model,
                metadata={"timeout": self.timeout_seconds},
            )
        except Exception as e:
            return LLMResult(
                text="",
                success=False,
                error=f"Unexpected error executing agy: {e}",
                model=self.model,
            )


class GeminiAPIClient(LLMClient):
    """Direct HTTP REST client for Google Gemini API."""

    def __init__(
        self,
        api_key: Optional[str] = None,
        model: str = "gemini-2.5-flash",
        timeout_seconds: int = 60,
    ):
        self.api_key = api_key or os.getenv("GEMINI_API_KEY", "")
        self.model = model
        self.timeout_seconds = timeout_seconds

    def generate(
        self, prompt: str, system_prompt: Optional[str] = None
    ) -> LLMResult:
        if not self.api_key:
            return LLMResult(
                text="",
                success=False,
                error="GEMINI_API_KEY is not set. Cannot invoke Gemini API.",
                model=self.model,
            )

        url = f"https://generativelanguage.googleapis.com/v1beta/models/{self.model}:generateContent?key={self.api_key}"
        headers = {"Content-Type": "application/json"}

        contents: List[Dict[str, Any]] = []
        if system_prompt:
            contents.append({"role": "user", "parts": [{"text": f"SYSTEM: {system_prompt}"}]})
            contents.append({"role": "model", "parts": [{"text": "Understood."}]})

        contents.append({"role": "user", "parts": [{"text": prompt}]})

        payload = {
            "contents": contents,
            "generationConfig": {
                "temperature": 0.2,
            },
        }

        try:
            req_data = json.dumps(payload).encode("utf-8")
            req = urllib.request.Request(url, data=req_data, headers=headers, method="POST")
            with urllib.request.urlopen(req, timeout=self.timeout_seconds) as resp:
                resp_bytes = resp.read()
                data = json.loads(resp_bytes.decode("utf-8"))

            candidates = data.get("candidates", [])
            if not candidates:
                return LLMResult(
                    text="",
                    success=False,
                    error="No candidates returned from Gemini API",
                    model=self.model,
                    raw_output=json.dumps(data),
                )

            parts = candidates[0].get("content", {}).get("parts", [])
            text_result = "".join(p.get("text", "") for p in parts)
            return LLMResult(
                text=text_result,
                success=True,
                error=None,
                model=self.model,
                raw_output=text_result,
            )

        except urllib.error.HTTPError as e:
            error_body = e.read().decode("utf-8", errors="replace")
            return LLMResult(
                text="",
                success=False,
                error=f"Gemini API HTTP Error {e.code}: {error_body}",
                model=self.model,
            )
        except Exception as e:
            return LLMResult(
                text="",
                success=False,
                error=f"Gemini API request failed: {e}",
                model=self.model,
            )


def get_llm_client(
    provider: Optional[str] = None,
    **kwargs: Any,
) -> LLMClient:
    """Factory creating an LLMClient instance based on specified or environment provider.

    Supported providers:
      - 'agy' (default): Antigravity CLI subprocess runner
      - 'mock': Mock/stub client for testing
      - 'gemini': Direct HTTP API client
    """
    chosen_provider = (
        provider or os.getenv("LLM_PROVIDER") or "agy"
    ).strip().lower()

    if chosen_provider in ("mock", "stub", "fake", "test"):
        return MockLLMClient(**kwargs)
    elif chosen_provider in ("gemini", "api", "google"):
        return GeminiAPIClient(**kwargs)
    elif chosen_provider in ("agy", "cli", "antigravity"):
        return AgyLLMClient(**kwargs)
    else:
        raise ValueError(
            f"Unknown LLM provider: '{chosen_provider}'. Expected one of: agy, mock, gemini"
        )
