namespace CoreIns.Host.Hosting;

/// <summary>The mode the single image runs in, selected by the <c>APP_ROLE</c> environment variable.</summary>
internal enum AppRole
{
    /// <summary>REST APIs, OpenAPI, static React app and the Hangfire dashboard.</summary>
    Api,

    /// <summary>Hangfire server, outbox dispatcher and integration adapters. No public endpoints except health.</summary>
    Worker,

    /// <summary>Applies database migrations, then exits.</summary>
    Migrate,
}

internal static class AppRoles
{
    /// <summary>Configuration key (environment variable) that selects the mode.</summary>
    public const string ConfigurationKey = "APP_ROLE";

    /// <summary>Parses the configured role. An unset value means <see cref="AppRole.Api"/> (local tooling); an unknown value fails fast.</summary>
    public static AppRole Parse(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        null or "" or "API" => AppRole.Api,
        "WORKER" => AppRole.Worker,
        "MIGRATE" => AppRole.Migrate,
        _ => throw new InvalidOperationException(
            $"Unknown {ConfigurationKey} '{value}'. Expected one of: api, worker, migrate."),
    };

    /// <summary>The lower-case name used in configuration, logs and telemetry.</summary>
    public static string ToConfigValue(this AppRole role) => role switch
    {
        AppRole.Api => "api",
        AppRole.Worker => "worker",
        AppRole.Migrate => "migrate",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };
}
