using CoreIns.Host.Hosting;
using CoreIns.SharedKernel;
using Microsoft.IdentityModel.Tokens;

namespace CoreIns.IntegrationTests;

public sealed class DevelopmentTokenLifetimeTests
{
    private static readonly DateTime Now = Instant.FromUtc(2027, 1, 15).ToUtcDateTime();
    private static readonly TokenValidationParameters Parameters = new() { ClockSkew = TimeSpan.FromSeconds(30), RequireExpirationTime = true };

    [Fact]
    public void Tokens_are_validated_against_the_shifted_clock_but_still_require_expiry_and_an_ordered_lifetime()
    {
        DevelopmentAuthentication.ValidateLifetime(Now, Now.AddMinutes(60), Parameters, Now).ShouldBeTrue();
        DevelopmentAuthentication.ValidateLifetime(Now, null, Parameters, Now).ShouldBeFalse();
        DevelopmentAuthentication.ValidateLifetime(Now.AddSeconds(10), Now.AddSeconds(-10), Parameters, Now).ShouldBeFalse();
        DevelopmentAuthentication.ValidateLifetime(Now.AddMinutes(1), Now.AddMinutes(60), Parameters, Now).ShouldBeFalse();
        DevelopmentAuthentication.ValidateLifetime(Now.AddMinutes(-60), Now.AddMinutes(-1), Parameters, Now).ShouldBeFalse();
    }
}
