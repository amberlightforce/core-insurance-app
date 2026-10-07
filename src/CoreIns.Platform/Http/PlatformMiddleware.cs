using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using CoreIns.Platform.Configuration;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Idempotency;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Platform.Http;

/// <summary>Marks an endpoint whose non-GET method is not state-changing (e.g. a search by POST): no Idempotency-Key needed.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class SkipIdempotencyAttribute : Attribute;

/// <summary>Header names used by the platform.</summary>
public static class PlatformHeaders
{
    /// <summary>The caller's idempotency key (contract §3.5.3).</summary>
    public const string IdempotencyKey = "Idempotency-Key";

    /// <summary>Set on a response replayed from the idempotency store.</summary>
    public const string IdempotentReplayed = "Idempotent-Replayed";

    /// <summary>Dry-run header (contract §3.5.3).</summary>
    public const string DryRun = "X-Dry-Run";

    /// <summary>Channel code of the caller (CHN code list).</summary>
    public const string Channel = "X-Channel";
}

/// <summary>
/// Fills the scope's <see cref="RequestContext"/> from the HTTP request: actor and roles from the validated token,
/// language (profile claim, Accept-Language, Greek), trace id, stamp legal entity and jurisdiction, idempotency key,
/// dry-run flag, channel, and the configuration hash pinned for the request (REQ-MKT-051).
/// </summary>
public sealed class RequestContextMiddleware(RequestDelegate next)
{
    private const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    /// <summary>Runs the middleware.</summary>
    public async Task InvokeAsync(HttpContext http, RequestContext context, IOptions<StampOptions> stamp, IConfigurationResolver configuration)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stamp);
        ArgumentNullException.ThrowIfNull(configuration);
        var user = http.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var id = user.FindFirstValue(ObjectIdClaim) ?? user.FindFirstValue("oid") ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? user.FindFirstValue("sub") ?? user.Identity.Name ?? "unknown";
            var kind = string.Equals(user.FindFirstValue("idtyp"), "app", StringComparison.OrdinalIgnoreCase) ? ActorKind.Service : ActorKind.User;
            context.Actor = new ActorRef(kind, id);
            context.Roles = [.. user.FindAll(ClaimTypes.Role).Concat(user.FindAll("roles")).Select(c => c.Value).Distinct(StringComparer.Ordinal)];
        }

        context.Language = LanguageResolver.Resolve(http);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(context.Language == Language.En ? "en" : "el-GR");
        context.CorrelationId = RequestContext.CurrentTraceId();
        if (LegalEntityCode.TryParse(stamp.Value.LegalEntity, out var entity))
        {
            context.LegalEntity = entity;
        }

        if (Jurisdiction.TryParse(stamp.Value.Country, out var country))
        {
            context.Jurisdiction = country;
        }

        if (IdempotencyKey.TryParse(http.Request.Headers[PlatformHeaders.IdempotencyKey], out var key))
        {
            context.IdempotencyKey = key;
        }

        context.DryRun = IsTrue(http.Request.Query["dryRun"]) || IsTrue(http.Request.Headers[PlatformHeaders.DryRun]);
        context.Channel = http.Request.Headers[PlatformHeaders.Channel] is { Count: 1 } channel ? channel.ToString() : "STAFF";
        context.ConfigurationHash = await configuration.CurrentHashAsync(null, http.RequestAborted).ConfigureAwait(false);
        await next(http).ConfigureAwait(false);
    }

    private static bool IsTrue(Microsoft.Extensions.Primitives.StringValues value) =>
        value.Count == 1 && string.Equals(value[0], "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// HTTP idempotency (D-API-01, contract §3.5.3): every state-changing API request (POST, PUT, PATCH, DELETE under
/// <c>/api/</c>) must carry a UUID <c>Idempotency-Key</c>. The first request records the key with a hash of the canonical
/// request and stores its response (status below 500); a retry with the same request gets the stored response
/// (<c>Idempotent-Replayed: true</c>); a retry with a different request gets 409 <c>&lt;MOD&gt;-ERR-IDEMPOTENCY-MISMATCH</c>;
/// a retry while the first is still running gets 409 <c>&lt;MOD&gt;-ERR-IDEMPOTENCY-IN-PROGRESS</c> (retryable). Records
/// live 7 days. The command pipeline's own idempotency record (same key, in the business transaction) guarantees that
/// even a crash between commit and response storage never executes the command twice.
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next)
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    /// <summary>Runs the middleware.</summary>
    public async Task InvokeAsync(HttpContext http, NpgsqlDataSource dataSource, IClock clock, RequestContext context, ProblemDetailsMapper problems)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(problems);
        if (!IsStateChanging(http))
        {
            await next(http).ConfigureAwait(false);
            return;
        }

        var module = ApiRoutes.ModuleOf(http.Request.Path);
        var header = http.Request.Headers[PlatformHeaders.IdempotencyKey];
        if (header.Count == 0 || string.IsNullOrWhiteSpace(header[0]))
        {
            await problems.WriteAsync(Error(module, PlatformErrors.IdempotencyKeyRequired, "State-changing requests need an Idempotency-Key header."), http)
                .ConfigureAwait(false);
            return;
        }

        if (header.Count > 1 || !IdempotencyKey.TryParse(header[0], out var key))
        {
            await problems.WriteAsync(Error(module, PlatformErrors.IdempotencyKeyInvalid, "The Idempotency-Key must be one UUID."), http).ConfigureAwait(false);
            return;
        }

        var scope = $"http:{context.Actor}:{http.Request.Method} {http.Request.Path}";
        var hash = await RequestHashAsync(http).ConfigureAwait(false);
        var now = clock.Now;
        await using (var connection = await dataSource.OpenConnectionAsync(http.RequestAborted).ConfigureAwait(false))
        {
            if (!await IdempotencyStore.TryBeginAsync(connection, null, scope, key, hash, now, http.RequestAborted).ConfigureAwait(false))
            {
                var existing = await IdempotencyStore.FindAsync(connection, null, scope, key, now, http.RequestAborted).ConfigureAwait(false);
                if (existing is not null && !string.Equals(existing.RequestHash, hash.Value, StringComparison.Ordinal))
                {
                    await problems.WriteAsync(
                        Error(module, PlatformErrors.IdempotencyMismatch, "This Idempotency-Key was used with a different request."), http).ConfigureAwait(false);
                    return;
                }

                if (existing is { Completed: true, ResponseStatus: { } status })
                {
                    await ReplayAsync(http, status, existing.ContentType, existing.Body).ConfigureAwait(false);
                    return;
                }

                if (!await IdempotencyStore.TakeOverStaleAsync(connection, scope, key, hash, now.Minus(StaleAfter), now, http.RequestAborted)
                        .ConfigureAwait(false))
                {
                    await problems.WriteAsync(
                        Error(module, PlatformErrors.IdempotencyInProgress, "The original request with this key is still in progress."), http).ConfigureAwait(false);
                    return;
                }
            }
        }

        context.IdempotencyKey = key;
        await ExecuteAndStoreAsync(http, dataSource, clock, scope, key).ConfigureAwait(false);
    }

    private async Task ExecuteAndStoreAsync(HttpContext http, NpgsqlDataSource dataSource, IClock clock, string scope, IdempotencyKey key)
    {
        var original = http.Response.Body;
        await using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        var stored = false;
        try
        {
            await next(http).ConfigureAwait(false);
            buffer.Position = 0;
            await buffer.CopyToAsync(original, http.RequestAborted).ConfigureAwait(false);
            if (http.Response.StatusCode < 500)
            {
                await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None).ConfigureAwait(false);
                await IdempotencyStore.CompleteAsync(
                    connection, null, scope, key, http.Response.StatusCode, http.Response.ContentType, buffer.ToArray(), clock.Now, CancellationToken.None)
                    .ConfigureAwait(false);
                stored = true;
            }
        }
        finally
        {
            http.Response.Body = original;
            if (!stored)
            {
                await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None).ConfigureAwait(false);
                await IdempotencyStore.ReleaseAsync(connection, scope, key, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task ReplayAsync(HttpContext http, int status, string? contentType, byte[]? body)
    {
        http.Response.StatusCode = status;
        http.Response.Headers[PlatformHeaders.IdempotentReplayed] = "true";
        if (contentType is not null)
        {
            http.Response.ContentType = contentType;
        }

        if (body is { Length: > 0 })
        {
            await http.Response.Body.WriteAsync(body, http.RequestAborted).ConfigureAwait(false);
        }
    }

    private static bool IsStateChanging(HttpContext http) =>
        (HttpMethods.IsPost(http.Request.Method) || HttpMethods.IsPut(http.Request.Method)
         || HttpMethods.IsPatch(http.Request.Method) || HttpMethods.IsDelete(http.Request.Method))
        && http.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        && http.GetEndpoint()?.Metadata.GetMetadata<SkipIdempotencyAttribute>() is null;

    /// <summary>SHA-256 over the canonical request: method, path, query and body (canonical JSON when the body is JSON).</summary>
    private static async Task<Sha256Hash> RequestHashAsync(HttpContext http)
    {
        http.Request.EnableBuffering();
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var body = await reader.ReadToEndAsync(http.RequestAborted).ConfigureAwait(false);
        http.Request.Body.Position = 0;

        JsonNode? canonicalBody;
        try
        {
            canonicalBody = body.Length == 0 ? null : JsonNode.Parse(CanonicalJson.Canonicalize(body));
        }
        catch (Exception ex) when (ex is CanonicalJsonException or System.Text.Json.JsonException)
        {
            canonicalBody = JsonValue.Create(Convert.ToBase64String(Encoding.UTF8.GetBytes(body)));
        }

        var document = new JsonObject
        {
            ["method"] = http.Request.Method.ToUpperInvariant(),
            ["path"] = http.Request.Path.Value,
            ["query"] = http.Request.QueryString.Value,
            ["body"] = canonicalBody,
        };
        return CanonicalJson.Hash(document);
    }

    private static DomainError Error(ModuleCode module, string name, string detail) => new(ErrorCode.For(module, name), detail);
}

/// <summary>Pipeline registration of the platform middleware.</summary>
public static class PlatformApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the request context and idempotency middleware. Call after authentication and authorisation, so the actor
    /// is known and unauthenticated requests are refused before an idempotency record is written.
    /// </summary>
    public static IApplicationBuilder UseCoreInsPlatform(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<RequestContextMiddleware>().UseMiddleware<IdempotencyMiddleware>();
    }
}
