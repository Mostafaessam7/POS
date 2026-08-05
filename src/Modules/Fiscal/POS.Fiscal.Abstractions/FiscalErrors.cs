using POS.SharedKernel;

namespace POS.Fiscal.Abstractions;

/// <summary>The module's error catalogue.</summary>
/// <remarks>
/// Lives in Abstractions rather than beside the pipeline so a country plugin can
/// return these without depending on the orchestrator. Plugins depend on contracts
/// only; nothing depends on the pipeline except the composition host.
/// </remarks>
public static class FiscalErrors
{
    public static Error ProfileNotFound(string code) => Error.NotFound(
        "fiscal.profile_not_found",
        $"No fiscal profile is registered for code '{code}'. Refusing to fall back to " +
        "GENERIC, which would produce documents that are not legally valid.");

    public static Error OfflineIssuanceProhibited(FiscalDocumentType type) => Error.BusinessRule(
        "fiscal.offline_prohibited",
        $"A {type} requires clearance by the tax authority before it can be issued, " +
        "and this terminal is offline. Issue a simplified receipt, or retry when connectivity returns.");

    public static Error OfflineSigningUnavailable => Error.BusinessRule(
        "fiscal.offline_signing_unavailable",
        "This document must be signed, and the configured signer cannot operate offline.");

    public static Error ClearanceRejected(IReadOnlyList<FiscalAuthorityMessage> messages) =>
        Error.BusinessRule(
            "fiscal.clearance_rejected",
            "The tax authority rejected this document: " +
            string.Join("; ", messages.Where(m => m.Severity == FiscalMessageSeverity.Error)
                                      .Select(m => $"[{m.Code}] {m.Text}")));

    public static Error BuyerTaxRegistrationRequired => Error.Validation(
        "fiscal.buyer_tax_registration_required",
        "A standard tax invoice requires the buyer's tax registration number.");
}
