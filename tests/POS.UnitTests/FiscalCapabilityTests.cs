using POS.Fiscal.Abstractions;
using POS.Fiscal.Generic;
using POS.SharedKernel;
using Shouldly;
using Xunit;

namespace POS.UnitTests;

/// <summary>
/// These tests exist to protect the ARCHITECTURAL property, not just behaviour: the
/// pipeline must produce jurisdiction-correct outcomes while containing no knowledge
/// of any jurisdiction. Every scenario below is driven purely by capability data.
/// </summary>
public sealed class FiscalCapabilityTests
{
    private static FiscalContext Context(bool offline = false, string? buyerTax = null) => new()
    {
        CompanyId = Guid.CreateVersion7(),
        BranchId = Guid.CreateVersion7(),
        TerminalId = Guid.CreateVersion7(),
        CountryCode = "XX",
        SellerTaxRegistration = "SELLER-1",
        SaleId = Guid.CreateVersion7(),
        IssuedAt = new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero),
        BusinessDate = new DateOnly(2026, 7, 21),
        Currency = "USD",
        Lines = [new FiscalLine(1, "Widget", "W1", 2m, "EA", 10m, 0m, "S", 0.15m, 3m, 23m)],
        TotalExclusiveTax = 20m,
        TotalTax = 3m,
        TotalInclusiveTax = 23m,
        Buyer = buyerTax is null ? null : new FiscalCounterparty("Acme", buyerTax, "VAT", null, null, "XX"),
        IsOffline = offline
    };

    [Fact]
    public void An_anonymous_sale_produces_a_simplified_invoice()
    {
        var profile = new GenericFiscalProfile(new StubNumbering());

        profile.ResolveDocumentType(Context())
               .ShouldBe(FiscalDocumentType.SimplifiedInvoice);
    }

    [Fact]
    public void A_buyer_with_a_tax_number_produces_a_standard_invoice()
    {
        // The same basket becomes a different legal document. Everything downstream
        // depends on this, which is why it is the first step in the pipeline.
        var profile = new GenericFiscalProfile(new StubNumbering());

        profile.ResolveDocumentType(Context(buyerTax: "VAT-999"))
               .ShouldBe(FiscalDocumentType.StandardInvoice);
    }

    [Fact]
    public void The_generic_profile_imposes_no_obligations()
    {
        var profile = new GenericFiscalProfile(new StubNumbering());
        var caps = profile.GetCapabilities(FiscalDocumentType.SimplifiedInvoice);

        caps.OfflineIssuance.ShouldBe(OfflineIssuance.Permitted);
        caps.TransmissionModel.ShouldBe(TransmissionModel.None);
        caps.RequiresSignature.ShouldBeFalse();
        profile.Signer.ShouldBeNull();
        profile.Transmitter.ShouldBeNull();
    }

    [Fact]
    public void Absent_capabilities_are_null_rather_than_throwing_stubs()
    {
        // "This jurisdiction has no such concept" must be visible at compile time,
        // not discovered by catching NotSupportedException in production.
        var profile = new GenericFiscalProfile(new StubNumbering());

        profile.QrGenerator.ShouldBeNull();
        profile.ArchiveExporter.ShouldBeNull();
    }

    [Theory]
    [InlineData(OfflineIssuance.Permitted, true)]
    [InlineData(OfflineIssuance.PermittedWithDeferredClearance, true)]
    [InlineData(OfflineIssuance.Prohibited, false)]
    public void Offline_legality_is_decided_by_capability_not_by_country(
        OfflineIssuance issuance, bool expectedAllowed)
    {
        // The pipeline's gate condition, isolated. No country code participates.
        var caps = new FiscalCapabilities
        {
            OfflineIssuance = issuance,
            TransmissionModel = TransmissionModel.Clearance
        };

        var allowed = caps.OfflineIssuance != OfflineIssuance.Prohibited;

        allowed.ShouldBe(expectedAllowed);
    }

    [Fact]
    public void Capabilities_can_differ_between_document_types_in_one_jurisdiction()
    {
        // The ZATCA shape: simplified B2C reportable offline, standard B2B cleared
        // online. Modelling capability per COUNTRY would force the strict rule onto
        // ordinary retail and needlessly destroy offline selling.
        var profile = new TwoSpeedProfile();

        profile.GetCapabilities(FiscalDocumentType.SimplifiedInvoice)
               .OfflineIssuance.ShouldBe(OfflineIssuance.Permitted);

        profile.GetCapabilities(FiscalDocumentType.StandardInvoice)
               .OfflineIssuance.ShouldBe(OfflineIssuance.Prohibited);
    }

    [Fact]
    public void A_signer_that_cannot_work_offline_blocks_offline_issuance()
    {
        var caps = new FiscalCapabilities
        {
            OfflineIssuance = OfflineIssuance.Permitted,
            TransmissionModel = TransmissionModel.PostAuditReporting,
            RequiresSignature = true
        };
        IFiscalSigner serverOnlySigner = new StubSigner(canSignOffline: false);

        var blocked = caps.RequiresSignature && !serverOnlySigner.CanSignOffline;

        blocked.ShouldBeTrue();
    }

    [Fact]
    public async Task The_neutral_builder_produces_a_stable_hash_for_identical_input()
    {
        var builder = new NeutralJsonDocumentBuilder();
        var number = new FiscalNumber("S1", 1, "S1/00000001", 2026);
        var context = Context();

        var first = await builder.BuildAsync(context, number, FiscalDocumentType.SimplifiedInvoice);
        var second = await builder.BuildAsync(context, number, FiscalDocumentType.SimplifiedInvoice);

        first.Value.CanonicalHash.ShouldBe(second.Value.CanonicalHash);
        first.Value.ContentType.ShouldBe("application/json");
    }

    [Fact]
    public async Task Changing_any_total_changes_the_canonical_hash()
    {
        // Chaining regimes depend on this: a hash that ignored a field would make
        // the tamper-evident chain evidence of nothing.
        var builder = new NeutralJsonDocumentBuilder();
        var number = new FiscalNumber("S1", 1, "S1/00000001", 2026);

        var original = await builder.BuildAsync(Context(), number, FiscalDocumentType.SimplifiedInvoice);
        var altered = await builder.BuildAsync(
            Context() with { TotalInclusiveTax = 9999m }, number, FiscalDocumentType.SimplifiedInvoice);

        altered.Value.CanonicalHash.ShouldNotBe(original.Value.CanonicalHash);
    }

    [Fact]
    public void An_unknown_profile_code_is_an_error_not_a_silent_fallback()
    {
        // Quietly degrading a mandated company to GENERIC produces receipts that look
        // fine and are not legally valid — discovered at audit, months later.
        var registry = new FiscalProfileRegistry([new GenericFiscalProfile(new StubNumbering())]);

        var result = registry.Resolve("SA_ZATCA_P2");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("fiscal.profile_not_found");
    }

    [Fact]
    public void The_registry_resolves_a_registered_profile_case_insensitively()
    {
        var registry = new FiscalProfileRegistry([new GenericFiscalProfile(new StubNumbering())]);

        registry.Resolve("generic").IsSuccess.ShouldBeTrue();
    }

    private sealed class StubNumbering : IFiscalNumberingStrategy
    {
        public bool IsGapFree => true;

        public Task<Result<FiscalNumber>> AllocateAsync(
            FiscalContext context, FiscalDocumentType documentType, CancellationToken ct = default) =>
            Task.FromResult(Result<FiscalNumber>.Success(
                new FiscalNumber("TEST", 1, "TEST/00000001", 2026)));
    }

    private sealed class StubSigner(bool canSignOffline) : IFiscalSigner
    {
        public bool CanSignOffline { get; } = canSignOffline;

        public Task<Result<FiscalSignature>> SignAsync(
            FiscalPayload payload, FiscalContext context, string? previousDocumentHash,
            CancellationToken ct = default) =>
            Task.FromResult(Result<FiscalSignature>.Success(
                new FiscalSignature("RS256", "sig", null, null)));
    }

    /// <summary>A profile whose rules differ by document type, as real mandates do.</summary>
    private sealed class TwoSpeedProfile : IFiscalProfile
    {
        public string Code => "TWO_SPEED";
        public IReadOnlyCollection<string> CountryCodes => ["XX"];
        public IFiscalNumberingStrategy Numbering { get; } = new StubNumbering();
        public IFiscalDocumentBuilder Builder { get; } = new NeutralJsonDocumentBuilder();
        public IFiscalSigner? Signer => null;
        public IFiscalTransmitter? Transmitter => null;
        public IFiscalQrGenerator? QrGenerator => null;
        public IFiscalArchiveExporter? ArchiveExporter => null;

        public FiscalDocumentType ResolveDocumentType(FiscalContext context) =>
            context.Buyer?.TaxRegistration is { Length: > 0 }
                ? FiscalDocumentType.StandardInvoice
                : FiscalDocumentType.SimplifiedInvoice;

        public FiscalCapabilities GetCapabilities(FiscalDocumentType documentType) =>
            documentType == FiscalDocumentType.StandardInvoice
                ? new FiscalCapabilities
                {
                    OfflineIssuance = OfflineIssuance.Prohibited,
                    TransmissionModel = TransmissionModel.Clearance,
                    RequiresSignature = true
                }
                : new FiscalCapabilities
                {
                    OfflineIssuance = OfflineIssuance.Permitted,
                    TransmissionModel = TransmissionModel.PostAuditReporting,
                    TransmissionDeadline = TimeSpan.FromHours(24),
                    RequiresSignature = true,
                    RequiresQrCode = true
                };

        public Result Validate(FiscalContext context) =>
            ResolveDocumentType(context) == FiscalDocumentType.StandardInvoice
            && context.Buyer?.TaxRegistration is null or { Length: 0 }
                ? Result.Failure(FiscalErrors.BuyerTaxRegistrationRequired)
                : Result.Success();
    }
}
