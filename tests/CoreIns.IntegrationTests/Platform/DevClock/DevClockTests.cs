using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Platform;
using CoreIns.Platform.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace CoreIns.IntegrationTests.Platform.DevClock;

/// <summary>
/// SL3-PLT-SUPPORT (D-SL3-12, REQ-PLT-332): the Development dev clock advance. Security probes first: only Platform.Admin,
/// only Development with a Shiftable clock, only forward, audited, shared by api and worker.
/// </summary>
public sealed class DevClockTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private const string Advance = "/api/plt/v1/dev/clock/advance";
    private const string Read = "/api/plt/v1/dev/clock";

    private static readonly Dictionary<string, string?> Shiftable = new() { [ClockConfiguration.ModeKey] = "Shiftable" };

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, object? body, string? roles, string? idempotencyKey = null, string user = "tester")
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (roles is not null)
        {
            request.Headers.Add(TestAuthHandler.RolesHeader, roles);
            request.Headers.Add(TestAuthHandler.UserHeader, user);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> AdvanceAsync(HttpClient client, object body, string roles = "Platform.Admin", string? key = null) =>
        SendAsync(client, HttpMethod.Post, Advance, body, roles, key ?? Guid.NewGuid().ToString());

    private static async Task<long> OffsetSecondsAsync(HttpClient client)
    {
        using var response = await SendAsync(client, HttpMethod.Get, Read, null, "Platform.Admin");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken))!["offsetSeconds"]!.GetValue<long>();
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Theory]
    [InlineData("Staff.Billing")]
    [InlineData("Staff.BillingManager")]
    [InlineData("Staff.UnderwritingManager")]
    public async Task A_caller_without_Platform_Admin_gets_403_and_the_clock_does_not_move(string role)
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString, "Development", Shiftable);
        using var client = factory.CreateClient();
        var before = await ScalarAsync("SELECT offset_micros FROM plt.dev_clock");

        using var advance = await AdvanceAsync(client, new { days = 1 }, role);
        using var read = await SendAsync(client, HttpMethod.Get, Read, null, role);

        advance.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        read.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ScalarAsync("SELECT offset_micros FROM plt.dev_clock")).ShouldBe(before);
    }

    [Fact]
    public async Task An_anonymous_caller_gets_401()
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString, "Development", Shiftable);
        using var client = factory.CreateClient();

        using var advance = await SendAsync(client, HttpMethod.Post, Advance, new { days = 1 }, roles: null, idempotencyKey: Guid.NewGuid().ToString());

        advance.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("Development", null)]
    [InlineData("Development", "System")]
    [InlineData("Testing", "Shiftable")]
    public async Task Outside_Development_with_a_Shiftable_clock_the_endpoints_do_not_exist_even_for_an_admin(string environment, string? mode)
    {
        var settings = mode is null ? null : new Dictionary<string, string?> { [ClockConfiguration.ModeKey] = mode };
        await using var factory = new ApiHostFactory(database.AppConnectionString, environment, settings);
        using var client = factory.CreateClient();
        var before = await ScalarAsync("SELECT offset_micros FROM plt.dev_clock");

        using var advance = await AdvanceAsync(client, new { days = 1 });
        using var read = await SendAsync(client, HttpMethod.Get, Read, null, "Platform.Admin");

        advance.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        factory.Services.GetService<IClockOffsetSource>().ShouldBeNull("the offset reader is not registered");
        factory.Services.GetService<DevClockStore>().ShouldBeNull();
        (await ScalarAsync("SELECT offset_micros FROM plt.dev_clock")).ShouldBe(before);
    }

    [Fact]
    public void Production_with_a_Shiftable_clock_stops_the_host_before_anything_is_registered()
    {
        using var factory = new ApiHostFactory(database.AppConnectionString, "Production", Shiftable, testAuthentication: false);

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        error.Message.ShouldContain(ClockConfiguration.ModeKey);
        error.Message.ShouldContain("Production");
        error.Message.ShouldContain("refuses to start");
    }

    [Theory]
    [InlineData("Production", null)]
    [InlineData("Production", "System")]
    [InlineData("Production", "Shiftable")]
    [InlineData("Staging", "Shiftable")]
    public void In_Production_no_dev_clock_service_is_registered_and_a_Shiftable_clock_cannot_even_be_resolved(string environment, string? mode)
    {
        var settings = mode is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { [ClockConfiguration.ModeKey] = mode };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddSingleton<IHostEnvironment>(new Environment(environment)).AddPlatformModule(configuration);

        // AddPlatformModule (which every app role runs) registers no offset reader and no dev clock command.
        services.Any(d => d.ServiceType == typeof(IClockOffsetSource) || d.ServiceType == typeof(DevClockStore)).ShouldBeFalse();
        services.Any(d => d.ServiceType.IsGenericType && d.ServiceType.GenericTypeArguments.Any(t => t.Name == "AdvanceDevClock")).ShouldBeFalse();

        using var provider = services.BuildServiceProvider();
        if (mode == "Shiftable" && environment == "Production")
        {
            Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<IClock>());
        }
        else if (mode != "Shiftable")
        {
            provider.GetRequiredService<IClock>().ShouldBeSameAs(SystemClock.Instance);
        }

        provider.GetService<IClockOffsetSource>().ShouldBeNull();
    }

    [Fact]
    public void The_host_wires_the_dev_clock_only_behind_the_guard()
    {
        // Structural check of Program.cs: the registration and the routes are the guard's `if` bodies and nothing else calls them.
        var program = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "src", "CoreIns.Host", "Program.cs"));
        program.ShouldContain("var devClock = DevClockRegistration.Guard(builder.Configuration, builder.Environment);");
        System.Text.RegularExpressions.Regex.Count(program, @"AddDevClock\(").ShouldBe(1);
        System.Text.RegularExpressions.Regex.Count(program, @"MapDevClock\(").ShouldBe(1);
        System.Text.RegularExpressions.Regex.IsMatch(program, @"if \(devClock\)\s*\{\s*builder\.Services\.AddDevClock\(").ShouldBeTrue();
        System.Text.RegularExpressions.Regex.IsMatch(program, @"if \(devClock\)\s*\{\s*app\.MapDevClock\(").ShouldBeTrue();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"days\":0,\"hours\":0}")]
    [InlineData("{\"days\":-1}")]
    [InlineData("{\"hours\":-5}")]
    [InlineData("{\"days\":1,\"hours\":-1}")]
    [InlineData("{\"days\":-1,\"hours\":48}")]
    [InlineData("{\"days\":3651}")]
    [InlineData("{\"days\":\"x\"}")]
    public async Task A_zero_negative_or_malformed_advance_is_refused_with_400_and_the_clock_does_not_move(string json)
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString, "Development", Shiftable);
        using var client = factory.CreateClient();
        var before = await ScalarAsync("SELECT offset_micros FROM plt.dev_clock");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Advance, UriKind.Relative))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TestAuthHandler.RolesHeader, "Platform.Admin");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ScalarAsync("SELECT offset_micros FROM plt.dev_clock")).ShouldBe(before);
    }

    [Fact]
    public async Task An_advance_without_a_body_or_without_an_Idempotency_Key_is_refused_with_400()
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString, "Development", Shiftable);
        using var client = factory.CreateClient();
        var before = await ScalarAsync("SELECT offset_micros FROM plt.dev_clock");

        using var noKey = await SendAsync(client, HttpMethod.Post, Advance, new { days = 1 }, "Platform.Admin", idempotencyKey: null);
        using var emptyRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(Advance, UriKind.Relative))
        {
            Content = new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json"),
        };
        emptyRequest.Headers.Add(TestAuthHandler.RolesHeader, "Platform.Admin");
        emptyRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var noBody = await client.SendAsync(emptyRequest, TestContext.Current.CancellationToken);

        noKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        noBody.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ScalarAsync("SELECT offset_micros FROM plt.dev_clock")).ShouldBe(before);
    }

    [Fact]
    public async Task An_admin_advances_the_clock_forward_it_is_audited_and_a_retry_with_the_same_key_does_not_advance_twice()
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString, "Development", Shiftable);
        using var client = factory.CreateClient();
        var clock = factory.Services.GetRequiredService<IClock>().ShouldBeOfType<ShiftableClock>();
        var baseline = await OffsetSecondsAsync(client);
        var auditBefore = await ScalarAsync("SELECT count(*) FROM plt.audit_event WHERE operation = 'plt.DevClock.advance' AND outcome = 'Succeeded'");
        var key = Guid.NewGuid().ToString();

        using var first = await AdvanceAsync(client, new { days = 2, hours = 3 }, key: key);
        using var retry = await AdvanceAsync(client, new { days = 2, hours = 3 }, key: key);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        var expected = baseline + (((2 * 24) + 3) * 3600);
        var firstBody = (await first.Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken))!;
        firstBody["offsetSeconds"]!.GetValue<long>().ShouldBe(expected);
        (firstBody["now"]!.GetValue<DateTimeOffset>() - DateTimeOffset.UtcNow).ShouldBeGreaterThan(TimeSpan.FromSeconds(expected - 5), "the answer's now already includes the advance");
        (await OffsetSecondsAsync(client)).ShouldBe(expected, "the replayed key did not advance again");
        (clock.Now - SystemClock.Instance.Now).ShouldBeGreaterThan(TimeSpan.FromSeconds(expected - 5));

        (await ScalarAsync("SELECT count(*) FROM plt.audit_event WHERE operation = 'plt.DevClock.advance' AND outcome = 'Succeeded'") - auditBefore).ShouldBe(1);
        (await ScalarAsync(
            "SELECT count(*) FROM plt.audit_event WHERE operation = 'plt.DevClock.advance' AND object_type = 'DevClock' AND actor_kind = 'USER' "
            + "AND role_codes = ARRAY['Platform.Admin'] AND changes::text LIKE '%advanceSeconds%'")).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Two_concurrent_advances_add_up_and_the_offset_never_goes_backwards()
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString, "Development", Shiftable);
        using var client = factory.CreateClient();
        var baseline = await OffsetSecondsAsync(client);
        var observed = new System.Collections.Concurrent.ConcurrentBag<long>();

        var calls = Enumerable.Range(0, 8).Select(async _ =>
        {
            using var response = await AdvanceAsync(client, new { hours = 1 });
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            observed.Add((await response.Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken))!["offsetSeconds"]!.GetValue<long>());
        });
        await Task.WhenAll(calls);

        (await OffsetSecondsAsync(client)).ShouldBe(baseline + (8 * 3600));
        observed.Distinct().Count().ShouldBe(8, "every advance saw its own total: the row lock serialised them");
        observed.Max().ShouldBe(baseline + (8 * 3600));
        observed.Min().ShouldBe(baseline + 3600);
    }

    [Fact]
    public async Task Another_process_sees_the_new_offset_within_a_second()
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString, "Development", Shiftable);
        using var client = factory.CreateClient();

        // The worker: its own data source, its own reader and clock over the same table (nothing shared in memory with the api).
        await using var workerSource = NpgsqlDataSource.Create(database.AppConnectionString);
        var workerReader = new DevClockStore(workerSource);
        var workerClock = new ShiftableClock(SystemClock.Instance, TimeSpan.Zero, workerReader);
        var before = workerClock.Now - SystemClock.Instance.Now;

        using var response = await AdvanceAsync(client, new { days = 5 });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stopwatch = Stopwatch.StartNew();
        var seen = TimeSpan.Zero;
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(1))
        {
            seen = workerClock.Now - SystemClock.Instance.Now;
            if (seen >= before + TimeSpan.FromDays(5) - TimeSpan.FromSeconds(1))
            {
                break;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        seen.ShouldBeGreaterThanOrEqualTo(before + TimeSpan.FromDays(5) - TimeSpan.FromSeconds(1), "the worker reads the shared offset within the cache time");
        DevClockStore.CacheTtl.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task The_database_itself_refuses_to_move_the_clock_backwards_or_delete_the_row_or_exceed_the_limit()
    {
        await using var factory = new ApiHostFactory(database.AppConnectionString, "Development", Shiftable);
        using var client = factory.CreateClient();
        using var advance = await AdvanceAsync(client, new { days = 1 });
        advance.StatusCode.ShouldBe(HttpStatusCode.OK);
        var ct = TestContext.Current.CancellationToken;

        await using var superuser = new NpgsqlConnection(database.SuperuserConnectionString);
        await superuser.OpenAsync(ct);
        foreach (var sql in new[]
                 {
                     "UPDATE plt.dev_clock SET offset_micros = offset_micros - 1",
                     "UPDATE plt.dev_clock SET offset_micros = 0",
                     "DELETE FROM plt.dev_clock",
                     "TRUNCATE plt.dev_clock",
                     "INSERT INTO plt.dev_clock (id, offset_micros, version, updated_at) VALUES (2, 0, 0, now())",
                     "UPDATE plt.dev_clock SET offset_micros = 3153600000000001",
                 })
        {
            await using var command = new NpgsqlCommand(sql, superuser);
            var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct), sql);
            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation, sql);
        }

        // The application role can read and advance the clock but never delete or rewrite it wholesale.
        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);
        foreach (var sql in new[] { "DELETE FROM plt.dev_clock", "TRUNCATE plt.dev_clock", "INSERT INTO plt.dev_clock (id, offset_micros, version, updated_at) VALUES (2, 0, 0, now())" })
        {
            await using var command = app.CreateCommand(sql);
            var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct), sql);
            error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, sql);
        }
    }

    [Fact]
    public void The_guard_activates_the_dev_clock_only_in_Development_with_Shiftable_and_throws_in_Production()
    {
        var shiftable = new ConfigurationBuilder().AddInMemoryCollection(Shiftable).Build();
        var system = new ConfigurationBuilder().Build();

        DevClockRegistration.Guard(shiftable, new Environment("Development")).ShouldBeTrue();
        DevClockRegistration.Guard(shiftable, new Environment("Testing")).ShouldBeFalse();
        DevClockRegistration.Guard(system, new Environment("Development")).ShouldBeFalse();
        DevClockRegistration.Guard(system, new Environment("Production")).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => DevClockRegistration.Guard(shiftable, new Environment("Production")));
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "CoreIns.Host";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
