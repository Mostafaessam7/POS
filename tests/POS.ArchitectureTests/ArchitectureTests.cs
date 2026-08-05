using ArchUnitNET.Domain;
using ArchUnitNET.Domain.Extensions;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace POS.ArchitectureTests;

/// <summary>
/// Boundaries that are not tested do not exist.
/// </summary>
/// <remarks>
/// In a modular monolith, module boundaries survive exactly as long as something
/// enforces them. Without an automated check, the first deadline produces a direct
/// reference from Sales into Catalog's internals, and within a year you have a
/// distributed monolith with all of the coupling and none of the benefits.
///
/// These run third in CI — after build, before unit tests — because they are the
/// fastest stage and catch the most common class of pull request mistake.
///
/// Add new rules only in response to a real defect. A rule set so strict that
/// developers routinely suppress it is worse than no rule set, because it creates
/// confidence that has not been earned.
/// </remarks>
public sealed class ArchitectureTests
{
    private const string DomainAssemblyPattern = @"POS\..*\.Domain";
    private const string ModuleAssemblyPattern = @"POS\.(Catalog|Inventory|Sales|Purchasing|Identity)(\..*)?";

    private static readonly string[] IsolatedModules =
        ["Catalog", "Inventory", "Sales", "Purchasing", "Identity"];

    /// <summary>
    /// Every POS assembly in the test output, discovered rather than listed.
    /// </summary>
    /// <remarks>
    /// A hand-maintained list is the failure mode these rules exist to prevent: a new
    /// module is added, nobody remembers to register it here, and the suite goes on
    /// reporting green over an architecture it is no longer looking at. Discovery from
    /// the output directory cannot silently omit anything — every project reference is
    /// copied there by the build.
    ///
    /// <c>GetReferencedAssemblies()</c> would be the obvious alternative and is wrong:
    /// the compiler drops references whose types are never used in IL, so exactly the
    /// modules this suite has no test touching would be the ones it stopped loading.
    /// </remarks>
    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(
            Directory.EnumerateFiles(AppContext.BaseDirectory, "POS.*.dll")
                .Where(path => !Path.GetFileName(path).StartsWith("POS.ArchitectureTests", StringComparison.Ordinal))
                .Select(System.Reflection.Assembly.LoadFrom)
                .ToArray())
        .Build();

    // ---------------------------------------------------------------------
    // Rule 1 — Domain purity.
    // The one boundary worth compile-time cost, because it is the one that rots
    // silently. An [Column] attribute on an entity is how persistence concerns
    // begin leaking into the model.
    // ---------------------------------------------------------------------
    [Fact]
    public void Domain_should_not_depend_on_infrastructure_frameworks()
    {
        IArchRule rule = Types()
            .That().ResideInAssembly(DomainAssemblyPattern, useRegularExpressions: true)
            .Should().NotDependOnAny(
                Types().That().ResideInNamespace("Microsoft.EntityFrameworkCore", true))
            .AndShould().NotDependOnAny(
                Types().That().ResideInNamespace("Microsoft.AspNetCore", true))
            .AndShould().NotDependOnAny(
                Types().That().ResideInNamespace("System.Text.Json", true))
            .Because("the domain model must be expressible without reference to how it is " +
                     "stored or transported");

        rule.Check(Architecture);
    }

    // ---------------------------------------------------------------------
    // Rule 2 — Module isolation.
    // Cross-module access goes through POS.Contracts or an integration event.
    // ---------------------------------------------------------------------
    [Fact]
    public void Modules_should_not_reference_each_other()
    {
        foreach (var module in IsolatedModules)
        {
            var others = IsolatedModules
                .Where(m => m != module)
                .Select(m => $@"POS\.{m}(\..*)?")
                .ToArray();

            foreach (var other in others)
            {
                IArchRule rule = Types()
                    .That().ResideInAssembly($@"POS\.{module}(\..*)?", useRegularExpressions: true)
                    .Should().NotDependOnAny(
                        Types().That().ResideInAssembly(other, useRegularExpressions: true))
                    .Because("modules communicate through POS.Contracts or integration events, " +
                             "never by reaching into each other's internals");

                rule.Check(Architecture);
            }
        }
    }

    // ---------------------------------------------------------------------
    // Rule 5 — Composition root direction.
    // Hosts wire everything together; nothing may depend on a host.
    // ---------------------------------------------------------------------
    [Fact]
    public void Nothing_should_depend_on_a_host()
    {
        IArchRule rule = Types()
            .That().DoNotResideInAssembly(@"POS\.(Api|TerminalAgent|Worker)", useRegularExpressions: true)
            .Should().NotDependOnAny(
                Types().That().ResideInAssembly(@"POS\.(Api|TerminalAgent|Worker)", useRegularExpressions: true))
            .Because("hosts are composition roots and are inherently unreusable");

        rule.Check(Architecture);
    }

    // ---------------------------------------------------------------------
    // Rule 6 — EF materialisation without exposing invalid construction.
    // ---------------------------------------------------------------------
    [Fact]
    public void Entities_should_not_have_a_public_parameterless_constructor()
    {
        var offenders = Architecture.Classes
            // IsAssignableTo returns bool? — null means the base type is outside the
            // loaded architecture, which is not the same as "no".
            .Where(c => c.IsAssignableTo("POS.SharedKernel.Entity`1") == true)
            .Where(c => c.IsAbstract != true)
            .Where(c => c.GetConstructors().Any(ctor =>
                ctor.Visibility == Visibility.Public && !ctor.Parameters.Any()))
            .Select(c => c.FullName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Entities must not be constructible in an invalid state. Make the " +
            "parameterless constructor protected (EF Core can still use it). " +
            "Offenders: " + string.Join(", ", offenders));
    }

    // ---------------------------------------------------------------------
    // Rule 7 — Aggregate invariants cannot be bypassed through a mutable collection.
    // ---------------------------------------------------------------------
    [Fact]
    public void Aggregates_should_not_expose_mutable_collections()
    {
        var offenders = Architecture.Classes
            .Where(c => c.IsAssignableTo("POS.SharedKernel.AggregateRoot`1") == true)
            .SelectMany(c => c.GetPropertyMembers()
                .Where(p => p.Visibility == Visibility.Public)
                .Where(p => p.Type.FullName.StartsWith("System.Collections.Generic.List", StringComparison.Ordinal)
                         || p.Type.FullName.StartsWith("System.Collections.Generic.ICollection", StringComparison.Ordinal))
                .Select(p => $"{c.Name}.{p.Name}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Public collections on an aggregate must be IReadOnlyList<T>. A caller " +
            "holding a List<T> can add a line item without the aggregate ever " +
            "validating it. Offenders: " + string.Join(", ", offenders));
    }

    // ---------------------------------------------------------------------
    // Rule 10 — Application logic stays transport-agnostic.
    // This is not purism. Phase 2's Terminal Agent hosts the same handlers
    // outside ASP.NET Core; an HttpContext reference makes that impossible.
    // ---------------------------------------------------------------------
    [Fact]
    public void Handlers_should_not_reference_HttpContext()
    {
        IArchRule rule = Classes()
            .That().HaveNameEndingWith("Handler")
            .Should().NotDependOnAny(
                Types().That().HaveFullName("Microsoft.AspNetCore.Http.HttpContext"))
            .Because("handlers must be callable from the terminal agent, which has no HTTP pipeline");

        rule.Check(Architecture);
    }
}
