namespace POS.SharedKernel;

/// <summary>
/// Populated by <c>AuditingInterceptor</c>. Never assigned by hand — the one
/// record where it matters will be the one somebody forgot.
/// </summary>
public interface IAuditable
{
    public DateTimeOffset CreatedAt { get; }
    public Guid CreatedBy { get; }
    public DateTimeOffset? ModifiedAt { get; }
    public Guid? ModifiedBy { get; }
}

/// <summary>
/// Applied SELECTIVELY. Master data and configuration: yes. Transactional records
/// (sales, payments, stock movements): never — they are immutable by policy, and a
/// void is a new document referencing the original. See ADR 007.
///
/// Every unique index on a soft-deletable table MUST be filtered on
/// <c>WHERE IsDeleted = 0</c>, or barcodes and SKUs can never be reused. There is
/// an integration test asserting this.
/// </summary>
public interface ISoftDeletable
{
    public bool IsDeleted { get; }
    public DateTimeOffset? DeletedAt { get; }
    public Guid? DeletedBy { get; }
}
