#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# No argument runs the build, tests, coverage and CRAP gates; --full adds every mutation scope, the
# Coyote interleavings and the fuzz campaigns; --mutate SCOPE, --coyote and --fuzz run one alone.
MODE=quality
SCOPE=""
case "${1:-}" in
  "") ;;
  --full) MODE=full ;;
  --mutate) MODE=mutate; SCOPE="${2:?--mutate needs a scope}" ;;
  --coyote) MODE=coyote ;;
  --fuzz) MODE=fuzz ;;
  *) echo "usage: $0 [--full | --mutate SCOPE | --coyote | --fuzz]" >&2; exit 2 ;;
esac

MIN_LINE="${MIN_LINE:-25}"
MIN_BRANCH="${MIN_BRANCH:-10}"
CRAP_LIMIT="${CRAP_LIMIT:-30}"
MIN_MUTATION="${MIN_MUTATION:-100}"

step() { printf '\n\033[1m== %s\033[0m\n' "$1"; }

quality() {
  step "build"
  dotnet build Fog.sln -c Release -v minimal

  mkdir -p .artifacts/coverage
  rm -f .artifacts/coverage/merged.json .artifacts/coverage/dotnet.xml

  coverage_test() {
    local project="$1"
    local format="$2"
    local output="$3"
    local merge_args=()

    if [ -f "$ROOT/.artifacts/coverage/merged.json" ]; then
      merge_args+=(/p:MergeWith="$ROOT/.artifacts/coverage/merged.json")
    fi

    dotnet test "$project" \
      -c Release --no-build -v minimal \
      /p:CollectCoverage=true \
      /p:CoverletOutputFormat="$format" \
      /p:CoverletOutput="$output" \
      "${merge_args[@]}" \
      /p:Include="[NinePSharp.Fog*]*" \
      /p:Exclude="[*.Tests]*%2c[*.Fuzzer]*"
  }

  step "Fog tests"
  coverage_test NinePSharp.Fog.Tests/NinePSharp.Fog.Tests.csproj json "$ROOT/.artifacts/coverage/merged.json"
  coverage_test NinePSharp.Fog.Server.Tests/NinePSharp.Fog.Server.Tests.csproj json "$ROOT/.artifacts/coverage/merged.json"
  coverage_test NinePSharp.Fog.Namespaces.Tests/NinePSharp.Fog.Namespaces.Tests.csproj json "$ROOT/.artifacts/coverage/merged.json"
  coverage_test NinePSharp.Fog.Auth.Tests/NinePSharp.Fog.Auth.Tests.csproj json "$ROOT/.artifacts/coverage/merged.json"
  coverage_test NinePSharp.Fog.Rc.Tests/NinePSharp.Fog.Rc.Tests.csproj json "$ROOT/.artifacts/coverage/merged.json"
  coverage_test NinePSharp.Fog.Thread.Tests/NinePSharp.Fog.Thread.Tests.csproj json "$ROOT/.artifacts/coverage/merged.json"
  coverage_test NinePSharp.Fog.Kernel.Tests/NinePSharp.Fog.Kernel.Tests.csproj cobertura "$ROOT/.artifacts/coverage/dotnet.xml"

  step "gate self-tests"
  python3 tools/test_crap.py
  python3 tools/test_coverage_report.py
  python3 tools/test_mutation_summary.py

  step "coverage thresholds"
  python3 tools/coverage_gate.py --min-line "$MIN_LINE" --min-branch "$MIN_BRANCH"

  step "CRAP score"
  python3 tools/crap.py \
    --threshold "$CRAP_LIMIT" --fail-over "$CRAP_LIMIT" \
    --json .artifacts/crap-dotnet.json
}

mutate() {
  local tests="$1"
  local config="$2"
  local output=".artifacts/$3"
  rm -rf "$output"
  (cd "$tests" && dotnet stryker --config-file "../$config" --reporter json --reporter progress --output "../$output" --skip-version-check --break-on-initial-test-failure --verbosity error ${STRYKER_CONCURRENCY:+--concurrency "$STRYKER_CONCURRENCY"})
  python3 tools/mutation_summary.py --output-dir "$output" --min-score "$MIN_MUTATION" --accepted-timeouts quality/stryker-timeouts.json
}

scope() {
  case "$1" in
    fog) mutate NinePSharp.Fog.Tests stryker-config-fog.json stryker-fog ;;
    fog-server) mutate NinePSharp.Fog.Server.Tests stryker-config-fog-server.json stryker-fog-server ;;
    fog-namespaces) mutate NinePSharp.Fog.Namespaces.Tests stryker-config-fog-namespaces.json stryker-fog-namespaces ;;
    # The keyfs tests need swtpm and python3 (for a stale socket); see NinePSharp.Fog.Auth.Tests.
    fog-auth) mutate NinePSharp.Fog.Auth.Tests stryker-config-fog-auth.json stryker-fog-auth ;;
    fog-rc) mutate NinePSharp.Fog.Rc.Tests stryker-config-fog-rc.json stryker-fog-rc ;;
    fog-commands) mutate NinePSharp.Fog.Rc.Tests stryker-config-fog-commands.json stryker-fog-commands ;;
    fog-kernel) mutate NinePSharp.Fog.Kernel.Tests stryker-config-fog-kernel.json stryker-fog-kernel ;;
    fog-thread) mutate NinePSharp.Fog.Thread.Tests stryker-config-fog-thread.json stryker-fog-thread ;;
    *) echo "unknown mutation scope: $1" >&2; exit 2 ;;
  esac
}

# Coyote explores interleavings only in assemblies rewritten to hand it their tasks, locks and awaits:
# the code under test and the test assembly, whose awaits would otherwise run outside its control.
# A scenario skips when its assemblies were not rewritten, so here a skip fails the gate.
coyote() {
  step "Coyote interleavings"
  local output="$ROOT/.artifacts/coyote"
  rm -rf "$output"
  dotnet build NinePSharp.Fog.Coyote.Tests -c Release -v minimal -p:CopyLocalLockFileAssemblies=true -o "$output"
  cat > "$output/rewrite.coyote.json" <<JSON
{
  "AssembliesPath": ".",
  "Assemblies": ["NinePSharp.Fog.Coyote.Tests.dll", "NinePSharp.Fog.Server.dll", "NinePSharp.Fog.dll", "NinePSharp.Fog.Thread.dll"]
}
JSON
  dotnet tool restore
  (cd "$output" && dotnet tool run coyote rewrite rewrite.coyote.json)
  dotnet test "$output/NinePSharp.Fog.Coyote.Tests.dll" --logger "trx;LogFileName=coyote.trx" --results-directory "$output/results"
  python3 - "$output/results/coyote.trx" <<'PY'
import sys
import xml.etree.ElementTree as tree

counters = tree.parse(sys.argv[1]).getroot().find("{*}ResultSummary/{*}Counters").attrib
if int(counters["notExecuted"]) or int(counters["passed"]) != int(counters["total"]):
    sys.exit(f"FAIL: Coyote ran {counters['passed']} of {counters['total']} scenarios; the rest were skipped or failed")
print(f"Coyote: {counters['passed']} scenarios explored")
PY
}

fuzz() {
  step "SharpFuzz/AFL fog record and transaction campaign"
  FUZZ_SECONDS="${FUZZ_SECONDS:-10}" bash scripts/fuzz.sh fog

  step "SharpFuzz/AFL fog control-fid campaign"
  FUZZ_SECONDS="${FUZZ_SECONDS:-10}" bash scripts/fuzz.sh fog-files

  step "SharpFuzz/AFL fog dispatcher session campaign"
  FUZZ_SECONDS="${FUZZ_SECONDS:-10}" bash scripts/fuzz.sh fog-dispatcher
}

case "$MODE" in
  quality) quality ;;
  full)
    quality
    step "mutation testing"
    for each in fog fog-server fog-namespaces fog-auth fog-rc fog-commands fog-kernel fog-thread; do
      scope "$each"
    done
    coyote
    fuzz
    ;;
  mutate)
    step "mutation testing: $SCOPE"
    scope "$SCOPE"
    ;;
  coyote) coyote ;;
  fuzz) fuzz ;;
esac

printf '\n\033[32mquality pipeline passed\033[0m\n'
