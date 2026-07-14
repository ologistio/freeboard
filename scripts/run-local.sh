#!/usr/bin/env bash
#
# Run Freeboard locally with a fresh, pre-seeded database and print login
# credentials. One command, no extra steps:
#
#   scripts/run-local.sh            # community edition (default)
#   scripts/run-local.sh --ee       # enterprise edition (CustomPolicies on)
#   scripts/run-local.sh --watch    # hot reload on source edits
#
# Pass --ee (or --enterprise) to start as an Enterprise install: it turns on the
# CustomPolicies entitlement (Enterprise:CustomPolicies), which enables the
# custom-role designer at /settings/custom-roles. Default is off (community).
#
# Pass --watch (or --hot-reload) to run the web app under `dotnet watch`, so edits
# to .cs/.cshtml/.razor hot-reload (or restart) without re-running the script. The
# CSS/JS asset build re-runs on .cshtml changes via MSBuild; for standalone asset
# iteration use `bun run watch:css` (see CLAUDE.md). Flags combine, e.g.
# `--ee --watch`.
#
# It brings up the local MySQL (the test compose stack), resets the freeboard
# database, applies migrations, imports the sample compliance config from
# examples/gitops, bootstraps a first admin, then runs the web app in the
# foreground and prints the admin email + password.
#
# Each run reseeds from scratch: the freeboard database is dropped and recreated,
# so any data entered in a previous session is discarded and the printed password
# always works. MySQL is left running on exit; stop it with:
#   docker compose -f tests/Freeboard.TestInfrastructure/docker-compose.yml down
#
# Dev/local only. The secrets below are generated fresh each run and never
# committed. Do not use this script or its settings in production.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# Flags: --ee/--enterprise turns on the CustomPolicies entitlement; --watch/
# --hot-reload runs the web app under `dotnet watch` for hot reload.
enterprise=false
hot_reload=false
for arg in "$@"; do
  case "$arg" in
    --ee|--enterprise) enterprise=true ;;
    --watch|--hot-reload) hot_reload=true ;;
    *) echo "error: unknown argument: $arg (supported: --ee, --watch)" >&2; exit 1 ;;
  esac
done

compose_file="tests/Freeboard.TestInfrastructure/docker-compose.yml"
db_conn="Server=127.0.0.1;Port=3306;Database=freeboard;User ID=freeboard;Password=freeboard;"
admin_email="admin@local.test"
admin_name="Local Admin"
http_url="http://localhost:5299"
https_url="https://localhost:7245"

need() { command -v "$1" >/dev/null 2>&1 || { echo "error: '$1' is required but not found in PATH." >&2; exit 1; }; }
need docker
need dotnet
need openssl
need curl

# Prefer the `docker compose` plugin; fall back to the standalone `docker-compose`
# binary for docker CLIs that ship without the plugin (e.g. orbstack).
if docker compose version >/dev/null 2>&1; then
  compose() { docker compose "$@"; }
elif command -v docker-compose >/dev/null 2>&1; then
  compose() { docker-compose "$@"; }
else
  echo "error: neither 'docker compose' nor 'docker-compose' is available." >&2
  exit 1
fi

# Fresh per-run secrets. Auth keys are 32 random bytes (base64); the bootstrap
# secret and admin password are throwaway. Nothing here is persisted to disk.
admin_password="Local-$(openssl rand -hex 6)"
bootstrap_secret="$(openssl rand -hex 16)"
key_password="$(openssl rand -base64 32)"
key_token="$(openssl rand -base64 32)"
key_protect="$(openssl rand -base64 32)"

echo "==> Starting local MySQL (compose)"
# No `--wait`: it is a `docker compose` (v2) option the standalone `docker-compose`
# (v1) fallback does not accept. Poll for readiness instead, which works on both.
compose -f "$compose_file" up -d

echo "==> Waiting for MySQL to accept connections"
mysql_ready=""
for _ in $(seq 1 60); do
  if compose -f "$compose_file" exec -T mysql \
      mysqladmin ping -uroot -proot --silent >/dev/null 2>&1; then
    mysql_ready=1
    break
  fi
  sleep 1
done
[ -n "$mysql_ready" ] || { echo "error: MySQL did not become ready in time." >&2; exit 1; }

echo "==> Resetting the freeboard database (fresh seed)"
compose -f "$compose_file" exec -T mysql \
  mysql -uroot -proot -e "DROP DATABASE IF EXISTS freeboard; CREATE DATABASE freeboard CHARACTER SET utf8mb4;"

echo "==> Building (once)"
dotnet build src/Freeboard.CLI -c Debug --nologo -v quiet
dotnet build src/Freeboard -c Debug --nologo -v quiet

echo "==> Applying schema migrations"
FREEBOARD_DB="$db_conn" dotnet run --no-build --project src/Freeboard.CLI -- system migrate

echo "==> Importing sample compliance config from examples/gitops"
FREEBOARD_DB="$db_conn" dotnet run --no-build --project src/Freeboard.CLI -- gitops sync examples/gitops

# The web app needs an HTTPS endpoint for the Secure __Host- session cookie.
# Ensure a dev cert exists (browser will still warn on the self-signed cert).
dotnet dev-certs https >/dev/null 2>&1 || true

# Fail fast if the ports are already taken - usually a previous run's app that did
# not shut down. Left running, it answers our readiness check and the bootstrap call
# with a stale secret, which surfaces as a confusing "Not authorized".
if curl -s -o /dev/null --max-time 2 "$http_url/"; then
  echo "error: something is already listening on $http_url." >&2
  echo "  A previous run may not have stopped. Find and stop it, e.g.:" >&2
  echo "    lsof -ti tcp:5299 tcp:7245 | xargs -r kill" >&2
  exit 1
fi

# --no-launch-profile so launchSettings.json does not override ASPNETCORE_URLS
# below; without it, `dotnet run` applies the default profile (http only) and the
# HTTPS listener never binds, breaking the Secure session cookie.

# Under --watch, run via `dotnet watch` for hot reload. --non-interactive makes it
# auto-apply/restart without prompting (there is no TTY on stdin here). Otherwise
# use the already-built output with --no-build.
if [ "$hot_reload" = true ]; then
  dotnet_cmd=(watch --non-interactive --project src/Freeboard run --no-launch-profile)
  echo "==> Starting the web app (hot reload via dotnet watch)"
else
  dotnet_cmd=(run --no-build --no-launch-profile --project src/Freeboard)
  echo "==> Starting the web app"
fi

web_log="$(mktemp -t freeboard-web.XXXXXX.log)"
# Start the app in its own process group (set -m) so cleanup can signal the whole
# tree. dotnet watch spawns a child `dotnet run` that spawns the app; signalling only
# $web_pid would orphan the app and leave it holding the HTTP ports.
set -m
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS="$https_url;$http_url" \
ConnectionStrings__Freeboard="$db_conn" \
Freeboard__GitOps__ReadOnly=false \
Enterprise__CustomPolicies="$enterprise" \
Auth__BootstrapSecret="$bootstrap_secret" \
Auth__PasswordSecrets__1="$key_password" \
Auth__CurrentPasswordSecretVersion=1 \
Auth__TokenKeys__1="$key_token" \
Auth__CurrentTokenKeyVersion=1 \
Auth__SecretProtectionKeys__1="$key_protect" \
Auth__CurrentSecretProtectionKeyVersion=1 \
  dotnet "${dotnet_cmd[@]}" >"$web_log" 2>&1 &
web_pid=$!
set +m

# Run once. We trap INT, TERM, and EXIT with the same handler, so without the guard
# it would run on the interrupt and again on EXIT; ignoring further INT/TERM also
# stops the "Stopping..." message repeating when an impatient Ctrl-C is mashed.
tail_pid=""
cleaned_up=false
cleanup() {
  if [ "$cleaned_up" = true ]; then return; fi
  cleaned_up=true
  trap '' INT TERM
  echo
  echo "==> Stopping the web app (MySQL is left running)."
  [ -n "$tail_pid" ] && kill "$tail_pid" >/dev/null 2>&1 || true
  # Signal the whole process group (-$web_pid), not just $web_pid: the app is a
  # grandchild of dotnet watch, so killing only the top process orphans it and it
  # keeps holding the HTTP ports. Ask the group to stop, wait, then force it. A bare
  # `dotnet run` dies on the first signal, so the loop breaks at once and adds no delay.
  if kill -0 "$web_pid" 2>/dev/null; then
    kill -TERM -"$web_pid" >/dev/null 2>&1 || true
    for _ in $(seq 1 10); do
      kill -0 "$web_pid" 2>/dev/null || break
      sleep 0.5
    done
    kill -KILL -"$web_pid" >/dev/null 2>&1 || true
  fi
  wait "$web_pid" 2>/dev/null || true
  rm -f "$web_log"
}
trap cleanup INT TERM EXIT

echo "==> Waiting for the app to listen"
# Bound each probe: under --watch, dotnet watch restarts the app when the asset
# build rewrites wwwroot/, and a probe landing in that window can connect to a
# socket that never answers. Without --max-time an untimed curl would hang here.
for attempt in $(seq 1 60); do
  if curl -s -o /dev/null --connect-timeout 2 --max-time 5 \
      "$http_url/compliance/statement-of-applicability"; then ready=1; break; fi
  if ! kill -0 "$web_pid" 2>/dev/null; then
    echo "error: the web app exited during startup. Last log lines:" >&2
    tail -n 30 "$web_log" >&2
    exit 1
  fi
  if [ "$attempt" = 15 ]; then echo "    (still starting - dotnet watch builds before it listens)"; fi
  sleep 1
done
if [ "${ready:-0}" != "1" ]; then
  echo "error: the web app did not become ready in time. Last log lines:" >&2
  tail -n 30 "$web_log" >&2
  exit 1
fi

echo "==> Bootstrapping the first admin"
FREEBOARD_BOOTSTRAP_SECRET="$bootstrap_secret" \
  dotnet run --no-build --project src/Freeboard.CLI -- \
    user bootstrap --email "$admin_email" --name "$admin_name" \
    --password "$admin_password" --api-url "$http_url" >/dev/null

if [ "$enterprise" = true ]; then
  edition_line="  Edition:   Enterprise (CustomPolicies on; custom-role
             designer at /settings/custom-roles)"
else
  edition_line="  Edition:   Community (run with --ee for Enterprise)"
fi

if [ "$hot_reload" = true ]; then
  hotreload_line="  Hot reload: on (dotnet watch) - save a .cs/.cshtml edit to apply"
else
  hotreload_line="  Hot reload: off (run with --watch to enable)"
fi

cat <<BANNER

============================================================
  Freeboard is running locally with seeded data.

  URL:       $https_url
             (self-signed cert - accept the browser warning)

$edition_line
$hotreload_line

  Admin login
    email:    $admin_email
    password: $admin_password

  Statement of Applicability:
    $https_url/compliance/statement-of-applicability

  Seeded from examples/gitops: 2 standards, 2 controls,
  2 organisations, 2 scopes. GitOps read-only mode is OFF,
  so app-managed writes are enabled.

  Press Ctrl-C to stop the app (MySQL stays up).
============================================================

BANNER

# Stream the web app log and block until the user stops it. The cleanup trap set
# above already tears down this tail (via $tail_pid) and the web app.
tail -n +1 -f "$web_log" &
tail_pid=$!
wait "$web_pid"
