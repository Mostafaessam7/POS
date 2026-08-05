using Shouldly;

namespace POS.IntegrationTests;

/// <summary>
/// The failure this protects against is not hypothetical: a terminal uploads a
/// batch, the server commits, the response is lost to a dropped connection, and
/// the terminal retries the identical batch. Without idempotency that is a
/// duplicated sale — real money, wrong stock, unbalanced drawer.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class SyncIdempotencyTests(ApiFixture fixture)
{
    [Fact]
    public async Task Replaying_an_identical_batch_creates_no_duplicates()
    {
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();
        var batch = fixture.BuildSaleBatch(terminal, sequenceFrom: 1, count: 10);

        var first = await fixture.UploadAsync(tenant, batch);
        var second = await fixture.UploadAsync(tenant, batch);

        first.Accepted.ShouldBe(10);
        second.Accepted.ShouldBe(0);
        second.Duplicates.ShouldBe(10);

        (await fixture.CountSalesAsync(tenant)).ShouldBe(10);
    }

    [Fact]
    public async Task Concurrent_identical_uploads_are_collapsed_by_the_unique_constraint()
    {
        // An application-level "select then insert" races here and duplicates.
        // The database constraint is the real guarantee.
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();
        var batch = fixture.BuildSaleBatch(terminal, sequenceFrom: 1, count: 5);

        var results = await Task.WhenAll(
            fixture.UploadAsync(tenant, batch),
            fixture.UploadAsync(tenant, batch),
            fixture.UploadAsync(tenant, batch));

        results.Sum(r => r.Accepted).ShouldBe(5);
        (await fixture.CountSalesAsync(tenant)).ShouldBe(5);
    }

    [Fact]
    public async Task One_malformed_record_does_not_block_the_valid_ones_behind_it()
    {
        // If a bad record failed the whole batch, the terminal would retry forever
        // and the store's takings would never reach HQ.
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();
        var batch = fixture.BuildSaleBatchWithOneMalformedRecord(terminal, count: 5);

        var result = await fixture.UploadAsync(tenant, batch);

        result.Accepted.ShouldBe(4);
        result.Rejected.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Sequence_regression_is_flagged_rather_than_silently_accepted()
    {
        // Indicates a restored backup or a cloned terminal. Accepting it silently
        // means two tills minting the same receipt numbers.
        var (tenant, terminal) = await fixture.CreateEnrolledTerminalAsync();

        await fixture.UploadAsync(tenant, fixture.BuildSaleBatch(terminal, sequenceFrom: 100, count: 5));

        var result = await fixture.UploadAsync(
            tenant, fixture.BuildSaleBatch(terminal, sequenceFrom: 50, count: 5));

        result.Rejected.ShouldNotBeEmpty();
    }
}
