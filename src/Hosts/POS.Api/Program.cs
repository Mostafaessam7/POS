using System.Threading.RateLimiting;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using POS.Api.Endpoints;
using POS.Catalog;
using POS.Common;
using POS.Common.Persistence;
using POS.Common.Tenancy;
using POS.Expenses;
using POS.Fiscal;
using POS.Identity;
using POS.Identity.Authentication;
using POS.Inventory;
using POS.Payments;
using POS.Payments.Abstractions;
using POS.Payments.Manual;
using POS.Purchasing;
using POS.Sales;
using POS.Sync;
using Scalar.AspNetCore;
using Serilog;

// THE COMPOSITION ROOT.
//
// This file is the only place in the solution that knows every module exists. Each
// module exposes exactly one Add*Module extension and nothing else; the host never
// reaches past it. That is what keeps "modular monolith" true rather than aspirational
// — a module can restructure its internals without this file being edited.
//
// Order matters in two places and nowhere else:
//   - AddPosCommon before any module, because module contexts resolve the interceptors
//     and tenant context it registers.
//   - In the pipeline, UseAuthentication -> UseAuthorization -> UseTenantResolution.
//     Tenant comes from a validated claim, so authentication must have run; see below.

var builder = WebApplication.CreateBuilder(args);

// Serilog replaces the default logger before anything else so that a failure during
// the rest of startup is still recorded in the same format as everything else.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

var connectionString = builder.Configuration.GetConnectionString(PosSqlServer.ConnectionStringName)
    ?? throw new InvalidOperationException(
        $"ConnectionStrings:{PosSqlServer.ConnectionStringName} is not configured. " +
        "Set it in appsettings.json or the ConnectionStrings__Pos environment variable.");

var tokenOptions = new TokenOptions
{
    Issuer = builder.Configuration["Identity:Issuer"] ?? "https://api.pos.example",
    Audience = builder.Configuration["Identity:Audience"] ?? "pos-terminals",
    SigningKey = IdentityModule.ReadSigningKey(builder.Configuration["Identity:SigningKey"])
};

builder.Services.AddPosCommon();

// Redis is a deployment decision like the SQL connection string, not a hardcoded
// dependency: absent, IdentityModule falls back to an in-process distributed-cache
// stand-in so every environment (including this host's own integration tests)
// exercises the same IDistributedCache code path in CachedPermissionResolver.
builder.Services.AddIdentityModule(
    connectionString,
    tokenOptions,
    builder.Configuration.GetConnectionString("Redis"));
builder.Services.AddCatalogModule(connectionString);
builder.Services.AddInventoryModule(
    connectionString,
    builder.Configuration.GetSection(InventoryPolicyOptions.SectionName).Get<InventoryPolicyOptions>());
builder.Services.AddSalesModule(connectionString);
builder.Services.AddPaymentsModule(connectionString);
builder.Services.AddFiscalModule(connectionString);
builder.Services.AddPurchasingModule(
    connectionString,
    builder.Configuration.GetSection(PurchasingPolicyOptions.SectionName).Get<PurchasingPolicyOptions>());

builder.Services.AddExpensesModule(connectionString);

// Validators are discovered from this assembly, where the request contracts live.
// ValidationEndpointFilter resolves them per endpoint via AddValidation<T>().
builder.Services.AddValidatorsFromAssemblyContaining<RecordExpenseRequestValidator>();

// Enums cross the wire as NAMES, not ordinals. A client reading {"status":2} has to
// keep a private copy of the enum in step with ours, and the day somebody inserts a
// value into the middle of PurchaseOrderStatus every stored integer silently changes
// meaning. Reading still accepts numbers, so existing callers are not broken.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSyncModule(connectionString);

// Payment providers are a DEPLOYMENT decision — which acquirer this estate uses — so
// they are registered by the host and discovered by the registry, never by the module.
// ManualCardProvider is the fallback every estate needs: an imprint-and-phone
// authorisation when the line is down. Its prompt is a terminal-UI concern and has no
// server-side implementation, so it is not registered here; the API composes payments
// but does not take them.
builder.Services.AddScoped<IPaymentProviderRegistry, PaymentProviderRegistry>();

builder.Services.AddPosAuthentication(tokenOptions);

// EDGE HARDENING. None of this existed before — the API had no rate limiting, no
// CORS policy, and no transport-security posture, which was fine while nothing
// spoke to it but a test harness and was not fine the moment it faces the internet.
//
// Rate limiting is a coarse circuit breaker, not the primary defence against
// enumeration or brute force — that job belongs to the credential itself (a JWT, an
// unguessable per-operator key). The GLOBAL limiter exists so one runaway client
// (a retried loop, a misbehaving terminal, a scripted attacker) cannot starve every
// other tenant's terminals of the same process's throughput. Health checks are
// exempted below (see HealthEndpoints) because an orchestrator polls them far more
// often than any real client calls anything else, and throttling a liveness probe
// turns a load spike into a false-positive restart storm.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Partitioned per client IP so one noisy caller cannot exhaust the budget for
    // everyone sharing the process — falls back to a single shared bucket only when
    // the IP is genuinely unknown (a proxy stripped it), which is safer than the
    // alternative of not limiting that traffic at all.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 3000,
                Window = TimeSpan.FromSeconds(10),
                QueueLimit = 50,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));

    // Provisioning bootstraps a tenant and mints/revokes operator credentials before
    // any tenant-scoped auth exists to gate it — the most sensitive ANONYMOUS surface
    // in this API (see ProvisioningEndpoints). It gets its own, separate policy rather
    // than relying on the global limiter alone, so tightening it later never requires
    // touching the limiter every other endpoint shares.
    options.AddPolicy("provisioning", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 300,
            Window = TimeSpan.FromSeconds(10),
            QueueLimit = 20,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        }));

    // Login is the other anonymous, enumeration-sensitive surface: a stricter,
    // per-IP policy than the global default, separate from "provisioning" so the two
    // can be tuned independently (a brute-force password guesser and a scripted
    // tenant-bootstrap abuser are different threat models with different acceptable
    // rates).
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 100,
            Window = TimeSpan.FromSeconds(10),
            QueueLimit = 10,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        }));
});

// CORS defaults to allowing NOBODY — refuse closed, the same stance
// RequireOperatorApiKeyFilter takes toward an unconfigured secret. There is no
// browser-based client shipped yet (no terminal or back-office UI exists), so the
// safe default is an empty allow-list; a future UI's origin must be added
// explicitly via Cors:AllowedOrigins, never inferred or wildcarded.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options =>
    options.AddPolicy("Default", policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            // AllowCredentials is required for the browser to send/receive the
            // httpOnly refresh-token cookie cross-origin; it is only permitted
            // alongside an explicit origin list (never AllowAnyOrigin), which this
            // policy already uses.
            policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        }
    }));

// The schema itself (AddOpenApi/MapOpenApi) is always registered — it costs nothing
// to generate and other tooling (a future admin UI's client generator, contract
// tests) can consume /openapi/v1.json regardless of whether the interactive Scalar
// page is exposed. Scalar is gated the same way Provisioning is: on by default only
// in Development, otherwise an explicit opt-in, because a schema browser is
// reconnaissance information an operator should choose to expose, not something
// that ships open by accident.
builder.Services.AddOpenApi();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("POS.Api"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());

var app = builder.Build();

app.UseExceptionHandler();
app.UseSerilogRequestLogging();

// HSTS is a production-only concern (a local dev loop over plain HTTP is normal and
// HSTS actively fights it), but redirection to HTTPS is unconditional — the
// middleware itself no-ops harmlessly when no HTTPS port is known (e.g. under the
// integration suite's in-memory TestServer), so there is no environment where
// enabling it costs anything.
if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseHttpsRedirection();

app.UseRateLimiter();
app.UseCors("Default");

app.UseAuthentication();
app.UseAuthorization();

// AFTER authentication, because the tenant is read from a validated claim and never
// from a header, route value or subdomain — all three are caller-supplied. See ADR 006.
app.UseMiddleware<TenantResolutionMiddleware>();

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Api:EnableOpenApiDocs"))
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

app.MapHealthEndpoints();
app.MapIdentityEndpoints();
app.MapOrganizationEndpoints();
app.MapCatalogEndpoints();
app.MapInventoryEndpoints();
app.MapPurchasingEndpoints();
app.MapExpensesEndpoints();
app.MapReconciliationEndpoints();
app.MapSyncEndpoints();
app.MapSettingsEndpoints();
app.MapSalesRegisterEndpoints();
app.MapUserManagementEndpoints();

// Provisioning has no permission model yet — see ProvisioningEndpoints — so it is
// opt-in and off by default. Enabling it in production is a deliberate configuration
// act, not an accident of deployment.
app.MapProvisioningEndpoints(
    enabled: app.Environment.IsDevelopment()
             || app.Configuration.GetValue<bool>("Provisioning:Enabled"));

await app.RunAsync();

/// <summary>
/// Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can reference the entry
/// point from the integration test project, which top-level statements otherwise
/// make internal.
/// </summary>
public partial class Program;
