#!/usr/bin/env bash
set -euo pipefail

dotnet build NinePSharp.sln -c Debug -v minimal
dotnet test NinePSharp.Tests/NinePSharp.Tests.csproj -c Debug --no-build -v minimal
dotnet test NinePSharp.Parser.Tests/NinePSharp.Parser.Tests.fsproj -c Debug --no-build -v minimal
dotnet test NinePSharp.Server.Abstractions.Tests/NinePSharp.Server.Abstractions.Tests.csproj -c Debug --no-build -v minimal
dotnet test NinePSharp.Client.Tests/NinePSharp.Client.Tests.csproj -c Debug --no-build -v minimal

if command -v semgrep >/dev/null 2>&1; then
  semgrep --config quality/semgrep.yml --error
else
  echo "semgrep not installed; skipping custom static rules"
fi
