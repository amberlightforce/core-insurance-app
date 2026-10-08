using CoreIns.Host.Database;
using CoreIns.Host.Health;
using CoreIns.Host.Hosting;
using CoreIns.Platform;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using Hangfire;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
var role = AppRoles.Parse(builder.Configuration[AppRoles.ConfigurationKey]);

// D-SLC-03: the Development-only local sign-in; the Host refuses to start if the flag is set in any other environment.
var devSignIn = DevelopmentAuthentication.Guard(builder.Configuration, builder.Environment);

builder.AddCoreInsObservability(role);
builder.Services.AddCoreInsModules(builder.Configuration);

if (role == AppRole.Migrate)
{
    builder.Services.AddSingleton<DatabaseBootstrapper>();
    builder.Services.AddSingleton<DatabaseMigrator>();

    // The migrate role resolves only the bootstrapper and the migrator; module services (which need the api/worker data
    // source and data protection) are registered but never built, so Development's build-time DI validation is skipped here.
    builder.Host.UseDefaultServiceProvider(options => options.ValidateOnBuild = false);
    await using var migrateApp = builder.Build();
    var stopping = migrateApp.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

    // Bootstrap phase (server administrator credential): roles, privileges, extensions. Then exit.
    if (builder.Configuration.GetValue<bool>(DatabaseBootstrapper.BootstrapKey))
    {
        return await migrateApp.Services.GetRequiredService<DatabaseBootstrapper>().RunAsync(stopping);
    }

    return await migrateApp.Services.GetRequiredService<DatabaseMigrator>().RunAsync(stopping);
}

var connectionString = builder.Configuration.GetRequiredCoreConnectionString();
builder.Services.AddCoreInsDataSource(connectionString);
builder.Services.AddCoreInsHealthChecks();
builder.Services.AddCoreInsJobs(connectionString, role);
builder.Services.AddCountryPacks(builder.Configuration, builder.Environment);
builder.Services.AddCoreInsDataProtection(builder.Configuration, builder.Environment);

if (role == AppRole.Worker)
{
    // Outbox dispatcher: in-process event handlers run in the worker only (D-ARC-02, INFRASTRUCTURE §1).
    builder.Services.AddOutboxDispatcher();
}

if (role == AppRole.Api)
{
    builder.Services.AddProblemDetails();
    builder.Services.AddControllers().AddModuleControllers(ModuleCatalog.ApiAssemblies);
    builder.Services.AddOpenApi();
    builder.Services.AddCoreInsSecurity(builder.Configuration, devSignIn);
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

    // Request context (actor, language, trace, stamp, configuration hash) and HTTP idempotency (D-API-01).
    app.UseCoreInsPlatform();

    app.MapCoreInsHealth();
    app.MapCoreInsProblemPages();
    app.MapControllers();
    if (devSignIn)
    {
        app.MapDevelopmentSignIn();
    }

    var openApi = app.MapOpenApi();
    if (app.Environment.IsDevelopment())
    {
        openApi.AllowAnonymous();
    }

    app.MapHangfireDashboardWithAuthorizationPolicy(AuthPolicies.Admin);

    // A request with a file extension matches no endpoint and would meet the authenticated-user fallback policy: the
    // browser's /favicon.ico is public like the rest of the static app.
    app.MapGet("/favicon.ico", () => Results.Redirect("/favicon.svg", permanent: false)).AllowAnonymous();

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
