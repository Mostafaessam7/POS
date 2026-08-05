using POS.Sync.Contracts;

namespace POS.Sync.Pull;

/// <summary>
/// Publishes one entity type's master data downward to terminals.
/// </summary>
/// <remarks>
/// Registered by the OWNING module (Catalog implements one for "Product"), the same
/// inversion <see cref="POS.Sync.Ingest.ISyncRecordHandler"/> uses for the upload
/// direction: Sync knows there are sources, never what any of them mean, which is
/// what keeps it from acquiring a project reference to every module (architecture
/// rule 2). <see cref="MasterDataPullService"/> discovers every registered source
/// through <see cref="IEnumerable{T}"/> and asks each for its snapshot.
/// </remarks>
public interface IMasterDataSource
{
    /// <summary>"Product", "PriceList", "TaxGroup" — matches <see cref="MasterDataChange.EntityType"/>.</summary>
    public string EntityType { get; }

    /// <summary>Every currently-active record this source owns, as of right now.</summary>
    public Task<IReadOnlyList<MasterDataChange>> GetFullSnapshotAsync(Guid tenantId, CancellationToken cancellationToken);
}
