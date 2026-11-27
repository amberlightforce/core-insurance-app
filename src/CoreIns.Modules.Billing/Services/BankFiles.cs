using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Xml;
using CoreIns.SharedKernel;

namespace CoreIns.Modules.Billing.Services;

/// <summary>
/// Where released bank files go (BO-BIL-10): the blob port of the batch release. The file carries IBANs and payee names (P2), so it
/// is handed to the store at release and never kept in the BIL tables. Outside Production the in-memory stub is bound; in Production
/// nothing is, and a batch can be neither prepared nor released (fail closed) until a real adapter exists.
/// </summary>
internal interface IBankFileStore
{
    /// <summary>Writes the file under <paramref name="reference"/> (idempotent: the same reference and content is one file).</summary>
    Task StoreAsync(string reference, byte[] content, CancellationToken cancellationToken);
}

/// <summary>Bank-file store stub: keeps the files in memory so tests can read them. Never bound in Production.</summary>
internal sealed class InMemoryBankFileStore : IBankFileStore
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public Task StoreAsync(string reference, byte[] content, CancellationToken cancellationToken)
    {
        _files[reference] = content;
        return Task.CompletedTask;
    }

    /// <summary>The stored file, or null.</summary>
    public byte[]? Read(string reference) => _files.TryGetValue(reference, out var content) ? content : null;
}

/// <summary>One credit transfer of the file; the IBAN is decrypted for the file only.</summary>
internal sealed record Pain001Transfer(string EndToEndId, decimal Amount, string CreditorName, string CreditorIban, string? Remittance);

/// <summary>
/// SEPA credit-transfer initiation (ISO 20022 pain.001.001.03) of one batch, built deterministically: the transfers are ordered by end-to-end
/// id, nothing depends on the clock but the creation instant stored with the batch, and the amounts are fixed-point with two decimals, so
/// the same batch always yields the same bytes and the same SHA-256 (BO-BIL-10). One payment-information block: one debtor account, one
/// requested execution date.
/// </summary>
internal static class Pain001
{
    public const string Namespace = "urn:iso:std:iso:20022:tech:xsd:pain.001.001.03";

    public static string Build(Guid batchId, Instant createdAt, string initiatingParty, string debtorIban, BusinessDate valueDate, IReadOnlyList<Pain001Transfer> transfers)
    {
        ArgumentNullException.ThrowIfNull(transfers);
        var ordered = transfers.OrderBy(t => t.EndToEndId, StringComparer.Ordinal).ToList();
        var count = ordered.Count.ToString(CultureInfo.InvariantCulture);
        var control = Amount(ordered.Sum(t => t.Amount));
        var messageId = "BIL" + batchId.ToString("N");
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, OmitXmlDeclaration = false, NewLineChars = "\n" };
        using var buffer = new MemoryStream();
        using (var xml = XmlWriter.Create(buffer, settings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("Document", Namespace);
            xml.WriteStartElement("CstmrCdtTrfInitn");

            xml.WriteStartElement("GrpHdr");
            xml.WriteElementString("MsgId", messageId);
            xml.WriteElementString("CreDtTm", createdAt.ToString());
            xml.WriteElementString("NbOfTxs", count);
            xml.WriteElementString("CtrlSum", control);
            xml.WriteStartElement("InitgPty");
            xml.WriteElementString("Nm", Text(initiatingParty, 70));
            xml.WriteEndElement();
            xml.WriteEndElement();

            xml.WriteStartElement("PmtInf");
            xml.WriteElementString("PmtInfId", messageId);
            xml.WriteElementString("PmtMtd", "TRF");
            xml.WriteElementString("NbOfTxs", count);
            xml.WriteElementString("CtrlSum", control);
            xml.WriteStartElement("PmtTpInf");
            xml.WriteStartElement("SvcLvl");
            xml.WriteElementString("Cd", "SEPA");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteElementString("ReqdExctnDt", valueDate.ToString());
            xml.WriteStartElement("Dbtr");
            xml.WriteElementString("Nm", Text(initiatingParty, 70));
            xml.WriteEndElement();
            xml.WriteStartElement("DbtrAcct");
            xml.WriteStartElement("Id");
            xml.WriteElementString("IBAN", debtorIban);
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteStartElement("DbtrAgt");
            xml.WriteStartElement("FinInstnId");
            xml.WriteElementString("Othr", "NOTPROVIDED");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteElementString("ChrgBr", "SLEV");

            foreach (var t in ordered)
            {
                xml.WriteStartElement("CdtTrfTxInf");
                xml.WriteStartElement("PmtId");
                xml.WriteElementString("EndToEndId", Text(t.EndToEndId, 35));
                xml.WriteEndElement();
                xml.WriteStartElement("Amt");
                xml.WriteStartElement("InstdAmt");
                xml.WriteAttributeString("Ccy", "EUR");
                xml.WriteString(Amount(t.Amount));
                xml.WriteEndElement();
                xml.WriteEndElement();
                xml.WriteStartElement("Cdtr");
                xml.WriteElementString("Nm", Text(t.CreditorName, 70));
                xml.WriteEndElement();
                xml.WriteStartElement("CdtrAcct");
                xml.WriteStartElement("Id");
                xml.WriteElementString("IBAN", t.CreditorIban);
                xml.WriteEndElement();
                xml.WriteEndElement();
                if (!string.IsNullOrWhiteSpace(t.Remittance))
                {
                    xml.WriteStartElement("RmtInf");
                    xml.WriteElementString("Ustrd", Text(t.Remittance, 140));
                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
            }

            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Fixed-point amount with two decimals, invariant culture.</summary>
    public static string Amount(decimal amount) => amount.ToString("F2", CultureInfo.InvariantCulture);

    private static string Text(string value, int max)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
