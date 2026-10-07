using System.Diagnostics.CodeAnalysis;

// The rule language's type names are the CEL type names (int, decimal, string, object). Renaming them to avoid the
// .NET keyword-like identifiers would make the API diverge from the language it models.
[assembly: SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Mirrors CEL type names.", Scope = "member", Target = "~F:CoreIns.Rules.RuleTypeKind.Int")]
[assembly: SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Mirrors CEL type names.", Scope = "member", Target = "~F:CoreIns.Rules.RuleTypeKind.Decimal")]
[assembly: SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Mirrors CEL type names.", Scope = "member", Target = "~F:CoreIns.Rules.RuleTypeKind.String")]
[assembly: SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Mirrors CEL type names.", Scope = "member", Target = "~F:CoreIns.Rules.RuleTypeKind.Object")]
[assembly: SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Mirrors CEL type names.", Scope = "member", Target = "~P:CoreIns.Rules.RuleType.Int")]
[assembly: SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Mirrors CEL type names.", Scope = "member", Target = "~P:CoreIns.Rules.RuleType.Decimal")]
[assembly: SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Mirrors CEL type names.", Scope = "member", Target = "~P:CoreIns.Rules.RuleType.String")]
