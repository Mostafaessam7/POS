using System.Net;
using System.Net.Http.Json;
using POS.Identity.Authorization;
using Shouldly;

namespace POS.IntegrationTests;

/// <summary>The Expenses API end to end.</summary>
[Collection(nameof(ApiCollection))]
public sealed class ExpensesApiTests(ApiFixture fixture)
{
    [Fact]
    public async Task Expense_is_recorded_and_read_back()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty,
            Permissions.Expenses.Record,
            Permissions.Expenses.View);

        using var _client = client;

        var created = await client.PostAsJsonAsync("/api/v1/expenses", NewExpense(org));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        var expense = await created.Content.ReadFromJsonAsync<ExpenseResponseDto>();
        expense!.Amount.ShouldBe(431.20m);
        expense.Status.ShouldBe("Recorded");
        expense.IsCapitalised.ShouldBeFalse();

        var fetched = await client.GetFromJsonAsync<ExpenseResponseDto>($"/api/v1/expenses/{expense.Id}");
        fetched!.ExpenseNumber.ShouldBe(expense.ExpenseNumber);
    }

    /// <summary>
    /// Self-approval is refused, and it is the aggregate that refuses it.
    /// </summary>
    /// <remarks>
    /// The recorder holds the approve permission, so authorization passes cleanly and
    /// the only thing standing between them and signing off their own claim is the
    /// separation-of-duties rule in the domain. That is deliberately where it lives: a
    /// control implemented in an endpoint is bypassed by the next endpoint.
    /// </remarks>
    [Fact]
    public async Task Recorder_cannot_approve_their_own_expense()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty,
            Permissions.Expenses.Record,
            Permissions.Expenses.Approve);

        using var _client = client;

        var created = await client.PostAsJsonAsync("/api/v1/expenses", NewExpense(org));
        var expense = await created.Content.ReadFromJsonAsync<ExpenseResponseDto>();

        var response = await client.PostAsync(
            new Uri($"/api/v1/expenses/{expense!.Id}/approve", UriKind.Relative), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();
        problem!.Code.ShouldBe("expense.self_approval_forbidden");
    }

    [Fact]
    public async Task A_second_user_can_approve()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        var (recorder, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Expenses.Record);

        using var _recorder = recorder;

        var created = await recorder.PostAsJsonAsync("/api/v1/expenses", NewExpense(org));
        var expense = await created.Content.ReadFromJsonAsync<ExpenseResponseDto>();

        var (approver, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Expenses.Approve);

        using var _approver = approver;

        var response = await approver.PostAsync(
            new Uri($"/api/v1/expenses/{expense!.Id}/approve", UriKind.Relative), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<ExpenseResponseDto>())!.Status.ShouldBe("Approved");
    }

    [Fact]
    public async Task Rejection_requires_a_reason()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        var (recorder, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Expenses.Record);

        using var _recorder = recorder;

        var created = await recorder.PostAsJsonAsync("/api/v1/expenses", NewExpense(org));
        var expense = await created.Content.ReadFromJsonAsync<ExpenseResponseDto>();

        var (rejecter, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Expenses.Reject);

        using var _rejecter = rejecter;

        var blank = await rejecter.PostAsJsonAsync(
            $"/api/v1/expenses/{expense!.Id}/reject", new { reason = "" });

        blank.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var withReason = await rejecter.PostAsJsonAsync(
            $"/api/v1/expenses/{expense.Id}/reject", new { reason = "No receipt attached" });

        withReason.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await withReason.Content.ReadFromJsonAsync<ExpenseResponseDto>())!.Status.ShouldBe("Rejected");
    }

    [Fact]
    public async Task Zero_amount_is_rejected_by_validation()
    {
        var org = await fixture.ProvisionOrganisationAsync();

        var (client, _) = await fixture.CreateClientWithPermissionsAsync(
            org.TenantId, Guid.Empty, Permissions.Expenses.Record);

        using var _client = client;

        var request = NewExpense(org) with { Amount = 0m };
        var response = await client.PostAsJsonAsync("/api/v1/expenses", request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblem>();
        problem!.Errors.Keys.ShouldContain("Amount");
    }

    /// <summary>An expense recorded for one tenant is invisible to another.</summary>
    [Fact]
    public async Task Another_tenants_expense_is_not_readable()
    {
        var owner = await fixture.ProvisionOrganisationAsync();
        var stranger = await fixture.ProvisionOrganisationAsync();

        var (ownerClient, _) = await fixture.CreateClientWithPermissionsAsync(
            owner.TenantId, Guid.Empty, Permissions.Expenses.Record);

        using var _owner = ownerClient;

        var created = await ownerClient.PostAsJsonAsync("/api/v1/expenses", NewExpense(owner));
        var expense = await created.Content.ReadFromJsonAsync<ExpenseResponseDto>();

        var (strangerClient, _) = await fixture.CreateClientWithPermissionsAsync(
            stranger.TenantId, Guid.Empty, Permissions.Expenses.View);

        using var _stranger = strangerClient;

        var response = await strangerClient.GetAsync(
            new Uri($"/api/v1/expenses/{expense!.Id}", UriKind.Relative));

        // 404, not 403: a 403 would confirm the expense exists, which is itself a leak
        // across a tenant boundary.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static RecordExpenseDto NewExpense((Guid TenantId, Guid CompanyId, Guid BranchId, Guid WarehouseId) org) =>
        new()
        {
            CompanyId = org.CompanyId,
            BranchId = org.BranchId,
            ExpenseNumber = $"EXP-{Guid.CreateVersion7():N}"[..20],
            Category = 11, // Utilities
            Amount = 431.20m,
            TaxAmount = 60.37m,
            Currency = "USD",
            IncurredOn = DateOnly.FromDateTime(DateTime.UtcNow),
            Description = "Quarterly electricity"
        };

    private sealed record RecordExpenseDto
    {
        public Guid CompanyId { get; init; }
        public Guid BranchId { get; init; }
        public string ExpenseNumber { get; init; } = string.Empty;
        public int Category { get; init; }
        public decimal Amount { get; init; }
        public decimal TaxAmount { get; init; }
        public string Currency { get; init; } = string.Empty;
        public DateOnly IncurredOn { get; init; }
        public string Description { get; init; } = string.Empty;
    }

    private sealed record ExpenseResponseDto(
        Guid Id,
        string ExpenseNumber,
        decimal Amount,
        decimal TaxAmount,
        string Status,
        bool IsCapitalised);

    private sealed record ValidationProblem(Dictionary<string, string[]> Errors);

    private sealed record ProblemResponse(string Code, string Title);
}
