using POS.SharedKernel;

namespace POS.Purchasing.Domain;

/// <summary>
/// Every way purchasing can refuse, in one place.
/// </summary>
/// <remarks>
/// Codes are stable and machine-readable; messages are for humans and are expected to be
/// localised at the edge. The same convention as <c>PaymentErrors</c> and
/// <c>InventoryErrors</c> — a caller should never have to match on message text.
///
/// The audience differs from the payments catalogue in a way worth noting: these are read
/// by buyers and storemen at a desk, not by a cashier with a queue. They can afford to be
/// specific about which line and which document, and being vague to save space would just
/// mean a phone call.
/// </remarks>
public static class PurchasingErrors
{
    // ---- Supplier ----

    public static readonly Error SupplierCodeRequired = Error.Validation(
        "purchasing.supplier_code_required",
        "A supplier product code is required.");

    public static readonly Error PackSizeMustBePositive = Error.Validation(
        "purchasing.pack_size_must_be_positive",
        "Pack size must be greater than zero.");

    public static readonly Error DuplicateSupplierProductCode = Error.Conflict(
        "purchasing.duplicate_supplier_product_code",
        "This supplier already has a code registered for that product.");

    // ---- Purchase order ----

    public static readonly Error OrderNotEditable = Error.Conflict(
        "purchasing.order_not_editable",
        "This purchase order can no longer be changed. Only draft and rejected orders may be edited.");

    public static readonly Error OrderHasNoLines = Error.Validation(
        "purchasing.order_has_no_lines",
        "A purchase order must have at least one line before it can be submitted.");

    public static readonly Error OrderQuantityMustBePositive = Error.Validation(
        "purchasing.order_quantity_must_be_positive",
        "Order quantity must be greater than zero.");

    public static readonly Error PriceCannotBeNegative = Error.Validation(
        "purchasing.price_cannot_be_negative",
        "Price cannot be negative.");

    public static readonly Error CurrencyMismatch = Error.Validation(
        "purchasing.currency_mismatch",
        "The currency does not match the document currency.");

    public static readonly Error DuplicateOrderLine = Error.Conflict(
        "purchasing.duplicate_order_line",
        "That product is already on this order. Change the existing line instead of adding a second one.");

    public static readonly Error OrderLineNotFound = Error.NotFound(
        "purchasing.order_line_not_found",
        "That line is not on the purchase order.");

    public static readonly Error OrderNotAwaitingApproval = Error.Conflict(
        "purchasing.order_not_awaiting_approval",
        "This order is not awaiting approval.");

    public static readonly Error SelfApprovalForbidden = Error.Forbidden(
        "purchasing.self_approval_forbidden",
        "A purchase order must be approved by someone other than the person who raised it.");

    public static readonly Error ApprovalLevelInsufficient = Error.Forbidden(
        "purchasing.approval_level_insufficient",
        "This order's value requires approval at a more senior level.");

    public static readonly Error DuplicateApproval = Error.Conflict(
        "purchasing.duplicate_approval",
        "This user has already approved the order.");

    public static readonly Error RejectionReasonRequired = Error.Validation(
        "purchasing.rejection_reason_required",
        "A reason is required when rejecting an order.");

    public static readonly Error OrderNotApproved = Error.Conflict(
        "purchasing.order_not_approved",
        "An order must be approved before it can be sent to the supplier.");

    public static readonly Error OrderNotReceivable = Error.Conflict(
        "purchasing.order_not_receivable",
        "Goods can only be received against an order that has been sent to the supplier.");

    public static readonly Error OrderNotFullyReceived = Error.Conflict(
        "purchasing.order_not_fully_received",
        "This order still has outstanding quantities. Receive or cancel them before closing it.");

    public static readonly Error CancellationReasonRequired = Error.Validation(
        "purchasing.cancellation_reason_required",
        "A reason is required when cancelling.");

    public static readonly Error CannotCancelPartiallyReceivedOrder = Error.Conflict(
        "purchasing.cannot_cancel_partially_received_order",
        "Goods have already been received against this order. Cancel the outstanding lines instead.");

    // ---- Goods receipt ----

    public static readonly Error ReceiptAlreadyPosted = Error.Conflict(
        "purchasing.receipt_already_posted",
        "This goods receipt has been posted and can no longer be changed.");

    public static readonly Error ReceiptHasNoLines = Error.Validation(
        "purchasing.receipt_has_no_lines",
        "A goods receipt must have at least one line.");

    public static readonly Error ReceiptQuantityMustBePositive = Error.Validation(
        "purchasing.receipt_quantity_must_be_positive",
        "Received quantity must be greater than zero.");

    public static readonly Error ReceiptOrderMismatch = Error.Validation(
        "purchasing.receipt_order_mismatch",
        "This receipt belongs to a different purchase order.");

    public static readonly Error ReceiptLineVariantMismatch = Error.Validation(
        "purchasing.receipt_line_variant_mismatch",
        "The product received does not match the product on that order line.");

    public static readonly Error OverReceiptExceedsTolerance = Error.BusinessRule(
        "purchasing.over_receipt_exceeds_tolerance",
        "More has been delivered than was ordered, by more than the agreed tolerance.");

    public static readonly Error LandedCostMustBePositive = Error.Validation(
        "purchasing.landed_cost_must_be_positive",
        "A landed cost must be a positive amount.");

    // ---- Supplier return ----

    public static readonly Error ReturnNotEditable = Error.Conflict(
        "purchasing.return_not_editable",
        "This return has already been dispatched and can no longer be changed.");

    public static readonly Error ReturnHasNoLines = Error.Validation(
        "purchasing.return_has_no_lines",
        "A supplier return must have at least one line.");

    public static readonly Error ReturnQuantityMustBePositive = Error.Validation(
        "purchasing.return_quantity_must_be_positive",
        "Return quantity must be greater than zero.");

    public static readonly Error ReturnNotAwaitingCredit = Error.Conflict(
        "purchasing.return_not_awaiting_credit",
        "A credit note can only be recorded against a return that has been dispatched.");

    public static readonly Error CreditNoteNumberRequired = Error.Validation(
        "purchasing.credit_note_number_required",
        "The supplier's credit note number is required.");

    public static readonly Error CreditCannotBeNegative = Error.Validation(
        "purchasing.credit_cannot_be_negative",
        "A credit note amount cannot be negative.");

    // ---- Purchase invoice ----

    public static readonly Error InvoiceNotEditable = Error.Conflict(
        "purchasing.invoice_not_editable",
        "An approved or paid invoice cannot be changed.");

    public static readonly Error InvoiceQuantityMustBePositive = Error.Validation(
        "purchasing.invoice_quantity_must_be_positive",
        "Invoiced quantity must be greater than zero.");

    public static readonly Error InvoiceNotMatched = Error.Conflict(
        "purchasing.invoice_not_matched",
        "Only a matched invoice can be approved for payment.");

    public static readonly Error InvoiceNotBlocked = Error.Conflict(
        "purchasing.invoice_not_blocked",
        "This invoice is not blocked, so there is nothing to override.");

    public static readonly Error OverrideReasonRequired = Error.Validation(
        "purchasing.override_reason_required",
        "A reason is required when overriding a blocked invoice.");

    public static readonly Error InvoiceNotApproved = Error.Conflict(
        "purchasing.invoice_not_approved",
        "An invoice must be approved before it can be marked as paid.");

    // ---- Document lookup ----
    // Returned by the posting services, which resolve documents by id rather than
    // receiving them already loaded. A missing document is genuinely NotFound and not a
    // business-rule failure, so it maps to 404 rather than 422.

    public static readonly Error ReceiptNotFound = Error.NotFound(
        "purchasing.receipt_not_found",
        "The goods receipt does not exist.");

    public static readonly Error OrderNotFound = Error.NotFound(
        "purchasing.order_not_found",
        "The purchase order does not exist.");

    public static readonly Error ReturnNotFound = Error.NotFound(
        "purchasing.return_not_found",
        "The supplier return does not exist.");
}
