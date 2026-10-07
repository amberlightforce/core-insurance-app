using System.Text;
using CoreIns.ContractGen;

// tools/CoreIns.ContractGen — see the .csproj for usage.
var check = args.Contains("--check");
var verbose = args.Contains("--verbose");
var rootIndex = Array.IndexOf(args, "--root");
var root = rootIndex >= 0 && rootIndex + 1 < args.Length ? args[rootIndex + 1] : FindRoot();

var generator = new Generator(root);
var files = generator.Run();
var stats = generator.Stats;

var stale = new List<string>();
var onDisk = Layout.OwnedDirectories
    .Select(dir => Path.Combine(root, dir))
    .Where(Directory.Exists)
    .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
    .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
    .ToHashSet(StringComparer.Ordinal);

if (check)
{
    // Determinism: a second run over the same contracts must produce byte-identical output.
    var second = new Generator(root).Run();
    if (!second.SequenceEqual(files))
    {
        Console.Error.WriteLine("ContractGen is not deterministic: two runs produced different output.");
        return 2;
    }

    foreach (var (path, content) in files)
    {
        var full = Path.Combine(root, path);
        if (!File.Exists(full) || Normalise(File.ReadAllText(full)) != content)
        {
            stale.Add(path);
        }
    }

    stale.AddRange(onDisk.Where(path => !files.ContainsKey(path)).Select(path => path + " (not generated any more)"));
    if (stale.Count > 0)
    {
        Console.Error.WriteLine($"Generated contract code is stale ({stale.Count} files). Run: dotnet run --project tools/CoreIns.ContractGen");
        foreach (var path in stale.Take(50))
        {
            Console.Error.WriteLine("  " + path);
        }

        return 1;
    }

    Console.WriteLine($"Generated contract code is current ({files.Count} files).");
}
else
{
    var written = 0;
    foreach (var (path, content) in files)
    {
        var full = Path.Combine(root, path);
        if (File.Exists(full) && Normalise(File.ReadAllText(full)) == content)
        {
            continue;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        written++;
    }

    var removed = 0;
    foreach (var path in onDisk.Where(path => !files.ContainsKey(path)))
    {
        File.Delete(Path.Combine(root, path));
        removed++;
    }

    Console.WriteLine($"{files.Count} files ({written} written, {removed} removed).");
}

Console.WriteLine($"events {stats.Events}, common types {stats.CommonTypes}, DTOs {stats.Dtos}, operations {stats.Operations} " +
                  $"(in-process {stats.InProcessOperations}), interfaces {stats.Interfaces}, fakes {stats.Fakes}, error constants {stats.ErrorConstants}");
if (verbose)
{
    Console.WriteLine("Unmapped Uuid property names (Guid): " + string.Join(", ", IdTypeMap.Unmapped));
    foreach (var issue in stats.Issues)
    {
        Console.WriteLine("ISSUE: " + issue);
    }
}
else if (stats.Issues.Count > 0)
{
    Console.WriteLine($"{stats.Issues.Count} contract observations (--verbose lists them).");
}

return 0;

static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

static string FindRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CoreIns.sln")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName ?? throw new InvalidOperationException("CoreIns.sln not found; pass --root <repo>.");
}
