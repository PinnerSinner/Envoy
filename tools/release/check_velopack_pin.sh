#!/usr/bin/env bash
# REQ-DIST-9: the Velopack NuGet version (Envoy.App.csproj, tools/typecheck/TypeCheck.csproj) and the vpk tool version
# (.config/dotnet-tools.json) must be one exact version. Usage: check_velopack_pin.sh [repo root] | --self-test
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"

package_version() {
  # The Version of <PackageReference Include="Velopack" Version="x" /> (attributes in either order).
  python3 - "$1" <<'PY'
import re, sys
text = open(sys.argv[1], encoding="utf-8").read()
for tag in re.findall(r"<PackageReference\b[^>]*>", text):
    if re.search(r'Include\s*=\s*"Velopack"', tag):
        m = re.search(r'Version\s*=\s*"([^"]*)"', tag)
        print(m.group(1) if m else "")
        break
PY
}

tool_version() {
  python3 - "$1" <<'PY'
import json, sys
tools = json.load(open(sys.argv[1], encoding="utf-8")).get("tools", {})
print(tools.get("vpk", {}).get("version", ""))
PY
}

check() {
  local root="$1"
  local app typecheck vpk
  app="$(package_version "$root/src/Envoy.App/Envoy.App.csproj")"
  typecheck="$(package_version "$root/tools/typecheck/TypeCheck.csproj")"
  vpk="$(tool_version "$root/.config/dotnet-tools.json")"
  echo "Velopack pins: Envoy.App.csproj=${app:-missing} TypeCheck.csproj=${typecheck:-missing} vpk=${vpk:-missing}"
  if [[ -z "$app" || -z "$typecheck" || -z "$vpk" ]]; then
    echo "::error::A Velopack pin is missing (Envoy.App.csproj, tools/typecheck/TypeCheck.csproj, .config/dotnet-tools.json)" >&2
    return 1
  fi
  if [[ ! "$app" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "::error::Velopack must be pinned to an exact version, not '$app'" >&2
    return 1
  fi
  if [[ "$app" != "$typecheck" || "$app" != "$vpk" ]]; then
    echo "::error::Velopack versions disagree; bump all three together (docs/releasing.md)" >&2
    return 1
  fi
  echo "Velopack pin ok ($app)"
}

self_test() {
  local root="$here/../.." tmp
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' RETURN
  mkdir -p "$tmp/src/Envoy.App" "$tmp/tools/typecheck" "$tmp/.config"
  cp "$root/src/Envoy.App/Envoy.App.csproj" "$tmp/src/Envoy.App/"
  cp "$root/tools/typecheck/TypeCheck.csproj" "$tmp/tools/typecheck/"
  cp "$root/.config/dotnet-tools.json" "$tmp/.config/"
  check "$tmp" > /dev/null || { echo "self-test: the real pins should agree" >&2; return 1; }
  sed -i.orig -E 's/(Include="Velopack" Version=")[^"]*/\10.0.1/' "$tmp/tools/typecheck/TypeCheck.csproj"
  if check "$tmp" > /dev/null 2>&1; then echo "self-test: a changed TypeCheck.csproj pin passed" >&2; return 1; fi
  mv "$tmp/tools/typecheck/TypeCheck.csproj.orig" "$tmp/tools/typecheck/TypeCheck.csproj"
  python3 - "$tmp/.config/dotnet-tools.json" <<'PY'
import json, sys
path = sys.argv[1]
data = json.load(open(path))
data["tools"]["vpk"]["version"] = "0.0.1"
json.dump(data, open(path, "w"))
PY
  if check "$tmp" > /dev/null 2>&1; then echo "self-test: a changed vpk pin passed" >&2; return 1; fi
  echo "check_velopack_pin self-test ok"
}

if [[ "${1:-}" == "--self-test" ]]; then
  self_test
else
  check "${1:-$here/../..}"
fi
