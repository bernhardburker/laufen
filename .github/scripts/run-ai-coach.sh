#!/usr/bin/env bash
# ==============================================================================
# run-ai-coach.sh - Agentic AI Running Coach using Antigravity CLI (agy)
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

ACTIVITIES_FILE="${1:-$REPO_ROOT/data/intervals_activities.json}"
OUTPUT_FILE="${2:-$REPO_ROOT/data/ai_coach_commentary.md}"
ATHLETE_FILE="${3:-$REPO_ROOT/config/athlete.json}"

# Locate agy executable
find_agy() {
  local bin="${AGY_BIN:-$(command -v agy 2>/dev/null || echo "${HOME}/.local/bin/agy")}"
  if [ ! -x "$bin" ]; then
    echo "Warning: agy CLI not found at '$bin'." >&2
    return 1
  fi
  echo "$bin"
}

# Locate python
PYTHON_EXEC="${PYTHON_EXEC:-$REPO_ROOT/.venv/bin/python}"
if [ ! -x "$PYTHON_EXEC" ]; then
  PYTHON_EXEC="$(command -v python3 || echo "python")"
fi

cd "$REPO_ROOT"

if [ ! -f "$ACTIVITIES_FILE" ]; then
  echo "Warning: Activities file not found: $ACTIVITIES_FILE. Skipping AI coach generation." >&2
  exit 0
fi

agy_bin="$(find_agy || true)"
if [ -z "$agy_bin" ]; then
  echo "Info: agy CLI not available on this runner. Skipping agentic AI coach step."
  exit 0
fi

mkdir -p "$(dirname "$OUTPUT_FILE")"

PROMPT_TMP="$(mktemp)"
AI_OUTPUT_TMP="$(mktemp)"
AGY_STDERR_TMP="$(mktemp)"

cleanup() {
  rm -f "$PROMPT_TMP" "$AI_OUTPUT_TMP" "$AGY_STDERR_TMP"
}
trap cleanup EXIT

echo "==> Generating AI coach prompt from training data..."
"$PYTHON_EXEC" "$REPO_ROOT/src/analysis/ai_coach.py" \
  --input "$ACTIVITIES_FILE" \
  --athlete "$ATHLETE_FILE" \
  --history "$REPO_ROOT/data/coach_history.json" \
  --output-prompt "$PROMPT_TMP"

MODEL="${AGY_MODEL:-Gemini 3.7 Flash (Medium)}"
TIMEOUT="${AGY_TIMEOUT:-3m}"

echo "==> Invoking Antigravity CLI (agy) with model '$MODEL'..."
if "$agy_bin" \
  --model "$MODEL" \
  --sandbox \
  --dangerously-skip-permissions \
  --disable-slash-commands \
  --print-timeout "$TIMEOUT" \
  --input-format text \
  --output-format text < "$PROMPT_TMP" > "$AI_OUTPUT_TMP" 2> "$AGY_STDERR_TMP"; then

  # Check if output is non-empty
  non_ws_lines="$(grep -c '[^[:space:]]' "$AI_OUTPUT_TMP" || true)"
  if [ "$non_ws_lines" -ge 3 ]; then
    # Inlining fallback: if agy produced a file:// link artifact, read file directly
    if grep -Eq 'file://.*/[^)]+\.md' "$AI_OUTPUT_TMP"; then
      linked_artifact="$(grep -Eo 'file://[^)]+\.md' "$AI_OUTPUT_TMP" | head -n 1 | sed 's|^file://||')"
      if [ -f "$linked_artifact" ]; then
        echo "Inlining artifact from '$linked_artifact'..."
        cat "$linked_artifact" > "$AI_OUTPUT_TMP"
      fi
    fi

    # Parse structured JSON summary and extracted commentary
    JSON_OUTPUT="$REPO_ROOT/data/ai_coach_summary.json"
    if "$PYTHON_EXEC" "$REPO_ROOT/src/analysis/ai_coach.py" \
        --parse-response "$AI_OUTPUT_TMP" \
        --input "$ACTIVITIES_FILE" \
        --athlete "$ATHLETE_FILE" \
        --history "$REPO_ROOT/data/coach_history.json" \
        --output-json "$JSON_OUTPUT" \
        --output-md "$OUTPUT_FILE"; then
      echo "==> Dynamic AI summary & recommendations saved to: $JSON_OUTPUT"
    else
      echo "Warning: Response was not valid JSON, saving raw output as commentary." >&2
      cp "$AI_OUTPUT_TMP" "$OUTPUT_FILE"
    fi

    echo "==> AI Coach output successfully generated at: $OUTPUT_FILE"
    echo "----------------------------------------------------------------------"
    cat "$OUTPUT_FILE"
    echo "----------------------------------------------------------------------"
  else
    echo "Warning: agy returned insufficient output ($non_ws_lines lines)." >&2
    if [ -s "$AGY_STDERR_TMP" ]; then
      cat "$AGY_STDERR_TMP" >&2
    fi
  fi
else
  echo "Warning: agy execution failed with code $?." >&2
  if [ -s "$AGY_STDERR_TMP" ]; then
    cat "$AGY_STDERR_TMP" >&2
  fi
fi
