#!/usr/bin/env bash
# check-secrets.sh - pre-commit secret guard for ControlHome (public repo, real home).
#
# Runs from .githooks/pre-commit (enable once per clone: `git config core.hooksPath .githooks`,
# which scripts/setup-dev.* does for you). It can also be run by hand at any time; it only looks at
# what is currently staged (`git diff --cached`), and only at ADDED lines, so removing a secret is never blocked.
#
#   1. Known values: every value stored in the API's user-secrets (`dotnet user-secrets list`) - and the
#      Password/Pwd part of any connection string stored there - must not appear in an added line.
#      Values shorter than 4 characters are ignored. Values are compared in memory and NEVER printed.
#   2. gitleaks (rules in .gitleaks.toml): the native binary if it is on PATH, otherwise the pinned Docker image
#      (run with --network none), otherwise a warning is printed (CI scans the full history on every push).
#
# Exit codes: 0 = clean, 1 = possible secret found (commit blocked), 2 = not run inside a git repository.
#
# Environment (all optional):
#   CONTROLHOME_USER_SECRETS_ID  user-secrets id to read (default: <UserSecretsId> of src/ControlHome.Api,
#                                falling back to "controlhome-api")
#   CONTROLHOME_SKIP_GITLEAKS=1  skip step 2 (step 1 always runs)
#   CONTROLHOME_GITLEAKS_IMAGE   override the Docker image used in step 2
#
# Requires bash 3.2+, git, awk, grep, tar; dotnet and gitleaks/Docker are optional.

set -euo pipefail

readonly GITLEAKS_IMAGE="${CONTROLHOME_GITLEAKS_IMAGE:-ghcr.io/gitleaks/gitleaks:v8.30.1@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f}"
readonly API_PROJECT_DIR="src/ControlHome.Api"
readonly DEFAULT_USER_SECRETS_ID="controlhome-api"
readonly MIN_SECRET_LENGTH=4
readonly LEAKS_EXIT_CODE=3 # passed to gitleaks --exit-code, to tell "leaks found" apart from "gitleaks failed"

info() { printf 'check-secrets: %s\n' "$*" >&2; }
warn() { printf 'check-secrets: WARNING: %s\n' "$*" >&2; }
error() { printf 'check-secrets: ERROR: %s\n' "$*" >&2; }

if ! repo_root="$(git rev-parse --show-toplevel 2>/dev/null)"; then
  error "not inside a git repository"
  exit 2
fi
cd "$repo_root"

blocked=0

work="$(mktemp -d "${TMPDIR:-/tmp}/check-secrets.XXXXXX")"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/scan" "$work/cfg"

# ---------------------------------------------------------------------------------------------------------------
# Rebuild the ADDED lines of every staged file under $work/scan/<path>, each at its real line number (other lines
# are left blank), so findings point at the right file and line.
# ---------------------------------------------------------------------------------------------------------------
staged_count=0
while IFS= read -r -d '' path; do
  staged_count=$((staged_count + 1))
  out="$work/scan/$path"
  mkdir -p "$(dirname "$out")"
  git diff --cached --no-color --no-ext-diff --no-textconv --unified=0 -- "$path" |
    awk '
      /^@@ / {
        in_hunk = 1
        match($0, /\+[0-9]+/)
        next_line = substr($0, RSTART + 1, RLENGTH - 1) + 0
        next
      }
      in_hunk && /^\+/ {
        while (written < next_line - 1) { print ""; written++ }
        print substr($0, 2)
        written++
        next_line++
      }
    ' >"$out"
done < <(git diff --cached --name-only -z --diff-filter=ACMRT)

if [ "$staged_count" -eq 0 ]; then
  info "nothing staged - nothing to check."
  exit 0
fi

# ---------------------------------------------------------------------------------------------------------------
# Step 1 - known values from user-secrets.
# ---------------------------------------------------------------------------------------------------------------
needles=()

add_needle() {
  local value="$1"
  # trim surrounding whitespace and one level of matching quotes (connection-string quoting: "" or '' inside = one quote)
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  case "$value" in
    \"*\")
      value="${value#\"}"; value="${value%\"}"; value="${value//\"\"/\"}" ;;
    \'*\')
      value="${value#\'}"; value="${value%\'}"; value="${value//\'\'/\'}" ;;
  esac
  if [ "${#value}" -ge "$MIN_SECRET_LENGTH" ]; then
    needles+=("$value")
  fi
}

user_secrets_args=()
if [ -n "${CONTROLHOME_USER_SECRETS_ID:-}" ]; then
  user_secrets_args=(--id "$CONTROLHOME_USER_SECRETS_ID")
else
  csproj="$API_PROJECT_DIR/ControlHome.Api.csproj"
  secrets_id=""
  if [ -f "$csproj" ]; then
    secrets_id="$(sed -n 's:.*<UserSecretsId>[[:space:]]*\([^<[:space:]]*\)[[:space:]]*</UserSecretsId>.*:\1:p' "$csproj" | head -n 1)"
  fi
  if [ -n "$secrets_id" ]; then
    user_secrets_args=(--id "$secrets_id")
  elif [ -f "$csproj" ]; then
    user_secrets_args=(--project "$API_PROJECT_DIR")
  else
    user_secrets_args=(--id "$DEFAULT_USER_SECRETS_ID")
  fi
fi

if ! command -v dotnet >/dev/null 2>&1; then
  warn "dotnet not found - skipped the user-secrets check."
elif ! secrets_output="$(dotnet user-secrets list "${user_secrets_args[@]}" 2>/dev/null)"; then
  warn "could not read user-secrets (${user_secrets_args[*]}) - skipped the user-secrets check."
else
  while IFS= read -r line; do
    line="${line%$'\r'}"
    case "$line" in *" = "*) ;; *) continue ;; esac
    value="${line#* = }"
    add_needle "$value"
    # Also the password part of a connection string, which may be pasted on its own.
    case "$value" in *=*\;* | *\;*=*)
      IFS=';' read -r -a parts <<<"$value" || true
      for part in "${parts[@]+"${parts[@]}"}"; do
        case "$part" in *=*) ;; *) continue ;; esac
        key="$(printf '%s' "${part%%=*}" | tr -d '[:space:]' | tr '[:upper:]' '[:lower:]')"
        if [ "$key" = "password" ] || [ "$key" = "pwd" ]; then
          add_needle "${part#*=}"
        fi
      done
      ;;
    esac
  done <<<"$secrets_output"
  unset secrets_output line value parts part key
fi

if [ "${#needles[@]}" -gt 0 ]; then
  hits="$(grep -rlF -f <(printf '%s\n' "${needles[@]}") "$work/scan" 2>/dev/null || true)"
  if [ -n "$hits" ]; then
    blocked=1
    error "staged changes contain a value stored in your user-secrets (value not shown) in:"
    while IFS= read -r hit; do
      printf '  - %s\n' "${hit#"$work/scan/"}" >&2
    done <<<"$hits"
  fi
  info "user-secrets check: ${#needles[@]} known value(s) compared against $staged_count staged file(s)."
else
  info "user-secrets check: no stored values to compare."
fi
needles=()

# ---------------------------------------------------------------------------------------------------------------
# Step 2 - gitleaks.
# ---------------------------------------------------------------------------------------------------------------
config_args=()
if [ -f .gitleaks.toml ]; then
  cp .gitleaks.toml "$work/cfg/gitleaks.toml"
  config_args=(--config .gitleaks.toml)
fi

gitleaks_rc=""
if [ "${CONTROLHOME_SKIP_GITLEAKS:-0}" = "1" ]; then
  warn "CONTROLHOME_SKIP_GITLEAKS=1 - skipped the gitleaks scan."
elif command -v gitleaks >/dev/null 2>&1; then
  info "running gitleaks ($(command -v gitleaks)) on the staged changes..."
  set +e
  gitleaks git --pre-commit --staged "${config_args[@]+"${config_args[@]}"}" \
    --redact --no-banner --no-color --verbose --exit-code "$LEAKS_EXIT_CODE"
  gitleaks_rc=$?
  set -e
elif command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
  info "gitleaks not on PATH - running $GITLEAKS_IMAGE in Docker on the staged changes..."
  container_config=""
  if [ -f "$work/cfg/gitleaks.toml" ]; then
    container_config="--config /tmp/w/cfg/gitleaks.toml"
  fi
  set +e
  tar -C "$work" -cf - cfg scan |
    MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' docker run --rm -i --network none --entrypoint sh "$GITLEAKS_IMAGE" -c \
      "mkdir -p /tmp/w && tar -xf - -C /tmp/w && gitleaks dir /tmp/w/scan $container_config --redact --no-banner --no-color --verbose --exit-code $LEAKS_EXIT_CODE" 2>&1 |
    sed 's|/tmp/w/scan/||g' >&2
  pipe_status=("${PIPESTATUS[@]}")
  set -e
  gitleaks_rc="${pipe_status[1]}"
else
  warn "gitleaks is not on PATH and Docker is not available - skipped the gitleaks scan (CI runs it on every push)."
fi

if [ -n "$gitleaks_rc" ]; then
  if [ "$gitleaks_rc" -eq 0 ]; then
    info "gitleaks: no leaks found."
  elif [ "$gitleaks_rc" -eq "$LEAKS_EXIT_CODE" ]; then
    blocked=1
    error "gitleaks found possible secrets in the staged changes (values redacted above)."
  else
    warn "gitleaks could not complete (exit $gitleaks_rc) - not blocking; CI scans every push."
  fi
fi

if [ "$blocked" -ne 0 ]; then
  cat >&2 <<'EOF'
check-secrets: commit blocked.
  - Real secret? Unstage it and keep it in user-secrets (dev) or the gitignored .env (compose).
  - False positive? Use a placeholder (<password>, ${VAR}) or, as a last resort, an inline `gitleaks:allow`
    comment, and explain it in the commit message. Do not bypass the hook for real values.
EOF
  exit 1
fi

info "OK - no secrets found."
exit 0
