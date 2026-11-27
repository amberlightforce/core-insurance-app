using System.Globalization;
using System.Text.Json;

namespace CoreIns.Modules.Migration.Services.Quality;

internal sealed record DqFieldProfileView(string Field, int Present, int NullCount, int BlankCount, string NullRate, int Distinct, string? Min, string? Max);

internal sealed record DqObjectProfileView(string ObjectType, int RecordCount, IReadOnlyList<DqFieldProfileView> Fields);

/// <summary>Profiling of a staged snapshot (BO-MIG-03): per object type record counts and per field null and blank rates, distinct counts, min and max of dates and amounts.</summary>
internal static class DqProfiler
{
    public static List<DqObjectProfileView> Profile(IReadOnlyList<DqRecord> records)
    {
        var result = new List<DqObjectProfileView>();
        foreach (var group in records.GroupBy(r => r.ObjectType, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var fields = new Dictionary<string, Acc>(StringComparer.Ordinal);
            var count = 0;
            foreach (var record in group)
            {
                count++;
                if (record.Data.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var property in record.Data.EnumerateObject())
                {
                    if (!fields.TryGetValue(property.Name, out var acc))
                    {
                        fields[property.Name] = acc = new Acc();
                    }

                    acc.Add(record.Text(property.Name), property.Name);
                }
            }

            result.Add(new DqObjectProfileView(group.Key, count, [.. fields.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Value.ToView(f.Key, count))]));
        }

        return result;
    }

    private sealed class Acc
    {
        private readonly HashSet<string> _distinct = new(StringComparer.Ordinal);
        private int _nonNull;
        private int _blank;
        private DateOnly? _minDate;
        private DateOnly? _maxDate;
        private decimal? _minAmount;
        private decimal? _maxAmount;

        public void Add(string? text, string field)
        {
            if (text is null)
            {
                return;
            }

            _nonNull++;
            if (string.IsNullOrWhiteSpace(text))
            {
                _blank++;
                return;
            }

            _distinct.Add(text);
            if (DqRules.IsDateField(field) && DqRules.TryDate(text.Trim(), out var date))
            {
                _minDate = _minDate is null || date < _minDate ? date : _minDate;
                _maxDate = _maxDate is null || date > _maxDate ? date : _maxDate;
            }
            else if (DqRules.IsAmountField(field) && DqRules.TryAmount(text.Trim(), out var amount))
            {
                _minAmount = _minAmount is null || amount < _minAmount ? amount : _minAmount;
                _maxAmount = _maxAmount is null || amount > _maxAmount ? amount : _maxAmount;
            }
        }

        public DqFieldProfileView ToView(string field, int records)
        {
            var nulls = records - _nonNull;
            var rate = records == 0 ? 0m : (nulls + _blank) / (decimal)records;
            string? min = null;
            string? max = null;
            if (_minDate is not null)
            {
                min = _minDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                max = _maxDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            else if (_minAmount is not null)
            {
                min = _minAmount.Value.ToString(CultureInfo.InvariantCulture);
                max = _maxAmount!.Value.ToString(CultureInfo.InvariantCulture);
            }

            return new DqFieldProfileView(field, _nonNull, nulls, _blank, rate.ToString("0.0000", CultureInfo.InvariantCulture), _distinct.Count, min, max);
        }
    }
}
