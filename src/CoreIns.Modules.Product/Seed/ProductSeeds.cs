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
    private const string Motor11Resource = "CoreIns.Modules.Product.Seed.motor-gr-1.1.product.json";

    /// <summary>The raw JSON of "Motor Private Car" (MOTOR-GR 1.0, legal entity GR-TEST): MTPL, own damage and windscreen.</summary>
    public static string MotorPrivateCarJson() => ReadResource(MotorResource);

    /// <summary>
    /// The raw JSON of MOTOR-GR 1.1 (SL3-PFC-MOTOR11): a new write-once version, effective from 2026-01-01. It adds the
    /// TERM_RATIO day count (provisional), refund methods per cancellation source and mid-term change permissions. 1.0 is never edited.
    /// </summary>
    public static string MotorPrivateCar11Json() => ReadResource(Motor11Resource);

    /// <summary>The import request that loads and locks MOTOR-GR 1.1.</summary>
    public static ProductVersionImportRequest MotorPrivateCar11Import(bool lockVersion = true) => new()
    {
        Definition = System.Text.Json.JsonSerializer.Deserialize<ProductArtefact>(MotorPrivateCar11Json(), SharedKernelJson.Options)
            ?? throw new InvalidOperationException("The motor 1.1 seed is empty."),
        Lock = lockVersion,
    };

    private static string ReadResource(string name)
    {
        using var stream = typeof(ProductSeeds).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
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
