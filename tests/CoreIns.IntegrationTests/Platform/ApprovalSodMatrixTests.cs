using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.IntegrationTests.Platform;

/// <summary>
/// NX-EC-09: the segregation-of-duties matrix. One scenario set runs over every approval type that carries an amount authority
/// (each row of <see cref="Rows"/> is one type; add a row when a module registers another): maker = checker, the maker's delegate and
/// principal, an earlier editor after edit-then-approve, a checker whose authority is below the amount, and the one valid checker.
/// Types with no amount (MIG.BATCH_RUN, PFC.Fallback, MKT.PackActivation, RI.CONTRACT_APPROVE) share the same platform decide path
/// exercised here but have no amount row to compare.
/// </summary>
public sealed class ApprovalSodMatrixTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public sealed record Row(string ApprovalType, ModuleCode Module, string SubjectType, string AuthorityType, string Amount, string LowRole, string HighRole, string ReferralRole)
    {
        public override string ToString() => ApprovalType;
    }

    public static TheoryData<Row> Rows =>
    [
        new Row("CLM.TRANSACTION_SET", ModuleCode.CLM, "TransactionSet", "CLM.PAYMENT", "6000.00", "Staff.ClaimsHandler", "Staff.ClaimsManager", "Staff.ClaimsManager"),
        new Row("BIL.REFUND", ModuleCode.BIL, "Refund", "BIL.REFUND", "1000.00", "Staff.Billing", "Staff.BillingManager", "Staff.BillingManager"),
    ];

    private ApiHostFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _ = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static string NewHash() => Sha256Hash.ComputeUtf8(Guid.NewGuid().ToString()).Value;

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N");

    private AsyncServiceScope Scope(ActorRef actor, string roles, ActorRef? onBehalfOf = null)
    {
        var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.Actor = actor;
        context.OnBehalfOf = onBehalfOf;
        context.Roles = roles.Split(',');
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.ConfigurationHash = ConfigurationHash.Parse(new string('a', 64));
        return scope;
    }

    private static ApprovalRequestRequest Body(Row row, string subject, string hash) => new()
    {
        Type = row.ApprovalType,
        ObjectRef = new ObjectRef(row.Module, row.SubjectType, subject),
        PayloadHash = Sha256Hash.Parse(hash),
        Authority = new ApprovalAuthority { Type = row.AuthorityType, Amount = Money.Of(decimal.Parse(row.Amount, System.Globalization.CultureInfo.InvariantCulture), "EUR") },
        ReferralRole = row.ReferralRole,
        Reason = "SoD matrix",
    };

    private async Task<ApprovalView> RequestAsync(Row row, ActorRef maker, string subject, string hash, ActorRef? onBehalfOf = null)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = Scope(maker, row.LowRole, onBehalfOf);
        var transaction = await scope.ServiceProvider.GetRequiredService<DbSession>().BeginTransactionAsync(ct);
        var response = await scope.ServiceProvider.GetRequiredService<IPlatformApprovalService>().RequestAsync(Body(row, subject, hash), CommandOptions.New(), ct);
        await transaction.CommitAsync(ct);
        return response.Request;
    }

    /// <summary>The PLT error code of a decision, or null when it was approved.</summary>
    private async Task<string?> DecideAsync(Guid requestId, string hash, ActorRef checker, string roles, ActorRef? onBehalfOf = null)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = Scope(checker, roles, onBehalfOf);
        var transaction = await scope.ServiceProvider.GetRequiredService<DbSession>().BeginTransactionAsync(ct);
        try
        {
            await scope.ServiceProvider.GetRequiredService<IPlatformApprovalService>().DecideAsync(
                new ApprovalDecideRequest { RequestId = requestId, Decision = ApprovalDecideRequest.DecisionValue.Approve, PayloadHash = Sha256Hash.Parse(hash) },
                CommandOptions.New(), ct);
            await transaction.CommitAsync(ct);
            return null;
        }
        catch (DomainException ex)
        {
            await transaction.RollbackAsync(ct);
            return ex.Error.Code.Value;
        }
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task The_maker_cannot_decide_even_holding_the_checker_role(Row row)
    {
        var hash = NewHash();
        var maker = ActorRef.User(Unique("sod-maker"));
        var request = await RequestAsync(row, maker, Guid.NewGuid().ToString(), hash);

        (await DecideAsync(request.RequestId, hash, maker, row.HighRole)).ShouldBe("PLT-ERR-SELF-APPROVAL");
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task The_makers_delegate_and_principal_cannot_decide(Row row)
    {
        var hash = NewHash();
        var principal = ActorRef.User(Unique("sod-principal"));
        var agent = new ActorRef(ActorKind.AiAgent, Unique("sod-agent"));
        var request = await RequestAsync(row, agent, Guid.NewGuid().ToString(), hash, onBehalfOf: principal);

        // The person the agent acted for is the maker in substance.
        (await DecideAsync(request.RequestId, hash, principal, row.HighRole)).ShouldBe("PLT-ERR-SELF-APPROVAL");

        // A checker acting on behalf of the maker (delegation) is refused too.
        var other = ActorRef.User(Unique("sod-other"));
        (await DecideAsync(request.RequestId, hash, other, row.HighRole, onBehalfOf: principal)).ShouldBe("PLT-ERR-SELF-APPROVAL");
        (await DecideAsync(request.RequestId, hash, other, row.HighRole, onBehalfOf: agent)).ShouldBe("PLT-ERR-SELF-APPROVAL");

        // The checker must be a person.
        (await DecideAsync(request.RequestId, hash, new ActorRef(ActorKind.AiAgent, Unique("sod-agent")), row.HighRole)).ShouldBe("PLT-ERR-CHECKER-MUST-BE-HUMAN");
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task Edit_then_approve_is_refused_for_every_earlier_author_and_a_stale_hash_is_refused(Row row)
    {
        var subject = Guid.NewGuid().ToString();
        var first = NewHash();
        var second = NewHash();
        var a = ActorRef.User(Unique("sod-a"));
        var b = ActorRef.User(Unique("sod-b"));
        var original = await RequestAsync(row, a, subject, first);
        var edited = await RequestAsync(row, b, subject, second);

        // a made the content before b changed it: an editor now. b is the maker of the new version.
        (await DecideAsync(edited.RequestId, second, a, row.HighRole)).ShouldBe("PLT-ERR-EDITOR-CANNOT-APPROVE");
        (await DecideAsync(edited.RequestId, second, b, row.HighRole)).ShouldBe("PLT-ERR-SELF-APPROVAL");

        // Approving what was seen before the edit does not approve the edit.
        var c = ActorRef.User(Unique("sod-c"));
        (await DecideAsync(edited.RequestId, first, c, row.HighRole)).ShouldBe("PLT-ERR-APPROVAL-STALE");
        (await DecideAsync(original.RequestId, first, c, row.HighRole)).ShouldBe("PLT-ERR-APPROVAL-STALE");
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task A_checker_with_authority_below_the_amount_is_refused_and_the_right_checker_approves(Row row)
    {
        var hash = NewHash();
        var request = await RequestAsync(row, ActorRef.User(Unique("sod-m")), Guid.NewGuid().ToString(), hash);

        var low = await DecideAsync(request.RequestId, hash, ActorRef.User(Unique("sod-low")), row.LowRole);
        low.ShouldBeOneOf("PLT-ERR-AUTHORITY-REFERRAL-REQUIRED", "PLT-ERR-AUTHORITY-DENIED");

        (await DecideAsync(request.RequestId, hash, ActorRef.User(Unique("sod-high")), row.HighRole)).ShouldBeNull();
    }
}
