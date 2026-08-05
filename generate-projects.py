#!/usr/bin/env python3
"""
Generates the .csproj files and POS.sln for the solution.

Dependencies below were DERIVED from the `using` directives actually present in the
source, not assumed from the architecture diagram. Where the two disagreed, the source
won and the discrepancy is recorded in the summary.
"""
import os, uuid, textwrap

ROOT = os.path.dirname(os.path.abspath(__file__))
NS = uuid.UUID("6ba7b810-9dad-11d1-80b4-00c04fd430c8")
CS_TYPE = "{9A19103F-16F7-4668-BE54-9A1E7A4F7556}"
FOLDER_TYPE = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}"

def guid(name):
    return "{" + str(uuid.uuid5(NS, name)).upper() + "}"

# name, path, sdk, solution folder, project refs, packages, framework refs, extra props
P = [
    # ---------- Shared ----------
    ("POS.SharedKernel", "src/Shared/POS.SharedKernel", "Microsoft.NET.Sdk", "src/Shared",
     [], [], [], {}),
    ("POS.Hardware.Abstractions", "src/Shared/POS.Hardware.Abstractions", "Microsoft.NET.Sdk",
     "src/Shared", ["POS.SharedKernel"], [], [], {}),
    ("POS.Contracts", "src/Shared/POS.Contracts", "Microsoft.NET.Sdk", "src/Shared",
     [], [], [], {}),
    ("POS.Common", "src/Shared/POS.Common", "Microsoft.NET.Sdk", "src/Shared",
     ["POS.SharedKernel"],
     ["Microsoft.EntityFrameworkCore", "Microsoft.EntityFrameworkCore.Relational",
      "Microsoft.EntityFrameworkCore.SqlServer", "Microsoft.EntityFrameworkCore.Design",
      "FluentValidation"],
     ["Microsoft.AspNetCore.App"], {}),

    # ---------- Catalog ----------
    ("POS.Catalog.Domain", "src/Modules/Catalog/POS.Catalog.Domain", "Microsoft.NET.Sdk",
     "src/Modules/Catalog", ["POS.SharedKernel"], [], [], {}),
    ("POS.Catalog", "src/Modules/Catalog/POS.Catalog", "Microsoft.NET.Sdk",
     "src/Modules/Catalog", ["POS.Catalog.Domain", "POS.Common"],
     ["Microsoft.EntityFrameworkCore", "Microsoft.EntityFrameworkCore.Relational"], [], {}),

    # ---------- Identity ----------
    ("POS.Identity.Domain", "src/Modules/Identity/POS.Identity.Domain", "Microsoft.NET.Sdk",
     "src/Modules/Identity", ["POS.SharedKernel"], [], [], {}),
    ("POS.Identity", "src/Modules/Identity/POS.Identity", "Microsoft.NET.Sdk",
     "src/Modules/Identity", ["POS.Identity.Domain", "POS.Common"],
     ["Microsoft.EntityFrameworkCore", "Microsoft.EntityFrameworkCore.Relational",
      "Konscious.Security.Cryptography.Argon2", "System.IdentityModel.Tokens.Jwt",
      "Microsoft.AspNetCore.Authentication.JwtBearer"],
     ["Microsoft.AspNetCore.App"], {}),

    # ---------- Sync ----------
    ("POS.Sync.Domain", "src/Modules/Sync/POS.Sync.Domain", "Microsoft.NET.Sdk",
     "src/Modules/Sync", ["POS.SharedKernel"], [], [], {}),
    ("POS.Sync", "src/Modules/Sync/POS.Sync", "Microsoft.NET.Sdk",
     "src/Modules/Sync", ["POS.Sync.Domain", "POS.Common"],
     ["Microsoft.EntityFrameworkCore", "Microsoft.EntityFrameworkCore.Relational"], [], {}),

    # ---------- Inventory ----------
    ("POS.Inventory.Domain", "src/Modules/Inventory/POS.Inventory.Domain", "Microsoft.NET.Sdk",
     "src/Modules/Inventory", ["POS.SharedKernel"], [], [], {}),
    ("POS.Inventory", "src/Modules/Inventory/POS.Inventory", "Microsoft.NET.Sdk",
     "src/Modules/Inventory", ["POS.Inventory.Domain", "POS.Common"],
     ["Microsoft.EntityFrameworkCore", "Microsoft.EntityFrameworkCore.Relational"], [], {}),


    # ---------- Payments (Phase 6) ----------
    # Same shape as Fiscal, for the same reason: a core that takes money without
    # knowing whose rails it runs on. Domain knows nothing of providers; providers
    # know nothing of each other; the orchestrator branches on capabilities only.
    ("POS.Payments.Domain", "src/Modules/Payments/POS.Payments.Domain", "Microsoft.NET.Sdk",
     "src/Modules/Payments", ["POS.SharedKernel"], [], [], {}),
    ("POS.Payments.Abstractions", "src/Modules/Payments/POS.Payments.Abstractions", "Microsoft.NET.Sdk",
     "src/Modules/Payments", ["POS.SharedKernel", "POS.Payments.Domain"], [], [], {}),
    ("POS.Payments", "src/Modules/Payments/POS.Payments", "Microsoft.NET.Sdk",
     "src/Modules/Payments", ["POS.Payments.Abstractions", "POS.Payments.Domain", "POS.Common"],
     ["Microsoft.EntityFrameworkCore", "Microsoft.EntityFrameworkCore.Relational"], [], {}),
    ("POS.Payments.Manual", "src/Modules/Payments/POS.Payments.Manual", "Microsoft.NET.Sdk",
     "src/Modules/Payments", ["POS.Payments.Abstractions", "POS.Payments.Domain"], [], [], {}),

    # ---------- Purchasing and Expenses (Phase 7) ----------
    ("POS.Purchasing.Domain", "src/Modules/Purchasing/POS.Purchasing.Domain", "Microsoft.NET.Sdk",
     "src/Modules/Purchasing", ["POS.SharedKernel"], [], [], {}),
    ("POS.Purchasing", "src/Modules/Purchasing/POS.Purchasing", "Microsoft.NET.Sdk",
     "src/Modules/Purchasing", ["POS.Purchasing.Domain", "POS.Common"],
     ["Microsoft.EntityFrameworkCore", "Microsoft.EntityFrameworkCore.Relational"], [], {}),
    ("POS.Expenses.Domain", "src/Modules/Expenses/POS.Expenses.Domain", "Microsoft.NET.Sdk",
     "src/Modules/Expenses", ["POS.SharedKernel"], [], [], {}),
    ("POS.Expenses", "src/Modules/Expenses/POS.Expenses", "Microsoft.NET.Sdk",
     "src/Modules/Expenses", ["POS.Expenses.Domain", "POS.Common"],
     ["Microsoft.EntityFrameworkCore", "Microsoft.EntityFrameworkCore.Relational"], [], {}),

    # ---------- Reconciliation (infrastructure milestone) ----------
    # Takes plain projections, never another module's aggregates, so it can depend on
    # nothing and still reconcile everything. See ADR 002 and the reconciler doc-comments.
    ("POS.Reconciliation.Domain", "src/Modules/Reconciliation/POS.Reconciliation.Domain",
     "Microsoft.NET.Sdk", "src/Modules/Reconciliation", ["POS.SharedKernel"], [], [], {}),

    # ---------- Fiscal ----------
    # Dependency direction here is load-bearing and enforced by FiscalAgnosticismTests:
    # plugins reference Abstractions ONLY. Nothing references the pipeline but the host.
    ("POS.Fiscal.Abstractions", "src/Modules/Fiscal/POS.Fiscal.Abstractions", "Microsoft.NET.Sdk",
     "src/Modules/Fiscal", ["POS.SharedKernel"], [], [], {}),
    ("POS.Fiscal.Domain", "src/Modules/Fiscal/POS.Fiscal.Domain", "Microsoft.NET.Sdk",
     "src/Modules/Fiscal", ["POS.SharedKernel"], [], [], {}),
    # POS.Fiscal references POS.Fiscal.Generic deliberately: the infrastructure module
    # supplies the EF-backed allocator the generic profile's numbering strategy needs.
    ("POS.Fiscal", "src/Modules/Fiscal/POS.Fiscal", "Microsoft.NET.Sdk",
     "src/Modules/Fiscal",
     ["POS.Fiscal.Abstractions", "POS.Fiscal.Domain", "POS.Fiscal.Generic", "POS.Common"],
     ["Microsoft.EntityFrameworkCore", "Microsoft.EntityFrameworkCore.Relational"], [], {}),
    ("POS.Fiscal.Generic", "src/Modules/Fiscal/POS.Fiscal.Generic", "Microsoft.NET.Sdk",
     "src/Modules/Fiscal", ["POS.Fiscal.Abstractions"], [], [], {}),

    # ---------- Sales ----------
    ("POS.Sales.Domain", "src/Modules/Sales/POS.Sales.Domain", "Microsoft.NET.Sdk",
     "src/Modules/Sales", ["POS.SharedKernel"], [], [], {}),
    ("POS.Sales", "src/Modules/Sales/POS.Sales", "Microsoft.NET.Sdk",
     "src/Modules/Sales", ["POS.Sales.Domain", "POS.Common", "POS.Sync"],
     ["Microsoft.EntityFrameworkCore", "Microsoft.EntityFrameworkCore.Relational"], [], {}),

    # ---------- Hosts ----------
    ("POS.Api", "src/Hosts/POS.Api", "Microsoft.NET.Sdk.Web", "src/Hosts",
     ["POS.Common", "POS.Catalog", "POS.Identity", "POS.Inventory", "POS.Sales",
      "POS.Sync", "POS.Fiscal", "POS.Fiscal.Generic",
      "POS.Payments", "POS.Payments.Domain", "POS.Payments.Abstractions",
      "POS.Payments.Manual", "POS.Hardware.Abstractions",
      "POS.Purchasing", "POS.Purchasing.Domain", "POS.Expenses", "POS.Expenses.Domain"],
     ["Serilog.AspNetCore", "Serilog.Sinks.Seq", "Microsoft.EntityFrameworkCore.SqlServer",
      "Microsoft.EntityFrameworkCore.Design", "FluentValidation.DependencyInjectionExtensions",
      "OpenTelemetry.Extensions.Hosting", "OpenTelemetry.Instrumentation.AspNetCore",
      "OpenTelemetry.Exporter.OpenTelemetryProtocol"],
     [], {}),

    # Library, not an executable: there is no Program.cs and inventing its host wiring
    # would be feature work. See summary.
    # Runs on the ASP.NET Core shared framework alone. Deliberately references NO
    # packages, which is what makes it buildable where POS.Api is not (ADR 056).
    ("POS.WalkingSkeleton", "src/Hosts/POS.WalkingSkeleton", "Microsoft.NET.Sdk.Web", "src/Hosts",
     ["POS.SharedKernel", "POS.Inventory.Domain", "POS.Purchasing.Domain",
      "POS.Reconciliation.Domain"], [], [], {}),

    ("POS.TerminalAgent", "src/Hosts/POS.TerminalAgent", "Microsoft.NET.Sdk", "src/Hosts",
     ["POS.Common", "POS.Sync"],
     ["Microsoft.EntityFrameworkCore.Sqlite", "Microsoft.Extensions.Hosting"], [], {}),

    # ---------- Tests ----------
    ("POS.UnitTests", "tests/POS.UnitTests", "Microsoft.NET.Sdk", "tests",
     ["POS.SharedKernel", "POS.Catalog.Domain", "POS.Identity", "POS.Identity.Domain",
      "POS.Inventory", "POS.Inventory.Domain", "POS.Sales", "POS.Sales.Domain",
      "POS.Fiscal.Abstractions", "POS.Fiscal.Generic",
      "POS.Payments", "POS.Payments.Domain", "POS.Payments.Abstractions",
      "POS.Payments.Manual", "POS.Hardware.Abstractions",
      "POS.Purchasing.Domain", "POS.Expenses.Domain", "POS.Reconciliation.Domain"],
     ["xunit", "xunit.runner.visualstudio", "Microsoft.NET.Test.Sdk", "Shouldly"],
     [], {"test": True}),

    ("POS.ArchitectureTests", "tests/POS.ArchitectureTests", "Microsoft.NET.Sdk", "tests",
     ["POS.SharedKernel", "POS.Common", "POS.Catalog", "POS.Catalog.Domain",
      "POS.Identity", "POS.Identity.Domain", "POS.Inventory", "POS.Inventory.Domain",
      "POS.Sales", "POS.Sales.Domain", "POS.Sync", "POS.Sync.Domain",
      "POS.Fiscal", "POS.Fiscal.Abstractions", "POS.Fiscal.Domain", "POS.Fiscal.Generic",
      "POS.Payments", "POS.Payments.Domain", "POS.Payments.Abstractions",
      "POS.Payments.Manual", "POS.Hardware.Abstractions",
      "POS.Purchasing.Domain", "POS.Expenses.Domain", "POS.Reconciliation.Domain"],
     ["xunit", "xunit.runner.visualstudio", "Microsoft.NET.Test.Sdk", "Shouldly",
      "TngTech.ArchUnitNET.xUnit"],
     [], {"test": True}),

    ("POS.IntegrationTests", "tests/POS.IntegrationTests", "Microsoft.NET.Sdk", "tests",
     ["POS.Api", "POS.SharedKernel", "POS.Common", "POS.Catalog", "POS.Identity",
      "POS.Identity.Domain", "POS.Inventory", "POS.Sales", "POS.Sync"],
     ["xunit", "xunit.runner.visualstudio", "Microsoft.NET.Test.Sdk", "Shouldly",
      "Microsoft.AspNetCore.Mvc.Testing", "Testcontainers.MsSql", "Respawn",
      "Microsoft.EntityFrameworkCore.SqlServer"],
     [], {"test": True}),
]

BY_NAME = {n: p for n, p, *_ in P}

def rel_path(frm, to):
    r = os.path.relpath(os.path.join(ROOT, to), os.path.join(ROOT, frm))
    return r.replace("/", "\\")

for name, path, sdk, folder, refs, pkgs, fwrefs, extra in P:
    is_test = extra.get("test", False)
    lines = [f'<Project Sdk="{sdk}">', ""]

    props = []
    if is_test:
        props += ["    <IsPackable>false</IsPackable>",
                  "    <IsTestProject>true</IsTestProject>",
                  "    <!-- Test method names use underscores by convention. -->",
                  "    <NoWarn>$(NoWarn);CA1707</NoWarn>"]
    if props:
        lines += ["  <PropertyGroup>"] + props + ["  </PropertyGroup>", ""]

    if is_test:
        # Project-level global usings. Several test files omit these directives, and
        # adding them here is build configuration rather than a source edit.
        lines += ["  <ItemGroup Label=\"Global usings\">",
                  '    <Using Include="Xunit" />',
                  '    <Using Include="Shouldly" />',
                  "  </ItemGroup>", ""]

    if fwrefs:
        lines.append("  <ItemGroup>")
        for f in fwrefs:
            lines.append(f'    <FrameworkReference Include="{f}" />')
        lines += ["  </ItemGroup>", ""]

    if pkgs:
        lines.append("  <ItemGroup>")
        for p in sorted(pkgs):
            lines.append(f'    <PackageReference Include="{p}" />')
        lines += ["  </ItemGroup>", ""]

    if refs:
        lines.append("  <ItemGroup>")
        for r in sorted(refs):
            lines.append(f'    <ProjectReference Include="{rel_path(path, BY_NAME[r])}\\{r}.csproj" />')
        lines += ["  </ItemGroup>", ""]

    lines.append("</Project>")
    os.makedirs(os.path.join(ROOT, path), exist_ok=True)
    with open(os.path.join(ROOT, path, f"{name}.csproj"), "w") as fh:
        fh.write("\n".join(lines).replace("\n\n\n", "\n\n") + "\n")

# ---------------------------------------------------------------- solution
folders = ["src", "src/Shared", "src/Modules", "src/Modules/Catalog",
           "src/Modules/Identity", "src/Modules/Inventory", "src/Modules/Sync",
           "src/Modules/Fiscal", "src/Modules/Sales", "src/Modules/Payments",
           "src/Modules/Purchasing", "src/Modules/Expenses",
           "src/Modules/Reconciliation",
           "src/Hosts", "tests", "build"]

s = ["Microsoft Visual Studio Solution File, Format Version 12.00",
     "# Visual Studio Version 17",
     "VisualStudioVersion = 17.12.35506.116",
     "MinimumVisualStudioVersion = 10.0.40219.1"]

for f in folders:
    leaf = f.split("/")[-1]
    s.append(f'Project("{FOLDER_TYPE}") = "{leaf}", "{leaf}", "{guid("folder:" + f)}"')
    if f == "build":
        s += ["\tProjectSection(SolutionItems) = preProject",
              "\t\tDirectory.Build.props = Directory.Build.props",
              "\t\tDirectory.Packages.props = Directory.Packages.props",
              "\t\t.editorconfig = .editorconfig",
              "\t\tdocker-compose.yml = docker-compose.yml",
              "\t\tREADME.md = README.md",
              "\tEndProjectSection"]
    s.append("EndProject")

for name, path, *_ in P:
    s.append(f'Project("{CS_TYPE}") = "{name}", "{path.replace("/", chr(92))}\\{name}.csproj", "{guid(name)}"')
    s.append("EndProject")

s += ["Global",
      "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution",
      "\t\tDebug|Any CPU = Debug|Any CPU",
      "\t\tRelease|Any CPU = Release|Any CPU",
      "\tEndGlobalSection",
      "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution"]

for name, *_ in P:
    g = guid(name)
    for cfg in ("Debug", "Release"):
        s.append(f"\t\t{g}.{cfg}|Any CPU.ActiveCfg = {cfg}|Any CPU")
        s.append(f"\t\t{g}.{cfg}|Any CPU.Build.0 = {cfg}|Any CPU")

s += ["\tEndGlobalSection",
      "\tGlobalSection(SolutionProperties) = preSolution",
      "\t\tHideSolutionNode = FALSE",
      "\tEndGlobalSection",
      "\tGlobalSection(NestedProjects) = preSolution"]

for f in folders:
    if "/" in f:
        parent = f.rsplit("/", 1)[0]
        s.append(f'\t\t{guid("folder:" + f)} = {guid("folder:" + parent)}')

for name, path, sdk, folder, *_ in P:
    s.append(f'\t\t{guid(name)} = {guid("folder:" + folder)}')

s += ["\tEndGlobalSection",
      "\tGlobalSection(ExtensibilityGlobals) = postSolution",
      f"\t\tSolutionGuid = {guid('solution:POS')}",
      "\tEndGlobalSection",
      "EndGlobal"]

with open(os.path.join(ROOT, "POS.sln"), "w") as fh:
    fh.write("\n".join(s) + "\n")

print(f"{len(P)} projects, {len(folders)} solution folders")
