using Microsoft.EntityFrameworkCore;
using POS.Catalog.Domain;
using POS.Catalog.Persistence;
using POS.Common.Errors;
using POS.SharedKernel;

namespace POS.Api.Endpoints;

/// <summary>
/// Catalog reads and writes.
/// </summary>
/// <remarks>
/// Every route here is tenant-scoped by the query filter, not by a WHERE clause in
/// this file. That is the point of applying the filter centrally: a new endpoint is
/// safe because it inherited the filter, not because whoever wrote it remembered.
///
/// The response shapes are declared here rather than returning entities. A domain
/// object serialised straight to the wire couples the API contract to the model and
/// leaks whatever field someone adds next.
/// </remarks>
public static class CatalogEndpoints
{
    private const string DefaultCategorySlug = "general";

    /// <summary>
    /// UN/CEFACT code for a discrete unit. Named rather than inlined because a bare
    /// two-letter literal in a comparison is indistinguishable from an ISO country code
    /// to the fiscal-agnosticism scan — and that rule is worth more than the two saved
    /// characters.
    /// </summary>
    private const string EachUnitCode = "EA";

    private const string StandardTaxCode = "STD";

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/catalog").RequireAuthorization();

        group.MapGet("/products", async (CatalogDbContext db, CancellationToken ct) =>
        {
            var items = await db.Products
                .AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => new ProductSummaryResponse(p.Id, p.Name, p.Variants.Select(v => v.Id).FirstOrDefault()))
                .ToListAsync(ct);

            return Results.Ok(new ProductListResponse(items));
        });

        group.MapGet("/products/{id:guid}", async (Guid id, CatalogDbContext db, CancellationToken ct) =>
        {
            var product = await db.Products
                .AsNoTracking()
                .Include(p => p.Variants)
                .FirstOrDefaultAsync(p => p.Id == id, ct);

            // 404 rather than 403 for a product belonging to another tenant. The query
            // filter makes that automatic — the row is simply not visible — which is
            // also why it cannot leak existence.
            return product is null
                ? Results.NotFound()
                : Results.Ok(new ProductSummaryResponse(
                    product.Id, product.Name, product.Variants.Count > 0 ? product.Variants[0].Id : Guid.Empty));
        });

        group.MapPost("/products", async (
            CreateProductRequest request,
            CatalogDbContext db,
            IClock clock,
            CancellationToken ct) =>
        {
            var reference = await ResolveReferenceDataAsync(db, request.Currency, ct);

            var product = Product.Create(
                request.Name,
                reference.CategoryId,
                brandId: null,
                reference.UnitOfMeasureId,
                reference.TaxGroupId,
                clock.UtcNow);

            // Every product has at least one variant, including a can of beans
            // (ADR 019). Stock, barcodes and prices attach to the variant, so a
            // product created without one could never be sold.
            var variant = product.AddVariant(
                request.Sku,
                request.Name,
                new Money(request.Price, request.Currency),
                clock.UtcNow);

            if (variant.IsFailure)
                return variant.Error.ToHttpResult();

            db.Products.Add(product);
            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/v1/catalog/products/{product.Id}",
                new CreatedProductResponse(product.Id, variant.Value.Id));
        });

        group.MapPut("/products/{id:guid}", async (
            Guid id,
            UpdateProductRequest request,
            CatalogDbContext db,
            IClock clock,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Error.Validation("catalog.product.name_required", "Name is required.").ToHttpResult();

            var product = await db.Products.Include(p => p.Variants).FirstOrDefaultAsync(p => p.Id == id, ct);

            if (product is null)
                return Results.NotFound();

            product.Rename(request.Name, clock.UtcNow);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new ProductSummaryResponse(
                product.Id, product.Name, product.Variants.Count > 0 ? product.Variants[0].Id : Guid.Empty));
        });

        group.MapDelete("/products/{id:guid}", async (
            Guid id,
            CatalogDbContext db,
            CancellationToken ct) =>
        {
            // Variant ids are PROJECTED, not Included, and that is load-bearing rather
            // than a micro-optimisation.
            //
            // Product -> Variants is configured OnDelete(Cascade). If the variants were
            // tracked when the product is marked Deleted, EF cascades the delete to
            // them in memory during DetectChanges — before any interceptor runs — and
            // then fails, because severing a variant from its required Barcode is not
            // something it can reconcile with the barcode merely being soft-deleted.
            //
            // Worse than the exception is what the cascade would MEAN: variants would
            // be hard-deleted while the product was only soft-deleted, orphaning every
            // historical sale line that references them. EF's cascade machinery does
            // not know about soft deletes, so it must not be given the opportunity.
            var variantIds = await db.Products
                .AsNoTracking()
                .Where(p => p.Id == id)
                .SelectMany(p => p.Variants.Select(v => v.Id))
                .ToListAsync(ct);

            var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);

            if (product is null)
                return Results.NotFound();

            // The barcodes go with it, and this is not tidying up.
            //
            // Barcode uniqueness is enforced by an index filtered on [IsDeleted] = 0,
            // precisely so a value can be reused after deletion. Leaving the barcode
            // rows live would keep that index occupied on behalf of a product the
            // merchant can no longer see, and reusing the barcode on a replacement
            // product would fail with a constraint violation they can neither explain
            // nor clear. That reaches support as "the system is broken".
            //
            // Barcodes hang off the variant, not the product, so nothing else cascades
            // this for us.
            var barcodes = await db.Barcodes
                .Where(b => variantIds.Contains(b.VariantId))
                .ToListAsync(ct);

            // Remove() on an ISoftDeletable is reinterpreted by AuditingInterceptor as
            // a soft delete. Historical sale lines reference this product; a hard
            // delete would break every report that joins to it.
            db.Barcodes.RemoveRange(barcodes);
            db.Products.Remove(product);

            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });

        group.MapPost("/products/{id:guid}/barcodes", async (
            Guid id,
            AddBarcodeRequest request,
            CatalogDbContext db,
            CancellationToken ct) =>
        {
            var product = await db.Products
                .Include(p => p.Variants)
                .FirstOrDefaultAsync(p => p.Id == id, ct);

            if (product is null)
                return Results.NotFound();

            var variant = product.Variants.Count > 0 ? product.Variants[0] : null;

            if (variant is null)
                return Error.NotFound("catalog.variant.none", "The product has no variant.").ToHttpResult();

            var barcode = Barcode.Create(variant.Id, request.Value, request.Symbology, isPrimary: true);

            if (barcode.IsFailure)
                return barcode.Error.ToHttpResult();

            db.Barcodes.Add(barcode.Value);
            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/v1/catalog/barcodes/{barcode.Value.Id}",
                new CreatedResource(barcode.Value.Id));
        });

        return app;
    }

    /// <summary>
    /// Finds or creates the category, unit and tax group a product cannot exist without.
    /// </summary>
    /// <remarks>
    /// Product.Create takes all three as required ids, correctly — a product with no
    /// tax treatment cannot be priced. Merchant onboarding would normally supply them;
    /// until that exists, creating them on demand is what makes the endpoint usable
    /// without pretending the ids are optional.
    /// </remarks>
    private static async Task<(Guid CategoryId, Guid UnitOfMeasureId, Guid TaxGroupId)> ResolveReferenceDataAsync(
        CatalogDbContext db,
        string currency,
        CancellationToken ct)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Slug == DefaultCategorySlug, ct);

        if (category is null)
        {
            category = Category.CreateRoot("General", DefaultCategorySlug);
            db.Categories.Add(category);
        }

        var unit = await db.UnitsOfMeasure.FirstOrDefaultAsync(u => u.Code == EachUnitCode, ct);

        if (unit is null)
        {
            unit = UnitOfMeasure.CreateBase(EachUnitCode, "Each", allowsFractions: false);
            db.UnitsOfMeasure.Add(unit);
        }

        var taxGroup = await db.TaxGroups.FirstOrDefaultAsync(t => t.Code == StandardTaxCode, ct);

        if (taxGroup is null)
        {
            taxGroup = TaxGroup.Create("Standard", StandardTaxCode);
            db.TaxGroups.Add(taxGroup);
        }

        await db.SaveChangesAsync(ct);

        _ = currency;
        return (category.Id, unit.Id, taxGroup.Id);
    }
}

public sealed record CreateProductRequest(string Name, string Sku, decimal Price = 1.00m, string Currency = "USD");

public sealed record UpdateProductRequest(string Name);

public sealed record AddBarcodeRequest(string Value, BarcodeSymbology Symbology = BarcodeSymbology.Ean13);

public sealed record CreatedProductResponse(Guid Id, Guid VariantId);

public sealed record ProductSummaryResponse(Guid Id, string Name, Guid VariantId);

public sealed record ProductListResponse(IReadOnlyList<ProductSummaryResponse> Items);
