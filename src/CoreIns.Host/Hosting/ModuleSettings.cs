using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;

namespace CoreIns.Host.Hosting;

/// <summary>
/// D-PRG-21: the permission map and the authority grants live in one file per module
/// (<c>permissions/&lt;module&gt;.json</c>, e.g. <c>pol.json</c>) instead of one shared section of appsettings.json, so parallel
/// work on different modules no longer edits the same lines. The files are merged at start-up, in file-name order, into
/// the same configuration keys the platform has always read (<c>Platform:Permissions:Grants</c>, <c>Platform:Authority:Grants</c>);
/// behaviour is unchanged. A file may hold only <c>Platform:Permissions:Grants</c> (operation to roles) and
/// <c>Platform:Authority:Grants</c> (a list). The same operation key in two files, or the same authority grant id, stops the Host.
/// </summary>
internal static class ModuleSettings
{
    /// <summary>The directory (below the content root) with one JSON file per module.</summary>
    public const string PermissionsDirectory = "permissions";

    private const string PermissionsPrefix = "Platform:Permissions:Grants";
    private const string AuthorityPrefix = "Platform:Authority:Grants";

    /// <summary>
    /// Adds the per-module files (and, in Development, <c>dev-users.Development.json</c>) right after the appsettings files, so
    /// environment variables, command-line arguments and test overrides still win, as they did for appsettings.json.
    /// </summary>
    public static void AddModuleSettings(this ConfigurationManager configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var sources = new List<IConfigurationSource> { new MemoryConfigurationSource { InitialData = Load(Path.Combine(environment.ContentRootPath, PermissionsDirectory)) } };
        if (environment.IsDevelopment())
        {
            // Development sign-in users (D-SLC-03): a separate file, never loaded in any other environment.
            sources.Add(new JsonConfigurationSource
            {
                Path = $"dev-users.{environment.EnvironmentName}.json",
                Optional = true,
                ReloadOnChange = false,
                FileProvider = environment.ContentRootFileProvider,
            });
        }

        var at = configuration.Sources.Count;
        for (var i = configuration.Sources.Count - 1; i >= 0; i--)
        {
            if (configuration.Sources[i] is JsonConfigurationSource)
            {
                at = i + 1;
                break;
            }
        }

        foreach (var source in sources)
        {
            configuration.Sources.Insert(at++, source);
        }
    }

    /// <summary>Reads and merges every <c>*.json</c> file of <paramref name="directory"/> (none is fine); throws on a duplicate.</summary>
    public static Dictionary<string, string?> Load(string directory)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory))
        {
            return data;
        }

        var operations = new Dictionary<string, string>(StringComparer.Ordinal);
        var grantIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var grantCount = 0;
        foreach (var file in Directory.GetFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(
                    File.ReadAllText(file),
                    documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Module settings file '{name}' is not valid JSON: {ex.Message}", ex);
            }

            var platform = root is JsonObject top && top.Count == 1 ? top["Platform"] as JsonObject : null;
            if (platform is null || platform.Any(p => p.Key is not ("Permissions" or "Authority")))
            {
                throw new InvalidOperationException(
                    $"Module settings file '{name}' must hold only Platform:Permissions:Grants and/or Platform:Authority:Grants.");
            }

            if (platform["Permissions"] is JsonObject permissions)
            {
                if (permissions.Count != 1 || permissions["Grants"] is not JsonObject grants)
                {
                    throw new InvalidOperationException($"Module settings file '{name}': Platform:Permissions must hold only 'Grants' (operation to roles).");
                }

                foreach (var (operation, roles) in grants)
                {
                    if (roles is not JsonArray array)
                    {
                        throw new InvalidOperationException($"Module settings file '{name}': permission '{operation}' must be an array of roles.");
                    }

                    if (!operations.TryAdd(operation, name))
                    {
                        throw new InvalidOperationException(
                            $"Permission '{operation}' is defined in both '{operations[operation]}' and '{name}'. Each operation is granted in exactly one module file.");
                    }

                    Flatten(data, $"{PermissionsPrefix}:{operation}", array);
                }
            }

            if (platform["Authority"] is JsonObject authority)
            {
                if (authority.Count != 1 || authority["Grants"] is not JsonArray list)
                {
                    throw new InvalidOperationException($"Module settings file '{name}': Platform:Authority must hold only 'Grants' (a list).");
                }

                foreach (var grant in list)
                {
                    var id = grant?["Id"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        throw new InvalidOperationException($"Module settings file '{name}': every authority grant needs an 'Id'.");
                    }

                    if (!grantIds.TryAdd(id, name))
                    {
                        throw new InvalidOperationException($"Authority grant '{id}' is defined in both '{grantIds[id]}' and '{name}'. Grant ids are unique.");
                    }

                    // Arrays merge by index in configuration, so each grant gets the next free index across all files.
                    Flatten(data, $"{AuthorityPrefix}:{grantCount++.ToString(CultureInfo.InvariantCulture)}", grant);
                }
            }
        }

        return data;
    }

    private static void Flatten(Dictionary<string, string?> data, string path, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    Flatten(data, $"{path}:{key}", value);
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    Flatten(data, $"{path}:{i.ToString(CultureInfo.InvariantCulture)}", array[i]);
                }

                break;
            case JsonValue value:
                data[path] = value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();
                break;
            default:
                data[path] = null;
                break;
        }
    }
}
