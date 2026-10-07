namespace CoreIns.Modules.Product.Domain;

/// <summary>Database codes of enums: <c>CloseToNewBusiness</c> becomes <c>CLOSE_TO_NEW_BUSINESS</c>.</summary>
internal static class Codes
{
    public static string Of<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        string.Concat(value.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToUpperInvariant();

    public static TEnum Parse<TEnum>(string code)
        where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().First(value => string.Equals(Of(value), code, StringComparison.Ordinal));

    /// <summary>SQL check expression listing the codes of <typeparamref name="TEnum"/>.</summary>
    public static string CheckSql<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"{column} IN ({string.Join(", ", Enum.GetValues<TEnum>().Select(v => $"'{Of(v)}'"))})";
}
