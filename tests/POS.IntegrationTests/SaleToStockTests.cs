using Microsoft.EntityFrameworkCore;
using POS.Inventory.Domain;
using POS.Inventory.Persistence;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// Uploaded sales take stock off the shelf.
/// </summary>
/// <remarks>
/// The other half of the seam the purchasing tests prove inbound. Until now a sale
/// could be synced, stored and reported on while inventory carried on believing the
/// goods were still there — which is the failure mode that makes every stock figure in
/// the system wrong and is invisible until someone counts a shelf.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class SaleToStockTests(ApiFixture fixture)
{
    [Fact]
    public async Task An_uploaded_sale_reduces_stock_for_every_line()
    {
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();
        var batch = fixture.BuildSaleBatch(terminal, sequenceFrom: 1, count: 3);

        var response = await fixture.UploadAsync(tenant, batch);
        response.Accepted.ShouldBe(3);

        // Each generated sale is one line of one unit, and every record in a batch uses
        // its own variant, so three sales means three variants each at -1.
        foreach (var variantId in ApiFixture.VariantsIn(batch))
        {
            var balance = await BalanceAsync(tenant, terminal, variantId);

            balance.ShouldNotBeNull();
            balance.QuantityOnHand.ShouldBe(-1m);

            // Negative is permitted and reported, not refused (ADR 027). An offline
            // terminal legitimately sells stock the server has never heard of.
            balance.IsNegative.ShouldBeTrue();
        }
    }

    /// <summary>
    /// Replaying a batch does not deplete stock a second time.
    /// </summary>
    /// <remarks>
    /// Two guards have to line up for this. The ingest service recognises the record as
    /// already seen, and the stock port recognises the document — the latter keyed on
    /// the TERMINAL-assigned sale id, which is why the payload carries it rather than
    /// the server minting a fresh one. Regenerating the id would make every retry look
    /// like a new sale and double the depletion, on an append-only ledger where it
    /// could never be tidied away.
    /// </remarks>
    [Fact]
    public async Task Replaying_a_batch_does_not_deplete_stock_twice()
    {
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();
        var batch = fixture.BuildSaleBatch(terminal, sequenceFrom: 1, count: 4);

        var first = await fixture.UploadAsync(tenant, batch);
        first.Accepted.ShouldBe(4);

        var second = await fixture.UploadAsync(tenant, batch);
        second.Accepted.ShouldBe(0);
        second.Duplicates.ShouldBe(4);

        var variantId = ApiFixture.VariantsIn(batch)[0];

        var balance = await BalanceAsync(tenant, terminal, variantId);
        balance!.QuantityOnHand.ShouldBe(-1m);

        var movements = await fixture.ReadAsync<InventoryDbContext, int>(tenant, db =>
            db.StockMovements.CountAsync(m => m.VariantId == variantId));

        movements.ShouldBe(1);
    }

    /// <summary>
    /// A rejected record moves no stock.
    /// </summary>
    /// <remarks>
    /// The malformed record in this batch has an unroutable type, so no handler runs
    /// for it and nothing should reach the ledger on its behalf. Worth asserting
    /// explicitly: the ledger is append-only, so a movement written for a record the
    /// system then refused could only ever be offset, never removed.
    /// </remarks>
    [Fact]
    public async Task A_rejected_record_moves_no_stock()
    {
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();
        var batch = fixture.BuildSaleBatchWithOneMalformedRecord(terminal, count: 5);

        var response = await fixture.UploadAsync(tenant, batch);

        response.Accepted.ShouldBe(4);
        response.Rejected.Count.ShouldBe(1);

        var movements = await fixture.ReadAsync<InventoryDbContext, int>(tenant, db =>
            db.StockMovements.CountAsync());

        movements.ShouldBe(4);
    }

    private Task<StockBalance?> BalanceAsync(Guid tenantId, Guid terminalId, Guid variantId) =>
        fixture.ReadAsync<InventoryDbContext, StockBalance?>(tenantId, db =>
            db.StockBalances
              .AsNoTracking()
              .FirstOrDefaultAsync(b =>
                  b.WarehouseId == fixture.WarehouseFor(terminalId) && b.VariantId == variantId));
}
