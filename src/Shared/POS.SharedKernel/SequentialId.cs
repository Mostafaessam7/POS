namespace POS.SharedKernel;

/// <summary>
/// Machine identity for records that may originate offline.
/// </summary>
/// <remarks>
/// UUID v7 is time-sortable, so it retains index locality in a clustered index —
/// avoiding the page-split penalty of random v4 GUIDs — while requiring no central
/// coordination. A till that has been disconnected for a week can still mint IDs
/// that will not collide with anything.
///
/// NEVER use database identity columns for anything created offline. See ADR 005.
/// Human-facing, gap-free fiscal numbering is a separate concern — see ReceiptNumber.
/// </remarks>
public static class SequentialId
{
    public static Guid New() => Guid.CreateVersion7();

    /// <summary>Generates an ID stamped with a specific instant, for deterministic tests.</summary>
    public static Guid NewAt(DateTimeOffset timestamp) => Guid.CreateVersion7(timestamp);
}
