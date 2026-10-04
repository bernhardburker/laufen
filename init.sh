#!/usr/bin/env bash
# ==============================================================================
# init.sh - System Inspection and Environment Initialization
#
# Checks system Python, manages virtual environment (.venv), and exports vital
# execution paths (.paths.env and .paths.json) for agents, CLI tools, and scripts.
# ==============================================================================

set -euo pipefail

show_help() {
  cat <<'EOF'
Usage: init.sh [OPTIONS]

Inspect the system environment, verify or create the virtual environment,
and export essential paths to .paths.env and .paths.json.

Options:
  -w, --workspace DIR     Workspace root directory (default: directory containing init.sh)
  -v, --venv DIR          Virtual environment directory (default: <workspace>/.venv)
  -p, --python BIN        System Python executable to inspect/use (default: autodetect)
  -e, --env-file FILE     Path for exported shell environment file (default: <workspace>/.paths.env)
  -j, --json-file FILE    Path for exported JSON paths file (default: <workspace>/.paths.json)
  -c, --check-only        Inspect and report only; do not create venv or write files
      --no-create         Do not create virtual environment if missing (fail instead)
  -q, --quiet             Quiet mode; suppress informative progress messages
  -h, --help              Show this help message and exit

Examples:
  ./init.sh               # Initialize environment and write .paths.env / .paths.json
  source ./init.sh        # Initialize and immediately activate in current shell
  ./init.sh --check-only  # Inspect system without modifying any files
EOF
}

WORKSPACE_ROOT=""
VENV_DIR=""
PYTHON_BIN=""
ENV_FILE=""
JSON_FILE=""
CHECK_ONLY=0
NO_CREATE=0
QUIET=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    -w|--workspace)
      WORKSPACE_ROOT="$2"
      shift 2
      ;;
    -v|--venv)
      VENV_DIR="$2"
      shift 2
      ;;
    -p|--python)
      PYTHON_BIN="$2"
      shift 2
      ;;
    -e|--env-file)
      ENV_FILE="$2"
      shift 2
      ;;
    -j|--json-file)
      JSON_FILE="$2"
      shift 2
      ;;
    -c|--check-only)
      CHECK_ONLY=1
      shift
      ;;
    --no-create)
      NO_CREATE=1
      shift
      ;;
    -q|--quiet)
      QUIET=1
      shift
      ;;
    -h|--help)
      show_help
      exit 0
      ;;
    *)
      echo "Error: Unknown option '$1'" >&2
      exit 1
      ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ -z "$WORKSPACE_ROOT" ]]; then
  WORKSPACE_ROOT="$SCRIPT_DIR"
fi
WORKSPACE_ROOT="$(cd "$WORKSPACE_ROOT" && pwd)"

if [[ -z "$VENV_DIR" ]]; then
  VENV_DIR="$WORKSPACE_ROOT/.venv"
fi

if [[ -z "$ENV_FILE" ]]; then
  ENV_FILE="$WORKSPACE_ROOT/.paths.env"
fi

if [[ -z "$JSON_FILE" ]]; then
  JSON_FILE="$WORKSPACE_ROOT/.paths.json"
fi

# 1. Locate and verify system Python interpreter
if [[ -n "$PYTHON_BIN" ]]; then
  if [[ ! -x "$PYTHON_BIN" ]]; then
    echo "Error: Python not found or not executable at: $PYTHON_BIN" >&2
    exit 1
  fi
  SYS_PYTHON="$PYTHON_BIN"
else
  if command -v python3 >/dev/null 2>&1; then
    SYS_PYTHON="$(command -v python3)"
  elif command -v python >/dev/null 2>&1; then
    SYS_PYTHON="$(command -v python)"
  else
    echo "Error: Python not found. Please install Python 3 on your system." >&2
    exit 1
  fi
fi

SYS_PYTHON_VERSION="$("$SYS_PYTHON" -c 'import sys; print(f"{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}")' 2>/dev/null || true)"
if [[ -z "$SYS_PYTHON_VERSION" ]]; then
  echo "Error: Python interpreter at $SYS_PYTHON failed to execute." >&2
  exit 1
fi

# 2. Inspect or provision virtual environment
VENV_STATUS="UNKNOWN"
if [[ ! -d "$VENV_DIR" || ! -x "$VENV_DIR/bin/python" ]]; then
  if [[ $CHECK_ONLY -eq 1 ]]; then
    VENV_STATUS="NOT_FOUND"
  elif [[ $NO_CREATE -eq 1 ]]; then
    echo "Error: Virtual environment not found at $VENV_DIR and --no-create was specified." >&2
    exit 1
  else
    if [[ $QUIET -eq 0 ]]; then
      echo "[INFO] Creating virtual environment at $VENV_DIR..."
    fi
    if ! "$SYS_PYTHON" -m venv "$VENV_DIR" 2>/dev/null; then
      # Fallback: create venv without pip (handles Debian/Ubuntu without python3-venv ensurepip)
      "$SYS_PYTHON" -m venv --without-pip "$VENV_DIR"
      # If reference pip exists in repo root venv or local user workspace, bootstrap it into the new venv
      REF_PIP=""
      if [[ -f "$SCRIPT_DIR/.venv/bin/pip" && -d "$SCRIPT_DIR/.venv/lib" ]]; then
        REF_PIP="$SCRIPT_DIR/.venv"
      elif [[ -f "/home/berni/laufen/.venv/bin/pip" && -d "/home/berni/laufen/.venv/lib" ]]; then
        REF_PIP="/home/berni/laufen/.venv"
      fi

      if [[ -n "$REF_PIP" && ! -f "$VENV_DIR/bin/pip" ]]; then
        cp -r "$REF_PIP/lib"/python*/site-packages/pip* "$VENV_DIR/lib"/python*/site-packages/ 2>/dev/null || true
        cp "$REF_PIP/bin/pip"* "$VENV_DIR/bin/" 2>/dev/null || true
        for p in "$VENV_DIR/bin/pip"*; do
          if [[ -f "$p" ]]; then
            sed -i "1s|.*|#\!$VENV_DIR/bin/python|" "$p" 2>/dev/null || true
          fi
        done
      fi

      if [[ ! -x "$VENV_DIR/bin/pip" ]]; then
        # Bootstrap pip via get-pip.py
        curl -sSL https://bootstrap.pypa.io/get-pip.py -o /tmp/get-pip.py 2>/dev/null || true
        if [[ -f /tmp/get-pip.py ]]; then
          "$VENV_DIR/bin/python" /tmp/get-pip.py -q 2>/dev/null || true
          rm -f /tmp/get-pip.py
        fi
      fi
    fi
    if [[ -f "$WORKSPACE_ROOT/requirements.txt" && -x "$VENV_DIR/bin/pip" ]]; then
      if [[ $QUIET -eq 0 ]]; then
        echo "[INFO] Installing dependencies from $WORKSPACE_ROOT/requirements.txt..."
      fi
      "$VENV_DIR/bin/pip" install -q -r "$WORKSPACE_ROOT/requirements.txt"
    fi
    VENV_STATUS="CREATED"
  fi
else
  VENV_STATUS="EXISTS"
fi

# 3. Resolve executables
VENV_PYTHON=""
VENV_PYTEST=""
VENV_PIP=""

if [[ -x "$VENV_DIR/bin/python" ]]; then
  VENV_PYTHON="$VENV_DIR/bin/python"
fi

if [[ -x "$VENV_DIR/bin/pytest" ]]; then
  VENV_PYTEST="$VENV_DIR/bin/pytest"
fi

if [[ -x "$VENV_DIR/bin/pip" ]]; then
  VENV_PIP="$VENV_DIR/bin/pip"
fi

# 4. Generate path output files (unless check-only)
if [[ $CHECK_ONLY -eq 0 ]]; then
  mkdir -p "$(dirname "$ENV_FILE")"
  cat <<EOF > "$ENV_FILE"
# Auto-generated by init.sh on $(date -u +"%Y-%m-%dT%H:%M:%SZ")
# Usage: source $(basename "$ENV_FILE")

export WORKSPACE_ROOT="$WORKSPACE_ROOT"
export VIRTUAL_ENV="$VENV_DIR"
export PYTHON_EXEC="$VENV_PYTHON"
export PYTEST_EXEC="$VENV_PYTEST"
export PIP_EXEC="$VENV_PIP"

# Prepend virtual environment binaries to PATH if present
if [[ -d "$VENV_DIR/bin" && ":\$PATH:" != *":$VENV_DIR/bin:"* ]]; then
    export PATH="$VENV_DIR/bin:\$PATH"
fi
EOF
  chmod +x "$ENV_FILE" 2>/dev/null || true

  mkdir -p "$(dirname "$JSON_FILE")"
  cat <<EOF > "$JSON_FILE"
{
  "workspace_root": "$WORKSPACE_ROOT",
  "virtual_env": "$VENV_DIR",
  "python": "$VENV_PYTHON",
  "pytest": "$VENV_PYTEST",
  "pip": "$VENV_PIP",
  "system_python": "$SYS_PYTHON",
  "python_version": "$SYS_PYTHON_VERSION",
  "config_dir": "$WORKSPACE_ROOT/config",
  "data_dir": "$WORKSPACE_ROOT/data",
  "src_dir": "$WORKSPACE_ROOT/src",
  "tests_dir": "$WORKSPACE_ROOT/tests"
}
EOF
fi

# 5. Output inspection summary
if [[ $QUIET -eq 0 ]]; then
  cat <<EOF
======================================================================
System & Environment Initialization
======================================================================
Workspace:      $WORKSPACE_ROOT
System Python:  $SYS_PYTHON (version: $SYS_PYTHON_VERSION)
Virtual Env:    $VENV_DIR ($VENV_STATUS)
Python Exec:    ${VENV_PYTHON:-[Not Found]}
Pytest Exec:    ${VENV_PYTEST:-[Not Found]}
Pip Exec:       ${VENV_PIP:-[Not Found]}
EOF

  if [[ $CHECK_ONLY -eq 0 ]]; then
    cat <<EOF
----------------------------------------------------------------------
Generated path files:
  - Shell: $ENV_FILE
  - JSON:  $JSON_FILE

Usage:
  source $(basename "$ENV_FILE")
  python --version
  pytest tests/
======================================================================
EOF
  else
    cat <<EOF
----------------------------------------------------------------------
Check-only mode: No path files or virtual environments were created.
======================================================================
EOF
  fi
fi

# 6. If sourced into an active shell, export variables directly
if (return 0 2>/dev/null); then
  export WORKSPACE_ROOT="$WORKSPACE_ROOT"
  export VIRTUAL_ENV="$VENV_DIR"
  export PYTHON_EXEC="$VENV_PYTHON"
  export PYTEST_EXEC="$VENV_PYTEST"
  export PIP_EXEC="$VENV_PIP"
  if [[ -d "$VENV_DIR/bin" && ":$PATH:" != *":$VENV_DIR/bin:"* ]]; then
    export PATH="$VENV_DIR/bin:$PATH"
  fi
fi
