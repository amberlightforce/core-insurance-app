using CoreIns.Modules.Party.Domain;
using CoreIns.SharedKernel;

namespace CoreIns.IntegrationTests.Party;

/// <summary>BO-PTY-11: the pure commission rate choice, table driven (no database).</summary>
public sealed class CommissionResolverTests
{
    private static readonly Guid AgreementAll = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid AgreementMotor = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid AgreementProduct = Guid.Parse("00000000-0000-0000-0000-0000000000a3");

    private static BusinessDate D(string text) => BusinessDate.Parse(text);

    private static ResolverVersion Version(Guid agreement, int no, string? product, string? line, string from, params ResolverLine[] lines) =>
        new(agreement, Guid.CreateVersion7(), no, product, line, CommissionRules.ScopeKey(product, line), D(from), lines);

    private static ResolverLine Line(int no, string businessType, string? line, string basis, decimal rate) => new(no, businessType, line, basis, rate);

    private static readonly ResolverVersion[] Versions =
    [
        Version(AgreementAll, 1, null, null, "2026-01-01", Line(1, "NEW_BUSINESS", null, "NET_PREMIUM", 10m), Line(2, "ANY", null, "NET_PREMIUM", 8m), Line(3, "ANY", "MOTOR", "NET_PREMIUM", 9m)),
        Version(AgreementAll, 2, null, null, "2026-07-01", Line(1, "NEW_BUSINESS", null, "NET_PREMIUM", 12m)),
        Version(AgreementMotor, 1, null, "MOTOR", "2026-01-01", Line(1, "ANY", null, "NET_PREMIUM", 15m)),
        Version(AgreementProduct, 1, "CAR-PLUS", null, "2026-03-01", Line(1, "ANY", null, "GROSS_PREMIUM", 20m)),
    ];

    [Theory]
    [InlineData("2026-02-01", null, null, "NEW_BUSINESS", "NET_PREMIUM", 10, 1)] // exact business type beats ANY
    [InlineData("2026-02-01", null, null, "RENEWAL", "NET_PREMIUM", 8, 2)] // no renewal line: ANY
    [InlineData("2026-02-01", null, "HOME", "NEW_BUSINESS", "NET_PREMIUM", 10, 1)] // line not in any narrower scope
    [InlineData("2026-02-01", null, "MOTOR", "RENEWAL", "NET_PREMIUM", 15, 1)] // line-specific agreement beats the all-scope agreement
    [InlineData("2026-08-01", null, null, "NEW_BUSINESS", "NET_PREMIUM", 12, 1)] // the second version is in force from its date
    [InlineData("2026-06-30", null, null, "NEW_BUSINESS", "NET_PREMIUM", 10, 1)] // and the first one until the day before
    [InlineData("2026-04-01", "CAR-PLUS", "MOTOR", "RENEWAL", "GROSS_PREMIUM", 20, 1)] // product scope wins when it has a line for the basis
    [InlineData("2026-04-01", "CAR-PLUS", "MOTOR", "RENEWAL", "NET_PREMIUM", 15, 1)] // product scope has no NET line: fall to the line scope
    public void The_most_specific_scope_then_business_type_then_line_wins(string date, string? product, string? line, string businessType, string basis, int rate, int lineNo)
    {
        var (resolved, miss) = CommissionResolver.Resolve(Versions, product, line, D(date), businessType, basis);

        miss.ShouldBeNull();
        resolved.ShouldNotBeNull();
        resolved.Line.RatePct.ShouldBe(rate);
        resolved.Line.LineNo.ShouldBe(lineNo);
        resolved.Explanation.ShouldNotBeEmpty();
    }

    [Fact]
    public void A_rate_line_for_the_exact_line_beats_a_generic_one_of_the_same_business_type()
    {
        var (resolved, _) = CommissionResolver.Resolve(Versions, null, "MOTOR", D("2026-02-01"), "RENEWAL", "NET_PREMIUM");
        resolved!.Version.AgreementId.ShouldBe(AgreementMotor);

        var only = new[] { Versions[0] };
        var (generic, _) = CommissionResolver.Resolve(only, null, "MOTOR", D("2026-02-01"), "RENEWAL", "NET_PREMIUM");
        generic!.Line.RatePct.ShouldBe(9m); // ANY + MOTOR beats ANY + all
    }

    [Theory]
    [InlineData("2025-12-31", "NO_AGREEMENT")] // before any version
    [InlineData("2026-02-01", "NO_RATE_LINE")] // GROSS premium: the in-force agreement has no such line
    public void No_match_is_an_explicit_miss_with_a_reason(string date, string reason)
    {
        var basis = reason == "NO_RATE_LINE" ? "GROSS_PREMIUM" : "NET_PREMIUM";
        var (resolved, miss) = CommissionResolver.Resolve(Versions, null, null, D(date), "NEW_BUSINESS", basis);

        resolved.ShouldBeNull();
        miss.ShouldNotBeNull();
        miss.Reason.ShouldBe(reason);
        miss.Explanation.ShouldNotBeEmpty();
    }

    [Fact]
    public void A_scope_the_request_does_not_match_never_applies_and_the_answer_is_stable()
    {
        // The product agreement does not apply to another product, and a request without a line never meets the MOTOR agreement.
        var (resolved, _) = CommissionResolver.Resolve(Versions, "OTHER", null, D("2026-04-01"), "RENEWAL", "GROSS_PREMIUM");
        resolved.ShouldBeNull();

        var first = CommissionResolver.Resolve(Versions, null, "MOTOR", D("2026-02-01"), "NEW_BUSINESS", "NET_PREMIUM");
        var second = CommissionResolver.Resolve(Versions.Reverse().ToArray(), null, "MOTOR", D("2026-02-01"), "NEW_BUSINESS", "NET_PREMIUM");
        second.Rate!.Version.VersionId.ShouldBe(first.Rate!.Version.VersionId);
        second.Rate.Line.ShouldBe(first.Rate.Line);
        second.Rate.Explanation.ShouldBe(first.Rate.Explanation);
    }
}
