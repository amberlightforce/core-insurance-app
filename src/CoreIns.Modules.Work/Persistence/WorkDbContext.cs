using CoreIns.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CoreIns.Modules.Work.Persistence;

/// <summary>
/// The Work module's EF Core context (schema <c>wrk</c>). Minimal until NX-HD-05 (tasks, queues, SLA) lands: it holds only the
/// DSAR restriction table, so the WRK participant can record restrictions once task data exists.
/// </summary>
internal sealed class WorkDbContext(DbContextOptions<WorkDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => WorkModule.Schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.MapDsarRestrictions();
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations</c>.</summary>
internal sealed class WorkDbContextDesignTimeFactory : IDesignTimeDbContextFactory<WorkDbContext>
{
    public WorkDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextRegistration.MigrationOptions<WorkDbContext>("Host=localhost;Database=coreins_design;Username=design", WorkModule.Schema));
}
