using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Common.Persistence;
using POS.Common.Tenancy;
using POS.Expenses.Persistence;

namespace POS.Expenses;

/// <summary>Expenses' composition. The host calls this; it never reaches inside the module.</summary>
public static class ExpensesModule
{
    public static IServiceCollection AddExpensesModule(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<ExpensesDbContext>((provider, options) =>
            options.UsePosSqlServer<ExpensesDbContext>(
                connectionString,
                ExpensesDbContext.MigrationsHistoryTable,
                ExpensesDbContext.Schema)
                .AddPosInterceptors(provider));

        return services;
    }
}

/// <inheritdoc cref="PosDesignTimeDbContextFactory{TContext}"/>
public sealed class ExpensesDbContextFactory : PosDesignTimeDbContextFactory<ExpensesDbContext>
{
    protected override string MigrationsHistoryTable => ExpensesDbContext.MigrationsHistoryTable;

    protected override string Schema => ExpensesDbContext.Schema;

    protected override ExpensesDbContext Create(
        DbContextOptions<ExpensesDbContext> options,
        ITenantContext tenantContext) => new(options, tenantContext);
}
