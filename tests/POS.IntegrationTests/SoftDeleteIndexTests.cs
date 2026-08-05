using Microsoft.EntityFrameworkCore;
using POS.SharedKernel;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// Asserts the rule that soft delete forces on unique indexes.
/// </summary>
/// <remarks>
/// This test exists because the rule is applied correctly to the first eight tables
/// and forgotten on the ninth — and the symptom is a merchant unable to reuse a
/// barcode belonging to a product they cannot see, which reaches support as
/// "the system is broken" rather than as an index problem.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class SoftDeleteIndexTests(ApiFixture fixture)
{
    /// <remarks>
    /// Every module context is checked, not one. There is no single <c>DbContext</c> to
    /// resolve — the platform has one per module, each with its own schema and
    /// migration history (ADR 002) — and checking whichever happened to be registered
    /// first is exactly the "correct on the first eight tables, forgotten on the ninth"
    /// failure this test exists to catch.
    /// </remarks>
    [Fact]
    public void Every_unique_index_on_a_soft_deletable_entity_is_filtered()
    {
        using var scope = fixture.Services.CreateScope();

        var violations = ApiFixture.ModuleContextTypes
            .Select(type => (DbContext)scope.ServiceProvider.GetRequiredService(type))
            .SelectMany(context => context.Model.GetEntityTypes())
            .Where(e => typeof(ISoftDeletable).IsAssignableFrom(e.ClrType))
            .SelectMany(e => e.GetIndexes())
            .Where(i => i.IsUnique)
            .Where(i => i.GetFilter() is null || !i.GetFilter()!.Contains("IsDeleted", StringComparison.Ordinal))
            .Select(i => $"{i.DeclaringEntityType.ClrType.Name}.{i.GetDatabaseName()}")
            .Distinct()
            .ToList();

        violations.ShouldBeEmpty(
            "Unique indexes on soft-deletable entities must be filtered on " +
            "[IsDeleted] = 0, or values can never be reused after deletion.");
    }

    [Fact]
    public async Task A_barcode_can_be_reused_after_its_product_is_deleted()
    {
        // The behaviour the filtered index buys, stated as a business outcome.
        var tenant = await fixture.CreateTenantAsync();

        var first = await fixture.SeedProductWithBarcodeAsync(tenant, "5000112637922");
        await fixture.DeleteProductAsync(tenant, first);

        var second = await fixture.SeedProductWithBarcodeAsync(tenant, "5000112637922");

        second.ShouldNotBe(Guid.Empty);
    }
}
