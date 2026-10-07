using CoreIns.Host.Database;

namespace CoreIns.Host.Tests;

public sealed class ScramVerifierTests
{
    // Independent reference: Python hashlib.pbkdf2_hmac / hmac / sha256 over the RFC 7677 example
    // (password "pencil", salt W22ZaJ0SNY7soEsUEjb6gQ==, 4096 iterations).
    private const string Rfc7677Verifier =
        "SCRAM-SHA-256$4096:W22ZaJ0SNY7soEsUEjb6gQ==$WG5d8oPm3OtcPnkdi4Uo7BkeZkBFzpcXkuLmtbsT4qY=:wfPLwcE6nTWhTAmQ7tl2KeoiWGPlZqQxSrmfPwDl2dU=";

    [Fact]
    public void Matches_an_independent_implementation_for_the_RFC_7677_example() =>
        ScramVerifier.Create("pencil", Convert.FromBase64String("W22ZaJ0SNY7soEsUEjb6gQ=="), 4096).ShouldBe(Rfc7677Verifier);

    [Fact]
    public void Fresh_verifiers_use_a_random_salt_and_never_contain_the_password()
    {
        var first = ScramVerifier.Create("apppw-SECRET1");
        var second = ScramVerifier.Create("apppw-SECRET1");

        first.ShouldNotBe(second);
        first.ShouldStartWith("SCRAM-SHA-256$4096:");
        first.ShouldNotContain("SECRET1");
        ScramVerifier.IsVerifier(first).ShouldBeTrue();
        ScramVerifier.IsVerifier("apppw-SECRET1").ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("pässwort")]
    [InlineData("tab\tinside")]
    public void Passwords_that_SASLprep_would_change_are_rejected(string password) =>
        Should.Throw<InvalidOperationException>(() => ScramVerifier.Create(password));
}
