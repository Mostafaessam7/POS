using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Api.Endpoints;
using POS.Payments.Abstractions;
using POS.Payments.Domain;
using POS.Catalog.Persistence;
using POS.Common.Persistence;
using POS.Expenses.Persistence;
using POS.Fiscal.Persistence;
using POS.Common.Tenancy;
using POS.Identity.Authentication;
using POS.Identity.Domain;
using POS.Identity.Persistence;
using POS.Inventory.Persistence;
using POS.Payments.Persistence;
using POS.Purchasing.Persistence;
using POS.Sales.Domain;
using POS.Sales.Persistence;
using POS.Sales.Sync;
using POS.SharedKernel;
using POS.Sync.Contracts;
using POS.Sync.Persistence;
using Respawn;
using Respawn.Graph;
using Testcontainers.MsSql;

namespace POS.IntegrationTests;

/// <summary>
/// Shared host + database for the integration suite.
/// </summary>
/// <remarks>
/// One database and one <see cref="WebApplicationFactory{TEntryPoint}"/> shared by
/// every test in <see cref="ApiCollection"/>. Startup is the dominant cost of this
/// suite; paying it per test class turns a forty-second run into a ten-minute one,
/// which is how integration suites end up disabled in CI.
///
/// WHERE THE DATABASE COMES FROM, in order:
///   1. POS_TEST_SQL, if set — a developer or CI pointing at a specific server
///   2. a local SQL Server on the default instance, if one answers
///   3. Testcontainers, which needs Docker
///
/// The local-server fallback is why this suite runs at all on a machine without
/// Docker. Testcontainers is the better default for CI because it guarantees a clean
/// server; it is not a reason to make the suite unrunnable everywhere else.
///
/// AUTHENTICATION IS REAL. The fixture mints a genuine JWT with the host's own signing
/// key and sends it as a bearer token, so every request goes through the same
/// authentication and tenant-resolution path production uses. A test-only header would
/// have been far simpler and would have proved nothing: tenancy is resolved from a
/// signed claim precisely so that a caller cannot name its own tenant (ADR 006), and a
/// suite that bypasses that is not testing tenant isolation.
/// </remarks>
public sealed class ApiFixture : IAsyncLifetime
{
    private const string SigningKey = "integration-test-signing-key-32ch!";
    private const string Issuer = "https://api.pos.example";
    private const string Audience = "pos-terminals";
    private const string RootApiKey = "integration-test-root-key";

    /// <summary>The root key's own value, exposed for the one test asserting it does NOT double as an operator key.</summary>
    public const string RootApiKeyForTests = RootApiKey;

    /// <summary>
    /// The key for the one <see cref="ProvisioningOperator"/> minted in
    /// <see cref="InitializeAsync"/>, via the real <c>POST /provisioning/operators</c>
    /// endpoint rather than a row seeded directly — so every test in this suite
    /// exercises the actual mint path, not a shortcut around it.
    /// </summary>
    private string _operatorApiKey = string.Empty;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly MsSqlContainer? _container;

    /// <summary>
    /// The organisational context each enrolled terminal belongs to.
    /// </summary>
    /// <remarks>
    /// A sale payload names its company, branch, shift and cashier, but the tests only
    /// hand <c>BuildSaleBatch</c> a terminal id. Remembering what each terminal was
    /// provisioned into keeps those signatures — which are the suite's spec — unchanged.
    /// </remarks>
    private readonly Dictionary<Guid, TerminalContext> _terminals = [];

    private WebApplicationFactory<Program>? _factory;
    private string _connectionString = string.Empty;
    private TokenService? _tokens;

    public ApiFixture()
    {
        var external = ResolveExternalConnectionString();

        if (external is null)
            _container = new MsSqlBuilder().Build();
        else
            _connectionString = external;
    }

    /// <summary>
    /// Every module context, for tests that assert over the whole model.
    /// </summary>
    /// <remarks>
    /// Listed rather than discovered. A missing entry weakens a test silently, so this
    /// is the one place worth keeping in step with the host's registrations by hand —
    /// and <see cref="MigrateAsync"/> reads the same list, so a context omitted here
    /// has no schema and fails loudly on the first test instead.
    /// </remarks>
    public static IReadOnlyList<Type> ModuleContextTypes { get; } =
    [
        typeof(IdentityDbContext),
        typeof(CatalogDbContext),
        typeof(InventoryDbContext),
        typeof(SalesDbContext),
        typeof(PaymentsDbContext),
        typeof(FiscalDbContext),
        typeof(PurchasingDbContext),
        typeof(ExpensesDbContext),
        typeof(SyncDbContext)
    ];

    private WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException(
            "The fixture has not been initialised. xUnit calls InitializeAsync before the " +
            "first test in the collection; a test seeing this ran outside the collection.");

    /// <summary>The host's service provider, for tests that inspect the model rather than call it.</summary>
    public IServiceProvider Services => Factory.Services;

    /// <summary>An unauthenticated client, for the handful of endpoints that don't require a bearer token.</summary>
    public HttpClient CreateAnonymousClient() => Factory.CreateClient();

    public async Task InitializeAsync()
    {
        if (_container is not null)
        {
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(web =>
            {
                web.UseSetting($"ConnectionStrings:{PosSqlServer.ConnectionStringName}", _connectionString);
                web.UseSetting("Identity:SigningKey", SigningKey);
                web.UseSetting("Identity:Issuer", Issuer);
                web.UseSetting("Identity:Audience", Audience);
                web.UseSetting("Provisioning:Enabled", "true");
                web.UseSetting("Provisioning:RootApiKey", RootApiKey);
                web.UseSetting("Api:EnableOpenApiDocs", "true");
                web.UseEnvironment("Testing");

                // A provider double so the indeterminate-payment sweep has something to
                // resolve against. It answers only to TEST_PROVIDER, so it is inert for
                // every other path. The estate registers no real acquirer, by design.
                web.ConfigureTestServices(services =>
                    services.AddSingleton<IPaymentProvider, TestPaymentProvider>());
            });

        await MigrateAsync();
        await ResetDatabaseAsync();

        _tokens = new TokenService(
            new TokenOptions
            {
                Issuer = Issuer,
                Audience = Audience,
                SigningKey = IdentityModuleKey()
            },
            new SystemClock());

        _operatorApiKey = await MintOperatorApiKeyAsync();
    }

    /// <summary>
    /// Mints the one operator identity every other helper in this fixture provisions
    /// tenants as.
    /// </summary>
    /// <remarks>
    /// Goes through the real root-gated endpoint rather than inserting a
    /// <c>ProvisioningOperator</c> row directly, so the mint path itself is exercised
    /// by every test run in this suite, not only by the tests in
    /// <c>ProvisioningApiTests</c> that name it explicitly.
    /// </remarks>
    private async Task<string> MintOperatorApiKeyAsync()
    {
        using var client = CreateAnonymousClient();
        client.DefaultRequestHeaders.Add(RequireRootOperatorApiKeyFilter.HeaderName, RootApiKey);

        var response = await client.PostAsJsonAsync(
            "/api/v1/provisioning/operators",
            new { name = $"integration-test-operator-{Guid.CreateVersion7():N}" });

        await EnsureSuccessAsync(response);

        var created = await response.Content.ReadFromJsonAsync<ProvisionedOperator>();
        return created!.ApiKey;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();

        if (_container is not null)
            await _container.DisposeAsync();
    }

    /// <summary>
    /// Brings every module's schema up to date.
    /// </summary>
    /// <remarks>
    /// <c>Migrate</c>, not <c>EnsureCreated</c>. EnsureCreated builds the schema from
    /// the model and skips migrations entirely, so the suite would pass against a
    /// schema no deployment will ever produce — and a broken migration would sail
    /// through CI. Running the real migrations means this suite is also the test that
    /// they work.
    /// </remarks>
    private async Task MigrateAsync()
    {
        using var scope = Factory.Services.CreateScope();

        foreach (var contextType in ModuleContextTypes)
        {
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(contextType);
            await context.Database.MigrateAsync();
        }
    }

    /// <summary>
    /// The nine module schemas, lower-cased exactly as they exist in SQL Server —
    /// <see cref="ModuleContextTypes"/> gives the CLR type, this gives the schema name
    /// Respawn needs to find their tables.
    /// </summary>
    private static readonly string[] ModuleSchemas =
        ["identity", "catalog", "inventory", "sales", "payments", "fiscal", "purchasing", "expenses", "sync"];

    /// <summary>
    /// Wipes every row the suite could have left behind from a previous run, without
    /// touching the schema migrations just brought up to date.
    /// </summary>
    /// <remarks>
    /// The suite reuses a persistent local SQL Server database across separate `dotnet
    /// test` invocations (see <see cref="ResolveExternalConnectionString"/>) — only the
    /// Testcontainers path starts genuinely empty. Without this, leftover rows from a
    /// prior run are indistinguishable from rows a currently-running test created,
    /// which is exactly what lets a subtly wrong assertion pass by accident. Each
    /// module's `__EFMigrationsHistory_*` table is excluded: Respawn only needs to
    /// clear DATA, and wiping migration history would make the next run believe every
    /// migration needs re-applying against a schema that already has it.
    /// </remarks>
    private async Task ResetDatabaseAsync()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            SchemasToInclude = ModuleSchemas,
            TablesToIgnore = ModuleSchemas
                .Select(schema => new Table(schema, $"__EFMigrationsHistory_{Capitalize(schema)}"))
                .ToArray(),
            DbAdapter = DbAdapter.SqlServer
        });

        await respawner.ResetAsync(connection);
    }

    private static string Capitalize(string schema) =>
        char.ToUpperInvariant(schema[0]) + schema[1..];

    private static byte[] IdentityModuleKey() => System.Text.Encoding.UTF8.GetBytes(SigningKey);

    /// <summary>An HTTP client whose requests carry a signed token for <paramref name="tenantId"/>.</summary>
    public HttpClient CreateClientFor(Guid tenantId) => CreateClientFor(tenantId, terminalId: null);

    public HttpClient CreateClientFor(Guid tenantId, Guid? terminalId)
    {
        var user = User.Create(
            $"test-{Guid.CreateVersion7():N}@example.test",
            "Integration Test",
            passwordHash: "not-used",
            passwordAlgorithm: "none");

        var pair = _tokens!.Issue(user, tenantId, terminalId, branchId: null, companyIds: []);

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pair.AccessToken);
        return client;
    }

    /// <summary>
    /// Creates a real user holding <paramref name="permissionCodes"/> at
    /// <paramref name="scopeId"/>, and returns a client authenticated as them.
    /// </summary>
    /// <remarks>
    /// SEEDS GENUINE IDENTITY ROWS rather than faking the permission resolver. Permission
    /// records, a role that grants them, a user, and a role assignment at a scope — the
    /// same graph a real tenant has. The request then goes through the actual resolver,
    /// the actual policy provider and the actual authorization handler.
    ///
    /// A stubbed resolver would have been far less work and would have tested nothing:
    /// the interesting failures in an authorization system are in how roles resolve to
    /// permissions at a scope, and a stub asserts only that the stub was called.
    ///
    /// <paramref name="scopeId"/> of <see cref="Guid.Empty"/> grants everywhere, which
    /// is how PermissionSet.Has treats it. Pass a specific branch or company id to test
    /// that reach is limited.
    /// </remarks>
    public async Task<(HttpClient Client, Guid UserId)> CreateClientWithPermissionsAsync(
        Guid tenantId,
        Guid scopeId,
        params string[] permissionCodes)
    {
        ArgumentNullException.ThrowIfNull(permissionCodes);

        using var scope = Factory.Services.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        using var _ = tenantContext.EnterTenant(tenantId, "integration test identity seeding");

        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        // Permissions are PLATFORM reference data, not tenant data, so they are shared
        // across every test and seeded only once. The unique index on Code is what makes
        // "insert if absent" the right shape here rather than a per-test insert.
        // A List, not the string[] parameter, and that is not cosmetic. On an array,
        // `Contains` binds to MemoryExtensions.Contains(ReadOnlySpan<T>, T) rather than
        // Enumerable.Contains, and a ReadOnlySpan cannot appear in an expression tree —
        // it fails at RUNTIME inside EF's parameter evaluation with a type-load error
        // that names neither this line nor the real cause.
        var requestedCodes = permissionCodes.ToList();

        var existing = await db.Permissions
            .Where(p => requestedCodes.Contains(p.Code))
            .ToListAsync();

        var missing = requestedCodes.Except(existing.Select(p => p.Code), StringComparer.Ordinal).ToList();

        foreach (var code in missing)
        {
            var permission = Permission.Define(code, code.Split('.')[0], $"Seeded for tests: {code}");
            db.Permissions.Add(permission);
            existing.Add(permission);
        }

        var role = Role.Create($"test-role-{Guid.CreateVersion7():N}", "Seeded for tests");

        foreach (var permission in existing)
            role.Grant(permission.Id);

        db.Roles.Add(role);

        var user = User.Create(
            $"test-{Guid.CreateVersion7():N}@example.test",
            "Integration Test User",
            passwordHash: "not-used",
            passwordAlgorithm: "none");

        user.AssignRole(role.Id, ScopeType.Branch, scopeId);
        db.Users.Add(user);

        await db.SaveChangesAsync();

        // The token carries the version AFTER the assignment. Minting it before would
        // give a token whose perm_version never matches a cache entry — the exact
        // staleness the version exists to prevent.
        var pair = _tokens!.Issue(user, tenantId, terminalId: null, branchId: null, companyIds: []);

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pair.AccessToken);

        return (client, user.Id);
    }

    public async Task<Guid> CreateTenantAsync() => (await ProvisionTenantAsync()).Id;

    /// <summary>Provisions a tenant and reports the organisation ids it was given.</summary>
    public async Task<(Guid TenantId, Guid CompanyId, Guid BranchId, Guid WarehouseId)> ProvisionOrganisationAsync()
    {
        var tenant = await ProvisionTenantAsync();
        return (tenant.Id, tenant.CompanyId, tenant.BranchId, tenant.WarehouseId);
    }

    public async Task<(Guid TenantA, Guid TenantB)> CreateTwoTenantsAsync()
        => (await CreateTenantAsync(), await CreateTenantAsync());

    /// <summary>
    /// Adds a warehouse beyond the one provisioning creates, for tests that move stock
    /// between warehouses — a transfer needs a source, a destination, and an in-transit
    /// location, and provisioning only ever creates one.
    /// </summary>
    public async Task<Guid> CreateWarehouseAsync(Guid tenantId, Guid branchId, WarehouseKind kind)
    {
        var warehouseId = Guid.Empty;

        await WriteAsync<IdentityDbContext>(tenantId, db =>
        {
            var warehouse = Warehouse.Create(branchId, $"W{Random.Shared.Next(1000000, 9999999)}", $"{kind} warehouse", kind);
            db.Warehouses.Add(warehouse);
            warehouseId = warehouse.Id;
        });

        return warehouseId;
    }

    public async Task<(Guid Tenant, Guid Terminal)> CreateEnrolledTerminalAsync()
    {
        var tenant = await ProvisionTenantAsync();

        using var client = CreateClientFor(tenant.Id);

        var response = await client.PostAsJsonAsync(
            "/api/v1/terminals",
            new { code = $"T{Random.Shared.Next(1000, 9999)}", name = "Till" });

        await EnsureSuccessAsync(response);

        var created = await response.Content.ReadFromJsonAsync<CreatedResource>();

        _terminals[created!.Id] = new TerminalContext(
            tenant.CompanyId,
            tenant.BranchId,
            tenant.WarehouseId,
            ShiftId: Guid.CreateVersion7(),
            CashierId: Guid.CreateVersion7());

        return (tenant.Id, created.Id);
    }

    public async Task<Guid> SeedProductAsync(Guid tenantId, string name)
    {
        using var client = CreateClientFor(tenantId);

        var response = await client.PostAsJsonAsync(
            "/api/v1/catalog/products",
            new { name, sku = $"SKU-{Guid.CreateVersion7():N}" });

        await EnsureSuccessAsync(response);

        var created = await response.Content.ReadFromJsonAsync<CreatedProduct>();
        return created!.Id;
    }

    public async Task<Guid> SeedProductWithBarcodeAsync(Guid tenantId, string barcode)
    {
        var productId = await SeedProductAsync(tenantId, $"Product {barcode}");

        using var client = CreateClientFor(tenantId);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/catalog/products/{productId}/barcodes",
            new { value = barcode, symbology = 0 });

        await EnsureSuccessAsync(response);
        return productId;
    }

    public async Task DeleteProductAsync(Guid tenantId, Guid productId)
    {
        using var client = CreateClientFor(tenantId);

        var response = await client.DeleteAsync(
            new Uri($"/api/v1/catalog/products/{productId}", UriKind.Relative));

        await EnsureSuccessAsync(response);
    }

    /// <summary>
    /// Creates whatever resource <paramref name="template"/> addresses, so the
    /// generated tenant-isolation theory has something to try to read.
    /// </summary>
    public Task<Guid> SeedResourceAsync(Guid tenantId, string template)
        => template.Contains("/products", StringComparison.Ordinal)
            ? SeedProductAsync(tenantId, "Seeded")
            : throw new NotSupportedException(
                $"No seeder is registered for '{template}'. Add one alongside the endpoint.");

    public async Task<UploadBatchResponse> UploadAsync(Guid tenantId, UploadBatchRequest batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        using var client = CreateClientFor(tenantId, batch.TerminalId);

        var response = await client.PostAsJsonAsync("/api/v1/sync/batches", batch);
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<UploadBatchResponse>())!;
    }

    /// <summary>A well-formed batch of <paramref name="count"/> sale records.</summary>
    public UploadBatchRequest BuildSaleBatch(Guid terminalId, long sequenceFrom, int count)
    {
        var records = Enumerable.Range(0, count)
            .Select(i => NewSaleRecord(terminalId, sequenceFrom + i))
            .ToList();

        return new UploadBatchRequest(
            SyncProtocol.CurrentVersion,
            terminalId,
            sequenceFrom,
            sequenceFrom + count - 1,
            records);
    }

    /// <summary>
    /// The same batch with its FIRST record unroutable, so a test can prove the records
    /// behind it are still applied.
    /// </summary>
    /// <remarks>
    /// "Malformed" here means a record type no handler is registered for. That is the
    /// rejection path the ingest service actually implements, and it is the one that
    /// matters: a bad record must not block the twelve valid sales queued behind it, or
    /// the terminal retries forever and the store's takings never reach HQ.
    /// </remarks>
    public UploadBatchRequest BuildSaleBatchWithOneMalformedRecord(Guid terminalId, int count)
    {
        var batch = BuildSaleBatch(terminalId, sequenceFrom: 1, count: count);

        var records = batch.Records.ToList();
        records[0] = records[0] with { Type = "not-a-registered-record-type" };

        return batch with { Records = records };
    }

    /// <summary>The stock location a terminal was enrolled against.</summary>
    public Guid WarehouseFor(Guid terminalId) => _terminals[terminalId].WarehouseId;

    /// <summary>
    /// The variant each record in a batch sells, read back out of its payload.
    /// </summary>
    /// <remarks>
    /// Deserialised rather than tracked alongside, so the assertions are made against
    /// what was actually uploaded. A record whose payload cannot be parsed is skipped:
    /// the malformed-record fixtures deliberately produce one, and it has no variant to
    /// report.
    /// </remarks>
    public static IReadOnlyList<Guid> VariantsIn(UploadBatchRequest batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var variants = new List<Guid>();

        foreach (var record in batch.Records)
        {
            SaleSyncPayload? payload;

            try
            {
                payload = JsonSerializer.Deserialize<SaleSyncPayload>(record.Payload, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (payload is { Lines.Count: > 0 })
                variants.Add(payload.Lines[0].VariantId);
        }

        return variants;
    }

    public async Task<int> CountSalesAsync(Guid tenantId)
    {
        using var client = CreateClientFor(tenantId);

        var page = await client.GetFromJsonAsync<SalesCount>("/api/v1/sales/count");
        return page!.Count;
    }

    /// <summary>
    /// Writes to a product through a DbContext scoped to a DIFFERENT tenant, bypassing
    /// the API and therefore the query filter.
    /// </summary>
    /// <remarks>
    /// This is the one helper that deliberately reaches around the API, because it is
    /// the only way to reach the case query filters cannot cover: an entity loaded
    /// under one tenant and saved under another, which is what a mis-scoped background
    /// job does. The write interceptor is the only thing standing between that and
    /// cross-tenant corruption.
    /// </remarks>
    public async Task AttemptDirectCrossTenantWriteAsync(Guid tenantId, Guid productId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();

        // Load without the filter — as a background job holding an id would — then
        // establish a different tenant before saving.
        var product = await db.Products
            .IgnoreQueryFilters()
            .FirstAsync(p => p.Id == productId);

        Resolve(tenantContext, tenantId);

        product.Deactivate(DateTimeOffset.UtcNow);

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Runs a write against a module context, acting as <paramref name="tenantId"/>.
    /// </summary>
    /// <remarks>
    /// For modules that have persistence but no endpoints yet. Going through the API
    /// would be better — a test that reaches around it proves the database works rather
    /// than the system — but Purchasing and Expenses expose no routes, and a mapping
    /// that has never written a row is not verified by anything.
    ///
    /// Each call gets its OWN scope, so a write and the read that checks it never share
    /// a change tracker. Reading back a tracked instance passes even when the mapping
    /// persists nothing.
    /// </remarks>
    public async Task WriteAsync<TContext>(Guid tenantId, Action<TContext> write)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(write);

        using var scope = Factory.Services.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        using var _ = tenantContext.EnterTenant(tenantId, "integration test seeding");

        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        write(db);
        await db.SaveChangesAsync();
    }

    /// <inheritdoc cref="WriteAsync{TContext}(Guid, Action{TContext})"/>
    /// <remarks>The async form, for a write that has to load a row before mutating it.</remarks>
    public async Task WriteAsync<TContext>(Guid tenantId, Func<TContext, Task> write)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(write);

        using var scope = Factory.Services.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        using var _ = tenantContext.EnterTenant(tenantId, "integration test seeding");

        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        await write(db);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Resolves a scoped service from the host and runs it, as a system operation.
    /// </summary>
    /// <remarks>
    /// For driving a background-job worker directly instead of waiting on its timer. No
    /// tenant is established on the outer scope on purpose: a sweep runs as a system
    /// operation and establishes each tenant itself as it processes rows.
    /// </remarks>
    public async Task<TResult> RunWorkerAsync<TWorker, TResult>(Func<TWorker, Task<TResult>> work)
        where TWorker : notnull
    {
        ArgumentNullException.ThrowIfNull(work);

        using var scope = Factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<TWorker>());
    }

    /// <summary>
    /// Seeds a payment stuck in the indeterminate state, for the resolution sweep to find.
    /// </summary>
    /// <remarks>
    /// Built through the aggregate — Initiate then MarkIndeterminate — rather than an
    /// INSERT, so it is a payment the domain agrees is genuinely indeterminate. Its
    /// provider is <see cref="TestPaymentProvider.Code"/> so the sweep has a provider to
    /// resolve it against.
    /// </remarks>
    public async Task<Guid> SeedIndeterminatePaymentAsync(Guid tenantId)
    {
        var paymentId = Guid.CreateVersion7();

        await WriteAsync<PaymentsDbContext>(tenantId, db =>
        {
            var payment = Payment.Initiate(
                paymentId,
                tenantId,
                branchId: Guid.CreateVersion7(),
                terminalId: Guid.CreateVersion7(),
                saleId: Guid.CreateVersion7(),
                PaymentKind.Sale,
                new Money(25.00m, "USD"),
                IdempotencyKey.New(),
                TestPaymentProvider.Code,
                DateTimeOffset.UtcNow,
                BusinessDate.Open(DateOnly.FromDateTime(DateTime.UtcNow)));

            payment.MarkIndeterminate("provider response lost", DateTimeOffset.UtcNow)
                   .IsSuccess.ShouldBeTrue();

            db.Payments.Add(payment);
        });

        return paymentId;
    }

    /// <inheritdoc cref="WriteAsync{TContext}(Guid, Action{TContext})"/>
    public async Task<TResult> ReadAsync<TContext, TResult>(Guid tenantId, Func<TContext, Task<TResult>> read)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(read);

        using var scope = Factory.Services.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        using var _ = tenantContext.EnterTenant(tenantId, "integration test verification");

        return await read(scope.ServiceProvider.GetRequiredService<TContext>());
    }

    /// <summary>
    /// Every route exposing a tenant-scoped resource.
    /// </summary>
    /// <remarks>
    /// Hand-listed for now. Reading the host's route table would be better and is what
    /// the suite eventually needs — a hand-written list covers the endpoints that
    /// existed the day it was written — but discovery needs a running host and this is
    /// a static member evaluated before the fixture exists.
    /// </remarks>
    public static TheoryData<string, string> DiscoverTenantScopedEndpoints() => new()
    {
        { "GET", "/api/v1/catalog/products/{id}" }
    };

    /// <summary>
    /// Sets the ambient tenant on a scoped context whose setter is internal to POS.Common.
    /// </summary>
    /// <remarks>
    /// TenantContext.Resolve is internal by design: only the middleware may establish a
    /// tenant. This test needs to simulate a mis-scoped background job, which is
    /// exactly the situation the interceptor exists to catch, so it reaches the setter
    /// reflectively rather than widening the production API for a test's convenience.
    /// </remarks>
    private static void Resolve(TenantContext context, Guid tenantId) =>
        typeof(TenantContext)
            .GetMethod("Resolve", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(context, [tenantId]);

    private async Task<ProvisionedTenant> ProvisionTenantAsync()
    {
        using var client = CreateAnonymousClient();
        client.DefaultRequestHeaders.Add(RequireOperatorApiKeyFilter.HeaderName, _operatorApiKey);

        var response = await client.PostAsJsonAsync(
            "/api/v1/tenants",
            new { name = $"Tenant {Guid.CreateVersion7():N}"[..24] });

        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<ProvisionedTenant>())!;
    }

    /// <summary>Provisions a tenant with a caller-supplied header value instead of the fixture's own minted operator key.</summary>
    /// <remarks>For tests exercising the operator gate itself — a fresh, revoked, or bogus key.</remarks>
    public async Task<HttpResponseMessage> ProvisionTenantWithKeyAsync(string? operatorApiKey)
    {
        using var client = CreateAnonymousClient();

        if (operatorApiKey is not null)
            client.DefaultRequestHeaders.Add(RequireOperatorApiKeyFilter.HeaderName, operatorApiKey);

        return await client.PostAsJsonAsync(
            "/api/v1/tenants",
            new { name = $"Tenant {Guid.CreateVersion7():N}"[..24] });
    }

    /// <summary>Mints a new operator via the real root-gated endpoint, for tests that need a SECOND, independently controllable identity.</summary>
    public async Task<(Guid Id, string ApiKey)> MintOperatorAsync(string? rootApiKey = null)
    {
        using var client = CreateAnonymousClient();
        client.DefaultRequestHeaders.Add(RequireRootOperatorApiKeyFilter.HeaderName, rootApiKey ?? RootApiKey);

        var response = await client.PostAsJsonAsync(
            "/api/v1/provisioning/operators",
            new { name = $"test-operator-{Guid.CreateVersion7():N}" });

        await EnsureSuccessAsync(response);

        var created = (await response.Content.ReadFromJsonAsync<ProvisionedOperator>())!;
        return (created.Id, created.ApiKey);
    }

    /// <summary>Revokes an operator via the real root-gated endpoint.</summary>
    public async Task<HttpResponseMessage> RevokeOperatorAsync(Guid operatorId, string? rootApiKey = null)
    {
        using var client = CreateAnonymousClient();
        client.DefaultRequestHeaders.Add(RequireRootOperatorApiKeyFilter.HeaderName, rootApiKey ?? RootApiKey);

        return await client.PostAsync(
            new Uri($"/api/v1/provisioning/operators/{operatorId}/revoke", UriKind.Relative), null);
    }

    /// <summary>
    /// The raw HTTP response from minting an operator, for tests exercising the root
    /// gate itself or a name collision. Unlike <see cref="MintOperatorAsync"/>, a null
    /// <paramref name="rootApiKey"/> here means the header is OMITTED entirely, not
    /// "use the fixture's own key" — pass <see cref="RootApiKeyForTests"/> explicitly
    /// for that.
    /// </summary>
    public async Task<HttpResponseMessage> MintOperatorViaHttpAsync(string? rootApiKey, string? name = null)
    {
        using var client = CreateAnonymousClient();

        if (rootApiKey is not null)
            client.DefaultRequestHeaders.Add(RequireRootOperatorApiKeyFilter.HeaderName, rootApiKey);

        return await client.PostAsJsonAsync(
            "/api/v1/provisioning/operators",
            new { name = name ?? $"test-operator-{Guid.CreateVersion7():N}" });
    }

    /// <summary>The operator list's raw JSON body, for asserting no credential material ever appears in it.</summary>
    public async Task<string> ListOperatorsRawAsync()
    {
        using var client = CreateAnonymousClient();
        client.DefaultRequestHeaders.Add(RequireRootOperatorApiKeyFilter.HeaderName, RootApiKey);

        var response = await client.GetAsync(new Uri("/api/v1/provisioning/operators", UriKind.Relative));
        await EnsureSuccessAsync(response);

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// A connection string supplied by the environment, or a reachable local server.
    /// </summary>
    private static string? ResolveExternalConnectionString()
    {
        var configured = Environment.GetEnvironmentVariable("POS_TEST_SQL");

        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        const string local =
            "Server=localhost;Database=PosIntegrationTests;Trusted_Connection=true;TrustServerCertificate=true;Encrypt=false";

        try
        {
            using var probe = new SqlConnection(
                "Server=localhost;Database=master;Trusted_Connection=true;TrustServerCertificate=true;Encrypt=false;Connect Timeout=3");

            probe.Open();
            return local;
        }
        catch (SqlException)
        {
            // No local server. Fall through to Testcontainers, which needs Docker.
            return null;
        }
    }

    /// <summary>
    /// Asserts success and puts the response body in the message when it is not.
    /// </summary>
    /// <remarks>
    /// <c>EnsureSuccessStatusCode</c> reports "Response status code does not indicate
    /// success: 500" and nothing else, which is the least useful sentence a failing
    /// integration suite can produce — the ProblemDetails body naming the actual fault
    /// is right there and gets thrown away.
    /// </remarks>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync();

        throw new HttpRequestException(
            $"{(int)response.StatusCode} {response.StatusCode} from {response.RequestMessage?.RequestUri}: {body}");
    }

    /// <summary>
    /// One completed sale as a terminal would upload it.
    /// </summary>
    /// <remarks>
    /// The receipt sequence is the record's terminal sequence, which is what makes a
    /// replayed batch idempotent: the handler keys on (terminal, series, sequence), so
    /// re-uploading the identical record finds the sale it already applied.
    ///
    /// The figures balance deliberately — one line at 10.00 with no tax, settled by a
    /// single cash tender of 10.00. Sale.Complete refuses an under-tendered sale, so an
    /// unbalanced fixture would fail as a domain rejection and prove nothing about the
    /// transport.
    /// </remarks>
    private SyncRecord NewSaleRecord(Guid terminalId, long sequence)
    {
        var recordId = Guid.CreateVersion7();
        var context = _terminals[terminalId];
        var now = DateTimeOffset.UtcNow;

        var payload = new SaleSyncPayload
        {
            // Derived from the terminal sequence, not random: a replayed record must
            // rebuild the SAME sale, or the stock movement would be attributed to a new
            // document and applied twice.
            SaleId = SaleIdFor(terminalId, sequence),

            CompanyId = context.CompanyId,
            BranchId = context.BranchId,
            TerminalId = terminalId,
            WarehouseId = context.WarehouseId,
            ShiftId = context.ShiftId,
            CashierId = context.CashierId,
            Currency = "USD",
            ReceiptSeries = "T01",
            ReceiptSequence = sequence,
            BusinessDate = DateOnly.FromDateTime(now.UtcDateTime),
            OpenedAt = now,
            CompletedAt = now,
            Lines =
            [
                new SaleSyncLine
                {
                    LineNumber = 1,
                    VariantId = Guid.CreateVersion7(),
                    Description = "Test item",
                    Quantity = 1m,
                    UnitOfMeasure = "EA",
                    UnitPrice = 10.00m,
                    TaxCode = "STD",
                    TaxRate = 0m,
                    Net = 10.00m,
                    Gross = 10.00m
                }
            ],
            Tenders =
            [
                // Card, not cash: a card tender exercises the electronic-payment
                // recording path, which is what most of the payment and reconciliation
                // assertions depend on. Card is permitted offline (no floor limit is
                // modelled yet), which is exactly the sync case.
                new SaleSyncTender
                {
                    Method = TenderMethod.Card,
                    Amount = 10.00m,
                    TakenAt = now,
                    Reference = $"AUTH-{recordId:N}"[..12]
                }
            ]
        };

        return new SyncRecord(
            recordId,
            sequence,
            SaleSyncHandler.Type,
            JsonSerializer.Serialize(payload, JsonOptions),
            now);
    }

    /// <summary>Rewrites a sale record's single tender to cash, for the cash-only path.</summary>
    /// <remarks>
    /// Deserialises the payload rather than tracking a parallel object, so the record it
    /// returns is exactly what was uploaded with only the tender changed.
    /// </remarks>
    public static SyncRecord WithCashTender(SyncRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var payload = JsonSerializer.Deserialize<SaleSyncPayload>(record.Payload, JsonOptions)!;

        var cashPayload = payload with
        {
            Tenders =
            [
                new SaleSyncTender
                {
                    Method = TenderMethod.Cash,
                    Amount = payload.Lines.Sum(l => l.Gross),
                    TakenAt = payload.CompletedAt
                }
            ]
        };

        return record with { Payload = JsonSerializer.Serialize(cashPayload, JsonOptions) };
    }

    private sealed record CreatedResource(Guid Id);

    private sealed record CreatedProduct(Guid Id, Guid VariantId);

    private sealed record ProvisionedTenant(Guid Id, Guid CompanyId, Guid BranchId, Guid WarehouseId);

    private sealed record SalesCount(int Count);

    private sealed record TerminalContext(
        Guid CompanyId,
        Guid BranchId,
        Guid WarehouseId,
        Guid ShiftId,
        Guid CashierId);

    /// <summary>
    /// A stable sale id for a (terminal, sequence) pair.
    /// </summary>
    /// <remarks>
    /// Stands in for the identity a real terminal assigns when it opens the sale
    /// (ADR 005). It has to be deterministic so that rebuilding a batch produces the
    /// same ids: that is what makes a replayed upload recognisable as the same sale
    /// rather than a new one, and it is what the stock idempotency guard keys on.
    /// </remarks>
    private static Guid SaleIdFor(Guid terminalId, long sequence)
    {
        // Folded rather than hashed. A hash would work too, but every general-purpose
        // one here is either flagged as cryptographically broken or is overkill for
        // deriving a test identifier: terminal ids are already unique per test, so
        // mixing the sequence into the low half is enough to be collision-free.
        Span<byte> bytes = stackalloc byte[16];
        terminalId.TryWriteBytes(bytes);

        Span<byte> sequenceBytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(sequenceBytes, sequence);

        for (var i = 0; i < sequenceBytes.Length; i++)
            bytes[8 + i] ^= sequenceBytes[i];

        return new Guid(bytes);
    }
}

/// <summary>Binds every integration test class to the one shared <see cref="ApiFixture"/>.</summary>
[CollectionDefinition(nameof(ApiCollection))]
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "'Collection' is xUnit's term for a test collection, not a data structure.")]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;
