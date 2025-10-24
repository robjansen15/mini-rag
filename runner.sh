#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BASE="$SCRIPT_DIR"
CUR="$BASE/current"

usage(){ echo "usage: $0 {setup|run|chat|extract|clean|reset} [args]"; exit 1; }

ensure_current(){
  mkdir -p "$CUR"
  cp -f "$BASE/setup.py" "$CUR/setup.py"
  cp -f "$BASE/run.py" "$CUR/run.py"
  cp -f "$BASE/chat.py" "$CUR/chat.py"
  cp -f "$BASE/extract.py" "$CUR/extract.py"
  chmod +x "$CUR/"*.py
}

cmd_setup(){
  ensure_current
  (cd "$CUR" && python3 ./setup.py)
  echo "ready: $CUR"
}

cmd_run(){
  [ -d "$CUR" ] || { echo "missing $CUR; run: $0 setup"; exit 2; }
  if [ -x "$CUR/venv/bin/python" ]; then (cd "$CUR" && ./venv/bin/python ./run.py)
  else (cd "$CUR" && python3 ./run.py); fi
}

cmd_chat(){
  [ -d "$CUR" ] || { echo "missing $CUR; run: $0 setup"; exit 2; }
  MSG="${1:-Hello}"
  if [ -x "$CUR/venv/bin/python" ]; then (cd "$CUR" && MSG="$MSG" ./venv/bin/python ./chat.py)
  else (cd "$CUR" && MSG="$MSG" python3 ./chat.py); fi
}

cmd_extract(){
  [ -d "$CUR" ] || { echo "missing $CUR; run: $0 setup"; exit 2; }
  PROJ="${1:-}"; DEPTH="${2:-}"
  [ -n "${PROJ}" ] || { echo "usage: $0 extract /path/to/project [max_depth]"; exit 3; }
  if [ -x "$CUR/venv/bin/python" ]; then
    if [ -n "$DEPTH" ]; then (cd "$CUR" && ./venv/bin/python ./extract.py "$PROJ" "$DEPTH")
    else (cd "$CUR" && ./venv/bin/python ./extract.py "$PROJ"); fi
  else
    if [ -n "$DEPTH" ]; then (cd "$CUR" && python3 ./extract.py "$PROJ" "$DEPTH")
    else (cd "$CUR" && python3 ./extract.py "$PROJ"); fi
  fi
}

cmd_clean(){ [ -d "$CUR" ] && rm -rf "$CUR"; echo "cleaned: $CUR"; }
cmd_reset(){ cmd_clean; cmd_setup; }

case "${1:-}" in
  setup)   cmd_setup ;;
  run)     shift; cmd_run "$@" ;;
  chat)    shift; cmd_chat "$@" ;;
  extract) shift; cmd_extract "$@" ;;
  clean)   cmd_clean ;;
  reset)   cmd_reset ;;
  *)       usage ;;
esac
