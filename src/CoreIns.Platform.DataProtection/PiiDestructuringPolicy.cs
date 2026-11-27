using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Serilog.Core;
using Serilog.Events;

namespace CoreIns.Platform.DataProtection;

/// <summary>
/// Serilog destructuring policy (NX-OT-13; NFR-PTY-011: no P2/P3 values in logs). When an object is logged with <c>{@Obj}</c>,
/// every public property classified <see cref="DataClass.P2"/> or higher by <see cref="DataClassAttribute"/>, and every property
/// whose name looks like a financial or national identifier (AFM, IBAN, tax id, card number...), is replaced with
/// <see cref="Mask"/>. Other properties are destructured normally. Scalars logged directly (<c>{Afm}</c>) are not covered:
/// callers must not log identifiers as plain values (TODO(hardening): add an analyzer).
/// </summary>
public sealed class PiiDestructuringPolicy : IDestructuringPolicy
{
    public const string Mask = "***";

    private static readonly string[] SensitiveNameParts = ["afm", "iban", "taxid", "vatnumber", "nationalid", "passport", "cardnumber"];

    public bool TryDestructure(object value, ILogEventPropertyValueFactory propertyValueFactory, [NotNullWhen(true)] out LogEventPropertyValue? result)
    {
        result = null;
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || value is System.Collections.IEnumerable || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
        {
            return false;
        }

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).ToList();
        if (!properties.Any(IsSensitive))
        {
            return false; // nothing to mask: let Serilog destructure it
        }

        var members = new List<LogEventProperty>();
        foreach (var property in properties)
        {
            LogEventPropertyValue propertyValue;
            if (IsSensitive(property))
            {
                propertyValue = new ScalarValue(Mask);
            }
            else
            {
                object? raw;
                try
                {
                    raw = property.GetValue(value);
                }
                catch (TargetInvocationException)
                {
                    continue;
                }

                propertyValue = propertyValueFactory.CreatePropertyValue(raw, destructureObjects: true);
            }

            members.Add(new LogEventProperty(property.Name, propertyValue));
        }

        result = new StructureValue(members, type.Name);
        return true;
    }

    private static bool IsSensitive(PropertyInfo property)
    {
        var attribute = property.GetCustomAttribute<DataClassAttribute>(inherit: true);
        if (attribute is not null)
        {
            return attribute.Classification >= DataClass.P2;
        }

        var name = property.Name;
        return SensitiveNameParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
    }
}
