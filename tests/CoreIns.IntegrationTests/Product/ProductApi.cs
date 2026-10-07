using System.Text.Json.Nodes;
using CoreIns.Modules.Product;
using CoreIns.IntegrationTests.Party;

namespace CoreIns.IntegrationTests.Product;

/// <summary>Helpers of the PFC tests: the seed document as a mutable JSON tree and the import/resolve calls.</summary>
internal static class ProductApi
{
    public const string Admin = "Platform.Admin";
    public const string Staff = PartyApi.Underwriter;

    /// <summary>The Motor Private Car seed as JSON, with a test-unique product code so tests never share a product.</summary>
    public static JsonObject Seed(string? productCode = null)
    {
        var definition = JsonNode.Parse(ProductSeeds.MotorPrivateCarJson())!.AsObject();
        if (productCode is not null)
        {
            definition["product"]!["code"] = productCode;
        }

        return definition;
    }

    public static JsonObject ImportBody(JsonObject definition, bool lockVersion) =>
        new() { ["definition"] = JsonNode.Parse(definition.ToJsonString()), ["lock"] = lockVersion };

    public static Task<(HttpResponseMessage Response, JsonNode? Body)> ImportAsync(HttpClient client, JsonObject definition, bool lockVersion = true, Guid? key = null, string roles = Admin) =>
        PartyApi.SendAsync(client, HttpMethod.Post, "/api/pfc/v1/product-versions/import", ImportBody(definition, lockVersion), roles, key);

    public static Task<(HttpResponseMessage Response, JsonNode? Body)> ResolveAsync(
        HttpClient client, string product, string channel, string date, string transactionType = "NewBusiness", string legalEntity = "GR-TEST", string roles = Staff) =>
        PartyApi.SendAsync(client, HttpMethod.Post, $"/api/pfc/v1/product-versions/resolve?validAt={date}",
            new { jurisdiction = "GR", legalEntity, product, channel, transactionType }, roles, withKey: false);

    public static Task<(HttpResponseMessage Response, JsonNode? Body)> GetAsync(HttpClient client, string path, string roles = Staff) =>
        PartyApi.SendAsync(client, HttpMethod.Get, path, roles: roles);
}
