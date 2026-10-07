using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Shouldly;
using Xunit;

namespace CoreIns.Rules.Tests;

public class ValuesAndInputsTests
{
    [Fact]
    public void Inputs_are_validated_against_the_schema()
    {
        Should.Throw<RuleInputException>(() => T.Schema.NewInputs().Set("x", 1).Build()).Path.ShouldBe("y");
        Should.Throw<RuleInputException>(() => T.Inputs().Set("x", "seven")).Path.ShouldBe("x");
        Should.Throw<RuleInputException>(() => T.Inputs().Set("unknown", 1)).Code.ShouldBe(RuleErrorCode.InputInvalid);
        Should.Throw<RuleInputException>(() => T.Inputs().Set("d", RuleValue.Null)).Message.ShouldContain("null is not allowed");
        Should.Throw<RuleInputException>(() => T.Inputs().Set("ints", RuleValue.List(1, "a"))).Path.ShouldBe("ints[1]");
        Should.Throw<RuleInputException>(() => T.Inputs().Set("ints", RuleValue.List(1, RuleValue.Null))).Path.ShouldBe("ints[1]");
        Should.Throw<RuleInputException>(() => T.Inputs().Set("names", RuleValue.Map(new[] { new KeyValuePair<RuleValue, RuleValue>("a", "b") })))
            .Path.ShouldBe("names[\"a\"]");
        Should.Throw<RuleInputException>(() => T.Inputs().Set("vehicle", T.Default["policy"])).Message.ShouldContain("schema Vehicle");
        Should.Throw<RuleInputException>(() => T.Inputs().Set("drivers", RuleValue.List(T.Default["vehicle"]))).Path.ShouldBe("drivers[0]");
    }

    [Fact]
    public void Int_inputs_widen_to_decimal_and_nullable_inputs_default_to_null()
    {
        var inputs = T.Inputs().Set("d", 5).Set("decs", RuleValue.List(1, 2)).Build();
        inputs["d"].ShouldBeOfType<DecimalValue>();
        ((ListValue)inputs["decs"]).Items.ShouldAllBe(v => v is DecimalValue);
        inputs["n"].ShouldBe(RuleValue.Null);
        Should.Throw<ArgumentException>(() => inputs["missing"]);
        var map = T.Inputs().Set("names", RuleValue.Map(new[] { new KeyValuePair<RuleValue, RuleValue>("k", 1) })).Build()["names"];
        map.ShouldBeOfType<MapValue>().Count.ShouldBe(1);
    }

    [Fact]
    public void Inputs_from_another_schema_are_rejected()
    {
        var other = InputSchema.Define().Variable("x", RuleType.Int).Build();
        Should.Throw<ArgumentException>(() => T.Env.Compile("x").Evaluate(other.NewInputs().Set("x", 1).Build()));
    }

    [Fact]
    public void Object_values_are_validated()
    {
        Should.Throw<RuleInputException>(() => T.Vehicle.NewValue().Set("colour", "red"));
        Should.Throw<RuleInputException>(() => T.Vehicle.NewValue().Set("value", 1m).Build()).Path.ShouldBe("Vehicle.powerKw");
        var v = T.Vehicle.NewValue().Set("value", 1).Set("powerKw", 50).Set("usage", "PRIVATE").Build();
        v.Get("value").ShouldBeOfType<DecimalValue>();
        v.Get("trackerFitted").ShouldBe(RuleValue.Null);
        Should.Throw<ArgumentException>(() => v.Get("colour"));
        v.ToString().ShouldBe("Vehicle{value: 1, powerKw: 50, usage: \"PRIVATE\", trackerFitted: null}");
        v.Schema.ShouldBeSameAs(T.Vehicle);
        T.Vehicle.ToString().ShouldBe("Vehicle");
    }

    [Fact]
    public void Schema_names_must_be_valid_identifiers()
    {
        Should.Throw<ArgumentException>(() => ObjectSchema.Define("1abc"));
        Should.Throw<ArgumentException>(() => ObjectSchema.Define("Ok").Field("if", RuleType.Int));
        Should.Throw<ArgumentException>(() => ObjectSchema.Define("Ok").Field("in", RuleType.Int));
        Should.Throw<ArgumentException>(() => ObjectSchema.Define("Ok").Field("a-b", RuleType.Int));
        Should.Throw<ArgumentException>(() => ObjectSchema.Define("Ok").Field("a", RuleType.Int).Field("a", RuleType.Int));
        Should.Throw<ArgumentException>(() => InputSchema.Define().Variable("x", RuleType.Int).Variable("x", RuleType.Int));
        Should.Throw<ArgumentException>(() => InputSchema.Define().Variable("", RuleType.Int));
    }

    [Fact]
    public void Values_have_value_equality_and_numeric_cross_kind_equality()
    {
        RuleValue one = 1;
        RuleValue oneDec = 1.0m;
        one.Equals(oneDec).ShouldBeTrue();
        oneDec.Equals(one).ShouldBeTrue();
        one.GetHashCode().ShouldBe(oneDec.GetHashCode());
        IntValue.Of(1000).ShouldBe(IntValue.Of(1000));
        IntValue.Of(5).ShouldBeSameAs(IntValue.Of(5));
        ((RuleValue)"a").Equals((RuleValue)"a").ShouldBeTrue();
        ((RuleValue)"a").Equals((object)"a").ShouldBeFalse();
        ((RuleValue)"a").GetHashCode().ShouldBe(new StringValue("a").GetHashCode());
        ((RuleValue)true).ShouldBeSameAs(BoolValue.True);
        BoolValue.True.GetHashCode().ShouldNotBe(BoolValue.False.GetHashCode());
        ((RuleValue)(string?)null).ShouldBe(RuleValue.Null);
        RuleValue.Null.GetHashCode().ShouldBe(0);
        ((RuleValue)new DateOnly(2026, 1, 1)).Equals(new DateValue(new DateOnly(2026, 1, 1))).ShouldBeTrue();
        new DateValue(new DateOnly(2026, 1, 1)).GetHashCode().ShouldBe(new DateValue(new DateOnly(2026, 1, 1)).GetHashCode());
        var ts = new DateTimeOffset(2026, 1, 1, 12, 0, 0, new TimeSpan(2, 0, 0));
        ((RuleValue)ts).ShouldBe(new TimestampValue(ts.ToUniversalTime()));
        new TimestampValue(ts).Value.Offset.ShouldBe(TimeSpan.Zero);
        new TimestampValue(ts).GetHashCode().ShouldBe(new TimestampValue(ts.ToUniversalTime()).GetHashCode());
        ((RuleValue)new TimeSpan(0, 0, 1)).ShouldBe(new DurationValue(new TimeSpan(0, 0, 1)));
        new DurationValue(new TimeSpan(0, 0, 1)).GetHashCode().ShouldBe(new TimeSpan(0, 0, 1).GetHashCode());
        RuleValue.List(1, 2).ShouldBe(RuleValue.List(1, 2));
        RuleValue.List(1, 2).GetHashCode().ShouldBe(RuleValue.List(1, 2).GetHashCode());
        RuleValue.List(1, 2).ShouldNotBe(RuleValue.List(2, 1));
        RuleValue.List(new List<RuleValue> { 1 }).Items.Count.ShouldBe(1);
        var m1 = RuleValue.Map(new[] { new KeyValuePair<RuleValue, RuleValue>("a", 1), new KeyValuePair<RuleValue, RuleValue>("b", 2) });
        var m2 = RuleValue.Map(new[] { new KeyValuePair<RuleValue, RuleValue>("b", 2), new KeyValuePair<RuleValue, RuleValue>("a", 1) });
        m1.ShouldBe(m2);
        m1.GetHashCode().ShouldBe(m2.GetHashCode());
        m1.ToString().ShouldBe("{\"a\": 1, \"b\": 2}");
        m1.TryGetValue("z", out var missing).ShouldBeFalse();
        missing.ShouldBe(RuleValue.Null);
        T.Default["drivers"].GetHashCode().ShouldBe(T.Inputs().Build()["drivers"].GetHashCode());
        T.Default["vehicle"].Equals(T.Default["policy"]).ShouldBeFalse();
        ((RuleValue)1).Equals("x").ShouldBeFalse();
        ((RuleValue)1.5m).Equals((RuleValue)"x").ShouldBeFalse();
        RuleValue.Null.Equals(1).ShouldBeFalse();
    }

    [Fact]
    public void Map_construction_validates_keys()
    {
        Should.Throw<ArgumentException>(() => RuleValue.Map(new[] { new KeyValuePair<RuleValue, RuleValue>(1.5m, 1) }));
        Should.Throw<ArgumentException>(() => RuleValue.Map(new[] { new KeyValuePair<RuleValue, RuleValue>("a", 1), new KeyValuePair<RuleValue, RuleValue>("a", 2) }));
    }

    [Fact]
    public void Display_text_is_unambiguous()
    {
        new StringValue("a\"b\\c\r\t\u0001").ToString().ShouldBe("\"a\\\"b\\\\c\\r\\t\\u0001\"");
        new DurationValue(TimeSpan.MinValue).ToString().ShouldBe("duration(\"-922337203685.4775808s\")");
        new DurationValue(TimeSpan.FromTicks(1)).ToString().ShouldBe("duration(\"0.0000001s\")");
        Should.Throw<ArgumentNullException>(() => new StringValue(null!));
    }

    [Fact]
    public void Decimal_rounding_utility_validates_arguments()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => DecimalRounding.Round(1m, -1, RoundingMode.HalfUp));
        Should.Throw<ArgumentOutOfRangeException>(() => DecimalRounding.Round(1m, 29, RoundingMode.HalfUp));
        Should.Throw<ArgumentOutOfRangeException>(() => DecimalRounding.Round(1m, 2, (RoundingMode)99));
        DecimalRounding.Round(decimal.MaxValue, 2, RoundingMode.HalfUp).ShouldBe(decimal.MaxValue);
        DecimalRounding.Round(0.0000000000000000000000000001m, 28, RoundingMode.HalfDown).ShouldBe(0.0000000000000000000000000001m);
        DecimalRounding.TryParseMode("HalfUp", out var mode).ShouldBeTrue();
        mode.ShouldBe(RoundingMode.HalfUp);
        DecimalRounding.TryParseMode("halfup", out _).ShouldBeFalse();
    }

    /// <summary>
    /// ADR rule 2 ("no double or float anywhere"): no field, property, parameter, return value or local variable in
    /// the rules assembly may be System.Double or System.Single.
    /// </summary>
    [Fact]
    public void Rules_assembly_never_uses_binary_floating_point()
    {
        var forbidden = new[] { typeof(double), typeof(float) };
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var offenders = new List<string>();
        foreach (var type in typeof(RuleValue).Assembly.GetTypes())
        {
            offenders.AddRange(type.GetFields(all).Where(f => forbidden.Contains(f.FieldType)).Select(f => $"{type}.{f.Name}"));
            offenders.AddRange(type.GetProperties(all).Where(p => forbidden.Contains(p.PropertyType)).Select(p => $"{type}.{p.Name}"));
            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                if (method is MethodInfo mi && forbidden.Contains(mi.ReturnType))
                {
                    offenders.Add($"{type}.{method.Name} returns floating point");
                }

                offenders.AddRange(method.GetParameters().Where(p => forbidden.Contains(p.ParameterType)).Select(p => $"{type}.{method.Name}({p.Name})"));
                var body = method.GetMethodBody();
                if (body is not null)
                {
                    offenders.AddRange(body.LocalVariables.Where(l => forbidden.Contains(l.LocalType)).Select(l => $"{type}.{method.Name} local #{l.LocalIndex}"));
                }
            }
        }

        offenders.ShouldBeEmpty();
    }
}
