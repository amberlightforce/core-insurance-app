using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Billing.Services;

/// <summary>One line of a parsed statement. The amount is positive; the direction says which way the money moved.</summary>
internal sealed record ParsedStatementLine(
    int LineNo, string Direction, BusinessDate BookingDate, BusinessDate? ValueDate, decimal Amount, string? Reference, string? EndToEndId);

/// <summary>A parsed statement (BO-BIL-11), independent of the file format.</summary>
internal sealed record ParsedStatement(
    string StatementRef, string? Iban, string Currency, BusinessDate From, BusinessDate To, decimal? Opening, decimal? Closing, IReadOnlyList<ParsedStatementLine> Lines);

/// <summary>
/// Parsers of the two statement formats (BO-BIL-11): camt.053 (any version; elements are read by local name, DTDs are refused) and the CSV of
/// the contract (<c>bookingDate,valueDate,amount,currency,reference,endToEndId</c>, amount signed). A malformed file is refused whole with the
/// line or element named, never with its content (a statement carries third-party IBANs and names).
/// </summary>
internal static class BankStatementParser
{
    public const int MaxLines = 5000;
    private const int MaxReference = 500;
    private const int MaxEndToEnd = 140;

    private static DomainError Invalid(string message) => DomainError.Of(ModuleCode.BIL, "STATEMENT-INVALID", message);

    private static string? Clip(string? text, int max)
    {
        var value = text?.Trim();
        return string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
    }

    private static bool TryDate(string? text, out BusinessDate date)
    {
        date = default;
        if (text is null || text.Length < 10 || !DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        date = new BusinessDate(parsed);
        return true;
    }

    private static bool TryAmount(string? text, out decimal amount) =>
        decimal.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);

    // ---- camt.053 ----

    private static XElement? Child(XElement? parent, string name) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static XElement? Path(XElement? parent, params string[] names)
    {
        var current = parent;
        foreach (var name in names)
        {
            current = Child(current, name);
        }

        return current;
    }

    private static string? DateOf(XElement? holder) => Child(holder, "Dt")?.Value ?? Child(holder, "DtTm")?.Value;

    public static Result<ParsedStatement> ParseCamt053(string xml)
    {
        XDocument document;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0 };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            return Invalid($"The file is not well-formed XML (line {ex.LineNumber}).");
        }

        var statements = document.Descendants().Where(e => e.Name.LocalName == "Stmt").ToList();
        if (statements.Count != 1)
        {
            return Invalid(statements.Count == 0 ? "The file has no camt.053 statement (Stmt)." : "The file has more than one statement; import one statement per file.");
        }

        var stmt = statements[0];
        var statementRef = Clip(Child(stmt, "Id")?.Value, 100);
        if (statementRef is null)
        {
            return Invalid("The statement has no Id.");
        }

        var iban = Clip(Path(stmt, "Acct", "Id", "IBAN")?.Value, 60);
        if (iban is null)
        {
            return Invalid("The statement does not name the bank account (Acct/Id/IBAN).");
        }

        var currency = Clip(Path(stmt, "Acct", "Ccy")?.Value, 3);
        decimal? opening = null;
        decimal? closing = null;
        BusinessDate? openingDate = null;
        foreach (var balance in stmt.Elements().Where(e => e.Name.LocalName == "Bal"))
        {
            var code = Path(balance, "Tp", "CdOrPrtry", "Cd")?.Value;
            var amountElement = Child(balance, "Amt");
            if (code is not ("OPBD" or "PRCD" or "CLBD") || amountElement is null || !TryAmount(amountElement.Value, out var value))
            {
                continue;
            }

            if (string.Equals(Child(balance, "CdtDbtInd")?.Value, "DBIT", StringComparison.Ordinal))
            {
                value = -value;
            }

            if (code == "CLBD")
            {
                closing = value;
            }
            else
            {
                opening = value;
                if (TryDate(DateOf(Child(balance, "Dt")), out var d))
                {
                    openingDate = d;
                }
            }

            currency ??= Clip(amountElement.Attribute("Ccy")?.Value, 3);
        }

        var lines = new List<ParsedStatementLine>();
        foreach (var entry in stmt.Elements().Where(e => e.Name.LocalName == "Ntry"))
        {
            var status = Child(entry, "Sts");
            var statusCode = Child(status, "Cd")?.Value ?? status?.Value;
            if (statusCode is not null && !string.Equals(statusCode.Trim(), "BOOK", StringComparison.Ordinal))
            {
                continue; // pending or information entries are not on the account yet
            }

            var lineNo = lines.Count + 1;
            var amountElement = Child(entry, "Amt");
            if (amountElement is null || !TryAmount(amountElement.Value, out var amount) || amount <= 0 || amount * 100m % 1m != 0m)
            {
                return Invalid($"Entry {lineNo}: the amount is missing, not positive or not in minor units.");
            }

            var entryCurrency = Clip(amountElement.Attribute("Ccy")?.Value, 3);
            currency ??= entryCurrency;
            if (entryCurrency is null || entryCurrency != currency)
            {
                return Invalid($"Entry {lineNo}: the currency differs from the statement currency.");
            }

            var indicator = Child(entry, "CdtDbtInd")?.Value;
            if (indicator is not ("CRDT" or "DBIT"))
            {
                return Invalid($"Entry {lineNo}: the credit/debit indicator is missing.");
            }

            var credit = indicator == "CRDT";
            if (string.Equals(Child(entry, "RvslInd")?.Value, "true", StringComparison.OrdinalIgnoreCase))
            {
                credit = !credit; // the reversal of a credit takes the money back
            }

            if (!TryDate(DateOf(Child(entry, "BookgDt")), out var booking))
            {
                return Invalid($"Entry {lineNo}: the booking date is missing.");
            }

            BusinessDate? value = TryDate(DateOf(Child(entry, "ValDt")), out var valueDate) ? valueDate : null;
            var endToEnd = Clip(entry.Descendants().FirstOrDefault(e => e.Name.LocalName == "EndToEndId")?.Value, MaxEndToEnd);
            if (string.Equals(endToEnd, "NOTPROVIDED", StringComparison.OrdinalIgnoreCase))
            {
                endToEnd = null;
            }

            var remittance = string.Join(' ', entry.Descendants().Where(e => e.Name.LocalName == "Ustrd").Select(e => e.Value.Trim()).Where(t => t.Length > 0));
            if (remittance.Length == 0)
            {
                remittance = entry.Descendants().FirstOrDefault(e => e.Name.LocalName == "Ref" && e.Parent?.Name.LocalName == "CdtrRefInf")?.Value ?? Child(entry, "AddtlNtryInf")?.Value ?? string.Empty;
            }

            lines.Add(new ParsedStatementLine(lineNo, credit ? "CREDIT" : "DEBIT", booking, value, amount, Clip(remittance, MaxReference), endToEnd));
            if (lines.Count > MaxLines)
            {
                return Invalid($"The statement has more than {MaxLines} lines.");
            }
        }

        if (currency is null || !Currency.TryFromCode(currency, out _))
        {
            return Invalid("The statement currency is missing or unknown.");
        }

        BusinessDate from;
        BusinessDate to;
        if (TryDate(Path(stmt, "FrToDt", "FrDtTm")?.Value, out var f) && TryDate(Path(stmt, "FrToDt", "ToDtTm")?.Value, out var t))
        {
            (from, to) = (f, t);
        }
        else if (lines.Count > 0)
        {
            (from, to) = (lines.Min(l => l.BookingDate), lines.Max(l => l.BookingDate));
        }
        else if (openingDate is { } o)
        {
            (from, to) = (o, o);
        }
        else
        {
            return Invalid("The statement has no period and no lines.");
        }

        if (to < from)
        {
            return Invalid("The statement period ends before it starts.");
        }

        return new ParsedStatement(statementRef, iban, currency, from, to, opening, closing, lines);
    }

    // ---- CSV ----

    private static readonly string[] CsvColumns = ["bookingDate", "valueDate", "amount", "currency", "reference", "endToEndId"];

    public static Result<ParsedStatement> ParseCsv(string text, string contentHash)
    {
        ArgumentNullException.ThrowIfNull(contentHash);
        var rows = new List<(int Number, List<string> Cells)>();
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (!TrySplit(line, out var cells))
            {
                return Invalid($"Line {lineNumber}: an opening quote is never closed.");
            }

            rows.Add((lineNumber, cells));
        }

        if (rows.Count == 0)
        {
            return Invalid("The file is empty.");
        }

        var header = rows[0].Cells.Select(c => c.Trim()).ToList();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++)
        {
            if (!CsvColumns.Contains(header[i], StringComparer.OrdinalIgnoreCase) || !index.TryAdd(header[i], i))
            {
                return Invalid($"Line {rows[0].Number}: the header has an unknown or repeated column.");
            }
        }

        foreach (var required in new[] { "bookingDate", "amount", "currency", "reference" })
        {
            if (!index.ContainsKey(required))
            {
                return Invalid($"Line {rows[0].Number}: the header lacks the column {required}.");
            }
        }

        string? Cell(List<string> cells, string name) => index.TryGetValue(name, out var i) && i < cells.Count ? cells[i].Trim() : null;

        var lines = new List<ParsedStatementLine>();
        string? currency = null;
        foreach (var (number, cells) in rows.Skip(1))
        {
            if (lines.Count >= MaxLines)
            {
                return Invalid($"The statement has more than {MaxLines} lines.");
            }

            if (cells.Count != header.Count)
            {
                return Invalid($"Line {number}: expected {header.Count} columns, found {cells.Count}.");
            }

            if (!TryDate(Cell(cells, "bookingDate"), out var booking) || Cell(cells, "bookingDate")!.Length != 10)
            {
                return Invalid($"Line {number}: the booking date is not yyyy-MM-dd.");
            }

            BusinessDate? value = null;
            if (Cell(cells, "valueDate") is { Length: > 0 } valueText)
            {
                if (valueText.Length != 10 || !TryDate(valueText, out var parsedValue))
                {
                    return Invalid($"Line {number}: the value date is not yyyy-MM-dd.");
                }

                value = parsedValue;
            }

            if (!TryAmount(Cell(cells, "amount"), out var signed) || signed == 0 || signed * 100m % 1m != 0m)
            {
                return Invalid($"Line {number}: the amount is not a non-zero number in minor units.");
            }

            var lineCurrency = Cell(cells, "currency");
            if (lineCurrency is null || !Currency.TryFromCode(lineCurrency, out _))
            {
                return Invalid($"Line {number}: the currency is not a known code.");
            }

            currency ??= lineCurrency;
            if (lineCurrency != currency)
            {
                return Invalid($"Line {number}: the currency differs from the first line.");
            }

            lines.Add(new ParsedStatementLine(
                lines.Count + 1, signed > 0 ? "CREDIT" : "DEBIT", booking, value, Math.Abs(signed), Clip(Cell(cells, "reference"), MaxReference), Clip(Cell(cells, "endToEndId"), MaxEndToEnd)));
        }

        if (lines.Count == 0)
        {
            return Invalid("The statement has no lines.");
        }

        return new ParsedStatement("CSV-" + contentHash[..16], null, currency!, lines.Min(l => l.BookingDate), lines.Max(l => l.BookingDate), null, null, lines);
    }

    /// <summary>Splits one CSV line on commas; a value may be quoted with doubled quotes inside. False when a quote is left open.</summary>
    private static bool TrySplit(string line, out List<string> cells)
    {
        cells = [];
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        cells.Add(current.ToString());
        return !quoted;
    }
}
