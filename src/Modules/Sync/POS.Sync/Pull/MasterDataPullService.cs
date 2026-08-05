using POS.SharedKernel;
using POS.Sync.Contracts;

namespace POS.Sync.Pull;

/// <summary>
/// Answers a terminal's master-data pull by asking every registered
/// <see cref="IMasterDataSource"/> for its current state.
/// </summary>
/// <remarks>
/// <para>
/// <b>DELIBERATELY A FULL SNAPSHOT EVERY TIME, NOT A TRUE INCREMENTAL DELTA.</b> The
/// domain scaffolding for incremental sync already exists —
/// <c>POS.Sync.Domain.MasterDataVersion</c> (a monotonic version marker per tenant
/// and entity type) and <c>TerminalSyncCursor</c> (a terminal's acknowledged
/// high-water mark) — but wiring them up for real would mean every write path in
/// every source module (each product create/update/deactivate, every price and tax
/// change) publishing a version bump and a durable change-log entry, which does not
/// exist yet as a table anyone appends to. That is a materially larger change than
/// closing the actual gap this service exists to close: today there is NO WAY AT
/// ALL for the server to push master data to a terminal. A full snapshot on every
/// pull is correct (a terminal that applies it always ends up in the right state)
/// and small enough for a product catalogue that fits comfortably in one response,
/// even though it is not the bandwidth-efficient delta the protocol's shape (a
/// per-entity-type cursor) is clearly designed for. <see cref="IMasterDataSource"/>
/// is exactly the seam a real incremental implementation would plug into later —
/// each source would start reading its own cursor and returning only what changed
/// since it, without this service or the wire contract changing at all.
/// </para>
/// <para>
/// <see cref="PullMasterDataRequest.Cursors"/> is accepted (so the wire contract and
/// a real terminal implementation are unaffected by this scoping decision) but not
/// yet used to filter the response — every source's full snapshot is returned
/// regardless of what the terminal claims to already have. A terminal that already
/// holds everything simply re-applies identical data, which is safe (every change
/// is an idempotent upsert or soft-remove) if wasteful over a slow link.
/// </para>
/// </remarks>
public sealed class MasterDataPullService(IEnumerable<IMasterDataSource> sources)
{
    public async Task<Result<PullMasterDataResponse>> PullAsync(
        Guid tenantId, PullMasterDataRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.CompareOrdinal(request.ProtocolVersion, SyncProtocol.MinimumSupportedVersion) < 0)
        {
            return Error.Validation(
                "sync.protocol.unsupported",
                $"Protocol {request.ProtocolVersion} is no longer supported. Update the terminal.");
        }

        var changes = new List<MasterDataChange>();
        var versions = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            var snapshot = await source.GetFullSnapshotAsync(tenantId, cancellationToken);
            changes.AddRange(snapshot);

            // 0 rather than a real version number: this response is always "the
            // whole thing as of now", so there is no meaningful high-water mark yet
            // for a terminal to store and echo back. See this type's remarks.
            versions[source.EntityType] = 0;
        }

        return new PullMasterDataResponse(versions, changes, IsFullSnapshot: true, HasMore: false);
    }
}
