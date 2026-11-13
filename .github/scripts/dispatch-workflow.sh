#!/usr/bin/env bash
# ==============================================================================
# dispatch-workflow.sh
# Triggers modular GitHub Actions workflows (coach-plan.yml, send-telegram.yml,
# post-run-review.yml, deploy-pages.yml) via gh CLI.
# ==============================================================================

set -euo pipefail

export PATH="/usr/local/bin:/usr/bin:/bin:$PATH"

REPO="${GITHUB_REPOSITORY:-bernhardburker/laufen}"
WORKFLOW="deploy-pages.yml"
REF="main"

if ! command -v gh >/dev/null 2>&1; then
  echo "[$(date '+%Y-%m-%d %H:%M:%S')] Error: GitHub CLI ('gh') is not installed or not in PATH." >&2
  exit 1
fi

GH_ARGS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --workflow|-w)
      WORKFLOW="${2:-deploy-pages.yml}"
      shift 2 || true
      ;;
    --mode|-m)
      if [[ "$WORKFLOW" == "deploy-pages.yml" ]]; then
        WORKFLOW="coach-plan.yml"
      fi
      GH_ARGS+=("-f" "mode=${2:-morning}")
      shift 2 || true
      ;;
    --athlete|-a)
      GH_ARGS+=("-f" "athlete=${2:-}")
      shift 2 || true
      ;;
    --target-date|--date)
      GH_ARGS+=("-f" "target_date=${2:-}")
      shift 2 || true
      ;;
    --action)
      GH_ARGS+=("-f" "action=${2:-}")
      shift 2 || true
      ;;
    --feedback)
      GH_ARGS+=("-f" "feedback=${2:-}")
      shift 2 || true
      ;;
    --run-id)
      if [[ "$WORKFLOW" == "deploy-pages.yml" ]]; then
        WORKFLOW="post-run-review.yml"
      fi
      GH_ARGS+=("-f" "run_id=${2:-}")
      shift 2 || true
      ;;
    --note)
      GH_ARGS+=("-f" "note=${2:-}")
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

echo "[$(date '+%Y-%m-%d %H:%M:%S')] Triggering workflow '$WORKFLOW' on repository '$REPO' (ref: $REF)..."

if [[ ${#GH_ARGS[@]} -gt 0 ]]; then
  gh workflow run "$WORKFLOW" --repo "$REPO" --ref "$REF" "${GH_ARGS[@]}"
else
  gh workflow run "$WORKFLOW" --repo "$REPO" --ref "$REF"
fi

echo "[$(date '+%Y-%m-%d %H:%M:%S')] Workflow dispatch successfully triggered."
