using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using POS.Catalog.Persistence;
using POS.Sync.Contracts;
using POS.Sync.Pull;

namespace POS.Catalog.Sync;

/// <inheritdoc cref="IMasterDataSource"/>
/// <remarks>
/// Every active variant, its owning product's tax group, and every barcode pointing
/// at it — the minimum a terminal needs to ring up a sale offline: look an item up
/// by scan or SKU, price it, and tax it correctly. See <see cref="MasterDataPullService"/>
/// for why this is a full snapshot rather than a true incremental delta.
/// </remarks>
public sealed class ProductMasterDataSource(CatalogDbContext db) : IMasterDataSource
{
    public string EntityType => "Product";

    public async Task<IReadOnlyList<MasterDataChange>> GetFullSnapshotAsync(
        Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await db.Variants
            .AsNoTracking()
            .Where(v => v.IsActive)
            .Join(
                db.Products.Where(p => p.IsActive),
                variant => variant.ProductId,
                product => product.Id,
                (variant, product) => new { variant, product })
            .ToListAsync(cancellationToken);

        var variantIds = rows.Select(r => r.variant.Id).ToList();

        var barcodesByVariant = await db.Barcodes
            .AsNoTracking()
            .Where(b => variantIds.Contains(b.VariantId))
            .GroupBy(b => b.VariantId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(b => b.Value).ToList(), cancellationToken);

        return rows.Select(r => new MasterDataChange(
            EntityType,
            r.variant.Id,
            0,
            ChangeOperation.Upsert,
            JsonSerializer.Serialize(new ProductMasterDataPayload(
                r.product.Id,
                r.variant.Id,
                r.variant.Sku,
                r.variant.Name,
                r.variant.DefaultPriceAmount,
                r.variant.DefaultPriceCurrency,
                r.product.TaxGroupId,
                barcodesByVariant.TryGetValue(r.variant.Id, out var codes) ? codes : []))))
            .ToList();
    }
}

/// <summary>The shape a terminal deserialises from each "Product" <see cref="MasterDataChange.Payload"/>.</summary>
public sealed record ProductMasterDataPayload(
    Guid ProductId,
    Guid VariantId,
    string Sku,
    string Name,
    decimal Price,
    string Currency,
    Guid TaxGroupId,
    IReadOnlyList<string> Barcodes);
