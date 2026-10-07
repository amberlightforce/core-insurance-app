using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using CoreIns.Platform;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CoreIns.IntegrationTests.Platform;

/// <summary>Idempotency at the HTTP boundary and in the command pipeline (contract §3.5.3, D-API-01, ADR §2 rule 6).</summary>
public sealed class IdempotencyTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private const string Path = "/api/wrk/v1/widgets";

    [Fact]
    public async Task A_command_retry_with_the_same_key_returns_the_stored_result_without_executing()
    {
        await using var harness = await PlatformHarness.CreateAsync(database);
        var key = IdempotencyKey.New();

        var first = await harness.SendAsync(new CreateWidget("once", 5m), key);
        var retry = await harness.SendAsync(new CreateWidget("once", 5m), key);
        var mismatch = await harness.SendAsync(new CreateWidget("different", 5m), key);
        var otherActor = await harness.SendAsync(new CreateWidget("once", 5m), key, actor: "user-2");

        retry.Value.ShouldBe(first.Value);
        harness.Log.Executions.Count(e => e == "once").ShouldBe(2, "the retry did not execute; another actor's key is a separate scope");
        mismatch.Error!.Code.Value.ShouldBe("WRK-ERR-IDEMPOTENCY-MISMATCH");
        otherActor.IsSuccess.ShouldBeTrue();
        (await harness.CountAsync("SELECT count(*) FROM tst.widget WHERE name = 'once'")).ShouldBe(2);
    }

    [Fact]
    public async Task A_command_without_a_key_is_refused_and_a_failed_command_releases_its_key()
    {
        await using var harness = await PlatformHarness.CreateAsync(database);
        var key = IdempotencyKey.New();

        (await harness.SendAsync(new CreateWidget("refused-once", 1m, Fail: "result"), key)).IsFailure.ShouldBeTrue();
        (await harness.SendAsync(new CreateWidget("refused-once", 1m), key)).IsSuccess.ShouldBeTrue("a failed attempt is not stored");

        await using var scope = harness.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CoreIns.Platform.Context.RequestContext>();
        context.ConfigurationHash = PlatformHarness.Hash;
        var noKey = await scope.ServiceProvider.GetRequiredService<ICommandHandler<CreateWidget, WidgetCreatedPayload>>()
            .HandleAsync(new CreateWidget("no-key", 1m), TestContext.Current.CancellationToken);
        noKey.Error!.Code.Value.ShouldBe("WRK-ERR-IDEMPOTENCY-KEY-REQUIRED");
    }

    [Fact]
    public async Task An_http_retry_replays_the_stored_response()
    {
        await using var api = await TestApi.StartAsync(database);
        var key = Guid.NewGuid().ToString();

        using var first = await api.PostAsync(key, new CreateWidget("http-once", 9.5m));
        using var retry = await api.PostAsync(key, new CreateWidget("http-once", 9.5m));

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        retry.Headers.GetValues(PlatformHeaders.IdempotentReplayed).ShouldBe(["true"]);
        (await retry.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldBe(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        api.Log.Executions.Count(e => e == "http-once").ShouldBe(1);
    }

    [Fact]
    public async Task An_http_retry_with_a_different_body_is_a_localized_409_problem()
    {
        await using var api = await TestApi.StartAsync(database);
        var key = Guid.NewGuid().ToString();
        using var first = await api.PostAsync(key, new CreateWidget("body-a", 1m));

        using var mismatch = await api.PostAsync(key, new CreateWidget("body-b", 1m), language: "en");

        mismatch.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        mismatch.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        using var problem = JsonDocument.Parse(await mismatch.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        problem.RootElement.GetProperty("code").GetString().ShouldBe("WRK-ERR-IDEMPOTENCY-MISMATCH");
        problem.RootElement.GetProperty("title").GetString().ShouldBe("The Idempotency-Key was already used with a different request");
        problem.RootElement.GetProperty("type").GetString().ShouldBe("https://contracts.coreinsurance.example/errors/WRK-ERR-IDEMPOTENCY-MISMATCH");
        problem.RootElement.GetProperty("traceId").GetString()!.Length.ShouldBe(32);
        api.Log.Executions.ShouldNotContain("body-b");
    }

    [Theory]
    [InlineData(null, "WRK-ERR-IDEMPOTENCY-KEY-REQUIRED")]
    [InlineData("not-a-uuid", "WRK-ERR-IDEMPOTENCY-KEY-INVALID")]
    public async Task State_changing_requests_need_a_uuid_key(string? key, string code)
    {
        await using var api = await TestApi.StartAsync(database);

        using var response = await api.PostAsync(key, new CreateWidget("keyless", 1m));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain(code);
        api.Log.Executions.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_server_error_releases_the_key_so_the_retry_executes()
    {
        await using var api = await TestApi.StartAsync(database);
        var key = Guid.NewGuid().ToString();

        using var failed = await api.PostAsync(key, new CreateWidget("unlucky", 1m, Fail: "throw"));
        using var retried = await api.PostAsync(key, new CreateWidget("unlucky", 1m, Fail: "throw"));

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await failed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("PLT-ERR-INTERNAL");
        retried.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        api.Log.Executions.Count(e => e == "unlucky").ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_duplicates_execute_once()
    {
        await using var api = await TestApi.StartAsync(database);
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => api.PostAsync(key, new CreateWidget("race", 3m))));

        api.Log.Executions.Count(e => e == "race").ShouldBe(1);
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBeGreaterThanOrEqualTo(1);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    /// <summary>A minimal API on a test server: platform middleware plus one state-changing endpoint.</summary>
    private sealed class TestApi : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;

        private TestApi(WebApplication app)
        {
            _app = app;
            _client = app.GetTestClient();
        }

        public ExecutionLog Log => _app.Services.GetRequiredService<ExecutionLog>();

        public static async Task<TestApi> StartAsync(PostgresFixture database)
        {
            await database.ExecuteAsMigratorAsync(WidgetDbContext.Ddl, CancellationToken.None);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Stamp:LegalEntity"] = "GR-TEST", ["Stamp:Country"] = "GR" });
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IClock>(new ManualClock(Instant.Parse("2026-10-07T08:00:00Z")));
            builder.Services.AddSingleton<ExecutionLog>();
            builder.Services.AddPlatformDataSource(database.AppConnectionString);
            builder.Services.AddPlatformModule(builder.Configuration);
            builder.Services.AddProblemDetails();
            builder.Services.AddModuleDbContext<WidgetDbContext>(WidgetDbContext.SchemaName);
            builder.Services.AddCommand<CreateWidget, WidgetCreatedPayload, CreateWidgetHandler>(
                new CommandDescriptor(OperationName.Parse("wrk.Widget.create"), ModuleCode.WRK));

            var app = builder.Build();
            app.UseExceptionHandler();
            app.Use((http, next) =>
            {
                // Stands in for Entra authentication: the X-User header becomes the principal.
                if (http.Request.Headers["X-User"] is { Count: 1 } user)
                {
                    http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", user.ToString()), new Claim("roles", "Tester")], "test"));
                }

                return next(http);
            });
            app.UseCoreInsPlatform();
            app.MapPost(Path, async (CreateWidget command, ICommandHandler<CreateWidget, WidgetCreatedPayload> handler, HttpContext http) =>
                (await handler.HandleAsync(command, http.RequestAborted)).ToHttpResult(http));
            await app.StartAsync();
            return new TestApi(app);
        }

        public async Task<HttpResponseMessage> PostAsync(string? key, CreateWidget command, string language = "el")
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = JsonContent.Create(command) };
            request.Headers.Add("X-User", "user-1");
            request.Headers.AcceptLanguage.ParseAdd(language);
            if (key is not null)
            {
                request.Headers.Add(PlatformHeaders.IdempotencyKey, key);
            }

            return await _client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.DisposeAsync();
            NpgsqlConnection.ClearAllPools();
        }
    }
}
