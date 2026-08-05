using POS.Catalog.Domain;
using Shouldly;

namespace POS.UnitTests;

public sealed class BarcodeTests
{
    [Theory]
    [InlineData("5000112637922")]   // real-world EAN-13
    [InlineData("4006381333931")]
    public void Accepts_valid_ean13(string value)
    {
        Barcode.Create(Guid.NewGuid(), value, BarcodeSymbology.Ean13, isPrimary: true)
               .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Rejects_ean13_with_a_bad_check_digit()
    {
        // Catching this at entry is far cheaper than at the till, where it presents
        // as "the scanner does not work" and generates a support call.
        var result = Barcode.Create(Guid.NewGuid(), "5000112637921", BarcodeSymbology.Ean13, true);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("catalog.barcode.invalid");
    }

    [Fact]
    public void Computes_the_check_digit_for_generated_internal_codes()
    {
        BarcodeValidator.ComputeCheckDigit("500011263792").ShouldBe("5000112637922");
    }
}

public sealed class Gs1ParserTests
{
    [Fact]
    public void Extracts_embedded_weight_from_a_deli_label()
    {
        // AI 01 = GTIN, AI 3102 = net weight in kg to 2 decimal places.
        // "001250" therefore means 12.50 kg.
        //
        // This is why a scan cannot return just an identifier: the till must
        // resolve it to (SKU, quantity). Designing for it after launch changes the
        // signature of every scan path in the system.
        var data = Gs1Parser.Parse("01050011263792203102001250");

        data.Gtin.ShouldBe("05001126379220");
        data.NetWeightKg.ShouldBe(12.50m);
        data.HasEmbeddedQuantity.ShouldBeTrue();
    }

    [Fact]
    public void Extracts_expiry_date()
    {
        var data = Gs1Parser.Parse("0105001126379220" + "17" + "260315");

        data.ExpiryDate.ShouldBe(new DateOnly(2026, 3, 15));
    }

    [Fact]
    public void Treats_day_zero_as_end_of_month_per_gs1()
    {
        var data = Gs1Parser.Parse("0105001126379220" + "17" + "260200");

        data.ExpiryDate.ShouldBe(new DateOnly(2026, 2, 28));
    }

    [Fact]
    public void Plain_ean13_yields_no_embedded_quantity()
    {
        Gs1Parser.Parse("5000112637922").HasEmbeddedQuantity.ShouldBeFalse();
    }
}
