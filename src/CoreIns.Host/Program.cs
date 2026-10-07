using CoreIns.Host.Database;
using CoreIns.Host.Health;
using CoreIns.Host.Hosting;
using Hangfire;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
var role = AppRoles.Parse(builder.Configuration[AppRoles.ConfigurationKey]);

builder.AddCoreInsObservability(role);
builder.Services.AddCoreInsModules(builder.Configuration);

if (role == AppRole.Migrate)
{
    builder.Services.AddSingleton<DatabaseMigrator>();
    await using var migrateApp = builder.Build();
    var lifetime = migrateApp.Services.GetRequiredService<IHostApplicationLifetime>();
    return await migrateApp.Services.GetRequiredService<DatabaseMigrator>().RunAsync(lifetime.ApplicationStopping);
}

var connectionString = builder.Configuration.GetRequiredCoreConnectionString();
builder.Services.AddCoreInsDataSource(connectionString);
builder.Services.AddCoreInsHealthChecks();
builder.Services.AddCoreInsJobs(connectionString, role);

if (role == AppRole.Api)
{
    builder.Services.AddProblemDetails();
    builder.Services.AddControllers();
    builder.Services.AddOpenApi();
    builder.Services.AddCoreInsSecurity(builder.Configuration);
}

var app = builder.Build();

if (role == AppRole.Api)
{
    app.UseExceptionHandler();
    app.UseSerilogRequestLogging();

    // The built React app (web/dist, copied to wwwroot by the Dockerfile) is public; the API is not.
    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapCoreInsHealth();
    app.MapControllers();

    var openApi = app.MapOpenApi();
    if (app.Environment.IsDevelopment())
    {
        openApi.AllowAnonymous();
    }

    app.MapHangfireDashboardWithAuthorizationPolicy(AuthPolicies.Admin);

    app.MapFallback("/api/{**path}", () => Results.NotFound());
    app.MapFallbackToFile("index.html").AllowAnonymous();
}
else
{
    app.MapCoreInsHealth();
}

await app.RunAsync();
return 0;

/// <summary>Entry point; public so integration tests can boot the Host with <c>WebApplicationFactory</c>.</summary>
public partial class Program;
