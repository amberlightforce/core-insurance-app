using System.Text;
using System.Text.RegularExpressions;

namespace CoreIns.Modules.Party.Domain;

/// <summary>Options of the Party module (section <c>Party</c>).</summary>
internal sealed class PartyOptions
{
    public const string Section = "Party";

    /// <summary>
    /// Country calling code (digits) used to complete national phone numbers (REQ-PTY-082: the pack's default region;
    /// Greece pack "30"). Pack data: configured per stamp, never assumed in core. Empty = only E.164 input is accepted.
    /// </summary>
    public string DefaultCallingCode { get; set; } = string.Empty;
}

/// <summary>Contact point normalisation (REQ-PTY-082 E.164, REQ-PTY-083 email syntax).</summary>
internal static partial class ContactPoints
{
    /// <summary>E.164: '+' and 8..15 digits; national numbers get the configured calling code. Null when invalid.</summary>
    public static string? NormalisePhone(string value, string defaultCallingCode)
    {
        var digits = new StringBuilder(value.Length);
        var trimmed = value.Trim();
        foreach (var c in trimmed)
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
            }
            else if (c is not (' ' or '-' or '.' or '(' or ')' or '+'))
            {
                return null;
            }
        }

        string candidate;
        if (trimmed.StartsWith('+'))
        {
            candidate = "+" + digits;
        }
        else if (digits.Length > 2 && digits[0] == '0' && digits[1] == '0')
        {
            candidate = "+" + digits.ToString(2, digits.Length - 2);
        }
        else if (defaultCallingCode.Length > 0)
        {
            candidate = "+" + defaultCallingCode + digits;
        }
        else
        {
            return null;
        }

        return E164().IsMatch(candidate) ? candidate : null;
    }

    /// <summary>Lower-cased email when its syntax is valid, else null.</summary>
    public static string? NormaliseEmail(string value)
    {
        var email = value.Trim().ToLowerInvariant();
        return email.Length <= 254 && Email().IsMatch(email) ? email : null;
    }

    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex E164();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Email();
}
