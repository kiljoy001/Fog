#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

FULL=0
if [[ "${1:-}" == "--full" ]]; then
  FULL=1
fi

MIN_LINE="${MIN_LINE:-25}"
MIN_BRANCH="${MIN_BRANCH:-10}"
CRAP_LIMIT="${CRAP_LIMIT:-30}"
MIN_MUTATION="${MIN_MUTATION:-100}"

step() { printf '\n\033[1m== %s\033[0m\n' "$1"; }

step "build"
dotnet build NinePSharp.sln -c Release -v minimal

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
    /p:Include="[NinePSharp*]*" \
    /p:Exclude="[*.Tests]*%2c[*.Fuzzer]*%2c[*.Examples]*"
}

step "unit, property, parser, backend, and client tests"
coverage_test NinePSharp.Tests/NinePSharp.Tests.csproj json "$ROOT/.artifacts/coverage/merged.json"
coverage_test NinePSharp.Parser.Tests/NinePSharp.Parser.Tests.fsproj json "$ROOT/.artifacts/coverage/merged.json"
coverage_test NinePSharp.Server.Abstractions.Tests/NinePSharp.Server.Abstractions.Tests.csproj json "$ROOT/.artifacts/coverage/merged.json"
coverage_test NinePSharp.Namespaces.Tests/NinePSharp.Namespaces.Tests.csproj json "$ROOT/.artifacts/coverage/merged.json"
coverage_test NinePSharp.Namespaces.Orleans.Tests/NinePSharp.Namespaces.Orleans.Tests.csproj json "$ROOT/.artifacts/coverage/merged.json"
coverage_test NinePSharp.Namespaces.Authorization.Tests/NinePSharp.Namespaces.Authorization.Tests.csproj json "$ROOT/.artifacts/coverage/merged.json"
coverage_test NinePSharp.Fog.Tests/NinePSharp.Fog.Tests.csproj json "$ROOT/.artifacts/coverage/merged.json"
coverage_test NinePSharp.Fog.Server.Tests/NinePSharp.Fog.Server.Tests.csproj json "$ROOT/.artifacts/coverage/merged.json"
coverage_test NinePSharp.Client.Tests/NinePSharp.Client.Tests.csproj cobertura "$ROOT/.artifacts/coverage/dotnet.xml"

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

if command -v semgrep >/dev/null 2>&1; then
  step "semgrep"
  semgrep --config quality/semgrep.yml --error
else
  echo "semgrep not installed; skipping custom static rules"
fi

if [[ $FULL -eq 1 ]]; then
  step "mutation testing"
  rm -rf .artifacts/stryker .artifacts/stryker-namespaces .artifacts/stryker-orleans .artifacts/stryker-orleans-server .artifacts/stryker-transport
  (cd NinePSharp.Tests && dotnet stryker --config-file ../stryker-config-ci.json --reporter json --reporter progress --output ../.artifacts/stryker --skip-version-check --break-on-initial-test-failure --verbosity error)
  (cd NinePSharp.Namespaces.Tests && dotnet stryker --config-file ../stryker-config-namespaces.json --reporter json --reporter progress --output ../.artifacts/stryker-namespaces --skip-version-check --break-on-initial-test-failure --verbosity error)
  (cd NinePSharp.Namespaces.Orleans.Tests && dotnet stryker --config-file ../stryker-config-orleans.json --reporter json --reporter progress --output ../.artifacts/stryker-orleans --skip-version-check --break-on-initial-test-failure --verbosity error)
  (cd NinePSharp.Namespaces.Orleans.Tests && dotnet stryker --config-file ../stryker-config-orleans-server.json --reporter json --reporter progress --output ../.artifacts/stryker-orleans-server --skip-version-check --break-on-initial-test-failure --verbosity error)
  (cd NinePSharp.Tests && dotnet stryker --config-file ../stryker-config-transport.json --reporter json --reporter progress --output ../.artifacts/stryker-transport --skip-version-check --break-on-initial-test-failure --verbosity error)
  python3 tools/mutation_summary.py --output-dir .artifacts/stryker --min-score "$MIN_MUTATION"
  python3 tools/mutation_summary.py --output-dir .artifacts/stryker-namespaces --min-score "$MIN_MUTATION"
  rm -rf .artifacts/stryker-namespaces-authorization
  (cd NinePSharp.Namespaces.Authorization.Tests && dotnet stryker --config-file ../stryker-config-namespaces-authorization.json --reporter json --reporter progress --output ../.artifacts/stryker-namespaces-authorization --skip-version-check --break-on-initial-test-failure --verbosity error)
  python3 tools/mutation_summary.py --output-dir .artifacts/stryker-namespaces-authorization --min-score "$MIN_MUTATION"
  python3 tools/mutation_summary.py --output-dir .artifacts/stryker-orleans --min-score "$MIN_MUTATION"
  python3 tools/mutation_summary.py --output-dir .artifacts/stryker-orleans-server --min-score "$MIN_MUTATION"
  python3 tools/mutation_summary.py --output-dir .artifacts/stryker-transport --min-score "$MIN_MUTATION"

  (cd NinePSharp.Fog.Tests && dotnet stryker --config-file ../stryker-config-fog.json --reporter json --reporter progress --output ../.artifacts/stryker-fog --skip-version-check --break-on-initial-test-failure --verbosity error)
  python3 tools/mutation_summary.py --output-dir .artifacts/stryker-fog --min-score "$MIN_MUTATION"

  (cd NinePSharp.Fog.Server.Tests && dotnet stryker --config-file ../stryker-config-fog-server.json --reporter json --reporter progress --output ../.artifacts/stryker-fog-server --skip-version-check --break-on-initial-test-failure --verbosity error)
  python3 tools/mutation_summary.py --output-dir .artifacts/stryker-fog-server --min-score "$MIN_MUTATION"
  (cd NinePSharp.Fog.Server.Tests && dotnet stryker --config-file ../stryker-config-control-client.json --reporter json --reporter progress --output ../.artifacts/stryker-control-client --skip-version-check --break-on-initial-test-failure --verbosity error)
  python3 tools/mutation_summary.py --output-dir .artifacts/stryker-control-client --min-score "$MIN_MUTATION"

  step "SharpFuzz/AFL parser campaign"
  FUZZ_SECONDS="${FUZZ_SECONDS:-10}" bash scripts/fuzz.sh parser

  step "SharpFuzz/AFL filesystem campaign"
  FUZZ_SECONDS="${FUZZ_SECONDS:-10}" bash scripts/fuzz.sh filesystem

  step "SharpFuzz/AFL namespace campaign"
  FUZZ_SECONDS="${FUZZ_SECONDS:-10}" bash scripts/fuzz.sh namespace

  step "SharpFuzz/AFL namespace syscall campaign"
  FUZZ_SECONDS="${FUZZ_SECONDS:-10}" bash scripts/fuzz.sh namespace-syscalls

  step "SharpFuzz/AFL namespace authorization campaign"
  FUZZ_SECONDS="${FUZZ_SECONDS:-10}" bash scripts/fuzz.sh authorization

  step "SharpFuzz/AFL Orleans gateway campaign"
  FUZZ_SECONDS="${FUZZ_SECONDS:-10}" bash scripts/fuzz.sh orleans

  step "SharpFuzz/AFL fog record and transaction campaign"
  FUZZ_SECONDS="${FUZZ_SECONDS:-10}" bash scripts/fuzz.sh fog

  step "SharpFuzz/AFL fog control-fid campaign"
  FUZZ_SECONDS="${FUZZ_SECONDS:-10}" bash scripts/fuzz.sh fog-files

  step "SharpFuzz/AFL fog dispatcher session campaign"
  FUZZ_SECONDS="${FUZZ_SECONDS:-10}" bash scripts/fuzz.sh fog-dispatcher
fi

printf '\n\033[32mquality pipeline passed\033[0m\n'
