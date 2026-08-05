using POS.SharedKernel;

namespace POS.Catalog.Domain;

/// <summary>
/// A scannable code. A first-class entity, not a column on the variant.
/// </summary>
/// <remarks>
/// One variant routinely has SEVERAL barcodes: the manufacturer's EAN, a case code,
/// a supplier-specific code, and an internally-generated one for unlabelled goods.
/// Modelling it as a single column forces merchants to pick one, and they will
/// instead paste four codes into the field separated by commas.
///
/// Uniqueness is scoped to the TENANT, not global — different suppliers reuse
/// codes, and merchants invent their own. The index must additionally be FILTERED
/// on IsDeleted = 0, or a barcode can never be reused after a product is removed.
/// See ADR 021.
/// </remarks>
public sealed class Barcode : Entity<Guid>, ITenantScoped, ISoftDeletable
{
    private Barcode() { }

    public static Result<Barcode> Create(Guid variantId, string value, BarcodeSymbology symbology, bool isPrimary)
    {
        var normalised = value.Trim();

        if (string.IsNullOrEmpty(normalised))
            return Error.Validation("catalog.barcode.empty", "Barcode value is required.");

        if (!BarcodeValidator.IsValid(normalised, symbology))
            return Error.Validation(
                "catalog.barcode.invalid",
                $"'{normalised}' is not a valid {symbology} barcode (check digit failed).");

        return new Barcode
        {
            Id = SequentialId.New(),
            VariantId = variantId,
            Value = normalised,
            Symbology = symbology,
            IsPrimary = isPrimary
        };
    }

    public Guid TenantId { get; private set; }
    public Guid VariantId { get; private set; }
    public string Value { get; private set; } = null!;
    public BarcodeSymbology Symbology { get; private set; }

    /// <summary>The code printed on shelf labels and receipts. Exactly one per variant.</summary>
    public bool IsPrimary { get; private set; }

    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }
}

public enum BarcodeSymbology
{
    Ean13 = 0,
    Ean8 = 1,
    UpcA = 2,
    UpcE = 3,
    Code128 = 4,

    /// <summary>Carries application identifiers — embedded weight, price, batch, expiry.</summary>
    Gs1128 = 5,

    /// <summary>Merchant-generated for unlabelled goods.</summary>
    Internal = 6
}

/// <summary>Check-digit validation. Rejecting bad codes at entry is far cheaper than at the till.</summary>
public static class BarcodeValidator
{
    public static bool IsValid(string value, BarcodeSymbology symbology) => symbology switch
    {
        BarcodeSymbology.Ean13 => value.Length == 13 && IsNumeric(value) && HasValidGs1CheckDigit(value),
        BarcodeSymbology.Ean8  => value.Length == 8  && IsNumeric(value) && HasValidGs1CheckDigit(value),
        BarcodeSymbology.UpcA  => value.Length == 12 && IsNumeric(value) && HasValidGs1CheckDigit(value),
        BarcodeSymbology.UpcE  => value.Length == 8  && IsNumeric(value),
        _ => value.Length is > 0 and <= 48
    };

    /// <summary>
    /// GS1 modulo-10: weight digits 3 and 1 alternately from the right, excluding
    /// the check digit itself.
    /// </summary>
    private static bool HasValidGs1CheckDigit(string value)
    {
        var sum = 0;
        var weight = 3;

        for (var i = value.Length - 2; i >= 0; i--)
        {
            sum += (value[i] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }

        var expected = (10 - sum % 10) % 10;
        return expected == value[^1] - '0';
    }

    public static string ComputeCheckDigit(string withoutCheckDigit)
    {
        var sum = 0;
        var weight = 3;

        for (var i = withoutCheckDigit.Length - 1; i >= 0; i--)
        {
            sum += (withoutCheckDigit[i] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }

        return withoutCheckDigit + (10 - sum % 10) % 10;
    }

    private static bool IsNumeric(string value) => value.All(char.IsAsciiDigit);
}
