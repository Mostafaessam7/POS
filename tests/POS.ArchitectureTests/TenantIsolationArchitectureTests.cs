using System.Text.RegularExpressions;

namespace POS.ArchitectureTests;

/// <summary>
/// Rule 12 — every DbContext applies the tenant query filter (ADR 006).
/// </summary>
/// <remarks>
/// <para>
/// This rule exists because the failure it prevents actually happened. Catalog,
/// Identity and Sync all called <c>TenantQueryFilter.ApplyTo</c>; Inventory did not,
/// and stock movements and balances were readable across tenants. Nothing caught it:
/// it compiles, and every test passes unless a test specifically looks for it.
/// </para>
/// <para>
/// The lesson is narrower than "be careful". <c>TenantQueryFilter</c> centralised the
/// mechanism — one method, applied by reflection, so a new ENTITY is filtered
/// automatically. But applying it stayed manual and per-context, so a new DBCONTEXT
/// was still unprotected by default. Centralising a mechanism does not remove the
/// failure mode if invoking it remains someone's responsibility to remember.
/// </para>
/// <para>
/// A source scan is the right instrument. What we need to assert is that a specific
/// call appears in a specific method, which is a syntactic fact rather than a
/// type-level dependency ArchUnit could see.
/// </para>
/// </remarks>
public sealed class TenantIsolationArchitectureTests
{
    /// <summary>
    /// Contexts exempt from tenant filtering, with the reason.
    /// </summary>
    /// <remarks>
    /// The terminal's local SQLite store belongs to exactly one tenant by
    /// construction — the till is physically installed in one merchant's shop and
    /// never holds another's data. A filter there would be theatre.
    /// </remarks>
    private static readonly string[] ExemptContexts = ["TerminalDbContext.cs"];

    private static readonly Regex ContextDeclaration = new(
        @"class\s+\w*DbContext\b",
        RegexOptions.Compiled);

    /// <summary>
    /// An abstract base context, which is not required to apply the filter itself.
    /// </summary>
    /// <remarks>
    /// This is a narrowing of the rule, not a hole in it. An abstract context cannot be
    /// instantiated and therefore never runs a query; every CONCRETE context that
    /// derives from it is still scanned and still has to call
    /// <c>TenantQueryFilter.ApplyTo</c> in its own <c>OnModelCreating</c>. Requiring the
    /// call in a base that has no model to build would only invite someone to satisfy
    /// the rule by moving the call somewhere it cannot see the derived model.
    /// </remarks>
    private static readonly Regex AbstractContextDeclaration = new(
        @"abstract\s+class\s+\w*DbContext\b",
        RegexOptions.Compiled);

    [Fact]
    public void Every_DbContext_applies_the_tenant_query_filter()
    {
        var root = SolutionRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(Path.Combine(root, "src"), "*DbContext.cs",
                                                SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                              StringComparison.Ordinal)
                || ExemptContexts.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            var text = File.ReadAllText(file);

            if (!ContextDeclaration.IsMatch(text) || AbstractContextDeclaration.IsMatch(text))
            {
                continue;
            }

            if (!text.Contains("TenantQueryFilter.ApplyTo", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(root, file));
            }
        }

        offenders.ShouldBeEmpty(
            "every DbContext must call TenantQueryFilter.ApplyTo in OnModelCreating. " +
            "Without it, reads are not scoped to the current tenant and data leaks " +
            "silently across the security boundary — see ADR 006. If a context is " +
            "genuinely single-tenant, add it to ExemptContexts with a written reason.");
    }

    /// <summary>
    /// A DbContext overriding SaveChanges must hook the overloads EF actually routes
    /// through, or the guard it is enforcing can be bypassed.
    /// </summary>
    /// <remarks>
    /// EF routes <c>SaveChanges()</c> to <c>SaveChanges(bool)</c> and
    /// <c>SaveChangesAsync(ct)</c> to <c>SaveChangesAsync(bool, ct)</c>. Overriding
    /// only the convenience overloads leaves the synchronous path — and any direct
    /// call to the two-argument form — running unguarded. Inventory's append-only
    /// ledger check had exactly this hole: an invariant that looked enforced and was
    /// not.
    /// </remarks>
    [Fact]
    public void SaveChanges_overrides_hook_the_root_overloads()
    {
        var root = SolutionRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(Path.Combine(root, "src"), "*DbContext.cs",
                                                SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                              StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (!text.Contains("override", StringComparison.Ordinal)
                || !text.Contains("SaveChanges", StringComparison.Ordinal))
            {
                continue;
            }

            var overridesAsync = text.Contains("override Task<int> SaveChangesAsync", StringComparison.Ordinal);
            var overridesSync = text.Contains("override int SaveChanges", StringComparison.Ordinal);

            if (!overridesAsync && !overridesSync)
            {
                continue;
            }

            var hooksAsyncRoot = text.Contains("SaveChangesAsync(\n        bool", StringComparison.Ordinal)
                                 || text.Contains("SaveChangesAsync(bool", StringComparison.Ordinal);
            var hooksSyncRoot = text.Contains("SaveChanges(bool", StringComparison.Ordinal);

            if (!hooksAsyncRoot || !hooksSyncRoot)
            {
                offenders.Add(Path.GetRelativePath(root, file));
            }
        }

        offenders.ShouldBeEmpty(
            "a DbContext that overrides SaveChanges must override BOTH root overloads " +
            "— SaveChanges(bool) and SaveChangesAsync(bool, CancellationToken) — or the " +
            "check it performs can be bypassed by calling the other path.");
    }

    private static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
               ?? throw new InvalidOperationException("Could not locate the solution root.");
    }
}
