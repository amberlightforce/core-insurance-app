using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CoreIns.IntegrationTests;

/// <summary>Boots the real Host in api mode against the test database.</summary>
internal sealed class ApiHostFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("APP_ROLE", "api");
        builder.UseSetting("ConnectionStrings:Core", connectionString);
        builder.UseSetting("AzureAd:TenantId", "00000000-0000-0000-0000-000000000001");
        builder.UseSetting("AzureAd:ClientId", "00000000-0000-0000-0000-000000000002");
    }
}
