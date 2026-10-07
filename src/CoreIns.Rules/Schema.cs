using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using CoreIns.Rules.Syntax;

namespace CoreIns.Rules;

/// <summary>A field of an <see cref="ObjectSchema"/>.</summary>
/// <param name="Name">Field name (an identifier).</param>
/// <param name="Type">Declared type.</param>
/// <param name="Nullable">Whether the field may hold <c>null</c> ("no value").</param>
/// <param name="Ordinal">Position of the field in the schema.</param>
public sealed record FieldDefinition(string Name, RuleType Type, bool Nullable, int Ordinal);

/// <summary>A declared top-level input of an expression.</summary>
/// <param name="Name">Input name (an identifier).</param>
/// <param name="Type">Declared type.</param>
/// <param name="Nullable">Whether the input may be <c>null</c>.</param>
/// <param name="Slot">Position of the input in the schema.</param>
public sealed record VariableDefinition(string Name, RuleType Type, bool Nullable, int Slot);

/// <summary>A typed object shape (for example <c>Driver</c> or <c>Vehicle</c>) usable as an input or field type.</summary>
public sealed class ObjectSchema
{
    private readonly Dictionary<string, FieldDefinition> _byName;

    private ObjectSchema(string name, IReadOnlyList<FieldDefinition> fields)
    {
        Name = name;
        Fields = fields;
        _byName = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
    }

    /// <summary>Schema name, used as the type name.</summary>
    public string Name { get; }

    /// <summary>Fields in declaration order.</summary>
    public IReadOnlyList<FieldDefinition> Fields { get; }

    /// <summary>Starts defining a schema.</summary>
    public static Builder Define(string name)
    {
        Identifiers.Validate(name, nameof(name));
        return new Builder(name);
    }

    /// <summary>Looks up a field by name.</summary>
    public bool TryGetField(string name, [NotNullWhen(true)] out FieldDefinition? field) =>
        _byName.TryGetValue(name, out field);

    /// <summary>Starts building a value of this schema.</summary>
    public ObjectValueBuilder NewValue() => new(this);

    /// <inheritdoc />
    public override string ToString() => Name;

    /// <summary>Fluent schema builder.</summary>
    public sealed class Builder
    {
        private readonly string _name;
        private readonly List<FieldDefinition> _fields = new();

        internal Builder(string name) => _name = name;

        /// <summary>Adds a field.</summary>
        public Builder Field(string name, RuleType type, bool nullable = false)
        {
            Identifiers.Validate(name, nameof(name));
            ArgumentNullException.ThrowIfNull(type);
            if (_fields.Any(f => f.Name == name))
            {
                throw new ArgumentException($"duplicate field '{name}' in schema '{_name}'", nameof(name));
            }

            _fields.Add(new FieldDefinition(name, type, nullable, _fields.Count));
            return this;
        }

        /// <summary>Builds the immutable schema.</summary>
        public ObjectSchema Build() => new(_name, _fields.ToArray());
    }
}

/// <summary>Builds an <see cref="ObjectValue"/>, validating each field against the schema.</summary>
public sealed class ObjectValueBuilder
{
    private readonly ObjectSchema _schema;
    private readonly RuleValue?[] _values;

    internal ObjectValueBuilder(ObjectSchema schema)
    {
        _schema = schema;
        _values = new RuleValue?[schema.Fields.Count];
    }

    /// <summary>Sets a field (int values are widened to decimal when the field is decimal).</summary>
    public ObjectValueBuilder Set(string field, RuleValue value)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(value);
        if (!_schema.TryGetField(field, out var def))
        {
            throw new RuleInputException(_schema.Name + "." + field, $"schema '{_schema.Name}' has no field '{field}'");
        }

        _values[def.Ordinal] = ValueConformance.Conform(value, def.Type, def.Nullable, _schema.Name + "." + field);
        return this;
    }

    /// <summary>Builds the value. Unset nullable fields are null; unset required fields are an error.</summary>
    public ObjectValue Build()
    {
        var result = new RuleValue[_values.Length];
        for (int i = 0; i < _values.Length; i++)
        {
            var def = _schema.Fields[i];
            if (_values[i] is { } v)
            {
                result[i] = v;
            }
            else if (def.Nullable)
            {
                result[i] = NullValue.Instance;
            }
            else
            {
                throw new RuleInputException(_schema.Name + "." + def.Name, "required field is not set");
            }
        }

        return new ObjectValue(_schema, result);
    }
}

/// <summary>The declared inputs of an expression or decision table. Expressions type-check against it.</summary>
public sealed class InputSchema
{
    private readonly Dictionary<string, VariableDefinition> _byName;

    private InputSchema(IReadOnlyList<VariableDefinition> variables)
    {
        Variables = variables;
        _byName = variables.ToDictionary(v => v.Name, StringComparer.Ordinal);
    }

    /// <summary>Declared inputs in declaration order.</summary>
    public IReadOnlyList<VariableDefinition> Variables { get; }

    /// <summary>Starts defining an input schema.</summary>
    public static Builder Define() => new();

    /// <summary>Looks up an input by name.</summary>
    public bool TryGetVariable(string name, [NotNullWhen(true)] out VariableDefinition? variable) =>
        _byName.TryGetValue(name, out variable);

    /// <summary>Starts building a set of input values for this schema.</summary>
    public RuleInputs.Builder NewInputs() => new(this);

    /// <summary>Creates an extended schema (same leading slots) with additional nullable variables.</summary>
    internal InputSchema Extend(IEnumerable<(string Name, RuleType Type)> extra)
    {
        var list = new List<VariableDefinition>(Variables);
        foreach (var (name, type) in extra)
        {
            list.Add(new VariableDefinition(name, type, true, list.Count));
        }

        return new InputSchema(list.ToArray());
    }

    /// <summary>Fluent builder.</summary>
    public sealed class Builder
    {
        private readonly List<VariableDefinition> _variables = new();

        internal Builder()
        {
        }

        /// <summary>Declares an input.</summary>
        public Builder Variable(string name, RuleType type, bool nullable = false)
        {
            Identifiers.Validate(name, nameof(name));
            ArgumentNullException.ThrowIfNull(type);
            if (_variables.Any(v => v.Name == name))
            {
                throw new ArgumentException($"duplicate input '{name}'", nameof(name));
            }

            _variables.Add(new VariableDefinition(name, type, nullable, _variables.Count));
            return this;
        }

        /// <summary>Builds the immutable schema.</summary>
        public InputSchema Build() => new(_variables.ToArray());
    }
}

/// <summary>A validated set of input values for one evaluation. Immutable once built.</summary>
public sealed class RuleInputs
{
    private readonly RuleValue[] _values;

    private RuleInputs(InputSchema schema, RuleValue[] values)
    {
        Schema = schema;
        _values = values;
    }

    /// <summary>The schema these inputs conform to.</summary>
    public InputSchema Schema { get; }

    /// <summary>Gets an input value by name.</summary>
    public RuleValue this[string name] =>
        Schema.TryGetVariable(name, out var v)
            ? _values[v.Slot]
            : throw new ArgumentException($"no input named '{name}'", nameof(name));

    internal void CopyTo(RuleValue[] slots) => Array.Copy(_values, slots, _values.Length);

    /// <summary>Fluent builder that validates each value against the schema.</summary>
    public sealed class Builder
    {
        private readonly InputSchema _schema;
        private readonly RuleValue?[] _values;

        internal Builder(InputSchema schema)
        {
            _schema = schema;
            _values = new RuleValue?[schema.Variables.Count];
        }

        /// <summary>Sets an input (int values are widened to decimal when the input is decimal).</summary>
        public Builder Set(string name, RuleValue value)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(value);
            if (!_schema.TryGetVariable(name, out var def))
            {
                throw new RuleInputException(name, "undeclared input");
            }

            _values[def.Slot] = ValueConformance.Conform(value, def.Type, def.Nullable, name);
            return this;
        }

        /// <summary>Builds the inputs. Unset nullable inputs are null; unset required inputs are an error.</summary>
        public RuleInputs Build()
        {
            var result = new RuleValue[_values.Length];
            for (int i = 0; i < _values.Length; i++)
            {
                var def = _schema.Variables[i];
                if (_values[i] is { } v)
                {
                    result[i] = v;
                }
                else if (def.Nullable)
                {
                    result[i] = NullValue.Instance;
                }
                else
                {
                    throw new RuleInputException(def.Name, "required input is not set");
                }
            }

            return new RuleInputs(_schema, result);
        }
    }
}
