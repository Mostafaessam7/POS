using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Common.Persistence;
using POS.Common.Tenancy;
using POS.Contracts.Fiscal;
using POS.Fiscal.Abstractions;
using POS.Fiscal.Generic;
using POS.Fiscal.Integration;
using POS.Fiscal.Jobs;
using POS.Fiscal.Persistence;
using POS.Fiscal.Pipeline;

namespace POS.Fiscal;

/// <summary>Fiscal's composition. The host calls this; it never reaches inside the module.</summary>
/// <remarks>
/// The GENERIC profile is registered here because it is the country-agnostic baseline
/// every deployment needs. Country plugins (ZATCA and friends) are registered by the
/// host, which is what makes them plugins: adding one must not require editing this
/// file, and the registry resolves by code rather than by branching on a country.
/// </remarks>
public static class FiscalModule
{
    public static IServiceCollection AddFiscalModule(
        this IServiceCollection services,
        string connectionString,
        FiscalJobOptions? jobs = null)
    {
        services.AddDbContext<FiscalDbContext>((provider, options) =>
            options.UsePosSqlServer<FiscalDbContext>(
                connectionString,
                FiscalDbContext.MigrationsHistoryTable,
                FiscalDbContext.Schema)
                .AddPosInterceptors(provider));

        services.AddScoped<IFiscalDocumentStore, EfFiscalDocumentStore>();
        services.AddScoped<IFiscalSequenceAllocator, EfFiscalSequenceAllocator>();

        services.AddScoped<IFiscalNumberingStrategy, TerminalSeriesNumberingStrategy>();
        services.AddScoped<IFiscalProfile, GenericFiscalProfile>();
        services.AddScoped<IFiscalProfileRegistry, FiscalProfileRegistry>();

        services.AddScoped<FiscalisationPipeline>();

        // The port other modules fiscalise through. Registered by Fiscal and consumed
        // via POS.Contracts, so nothing outside this module sees a FiscalContext.
        services.AddScoped<IFiscalisationPort, FiscalisationAdapter>();

        // The deadline monitor. Scoped worker, singleton timer-driven job.
        services.AddSingleton(jobs ?? new FiscalJobOptions());
        services.AddScoped<FiscalDeadlineMonitor>();
        services.AddHostedService<FiscalDeadlineMonitorJob>();

        return services;
    }
}

/// <inheritdoc cref="PosDesignTimeDbContextFactory{TContext}"/>
public sealed class FiscalDbContextFactory : PosDesignTimeDbContextFactory<FiscalDbContext>
{
    protected override string MigrationsHistoryTable => FiscalDbContext.MigrationsHistoryTable;

    protected override string Schema => FiscalDbContext.Schema;

    protected override FiscalDbContext Create(
        DbContextOptions<FiscalDbContext> options,
        ITenantContext tenantContext) => new(options, tenantContext);
}
