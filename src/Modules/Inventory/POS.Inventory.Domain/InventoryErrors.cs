using POS.SharedKernel;

namespace POS.Inventory.Domain;

/// <summary>
/// Expected failures in the Inventory module, returned as <see cref="Result"/> rather
/// than thrown. Exceptions are reserved for faults — see ADR 001 (P0-4).
/// </summary>
public static class InventoryErrors
{
    public static readonly Error InsufficientStock = Error.Conflict(
        "inventory.insufficient_stock",
        "The movement would drive stock below zero and the warehouse policy forbids it.");

    public static readonly Error ReasonCodeRequired = Error.Validation(
        "inventory.reason_code_required",
        "This movement type requires a reason code.");

    public static readonly Error UnknownVariant = Error.NotFound(
        "inventory.unknown_variant",
        "The variant does not exist or is not stocked.");

    public static readonly Error NotStocked = Error.Validation(
        "inventory.not_stocked",
        "Stock cannot be recorded against a service or non-stocked product.");

    public static readonly Error TransferNotDraft = Error.Conflict(
        "inventory.transfer_not_draft",
        "The transfer is no longer a draft.");

    public static readonly Error TransferNotInTransit = Error.Conflict(
        "inventory.transfer_not_in_transit",
        "Only a dispatched transfer can be received.");

    public static readonly Error TransferHasNoLines = Error.Validation(
        "inventory.transfer_has_no_lines",
        "A transfer must have at least one line.");

    public static readonly Error TransferHasNoVariance = Error.Conflict(
        "inventory.transfer_has_no_variance",
        "There is no outstanding variance to write off.");

    public static readonly Error TransferNotFound = Error.NotFound(
        "inventory.transfer_not_found",
        "The transfer does not exist.");

    public static readonly Error SelfApprovalForbidden = Error.Forbidden(
        "inventory.self_approval_forbidden",
        "A transfer's variance must be written off by someone other than the person who received it.");

    public static readonly Error ApprovalLevelInsufficient = Error.Forbidden(
        "inventory.approval_level_insufficient",
        "This variance's value requires write-off approval at a more senior level.");

    public static readonly Error StocktakeNotFound = Error.NotFound(
        "inventory.stocktake_not_found",
        "The stocktake does not exist.");

    public static readonly Error StocktakeNotCounting = Error.Conflict(
        "inventory.stocktake_not_counting",
        "The stocktake is not open for counting.");

    public static readonly Error StocktakeNotPendingReview = Error.Conflict(
        "inventory.stocktake_not_pending_review",
        "The stocktake must be reviewed before it can be posted.");

    public static readonly Error StocktakeHasNoLines = Error.Validation(
        "inventory.stocktake_has_no_lines",
        "A stocktake must have at least one counted line.");

    public static readonly Error StocktakeCannotBeCancelled = Error.Conflict(
        "inventory.stocktake_cannot_be_cancelled",
        "A posted stocktake cannot be cancelled; its adjustments are already in the ledger.");

    public static readonly Error CancellationReasonRequired = Error.Validation(
        "inventory.cancellation_reason_required",
        "Cancelling requires a reason.");

    public static readonly Error NegativeCount = Error.Validation(
        "inventory.negative_count",
        "A counted quantity cannot be negative.");

    public static readonly Error BalanceLedgerDivergence = Error.BusinessRule(
        "inventory.balance_ledger_divergence",
        "The materialised balance does not agree with the ledger.");

    public static readonly Error BalanceRowMissing = Error.Conflict(
        "inventory.balance_row_missing",
        "The materialised balance row disappeared while a movement was being applied.");
}
