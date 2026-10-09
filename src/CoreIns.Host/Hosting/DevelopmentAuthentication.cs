using System.Security.Claims;
using System.Security.Cryptography;
using CoreIns.Platform.Http;
using CoreIns.Platform.Time;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CoreIns.Host.Hosting;

/// <summary>A configured development user (synthetic; never a real person).</summary>
internal sealed class DevUser
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<string> Roles { get; } = [];
}

/// <summary>Section <c>DevAuthentication</c>: the Development-only local sign-in (D-SLC-03).</summary>
internal sealed class DevAuthenticationOptions
{
    public const string Section = "DevAuthentication";

    /// <summary>The explicit switch. Honoured only when ASPNETCORE_ENVIRONMENT=Development; anywhere else the Host refuses to start.</summary>
    public bool Enabled { get; set; }

    /// <summary>Token lifetime in minutes (default 8 hours).</summary>
    public int TokenLifetimeMinutes { get; set; } = 480;

    /// <summary>The users offered on the dev sign-in page.</summary>
    public List<DevUser> Users { get; } = [];
}

/// <summary>
/// Development-only local sign-in (D-SLC-03) so the stack runs without an Entra tenant. When
/// <c>DevAuthentication:Enabled=true</c> <b>and</b> the environment is Development, the api offers
/// <c>GET /api/plt/v1/dev/users</c> and <c>POST /api/plt/v1/dev/sign-in</c>, which issues a short-lived bearer token
/// signed with a random key generated at process start (no secret in configuration or the repository; tokens die with
/// the process). The token carries the configured user's id and roles and is validated by its own JWT scheme; any other
/// bearer token still goes to Entra ID. With the flag set in any other environment, <see cref="Guard"/> stops the Host.
/// </summary>
internal static class DevelopmentAuthentication
{
    public const string Scheme = "DevLocal";
    public const string Issuer = "coreins-dev-local";
    public const string Audience = "coreins-api-dev";
    public const string EnabledKey = DevAuthenticationOptions.Section + ":Enabled";

    /// <summary>
    /// Refuses to start when the flag is set outside Development (every app role calls it before building the host).
    /// Returns whether dev sign-in is active.
    /// </summary>
    public static bool Guard(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue<bool>(EnabledKey))
        {
            return false;
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{EnabledKey} is set but the environment is '{environment.EnvironmentName}'. The local sign-in exists only for "
                + "Development (D-SLC-03); remove the flag. The Host refuses to start.");
        }

        return true;
    }

    /// <summary>Registers the dev JWT scheme (api role, after <see cref="Guard"/> returned true).</summary>
    public static AuthenticationBuilder AddDevelopmentSignIn(this AuthenticationBuilder builder, IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DevAuthenticationOptions>().Bind(configuration.GetSection(DevAuthenticationOptions.Section));
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = "dev-" + Guid.NewGuid().ToString("N")[..8] };
        services.AddSingleton(new DevSigningKey(key));
        // The token times come from IClock (the shiftable dev clock, D-SL3-12), so the lifetime is checked against the same clock;
        // the library default would compare against the real clock and reject every token issued after a dev clock advance.
        services.AddOptions<JwtBearerOptions>(Scheme).PostConfigure<IClock>((options, clock) =>
            options.TokenValidationParameters.LifetimeValidator = (notBefore, expires, _, parameters) =>
            {
                var now = clock.Now.ToUtcDateTime();
                var skew = parameters.ClockSkew;
                return (notBefore is null || notBefore.Value <= now + skew) && (expires is null || expires.Value >= now - skew);
            });
        return builder.AddJwtBearer(Scheme, options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudience = Audience,
                IssuerSigningKey = key,
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                NameClaimType = "name",
                RoleClaimType = "roles",
                ClockSkew = TimeSpan.FromSeconds(30),
            };
        });
    }

    /// <summary>True when the request's bearer token was issued by the dev sign-in (routing only; the scheme validates it).</summary>
    public static bool IsDevToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var handler = new JsonWebTokenHandler();
        var token = header["Bearer ".Length..].Trim();
        return handler.CanReadToken(token) && string.Equals(handler.ReadJsonWebToken(token).Issuer, Issuer, StringComparison.Ordinal);
    }

    /// <summary>Maps the dev sign-in endpoints (anonymous, no Idempotency-Key: signing in changes no business state).</summary>
    public static void MapDevelopmentSignIn(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/plt/v1/dev").AllowAnonymous().WithMetadata(new SkipIdempotencyAttribute());
        group.MapGet("/users", (IOptions<DevAuthenticationOptions> options) =>
            Results.Ok(new { items = options.Value.Users.Select(u => new { id = u.Id, name = u.Name, roles = u.Roles }) }));
        group.MapPost("/sign-in", (DevSignInRequest request, IOptions<DevAuthenticationOptions> options, DevSigningKey key, IClock clock) =>
        {
            var user = options.Value.Users.FirstOrDefault(u => string.Equals(u.Id, request.UserId, StringComparison.Ordinal));
            if (user is null)
            {
                return Results.NotFound();
            }

            var now = clock.Now.ToUtcDateTime();
            var expires = now + TimeSpan.FromMinutes(options.Value.TokenLifetimeMinutes);
            var identity = new ClaimsIdentity([new Claim("sub", "dev:" + user.Id), new Claim("oid", "dev:" + user.Id), new Claim("name", user.Name)]);
            identity.AddClaims(user.Roles.Select(role => new Claim("roles", role)));
            var token = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = Audience,
                Subject = identity,
                IssuedAt = now,
                NotBefore = now,
                Expires = expires,
                SigningCredentials = new SigningCredentials(key.Key, SecurityAlgorithms.HmacSha256),
            });
            return Results.Ok(new { accessToken = token, tokenType = "Bearer", expiresAt = expires, user = new { id = user.Id, name = user.Name, roles = user.Roles } });
        });
    }

    internal sealed record DevSignInRequest(string UserId);

    internal sealed record DevSigningKey(SymmetricSecurityKey Key);
}
