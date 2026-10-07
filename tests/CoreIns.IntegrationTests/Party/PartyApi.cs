using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoreIns.IntegrationTests.Party;

/// <summary>HTTP helpers for the Party API tests: role header, Idempotency-Key, JSON bodies.</summary>
internal static class PartyApi
{
    public const string Underwriter = "Staff.Underwriter";
    public const string Billing = "Staff.Billing";

    public static async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(
        HttpClient client, HttpMethod method, string path, object? body = null, string roles = Underwriter, Guid? key = null, bool withKey = true)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.AcceptLanguage.ParseAdd("en");
        if (withKey && method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    /// <summary>A Greek person with AFM, a Greek address, an email and a national mobile number (synthetic data).</summary>
    public static object Person(string given, string family, string? afm, string birthDate = "1980-05-17", string postcode = "11526") => new
    {
        partyType = "PERSON",
        person = new { givenNames = given, familyName = family, fatherName = "Γεώργιος", birthDate },
        identifiers = afm is null ? Array.Empty<object>() : [new { scheme = "AFM", value = afm }],
        addresses = new[]
        {
            new { types = new[] { "LEGAL", "MAILING" }, country = "GR", street = "Λεωφ. Κηφισίας", number = "124", postcode, locality = "Αθήνα" },
        },
        contactPoints = new[]
        {
            new { type = "EMAIL", value = "Test.Person@Example.org", primary = true },
            new { type = "MOBILE", value = "691 234 5678", primary = true },
        },
        reason = "NEW_CUSTOMER",
    };

    public static string Text(this JsonNode? node, string path)
    {
        var current = node;
        foreach (var part in path.Split('.'))
        {
            current = int.TryParse(part, out var index) ? current![index] : current![part];
        }

        return current is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : current?.ToJsonString() ?? "null";
    }
}
