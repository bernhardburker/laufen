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

MODEL="${AGY_MODEL:-Gemini 3.8 Flash (Medium)}"
TIMEOUT="${AGY_TIMEOUT:-3m}"
JSON_OUTPUT="$REPO_ROOT/data/ai_coach_summary.json"

export AGY_BIN="$agy_bin"
export AGY_MODEL="$MODEL"
export AGY_TIMEOUT="$TIMEOUT"

echo "==> Running AI Coach engine (using abstracted LLM client with model '$MODEL')..."
if "$PYTHON_EXEC" "$REPO_ROOT/src/analysis/ai_coach.py" \
  --execute \
  --skip-if-no-pending \
  --input "$ACTIVITIES_FILE" \
  --athlete "$ATHLETE_FILE" \
  --history "$REPO_ROOT/data/coach_history.json" \
  --reviews "$REPO_ROOT/data/run_reviews.json" \
  --output-json "$JSON_OUTPUT" \
  --output-md "$OUTPUT_FILE" \
  --model "$MODEL" \
  --timeout "$TIMEOUT"; then

  if [ -f "$OUTPUT_FILE" ]; then
    echo "==> AI Coach output generated at: $OUTPUT_FILE"
    echo "----------------------------------------------------------------------"
    cat "$OUTPUT_FILE"
    echo "----------------------------------------------------------------------"
  fi
else
  echo "Warning: AI Coach execution encountered an error." >&2
fi

