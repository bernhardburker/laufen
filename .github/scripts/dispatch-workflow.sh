#!/usr/bin/env bash
# ==============================================================================
# dispatch-workflow.sh
# Triggers the GitHub Actions "Deploy Dashboard to GitHub Pages" workflow via gh CLI.
# Designed for execution via cron or manual dispatch.
# ==============================================================================

set -euo pipefail

# Ensure standard binaries and gh CLI are in PATH
export PATH="/usr/local/bin:/usr/bin:/bin:$PATH"

REPO="bernhardburker/laufen"
WORKFLOW="deploy-pages.yml"
REF="main"

echo "[$(date '+%Y-%m-%d %H:%M:%S')] Triggering workflow '$WORKFLOW' on repository '$REPO' (ref: $REF)..."

if ! command -v gh >/dev/null 2>&1; then
  echo "[$(date '+%Y-%m-%d %H:%M:%S')] Error: GitHub CLI ('gh') is not installed or not in PATH." >&2
  exit 1
fi

gh workflow run "$WORKFLOW" --repo "$REPO" --ref "$REF" "$@"

echo "[$(date '+%Y-%m-%d %H:%M:%S')] Workflow dispatch successfully triggered."
