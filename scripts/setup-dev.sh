#!/usr/bin/env bash
# setup-dev.sh - prepares a ControlHome development clone (Linux/macOS/Git Bash). Safe to run again at any time.
#
#   1. Checks for Node.js 24 (switches this shell to nvm's v24.21.0 when it is installed).
#   2. Enables the secret-scanning pre-commit hook: git config core.hooksPath .githooks
#   3. dotnet tool restore, dotnet restore, npm ci --prefix web
#      (each step is skipped while that part of the repository does not exist yet).
#   4. Optionally stores the development database connection string in the API's user-secrets
#      (ConnectionStrings:ControlHome). The value is never echoed and never passed on a command line.
#
# Usage:
#   scripts/setup-dev.sh
#   scripts/setup-dev.sh --prompt-connection-string        # hidden prompt (recommended)
#   scripts/setup-dev.sh --connection-string "<value>"     # ends up in your shell history
#
# The connection string's database must match ^ControlHome_(Dev|Test|Compose)(_[A-Za-z0-9]+)?$, e.g.
#   Server=<host>,1433;Database=ControlHome_Dev;User ID=<login>;Password=<password>;TrustServerCertificate=True

set -euo pipefail

readonly NODE_VERSION="24.21.0"
readonly API_PROJECT_DIR="src/ControlHome.Api"
readonly DEFAULT_USER_SECRETS_ID="controlhome-api"
readonly DATABASE_NAME_PATTERN='^ControlHome_(Dev|Test|Compose)(_[A-Za-z0-9]+)?$'

step() { printf '\033[36m==> %s\033[0m\n' "$*"; }
note() { printf '    %s\n' "$*"; }
warn() { printf '\033[33mWARNING: %s\033[0m\n' "$*" >&2; }
die() { printf '\033[31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

usage() { sed -n '2,18p' "$0" | sed 's/^# \{0,1\}//'; }

connection_string=""
prompt_connection_string=0
while [ $# -gt 0 ]; do
  case "$1" in
    --connection-string)
      [ $# -ge 2 ] || die "--connection-string needs a value"
      connection_string="$2"
      shift 2
      ;;
    --connection-string=*)
      connection_string="${1#*=}"
      shift
      ;;
    --prompt-connection-string)
      prompt_connection_string=1
      shift
      ;;
    -h | --help)
      usage
      exit 0
      ;;
    *) die "unknown argument: $1 (see --help)" ;;
  esac
done

cd "$(dirname "$0")/.."
failures=()

run_checked() {
  local description="$1"
  shift
  if ! "$@"; then
    warn "$description failed."
    failures+=("$description")
  fi
}

has() { command -v "$1" >/dev/null 2>&1; }

node_is_24() { has node && node -v 2>/dev/null | grep -q '^v24\.'; }

use_node_24() {
  node_is_24 && return 0
  local dir candidates=()
  if [ -n "${NVM_HOME:-}" ]; then
    if has cygpath; then candidates+=("$(cygpath -u "$NVM_HOME")/v$NODE_VERSION"); else candidates+=("$NVM_HOME/v$NODE_VERSION"); fi
  fi
  if [ -n "${LOCALAPPDATA:-}" ]; then
    if has cygpath; then candidates+=("$(cygpath -u "$LOCALAPPDATA")/nvm/v$NODE_VERSION"); else candidates+=("$LOCALAPPDATA/nvm/v$NODE_VERSION"); fi
  fi
  candidates+=("${NVM_DIR:-$HOME/.nvm}/versions/node/v$NODE_VERSION/bin")
  for dir in "${candidates[@]}"; do
    if [ -x "$dir/node" ] || [ -f "$dir/node.exe" ]; then
      PATH="$dir:$PATH"
      export PATH
      if node_is_24; then
        note "using Node $(node -v) from $dir for this shell"
        return 0
      fi
    fi
  done
  local found="not installed"
  has node && found="$(node -v 2>/dev/null || echo unknown)"
  warn "Node 24 is required (found: $found). Install it with nvm and run: nvm use $NODE_VERSION"
  return 1
}

user_secrets_args() {
  if [ -n "${CONTROLHOME_USER_SECRETS_ID:-}" ]; then
    printf '%s\n' --id "$CONTROLHOME_USER_SECRETS_ID"
  elif [ -f "$API_PROJECT_DIR/ControlHome.Api.csproj" ]; then
    printf '%s\n' --project "$API_PROJECT_DIR"
  else
    printf '%s\n' --id "$DEFAULT_USER_SECRETS_ID"
  fi
}

step "Node.js 24"
node_ok=0
if use_node_24; then
  node_ok=1
  note "node $(node -v)"
fi

step "Pre-commit secret check (core.hooksPath = .githooks)"
if has git; then
  run_checked "git config core.hooksPath" git config core.hooksPath .githooks
else
  warn "git was not found on PATH."
  failures+=("git config core.hooksPath")
fi

has_dotnet=0
if has dotnet; then has_dotnet=1; else warn "dotnet was not found on PATH - install the .NET SDK pinned in global.json."; fi

step ".NET local tools"
if [ ! -f .config/dotnet-tools.json ]; then
  note "skipped (.config/dotnet-tools.json not found)"
elif [ "$has_dotnet" -eq 0 ]; then
  failures+=("dotnet tool restore")
else
  run_checked "dotnet tool restore" dotnet tool restore
fi

step ".NET packages"
if [ ! -f ControlHome.slnx ]; then
  note "skipped (ControlHome.slnx not found)"
elif [ "$has_dotnet" -eq 0 ]; then
  failures+=("dotnet restore")
else
  run_checked "dotnet restore" dotnet restore
fi

step "Web packages (npm ci --prefix web)"
if [ ! -f web/package-lock.json ]; then
  note "skipped (web/package-lock.json not found)"
elif [ "$node_ok" -eq 0 ]; then
  failures+=("npm ci (Node 24 required)")
else
  run_checked "npm ci" npm ci --prefix web
fi

if [ "$prompt_connection_string" -eq 1 ]; then
  printf 'Development connection string (input hidden): ' >&2
  IFS= read -r -s connection_string
  printf '\n' >&2
fi

if [ -n "$connection_string" ]; then
  step "Development connection string -> user-secrets (ConnectionStrings:ControlHome)"
  database=""
  IFS=';' read -r -a parts <<<"$connection_string" || true
  for part in "${parts[@]+"${parts[@]}"}"; do
    case "$part" in *=*) ;; *) continue ;; esac
    key="$(printf '%s' "${part%%=*}" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')"
    if [ "$key" = "database" ] || [ "$key" = "initial catalog" ]; then
      database="$(printf '%s' "${part#*=}" | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')"
    fi
  done
  unset parts part key
  if ! [[ "$database" =~ $DATABASE_NAME_PATTERN ]]; then
    die "the connection string's database must match $DATABASE_NAME_PATTERN (found: '$database'). The server hosts other databases; ControlHome tooling only touches its own."
  fi
  case "$connection_string" in *[[:cntrl:]]*) die "the connection string contains control characters." ;; esac
  [ "$has_dotnet" -eq 1 ] || die "dotnet is required to store user-secrets."

  selector=()
  while IFS= read -r arg; do selector+=("$arg"); done < <(user_secrets_args)
  # JSON on stdin keeps the value off the process command line.
  escaped="${connection_string//\\/\\\\}"
  escaped="${escaped//\"/\\\"}"
  if printf '{"ConnectionStrings":{"ControlHome":"%s"}}' "$escaped" | dotnet user-secrets set "${selector[@]}" >/dev/null; then
    note "stored for database '$database' (${selector[*]})."
  else
    failures+=("dotnet user-secrets set")
  fi
  unset escaped connection_string
fi

echo
if [ "${#failures[@]}" -gt 0 ]; then
  warn "setup finished with problems: $(printf '%s; ' "${failures[@]}")"
  exit 1
fi
printf '\033[32mSetup complete. Next: run the API (dotnet watch run --project %s --launch-profile http) and the web app (npm run dev --prefix web), or ./scripts/dev.ps1 with PowerShell 7.\033[0m\n' "$API_PROJECT_DIR"
