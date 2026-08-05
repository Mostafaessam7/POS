using POS.SharedKernel;

namespace POS.Hardware.Abstractions;

/// <summary>
/// Contracts for the physical devices attached to a till.
/// </summary>
/// <remarks>
/// <para>
/// These interfaces are the reason the architecture includes a .NET Terminal Agent
/// rather than being a pure browser application. WebUSB, WebHID and Web Serial are
/// Chromium-only — no Safari, no Firefox, no iOS — and an ESC/POS printer needs raw
/// byte access.
/// </para>
/// <para>
/// They live in a shared project rather than inside the Terminal Agent because the
/// payment module and the receipt renderer both need to speak about a printer and a
/// card reader without depending on a host executable.
/// </para>
/// <para>
/// <b>Every method is async and every method can fail.</b> These are I/O against
/// devices that are out of paper, powered off, or unplugged by a cleaner. The
/// governing domain rule: <b>a hardware failure must never fail a completed sale.</b>
/// The money has changed hands; a print failure is recoverable and reprintable. Any
/// design that lets a printer error roll back a payment has the priority backwards.
/// </para>
/// </remarks>
public interface IReceiptPrinter
{
    public Task<HardwareResult> PrintAsync(ReceiptDocument document, CancellationToken cancellationToken = default);

    public Task<HardwareStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>Opens the cash drawer.</summary>
/// <remarks>
/// On most estates this is a byte sequence sent to the printer's DK port rather than a
/// separate device, which is why implementations take a printer dependency and why this
/// abstraction hides that.
/// </remarks>
public interface ICashDrawer
{
    public Task<HardwareResult> OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the drawer is currently open, where the hardware reports it.
    /// </summary>
    /// <remarks>
    /// Worth having because "drawer left open" is a shrinkage signal, and because a
    /// blind close (ADR 039) is undermined if the drawer was open the whole shift.
    /// </remarks>
    public Task<bool?> IsOpenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Streams scans from a barcode reader.</summary>
/// <remarks>
/// Most scanners present as HID keyboards and need no driver, but a scale-integrated
/// scanner at a deli counter does. A scan may carry embedded weight or price via
/// GS1-128, so the result is an identifier plus transaction data, parsed by
/// <c>Gs1Parser</c> in the Catalog module rather than here.
/// </remarks>
public interface IBarcodeScanner
{
    public IAsyncEnumerable<ScanEvent> ScansAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reads a stable weight from an attached scale.</summary>
/// <remarks>
/// "Stable" is the entire difficulty. A scale reports a continuously fluctuating value
/// while goods settle, and reading too early overcharges or undercharges the customer.
/// Implementations must wait for the device's own stability flag rather than sampling
/// and hoping. In most jurisdictions a scale used for trade is legally controlled
/// equipment, so this returns the device's declared reading and never a computed one.
/// </remarks>
public interface IWeighingScale
{
    public Task<Result<decimal>> ReadStableWeightAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A P2PE card reader.
/// </summary>
/// <remarks>
/// <para>
/// The return type is the security boundary. <see cref="CardReadResult"/> carries an
/// opaque encrypted payload and a masked PAN — nothing this application can decrypt,
/// and nothing that constitutes cardholder data. The keys live in the device and the
/// acquirer's HSM.
/// </para>
/// <para>
/// This is what keeps the whole platform in PCI-DSS SAQ P2PE scope rather than
/// requiring a full Report on Compliance. Adding a method here that returns a clear
/// PAN would not be a feature, it would be a change of regulatory regime for every
/// customer (ADR 045).
/// </para>
/// </remarks>
public interface ICardReader
{
    public Task<Result<CardReadResult>> ReadAsync(Money amount, CancellationToken cancellationToken = default);

    public Task<HardwareStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Cancels a read in progress, e.g. the customer walked away.</summary>
    public Task CancelAsync(CancellationToken cancellationToken = default);
}

/// <summary>The customer-facing display pole.</summary>
public interface ICustomerDisplay
{
    public Task ShowAsync(string line1, string? line2 = null, CancellationToken cancellationToken = default);

    public Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>A receipt ready to render.</summary>
/// <remarks>
/// <see cref="QrPayload"/> is present because several fiscal regimes mandate a QR code
/// on the printed receipt. The printer must not construct it: the payload is produced
/// by the fiscal profile and passed through opaquely, so that adding a jurisdiction
/// never means editing a printer driver (ADR 031).
/// </remarks>
public sealed record ReceiptDocument(
    string Header,
    IReadOnlyList<string> Lines,
    string Footer,
    bool CutPaper = true,
    string? QrPayload = null,
    bool OpenDrawer = false);

public sealed record ScanEvent(string RawData, DateTimeOffset ScannedAt);

/// <summary>
/// The non-sensitive result of a card read.
/// </summary>
/// <remarks>
/// <see cref="EncryptedPayload"/> is passed to the payment provider unaltered and
/// unexamined. There is deliberately no property here for a PAN, expiry or CVV.
/// </remarks>
public sealed record CardReadResult(
    byte[] EncryptedPayload,
    string MaskedPan,
    string? Scheme,
    string EntryMode);

/// <summary>Outcome of a device operation.</summary>
/// <remarks>
/// Distinct from <c>Result</c> in the shared kernel because a hardware failure is not a
/// domain error: it does not invalidate a business operation and it is almost always
/// retryable. Conflating them led a caller to treat "out of paper" as a reason to
/// reject a sale.
/// </remarks>
public sealed record HardwareResult(bool Succeeded, string? FailureReason = null)
{
    public static HardwareResult Ok() => new(true);

    public static HardwareResult Failed(string reason) => new(false, reason);
}

public enum HardwareStatus
{
    Ready = 0,
    OutOfPaper = 1,
    CoverOpen = 2,
    Offline = 3,
    Busy = 4,
    Error = 5,
}

/// <summary>Hardware error catalogue.</summary>
public static class HardwareErrors
{
    public static readonly Error NoDevice =
        Error.NotFound("hardware.no_device", "The device is not connected.");

    public static readonly Error Timeout =
        Error.Conflict("hardware.timeout", "The device did not respond in time.");

    public static readonly Error ReadCancelled =
        Error.Conflict("hardware.read_cancelled", "The card read was cancelled.");

    public static readonly Error UnstableWeight =
        Error.Conflict("hardware.unstable_weight", "The scale reading did not settle.");
}
