using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace CoreIns.Host.Hosting;

/// <summary>Configuration of the HTTP hardening (section <c>HttpHardening</c>). Strict defaults in Production, generous elsewhere.</summary>
internal sealed class HttpHardeningOptions
{
    public const string Section = "HttpHardening";

    /// <summary>Sign-in attempts per client IP per minute. Null: 10 in Production, 100000 elsewhere (E2E suites sign in a lot).</summary>
    public int? SignInPermitsPerMinute { get; set; }

    /// <summary>Write requests (POST/PUT/PATCH/DELETE) per user (or IP when anonymous) per minute. Null: 600 in Production, 1000000 elsewhere.</summary>
    public int? WritePermitsPerMinute { get; set; }

    /// <summary>Largest accepted request body in bytes (default 25 MiB, enough for document uploads).</summary>
    public long MaxRequestBodyBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>Browser origins allowed to call the API cross-origin. Empty (default): same-origin only, no CORS headers.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>Strict-Transport-Security max-age in seconds (Production only).</summary>
    public int HstsMaxAgeSeconds { get; set; } = 31536000;

    /// <summary>Overrides the Content-Security-Policy header when set.</summary>
    public string? ContentSecurityPolicy { get; set; }
}

/// <summary>HSTS, CSP, nosniff, an explicit CORS policy, rate limiting and request body limits (NX-OT-05).</summary>
internal static class HttpHardeningExtensions
{
    internal const string CorsPolicy = "CoreInsCors";
    internal const string DevSignInPath = "/api/plt/v1/dev/sign-in";

    /// <summary>
    /// The SPA is same-origin: its own scripts, styles (React sets inline style via CSSOM, hence 'unsafe-inline' for styles only),
    /// self-hosted fonts (data: for inlined assets) and same-origin API calls. Nothing else may load, frame or submit.
    /// </summary>
    internal const string DefaultContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; "
        + "font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public static IServiceCollection AddCoreInsHttpHardening(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var options = new HttpHardeningOptions();
        configuration.GetSection(HttpHardeningOptions.Section).Bind(options);
        var production = environment.IsProduction();

        services.Configure<KestrelServerOptions>(kestrel => kestrel.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes);
        services.Configure<FormOptions>(form => form.MultipartBodyLengthLimit = options.MaxRequestBodyBytes);

        services.AddCors(cors => cors.AddPolicy(CorsPolicy, policy =>
        {
            var origins = options.AllowedOrigins.Where(o => !string.IsNullOrWhiteSpace(o) && o != "*").ToArray();
            if (origins.Length > 0)
            {
                policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders(CoreIns.Platform.Http.PlatformHeaders.CorrelationId);
            }
        }));

        var signIn = options.SignInPermitsPerMinute ?? (production ? 10 : 100_000);
        var writes = options.WritePermitsPerMinute ?? (production ? 600 : 1_000_000);
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                // Every window is one minute; a precise value would need floating point.
                context.HttpContext.Response.Headers.RetryAfter = "60";

                return ValueTask.CompletedTask;
            };

            // TODO(hardening): behind a reverse proxy configure ForwardedHeaders so RemoteIpAddress is the client, not the proxy.
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => Partition(context, signIn, writes));
        });

        return services;
    }

    internal static RateLimitPartition<string> Partition(HttpContext context, int signInPermits, int writePermits)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var method = context.Request.Method;
        if (HttpMethods.IsPost(method) && context.Request.Path.StartsWithSegments(DevSignInPath, StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetFixedWindowLimiter("signin:" + ip, _ => Window(signInPermits));
        }

        if (HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method))
        {
            var who = context.User.Identity?.IsAuthenticated == true
                ? context.User.FindFirst("sub")?.Value ?? context.User.Identity.Name ?? ip
                : ip;
            return RateLimitPartition.GetFixedWindowLimiter("write:" + who, _ => Window(writePermits));
        }

        return RateLimitPartition.GetNoLimiter("none");
    }

    private static FixedWindowRateLimiterOptions Window(int permits) => new()
    {
        PermitLimit = Math.Max(1, permits),
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    };

    /// <summary>Security response headers; the Hangfire dashboard keeps its inline scripts and skips the CSP.</summary>
    public static IApplicationBuilder UseCoreInsSecurityHeaders(this IApplicationBuilder app, IHostEnvironment environment, HttpHardeningOptions options)
    {
        var csp = string.IsNullOrWhiteSpace(options.ContentSecurityPolicy) ? DefaultContentSecurityPolicy : options.ContentSecurityPolicy;
        var hsts = environment.IsProduction()
            ? "max-age=" + options.HstsMaxAgeSeconds.ToString(CultureInfo.InvariantCulture) + "; includeSubDomains"
            : null;
        return app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                if (hsts is not null)
                {
                    headers["Strict-Transport-Security"] = hsts;
                }

                if (!context.Request.Path.StartsWithSegments("/hangfire", StringComparison.OrdinalIgnoreCase))
                {
                    headers["Content-Security-Policy"] = csp;
                }

                return Task.CompletedTask;
            });
            return next();
        });
    }
}
