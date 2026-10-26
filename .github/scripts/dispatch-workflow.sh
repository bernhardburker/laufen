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

GH_ARGS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --athlete|-a)
      GH_ARGS+=("-f" "athlete=${2:-}")
      shift 2 || true
      ;;
    --force-full-history)
      GH_ARGS+=("-f" "force_full_history=true")
      shift
      ;;
    *)
      GH_ARGS+=("$1")
      shift
      ;;
  esac
done

if [[ ${#GH_ARGS[@]} -gt 0 ]]; then
  gh workflow run "$WORKFLOW" --repo "$REPO" --ref "$REF" "${GH_ARGS[@]}"
else
  gh workflow run "$WORKFLOW" --repo "$REPO" --ref "$REF"
fi

echo "[$(date '+%Y-%m-%d %H:%M:%S')] Workflow dispatch successfully triggered."
