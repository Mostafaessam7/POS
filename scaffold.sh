#!/usr/bin/env bash
#
# Generates the solution and project wiring using the dotnet CLI.
#
# The .sln and .csproj files are produced by the CLI rather than written by hand,
# because hand-authored project XML and solution GUIDs are the most error-prone
# and least interesting part of this exercise.
#
# Run once from the repository root:  ./scaffold.sh
#
set -euo pipefail

SOLUTION="POS"

require() { command -v "$1" >/dev/null 2>&1 || { echo "Missing: $1" >&2; exit 1; }; }
require dotnet

echo "==> .NET SDK $(dotnet --version)"

# ---------------------------------------------------------------------------
# Solution
# ---------------------------------------------------------------------------
[ -f "${SOLUTION}.sln" ] || dotnet new sln -n "${SOLUTION}"

# classlib  : library project
# xunit     : test project
# webapi    : the cloud host
# worker    : background jobs and (later) the terminal agent

# Guard on the project file, not the directory: source files are committed into
# these directories already, so a directory check would skip project creation.
new_lib()  { [ -f "$2/$1.csproj" ] || dotnet new classlib -n "$1" -o "$2" --framework net9.0; }
new_test() { [ -f "$2/$1.csproj" ] || dotnet new xunit    -n "$1" -o "$2" --framework net9.0; }

# ---------------------------------------------------------------------------
# Shared
# ---------------------------------------------------------------------------
new_lib POS.SharedKernel src/Shared/POS.SharedKernel
new_lib POS.Common       src/Shared/POS.Common
new_lib POS.Contracts    src/Shared/POS.Contracts

# The CLI emits a Class1.cs placeholder in every new library. Remove it.
find src tests -name 'Class1.cs' -delete 2>/dev/null || true
find src tests -name 'UnitTest1.cs' -delete 2>/dev/null || true

# ---------------------------------------------------------------------------
# Modules — two projects each (see ADR 001 and the Phase 0 design, P0-1).
#
#   <Module>.Domain   entities, value objects, domain events. ZERO dependencies.
#   <Module>          application + infrastructure. References Domain + EF Core.
#
# Only the Domain boundary is compile-time enforced, because it is the one that
# rots silently. The rest is enforced by tests/POS.ArchitectureTests.
# ---------------------------------------------------------------------------
for module in Catalog Inventory Sales Purchasing Identity; do
  new_lib "POS.${module}.Domain" "src/Modules/${module}/POS.${module}.Domain"
  new_lib "POS.${module}"        "src/Modules/${module}/POS.${module}"

  dotnet add "src/Modules/${module}/POS.${module}.Domain" reference \
    src/Shared/POS.SharedKernel

  dotnet add "src/Modules/${module}/POS.${module}" reference \
    "src/Modules/${module}/POS.${module}.Domain" \
    src/Shared/POS.SharedKernel \
    src/Shared/POS.Common \
    src/Shared/POS.Contracts
done

find src -name 'Class1.cs' -delete 2>/dev/null || true

# ---------------------------------------------------------------------------
# Hosts — composition roots only. No business logic.
# ---------------------------------------------------------------------------
[ -f src/Hosts/POS.Api/POS.Api.csproj ] || dotnet new webapi -n POS.Api -o src/Hosts/POS.Api \
  --framework net9.0 --use-minimal-apis --no-https false

for module in Catalog Inventory Sales Purchasing Identity; do
  dotnet add src/Hosts/POS.Api reference "src/Modules/${module}/POS.${module}"
done

# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------
new_test POS.ArchitectureTests tests/POS.ArchitectureTests
new_test POS.UnitTests         tests/POS.UnitTests
new_test POS.IntegrationTests  tests/POS.IntegrationTests

find tests -name 'UnitTest1.cs' -delete 2>/dev/null || true

dotnet add tests/POS.ArchitectureTests reference \
  src/Shared/POS.SharedKernel src/Shared/POS.Common
for module in Catalog Inventory Sales Purchasing Identity; do
  dotnet add tests/POS.ArchitectureTests reference \
    "src/Modules/${module}/POS.${module}" \
    "src/Modules/${module}/POS.${module}.Domain"
done

dotnet add tests/POS.UnitTests        reference src/Shared/POS.SharedKernel
dotnet add tests/POS.IntegrationTests reference src/Hosts/POS.Api

# ---------------------------------------------------------------------------
# Packages. Versions come from Directory.Packages.props, so no version here.
# ---------------------------------------------------------------------------
add_pkg() { dotnet add "$1" package "$2" --no-restore; }

add_pkg src/Shared/POS.Common FluentValidation
add_pkg src/Shared/POS.Common Microsoft.EntityFrameworkCore

for module in Catalog Inventory Sales Purchasing Identity; do
  add_pkg "src/Modules/${module}/POS.${module}" Microsoft.EntityFrameworkCore
  add_pkg "src/Modules/${module}/POS.${module}" Microsoft.EntityFrameworkCore.SqlServer
  add_pkg "src/Modules/${module}/POS.${module}" FluentValidation
done

add_pkg src/Hosts/POS.Api Serilog.AspNetCore
add_pkg src/Hosts/POS.Api Serilog.Sinks.Seq
add_pkg src/Hosts/POS.Api OpenTelemetry.Extensions.Hosting
add_pkg src/Hosts/POS.Api OpenTelemetry.Exporter.OpenTelemetryProtocol
add_pkg src/Hosts/POS.Api OpenTelemetry.Instrumentation.AspNetCore
add_pkg src/Hosts/POS.Api OpenTelemetry.Instrumentation.Http
add_pkg src/Hosts/POS.Api Microsoft.EntityFrameworkCore.Design
add_pkg src/Hosts/POS.Api FluentValidation.DependencyInjectionExtensions

for project in tests/POS.ArchitectureTests tests/POS.UnitTests tests/POS.IntegrationTests; do
  add_pkg "$project" Shouldly
done
add_pkg tests/POS.ArchitectureTests TngTech.ArchUnitNET.xUnit
add_pkg tests/POS.UnitTests         NSubstitute
add_pkg tests/POS.UnitTests         FsCheck.Xunit
add_pkg tests/POS.IntegrationTests  Microsoft.AspNetCore.Mvc.Testing
add_pkg tests/POS.IntegrationTests  Testcontainers.MsSql
add_pkg tests/POS.IntegrationTests  Respawn

# ---------------------------------------------------------------------------
# Register everything with the solution
# ---------------------------------------------------------------------------
find src tests -name '*.csproj' -exec dotnet sln "${SOLUTION}.sln" add {} \;

# ---------------------------------------------------------------------------
# Lock files: reproducible restores. CI uses --locked-mode.
# ---------------------------------------------------------------------------
dotnet restore --use-lock-file

echo
echo "==> Building"
dotnet build --no-restore

echo
echo "Done. Next:"
echo "  docker compose up -d"
echo "  dotnet test tests/POS.ArchitectureTests"
