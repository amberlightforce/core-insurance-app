using System.Text.Json;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoreIns.Platform.Tests;

/// <summary>RFC 9457 Problem Details (D-API-01, D-API-05): type URI, code, localized title, traceId, errors[], retryable.</summary>
public sealed class ProblemDetailsTests
{
    private static DefaultHttpContext Http(string path, string? acceptLanguage = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        if (acceptLanguage is not null)
        {
            http.Request.Headers.AcceptLanguage = acceptLanguage;
        }

        http.Response.Body = new MemoryStream();
        return http;
    }

    [Fact]
    public void Problems_carry_code_type_status_localized_title_and_trace()
    {
        var mapper = new ProblemDetailsMapper(new ErrorCatalog());
        var error = DomainError.Of(ModuleCode.POL, PlatformErrors.IdempotencyMismatch, "Key reused.");

        var greek = mapper.Create(error, Http("/api/pol/v1/jobs"));
        var english = mapper.Create(error, Http("/api/pol/v1/jobs", "en-GB,el;q=0.5"));

        greek.Status.ShouldBe(409);
        greek.Type.ShouldBe("https://contracts.coreinsurance.example/errors/POL-ERR-IDEMPOTENCY-MISMATCH");
        greek.Title.ShouldBe("Το Idempotency-Key χρησιμοποιήθηκε ήδη με διαφορετικό αίτημα");
        greek.Detail.ShouldBe("Key reused.");
        greek.Extensions["code"].ShouldBe("POL-ERR-IDEMPOTENCY-MISMATCH");
        greek.Extensions["retryable"].ShouldBe(false);
        greek.Extensions.ShouldContainKey("traceId");
        english.Title.ShouldBe("The Idempotency-Key was already used with a different request");
    }

    [Theory]
    [InlineData("el-GR,en;q=0.8", "el")]
    [InlineData("en;q=0.9,el;q=0.4", "en")]
    [InlineData("de-DE,en;q=0.5", "en")]
    [InlineData("de-DE", "el")]
    [InlineData("en;q=0,el;q=0.1", "el")]
    public void Accept_language_picks_the_best_supported_language(string header, string expected) =>
        CoreIns.SharedKernel.Languages.ToTag(LanguageResolver.Resolve(Http("/", header))).ShouldBe(expected);

    [Fact]
    public void Unknown_codes_fall_back_to_a_generic_400_and_module_codes_can_be_registered()
    {
        var catalog = new ErrorCatalog([ErrorDefinition.For(ModuleCode.POL, "QUOTE-EXPIRED", 410, "Η προσφορά έληξε", "The quote expired")]);

        catalog.Find(ErrorCode.Parse("POL-ERR-QUOTE-EXPIRED")).Status.ShouldBe(410);
        catalog.Find(ErrorCode.Parse("BIL-ERR-QUOTE-EXPIRED")).ShouldBe(ErrorCatalog.Unknown);
        catalog.Find(ErrorCode.Parse("BIL-ERR-NOT-FOUND")).Status.ShouldBe(404);
    }

    [Fact]
    public void Validation_errors_list_their_fields()
    {
        var problem = new ProblemDetailsMapper(new ErrorCatalog()).Create(
            new DomainError(ErrorCode.Parse("BIL-ERR-VALIDATION")) { FieldErrors = [new FieldError("amount", "GreaterThan", "GreaterThan", "must be > 0")] },
            Http("/api/bil/v1/refunds"));

        problem.Status.ShouldBe(400);
        JsonSerializer.Serialize(problem.Extensions["errors"]).ShouldContain("\"field\":\"amount\"");
    }

    [Fact]
    public async Task Exceptions_map_to_problems_without_internal_details()
    {
        var handler = new CoreInsExceptionHandler(new ProblemDetailsMapper(new ErrorCatalog()), NullLogger<CoreInsExceptionHandler>.Instance);
        var http = Http("/api/pty/v1/parties", "en");

        (await handler.TryHandleAsync(http, new InvalidOperationException("secret connection string"), TestContext.Current.CancellationToken)).ShouldBeTrue();

        http.Response.StatusCode.ShouldBe(500);
        http.Response.ContentType.ShouldBe("application/problem+json");
        http.Response.Body.Position = 0;
        var body = await new StreamReader(http.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("PLT-ERR-INTERNAL");
        body.ShouldNotContain("secret");

        CoreInsExceptionHandler.ToError(new FluentValidation.ValidationException([new ValidationFailure("name", "required") { ErrorCode = "NotEmpty" }]), ModuleCode.PTY)
            .Code.Value.ShouldBe("PTY-ERR-VALIDATION");
        ApiRoutes.ModuleOf("/api/clm/v1/claims").ShouldBe(ModuleCode.CLM);
        ApiRoutes.ModuleOf("/health/ready").ShouldBe(ModuleCode.PLT);
    }
}
