using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Common.Persistence;
using POS.Common.Tenancy;
using POS.Contracts.Payments;
using POS.Payments.Abstractions;
using POS.Payments.Integration;
using POS.Payments.Jobs;
using POS.Payments.Orchestration;
using POS.Payments.Persistence;

namespace POS.Payments;

/// <summary>Payments' composition. The host calls this; it never reaches inside the module.</summary>
/// <remarks>
/// Providers are NOT registered here. A provider is a deployment decision — which
/// acquirer this estate uses — so the host adds them and the registry discovers
/// whatever was added. That is what keeps the seam a seam: adding a real acquirer must
/// not require editing this file.
/// </remarks>
public static class PaymentsModule
{
    public static IServiceCollection AddPaymentsModule(
        this IServiceCollection services,
        string connectionString,
        PaymentsJobOptions? jobs = null)
    {
        services.AddDbContext<PaymentsDbContext>((provider, options) =>
            options.UsePosSqlServer<PaymentsDbContext>(
                connectionString,
                PaymentsDbContext.MigrationsHistoryTable,
                PaymentsDbContext.Schema)
                .AddPosInterceptors(provider));

        services.AddScoped<IPaymentStore, EfPaymentStore>();
        services.AddScoped<PaymentOrchestrator>();

        // The port other modules record electronic tenders through. Registered by
        // Payments and consumed via POS.Contracts, so nothing outside this module sees
        // a Payment aggregate.
        services.AddScoped<IPaymentRecordingPort, PaymentRecordingAdapter>();

        // The indeterminate-payment sweep. The worker is scoped (it touches the
        // DbContext and orchestrator); the hosted job that drives it on a timer is a
        // singleton and creates a scope per tick.
        services.AddSingleton(jobs ?? new PaymentsJobOptions());
        services.AddScoped<IndeterminatePaymentSweeper>();
        services.AddHostedService<IndeterminatePaymentSweepJob>();

        return services;
    }
}

/// <inheritdoc cref="PosDesignTimeDbContextFactory{TContext}"/>
public sealed class PaymentsDbContextFactory : PosDesignTimeDbContextFactory<PaymentsDbContext>
{
    protected override string MigrationsHistoryTable => PaymentsDbContext.MigrationsHistoryTable;

    protected override string Schema => PaymentsDbContext.Schema;

    protected override PaymentsDbContext Create(
        DbContextOptions<PaymentsDbContext> options,
        ITenantContext tenantContext) => new(options, tenantContext);
}
