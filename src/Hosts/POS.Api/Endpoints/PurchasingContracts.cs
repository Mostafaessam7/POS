using FluentValidation;
using POS.Purchasing.Domain;

namespace POS.Api.Endpoints;

// Request and response shapes for the Purchasing API, with their validators beside
// them. Kept together deliberately: a contract and the rules that make it valid are one
// thing, and separating them is how a new field acquires no validation.
//
// DOCUMENT NUMBERS ARE SUPPLIED BY THE CALLER, not generated here. Purchase orders,
// receipts, invoices and returns are business documents whose numbering usually follows
// the merchant's own scheme and often has to match a paper pad or an ERP feed. The
// uniqueness guarantee is the database index (UX_PurchaseOrders_Number and friends), so
// a collision is a 409 rather than a silent overwrite. The alternative — a server-side
// gap-free allocator per document type — is what the fiscal module needs and this one
// does not: a gap in a purchase order series is an inconvenience, not a compliance
// finding.

public sealed record CreateSupplierRequest(
    Guid CompanyId,
    string Code,
    string Name,
    string Currency,
    int PaymentTermDays = 30,
    int LeadTimeDays = 7,
    decimal MinimumOrderValue = 0m,
    string? TaxRegistrationNumber = null);

public sealed class CreateSupplierRequestValidator : AbstractValidator<CreateSupplierRequest>
{
    public CreateSupplierRequestValidator()
    {
        RuleFor(r => r.CompanyId).NotEmpty();
        RuleFor(r => r.Code).NotEmpty().MaximumLength(30);
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200);
        RuleFor(r => r.Currency).NotEmpty().Length(3);
        RuleFor(r => r.PaymentTermDays).InclusiveBetween(0, 365);
        RuleFor(r => r.LeadTimeDays).InclusiveBetween(0, 365);
        RuleFor(r => r.MinimumOrderValue).GreaterThanOrEqualTo(0m);
        RuleFor(r => r.TaxRegistrationNumber).MaximumLength(50);
    }
}

public sealed record AddSupplierProductCodeRequest(
    Guid VariantId,
    string Code,
    decimal PackSize,
    string? Description = null);

public sealed class AddSupplierProductCodeRequestValidator : AbstractValidator<AddSupplierProductCodeRequest>
{
    public AddSupplierProductCodeRequestValidator()
    {
        RuleFor(r => r.VariantId).NotEmpty();
        RuleFor(r => r.Code).NotEmpty().MaximumLength(60);
        RuleFor(r => r.PackSize).GreaterThan(0m);
        RuleFor(r => r.Description).MaximumLength(200);
    }
}

public sealed record RaisePurchaseOrderRequest(
    Guid SupplierId,
    Guid CompanyId,
    Guid BranchId,
    Guid WarehouseId,
    string OrderNumber,
    DateOnly BusinessDate,
    DateOnly ExpectedDeliveryDate,
    IReadOnlyList<PurchaseOrderLineRequest> Lines);

public sealed record PurchaseOrderLineRequest(
    Guid VariantId,
    decimal Quantity,
    decimal UnitPrice,
    string? SupplierCode = null,
    string? Description = null);

public sealed class RaisePurchaseOrderRequestValidator : AbstractValidator<RaisePurchaseOrderRequest>
{
    public RaisePurchaseOrderRequestValidator()
    {
        RuleFor(r => r.SupplierId).NotEmpty();
        RuleFor(r => r.CompanyId).NotEmpty();
        RuleFor(r => r.BranchId).NotEmpty();
        RuleFor(r => r.WarehouseId).NotEmpty();
        RuleFor(r => r.OrderNumber).NotEmpty().MaximumLength(30);

        // An order with no lines is not a draft, it is a mistake — and the aggregate
        // refuses to submit it later, which is a worse place to find out.
        RuleFor(r => r.Lines).NotEmpty();

        RuleForEach(r => r.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.VariantId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0m);

            // Zero is legitimate — free-of-charge replacement stock still has to be
            // received and costed — but negative is always an error.
            line.RuleFor(l => l.UnitPrice).GreaterThanOrEqualTo(0m);

            line.RuleFor(l => l.SupplierCode).MaximumLength(60);
            line.RuleFor(l => l.Description).MaximumLength(200);
        });
    }
}

public sealed record CancelRequest(string Reason);

public sealed class CancelRequestValidator : AbstractValidator<CancelRequest>
{
    public CancelRequestValidator()
    {
        // A cancellation with no reason is unauditable, and "why was this cancelled?"
        // is the first question asked when a supplier chases an order.
        RuleFor(r => r.Reason).NotEmpty().MaximumLength(500);
    }
}

public sealed record CreateGoodsReceiptRequest(
    Guid PurchaseOrderId,
    string ReceiptNumber,
    DateOnly BusinessDate,
    string? SupplierDeliveryNote,
    IReadOnlyList<GoodsReceiptLineRequest> Lines,
    IReadOnlyList<LandedCostRequest>? LandedCosts = null);

public sealed record GoodsReceiptLineRequest(
    int PurchaseOrderLineNumber,
    Guid VariantId,
    decimal QuantityReceived,
    decimal UnitPrice);

public sealed record LandedCostRequest(
    LandedCostType Type,
    decimal Amount,
    string Reference,
    LandedCostAllocationBasis Basis);

public sealed class CreateGoodsReceiptRequestValidator : AbstractValidator<CreateGoodsReceiptRequest>
{
    public CreateGoodsReceiptRequestValidator()
    {
        RuleFor(r => r.PurchaseOrderId).NotEmpty();
        RuleFor(r => r.ReceiptNumber).NotEmpty().MaximumLength(30);
        RuleFor(r => r.SupplierDeliveryNote).MaximumLength(60);
        RuleFor(r => r.Lines).NotEmpty();

        RuleForEach(r => r.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.PurchaseOrderLineNumber).GreaterThan(0);
            line.RuleFor(l => l.VariantId).NotEmpty();
            line.RuleFor(l => l.QuantityReceived).GreaterThan(0m);
            line.RuleFor(l => l.UnitPrice).GreaterThanOrEqualTo(0m);
        });

        RuleForEach(r => r.LandedCosts!).ChildRules(cost =>
        {
            // Zero-value freight is not a charge, it is noise on the landed cost
            // allocation; the aggregate rejects it and so does this.
            cost.RuleFor(c => c.Amount).GreaterThan(0m);
            cost.RuleFor(c => c.Reference).NotEmpty().MaximumLength(60);
        })
        .When(r => r.LandedCosts is not null);
    }
}

public sealed record RecordPurchaseInvoiceRequest(
    Guid SupplierId,
    Guid CompanyId,
    Guid PurchaseOrderId,
    string SupplierInvoiceNumber,
    string Currency,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    IReadOnlyList<PurchaseInvoiceLineRequest> Lines);

public sealed record PurchaseInvoiceLineRequest(
    int PurchaseOrderLineNumber,
    Guid VariantId,
    decimal Quantity,
    decimal UnitPrice);

public sealed class RecordPurchaseInvoiceRequestValidator : AbstractValidator<RecordPurchaseInvoiceRequest>
{
    public RecordPurchaseInvoiceRequestValidator()
    {
        RuleFor(r => r.SupplierId).NotEmpty();
        RuleFor(r => r.CompanyId).NotEmpty();
        RuleFor(r => r.PurchaseOrderId).NotEmpty();
        RuleFor(r => r.SupplierInvoiceNumber).NotEmpty().MaximumLength(60);
        RuleFor(r => r.Currency).NotEmpty().Length(3);
        RuleFor(r => r.Lines).NotEmpty();

        // A due date before the invoice date makes every ageing report wrong and is
        // always a transcription error.
        RuleFor(r => r.DueDate).GreaterThanOrEqualTo(r => r.InvoiceDate);

        RuleForEach(r => r.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.PurchaseOrderLineNumber).GreaterThan(0);
            line.RuleFor(l => l.VariantId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0m);
            line.RuleFor(l => l.UnitPrice).GreaterThanOrEqualTo(0m);
        });
    }
}

public sealed record OverrideInvoiceBlockRequest(string Reason);

public sealed class OverrideInvoiceBlockRequestValidator : AbstractValidator<OverrideInvoiceBlockRequest>
{
    public OverrideInvoiceBlockRequestValidator() =>
        // Releasing a blocked invoice moves real money against a match the system
        // refused. The reason is the audit trail and is not optional.
        RuleFor(r => r.Reason).NotEmpty().MaximumLength(500);
}

public sealed record CreateSupplierReturnRequest(
    Guid SupplierId,
    Guid BranchId,
    Guid WarehouseId,
    string ReturnNumber,
    string Currency,
    SupplierReturnReason Reason,
    DateOnly BusinessDate,
    IReadOnlyList<SupplierReturnLineRequest> Lines,
    Guid? OriginalGoodsReceiptId = null);

public sealed record SupplierReturnLineRequest(Guid VariantId, decimal Quantity, decimal UnitCost);

public sealed class CreateSupplierReturnRequestValidator : AbstractValidator<CreateSupplierReturnRequest>
{
    public CreateSupplierReturnRequestValidator()
    {
        RuleFor(r => r.SupplierId).NotEmpty();
        RuleFor(r => r.BranchId).NotEmpty();
        RuleFor(r => r.WarehouseId).NotEmpty();
        RuleFor(r => r.ReturnNumber).NotEmpty().MaximumLength(30);
        RuleFor(r => r.Currency).NotEmpty().Length(3);
        RuleFor(r => r.Reason).IsInEnum();
        RuleFor(r => r.Lines).NotEmpty();

        RuleForEach(r => r.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.VariantId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0m);
            line.RuleFor(l => l.UnitCost).GreaterThanOrEqualTo(0m);
        });
    }
}

public sealed record RecordCreditNoteRequest(string CreditNoteNumber, decimal Amount, DateOnly CreditNoteDate);

public sealed class RecordCreditNoteRequestValidator : AbstractValidator<RecordCreditNoteRequest>
{
    public RecordCreditNoteRequestValidator()
    {
        RuleFor(r => r.CreditNoteNumber).NotEmpty().MaximumLength(60);
        RuleFor(r => r.Amount).GreaterThan(0m);
    }
}

// ---------------------------------------------------------------------------
// Responses. Declared rather than returning aggregates: serialising a domain
// object straight to the wire couples the API contract to the model and leaks
// whatever field somebody adds next.
// ---------------------------------------------------------------------------

public sealed record SupplierResponse(
    Guid Id,
    string Code,
    string Name,
    string Currency,
    bool IsActive,
    int PaymentTermDays,
    int LeadTimeDays);

public sealed record PurchaseOrderResponse(
    Guid Id,
    string OrderNumber,
    Guid SupplierId,
    string Currency,
    PurchaseOrderStatus Status,
    decimal TotalValue,
    DateOnly ExpectedDeliveryDate,
    IReadOnlyList<PurchaseOrderLineResponse> Lines);

public sealed record PurchaseOrderLineResponse(
    int LineNumber,
    Guid VariantId,
    decimal QuantityOrdered,
    decimal QuantityReceived,
    decimal OutstandingQuantity,
    decimal UnitPrice);

public sealed record GoodsReceiptResponse(
    Guid Id,
    string ReceiptNumber,
    Guid PurchaseOrderId,
    GoodsReceiptStatus Status,
    decimal GoodsValue,
    decimal LandedCostTotal);

public sealed record PurchaseInvoiceResponse(
    Guid Id,
    string SupplierInvoiceNumber,
    Guid PurchaseOrderId,
    PurchaseInvoiceStatus Status,
    decimal NetTotal,
    string? BlockReason);

public sealed record ThreeWayMatchResponse(
    MatchOutcome Outcome,
    bool IsPayable,
    IReadOnlyList<MatchVarianceResponse> Variances);

public sealed record MatchVarianceResponse(
    int PurchaseOrderLineNumber,
    MatchVarianceType Type,
    decimal Billed,
    decimal Expected,
    string Description);

public sealed record SupplierReturnResponse(
    Guid Id,
    string ReturnNumber,
    Guid SupplierId,
    SupplierReturnStatus Status,
    decimal ExpectedCredit,
    decimal? CreditedAmount,
    string? CreditNoteNumber);
