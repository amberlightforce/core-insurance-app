using System.Reflection;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.SharedKernel.Json;

namespace CoreIns.Modules.Product;

/// <summary>
/// Product definitions shipped as data. Each is a product source document in the exact shape the import path accepts
/// (<c>pfc.ProductVersion.import</c>), so loading a seed and authoring a version use the same code.
/// </summary>
public static class ProductSeeds
{
    private const string MotorResource = "CoreIns.Modules.Product.Seed.motor-gr.product.json";

    /// <summary>The raw JSON of "Motor Private Car" (MOTOR-GR 1.0, legal entity GR-TEST): MTPL, own damage and windscreen.</summary>
    public static string MotorPrivateCarJson()
    {
        using var stream = typeof(ProductSeeds).Assembly.GetManifestResourceStream(MotorResource)
            ?? throw new InvalidOperationException($"Embedded resource {MotorResource} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The import request that loads and locks "Motor Private Car".</summary>
    public static ProductVersionImportRequest MotorPrivateCarImport(bool lockVersion = true) => new()
    {
        Definition = System.Text.Json.JsonSerializer.Deserialize<ProductArtefact>(MotorPrivateCarJson(), SharedKernelJson.Options)
            ?? throw new InvalidOperationException("The motor seed is empty."),
        Lock = lockVersion,
    };
}
