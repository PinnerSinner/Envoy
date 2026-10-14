#!/usr/bin/env bash
# Type-checks the Windows app (and the XAML resource keys) on any OS. Usage: tools/typecheck/typecheck.sh
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
rm -rf "$here/stubs"
python3 "$here/gen_stubs.py" "$here/../../src/Envoy.App" "$here/stubs" > /dev/null
python3 "$here/check_resources.py" "$here/../../src/Envoy.App"
dotnet build "$here/TypeCheck.csproj" -nologo -v quiet "$@"
