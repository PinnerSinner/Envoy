#!/usr/bin/env bash
# Type-checks the Windows app (and the XAML resource keys) on any OS. Usage: tools/typecheck/typecheck.sh
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
app="$here/../../src/Envoy.App"
export PYTHONDONTWRITEBYTECODE=1
python3 -m unittest discover -s "$here" -p 'test_*.py' -q
rm -rf "$here/stubs"
python3 "$here/gen_stubs.py" "$app" "$here/stubs" > /dev/null
python3 "$here/check_resources.py" "$app"
python3 "$here/check_a11y.py" "$app"
python3 "$here/check_ui_rules.py" "$app"
dotnet build "$here/TypeCheck.csproj" -nologo -v quiet "$@"
