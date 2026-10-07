using System.Security.Claims;
using System.Text.Json;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using CoreIns.SharedKernel.StateMachines;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace CoreIns.Platform.Errors;

/// <summary>An expected business failure raised as an exception (where a <see cref="Result{T}"/> cannot be returned).</summary>
public sealed class DomainException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DomainException(DomainError error)
        : base(error?.ToString())
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    /// <summary>Creates the exception.</summary>
    public DomainException()
        : this(new DomainError(ErrorCode.For(ModuleCode.PLT, PlatformErrors.Internal)))
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public DomainException(string message)
        : this(new DomainError(ErrorCode.For(ModuleCode.PLT, PlatformErrors.Internal), message))
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
        Error = new DomainError(ErrorCode.For(ModuleCode.PLT, PlatformErrors.Internal), message);
    }

    /// <summary>The failure.</summary>
    public DomainError Error { get; }
}

/// <summary>REST conventions of the API paths (contract §3.5.1: <c>/api/&lt;mod&gt;/v&lt;major&gt;/…</c>).</summary>
public static class ApiRoutes
{
    /// <summary>The module of an API path, or PLT when the path names none.</summary>
    public static ModuleCode ModuleOf(PathString path)
    {
        var segments = (path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is ["api", var module, ..] && ModuleCodes.TryParse(module.ToUpperInvariant(), out var code) ? code : ModuleCode.PLT;
    }

    /// <summary>The stable <c>type</c> URI of an error code (RFC 9457 §3.1.1).</summary>
    public static Uri TypeUri(ErrorCode code) => new($"https://contracts.coreinsurance.example/errors/{code.Value}");
}

/// <summary>Chooses the response language: the user's profile claim, then <c>Accept-Language</c>, then Greek (R-101).</summary>
public static class LanguageResolver
{
    /// <summary>Claim carrying the user's saved language (PLT profile, R-101).</summary>
    public const string LanguageClaim = "ui_language";

    /// <summary>Resolves the language of a request.</summary>
    public static Language Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Languages.TryParse(context.User.FindFirstValue(LanguageClaim), out var profile))
        {
            return profile;
        }

        // Accept-Language entries "tag;q=0.8", best quality first (quality parsed as decimal: no floating point).
        var best = context.Request.Headers.AcceptLanguage
            .SelectMany(header => (header ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select((entry, order) => (Tag: entry.Split(';')[0].Trim(), Quality: QualityOf(entry), Order: order))
            .Where(candidate => candidate.Quality > 0m)
            .OrderByDescending(candidate => candidate.Quality)
            .ThenBy(candidate => candidate.Order);
        foreach (var candidate in best)
        {
            if (Languages.TryParse(candidate.Tag, out var language))
            {
                return language;
            }
        }

        return Language.El;
    }

    private static decimal QualityOf(string entry)
    {
        var parameter = entry.Split(';').Skip(1).Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("q=", StringComparison.OrdinalIgnoreCase));
        return parameter is null
            ? 1m
            : decimal.TryParse(parameter[2..], System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var q) ? q : 0m;
    }
}

/// <summary>
/// Builds RFC 9457 Problem Details (D-API-05): stable <c>type</c> URI, <c>code</c> (<c>&lt;MOD&gt;-ERR-…</c>), localized
/// <c>title</c>, <c>status</c>, <c>detail</c>, <c>traceId</c> (W3C trace), <c>errors[]</c> and <c>retryable</c>.
/// </summary>
public sealed class ProblemDetailsMapper(ErrorCatalog catalog)
{
    /// <summary>The problem for a domain error.</summary>
    public ProblemDetails Create(DomainError error, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(context);
        var definition = catalog.Find(error.Code);
        var language = context.RequestServices?.GetService<RequestContext>()?.Language ?? LanguageResolver.Resolve(context);
        var problem = new ProblemDetails
        {
            Type = ApiRoutes.TypeUri(error.Code).ToString(),
            Title = definition.Title.In(language),
            Status = definition.Status,
            Detail = error.Detail,
            Instance = context.Request.Path.Value,
        };
        problem.Extensions["code"] = error.Code.Value;
        problem.Extensions["traceId"] = TraceIdOf(context);
        problem.Extensions["retryable"] = definition.Retryable;
        if (error.FieldErrors.Count > 0)
        {
            problem.Extensions["errors"] = error.FieldErrors
                .Select(f => new Dictionary<string, string?>
                {
                    ["field"] = f.Field,
                    ["code"] = f.Code,
                    ["messageKey"] = f.MessageKey,
                    ["message"] = f.Message,
                })
                .ToList();
        }

        foreach (var (key, value) in error.Metadata)
        {
            problem.Extensions.TryAdd(key, value);
        }

        return problem;
    }

    /// <summary>Writes the problem as <c>application/problem+json</c>.</summary>
    public async Task WriteAsync(DomainError error, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var problem = Create(error, context);
        context.Response.StatusCode = problem.Status ?? StatusCodes.Status400BadRequest;
        context.Response.Headers[HeaderNames.CacheControl] = "no-store";
        await context.Response.WriteAsJsonAsync(problem, ProblemJson.Options, "application/problem+json").ConfigureAwait(false);
    }

    /// <summary>A minimal-API result for a domain error.</summary>
    public IResult ToResult(DomainError error, HttpContext context) => new ProblemResult(this, error);

    private static string TraceIdOf(HttpContext context) =>
        context.RequestServices?.GetService<RequestContext>()?.CorrelationId.Value
        ?? (System.Diagnostics.Activity.Current?.TraceId.ToHexString() ?? context.TraceIdentifier);

    private sealed class ProblemResult(ProblemDetailsMapper mapper, DomainError error) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => mapper.WriteAsync(error, httpContext);
    }
}

/// <summary>JSON settings of Problem Details responses.</summary>
internal static class ProblemJson
{
    public static JsonSerializerOptions Options { get; } = SharedKernelJson.Options;
}

/// <summary>Converts <see cref="Result{T}"/> to HTTP results.</summary>
public static class ResultHttpExtensions
{
    /// <summary>200 with the value, or the Problem Details of the error.</summary>
    public static IResult ToHttpResult<T>(this Result<T> result, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : context.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(result.Error, context);
    }
}

/// <summary>
/// Maps unhandled exceptions to Problem Details: <see cref="DomainException"/> and invalid state transitions keep their
/// code; FluentValidation failures become <c>&lt;MOD&gt;-ERR-VALIDATION</c>; anything else is a 500
/// <c>PLT-ERR-INTERNAL</c> without internal details.
/// </summary>
public sealed partial class CoreInsExceptionHandler(ProblemDetailsMapper mapper, ILogger<CoreInsExceptionHandler> logger) : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);
        var error = ToError(exception, ApiRoutes.ModuleOf(httpContext.Request.Path));
        if (error.Code.Name == PlatformErrors.Internal)
        {
            LogUnhandled(logger, exception, httpContext.Request.Path.Value ?? string.Empty);
        }

        await mapper.WriteAsync(error, httpContext).ConfigureAwait(false);
        return true;
    }

    /// <summary>The domain error an exception maps to.</summary>
    public static DomainError ToError(Exception exception, ModuleCode module) => exception switch
    {
        DomainException domain => domain.Error,
        InvalidStateTransitionException { Error: { } transition } => transition,
        FluentValidation.ValidationException validation => new DomainError(ErrorCode.For(module, PlatformErrors.Validation), "The request is not valid.")
        {
            FieldErrors = [.. validation.Errors.Select(e => new FieldError(e.PropertyName, e.ErrorCode, e.ErrorCode, e.ErrorMessage))],
        },
        BadHttpRequestException => new DomainError(ErrorCode.For(module, PlatformErrors.Validation), "The request could not be read."),
        _ => new DomainError(ErrorCode.For(ModuleCode.PLT, PlatformErrors.Internal)),
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string path);
}
