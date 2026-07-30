#!/usr/bin/env bash
#
# Run the public website (src/Freeboard.Web) locally:
#
#   scripts/run-web.sh            # serve the Razor pages live
#   scripts/run-web.sh --watch    # hot reload on source edits
#   scripts/run-web.sh --static   # generate _site, then serve those files
#
# No database, no auth, no HTTPS: the site is static content, so this is just the
# app in the foreground on http://localhost:5300.
#
# --static builds the site exactly as it deploys (AspNetStatic writes _site, then a
# plain file server serves it). Use it to check the generated output - links, asset
# paths, the index.html layout - rather than the live-rendered pages.
#
# Dev/local only.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

hot_reload=false
static=false
for arg in "$@"; do
  case "$arg" in
    --watch|--hot-reload) hot_reload=true ;;
    --static|--ssg) static=true ;;
    *) echo "error: unknown argument: $arg (supported: --watch, --static)" >&2; exit 1 ;;
  esac
done

if [ "$hot_reload" = true ] && [ "$static" = true ]; then
  echo "error: --watch and --static cannot be combined; --static generates once." >&2
  exit 1
fi

need() { command -v "$1" >/dev/null 2>&1 || { echo "error: '$1' is required but not found in PATH." >&2; exit 1; }; }
need dotnet
need bun   # the MSBuild asset target shells out to bun for Tailwind and Alpine

url="http://localhost:5300"
project="src/Freeboard.Web"

if [ "$static" = true ]; then
  need python3

  echo "==> Generating the static site into $project/_site"
  rm -rf "$project/_site"
  dotnet run --project "$project" -- ssg-only >/dev/null

  echo
  echo "  Static site: $url"
  echo "  Files:       $project/_site"
  echo "  Ctrl-C to stop."
  echo
  # Serves index.html for a directory, which is how the generated tree is laid out.
  exec python3 -m http.server 5300 --bind 127.0.0.1 --directory "$project/_site"
fi

# --no-launch-profile so ASPNETCORE_URLS below is not overridden by launchSettings.
if [ "$hot_reload" = true ]; then
  echo "==> Starting the website (hot reload via dotnet watch)"
  dotnet_cmd=(watch --non-interactive --project "$project" run --no-launch-profile)
else
  echo "==> Building"
  dotnet build "$project" -c Debug --nologo -v quiet
  echo "==> Starting the website"
  dotnet_cmd=(run --no-build --no-launch-profile --project "$project")
fi

echo
echo "  Website: $url"
echo "  Docs:    $url/docs"
echo "  Ctrl-C to stop."
echo

exec env ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="$url" dotnet "${dotnet_cmd[@]}"
