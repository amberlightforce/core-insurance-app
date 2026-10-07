# CoreIns.Rules: rule-expression language (CEL subset) and decision tables

This is the programme's one typed, deterministic rule-expression language (decision D10, D-ARC-10, REQ-PLT-364). It is
compatible with a subset of the open [CEL](https://github.com/google/cel-spec) specification and is built in-house
with no package dependencies. PFC (product rules), UW (rule sets and variables), RAT (step conditions and computed
expressions), BIL/FIN (ledger amount expressions), PLT (decision-table runtime) and MKT (pack rules and constraints)
all use it.

**Language version: `1.0`.** The version is part of every canonical text, so changing the language changes every
content hash.

## Contents

1. [Usage](#1-usage)
2. [Types](#2-types)
3. [Grammar](#3-grammar)
4. [Operators](#4-operators)
5. [Functions and macros](#5-functions-and-macros)
6. [Semantics](#6-semantics)
7. [Errors](#7-errors)
8. [Limits](#8-limits)
9. [Explainability: traces and content hashes](#9-explainability-traces-and-content-hashes)
10. [Decision tables](#10-decision-tables)
11. [Host functions](#11-host-functions)
12. [Differences from full CEL](#12-differences-from-full-cel)

## 1. Usage

```csharp
var driver = ObjectSchema.Define("Driver")
    .Field("birthDate", RuleType.Date)
    .Field("claimsLast3Years", RuleType.Int)
    .Build();

var schema = InputSchema.Define()
    .Variable("premium", RuleType.Decimal)
    .Variable("effectiveDate", RuleType.Date)          // "today" is always an explicit input
    .Variable("drivers", RuleType.ListOf(RuleType.ObjectOf(driver)))
    .Build();

var env = RuleEnvironment.Create(schema);               // optional: RuleLimits, host functions

// Compile = parse + type-check against the schema. Errors carry a code and a line:column position.
CompiledExpression expr = env.Compile("round(premium * 0.15, 2, \"HalfUp\")", RuleType.Decimal);

RuleInputs inputs = schema.NewInputs()
    .Set("premium", 100.10m)
    .Set("effectiveDate", new DateOnly(2026, 11, 1))
    .Set("drivers", RuleValue.List(driver.NewValue().Set("birthDate", new DateOnly(2009, 1, 15)).Set("claimsLast3Years", 0).Build()))
    .Build();

EvaluationResult result = expr.Evaluate(inputs, new EvaluationOptions { Trace = true });
// result.Value == 15.02 ; result.Trace lists every sub-expression and its value ; result.Error is typed on failure
```

* `CompiledExpression` is immutable and thread-safe. Compile once, evaluate concurrently. `env.GetOrCompile(...)`
  caches compiled expressions per environment.
* `ContentHash` is the lower-case hex SHA-256 of three length-prefixed parts: `CanonicalText` (a whitespace- and
  comment-free S-expression of the syntax tree), the result type, and `EnvironmentFingerprint` (every
  `RuleLimits` budget value, the input schema with types, nullability and object shapes, and the signatures of the
  host functions the expression calls; ruling D-ARC-10e). Equal hashes mean equal logic, equal inputs contract and
  equal limits: for the same inputs they give the same business result or the same typed failure. Use the hash in
  the configuration hash.
* Evaluation fails closed. A failed evaluation returns `IsSuccess == false` and a typed `RuleEvaluationError`, never
  a partial value.
* `RULE-TIMEOUT` and `RULE-CANCELLED` are **operational** failures (wall clock, host load, caller cancellation):
  they are fail-closed and retryable, and never a different business result. Every other error code is a
  deterministic function of the expression, its limits and its inputs.

## 2. Types

| Type | CLR value | Literal / constructor | Notes |
|---|---|---|---|
| `int` | `long` | `42`, `-7`, `0x1F` | 64-bit, checked: overflow is an error |
| `decimal` | `decimal` | `100.10`, `.5`, `-0.15` | **Every number with a fraction is decimal. There is no double.** Up to 28 significant digits; the scale is preserved (`1.50` stays `1.50`). `+ - *` never round (see §6) |
| `string` | `string` | `"a"`, `'a'`, `"""multi"""`, `r"raw\d"` | CEL escapes `\n \t \" \\ \xHH \uHHHH \UHHHHHHHH \ooo` |
| `bool` | `bool` | `true`, `false` | |
| `null_type` | — | `null` | "No value". Declared nullable inputs and fields can hold it |
| `date` | `DateOnly` | `date("2026-11-01")` | Calendar (business) date. Insurance extension, not in CEL |
| `timestamp` | `DateTimeOffset` (UTC) | `timestamp("2026-11-01T10:00:00Z")` | RFC 3339, `Z` or `±hh:mm` offset; normalised to UTC |
| `duration` | `TimeSpan` | `duration("1h30m")`, `duration("90s")` | Units `h m s ms us ns`, decimal amounts allowed. **Resolution is 100 ns**: a literal that is not a whole number of 100 ns (`"150ns"`) is rejected, never rounded |
| `list(T)` | `ListValue` | `[1, 2, 3]` | Homogeneous. `[1, 2.5]` is `list(decimal)` |
| `map(K, V)` | `MapValue` | `{"a": 1}` | Keys are `int`, `string` or `bool`. Insertion order is kept for iteration |
| object | `ObjectValue` | from inputs only | Declared with `ObjectSchema`; fields are selected with `.` |
| `dyn` | any | — | Only the element type of `[]`, or a host function's declared result. Checked at run time |

**Nullability (ruling D-ARC-10c):** types are non-null by default. Declare "no value" explicitly:
`InputSchema.Define().Variable("n", RuleType.Decimal.Nullable())` (or `nullable: true`), `Field(..., nullable: true)`,
an expected result type `RuleType.Decimal.Nullable()`, or a nullable decision-table column or output type. An
expression compiled with a non-nullable expected type that produces `null` at run time fails with
`RULE-NULL-VALUE`; the literal `null` for a non-nullable expected type fails compilation. Nullability is an
annotation: it does not affect type equality, but it is part of type names (`decimal?`) and of content hashes.

**Implicit widening:** `int` widens to `decimal` wherever a decimal is expected: mixed arithmetic, list and map
literals, ternary branches, the expected result type, host-function arguments and input values. Nothing else is
converted implicitly.

## 3. Grammar

CEL precedence, lowest first. `//` starts a line comment. Identifiers are ASCII `[A-Za-z_][A-Za-z0-9_]*`.

```
Expr     = Or ["?" Or ":" Expr]
Or       = And {"||" And}
And      = Relation {"&&" Relation}
Relation = Add {("<" | "<=" | ">" | ">=" | "==" | "!=" | "in") Add}
Add      = Mul {("+" | "-") Mul}
Mul      = Unary {("*" | "/" | "%") Unary}
Unary    = Member | "!" Unary | "-" Unary
Member   = Primary {"." IDENT ["(" [Args] ")"] | "[" Expr "]"}
Primary  = IDENT ["(" [Args] ")"] | "(" Expr ")"
         | "[" [Expr {"," Expr}] [","] "]"
         | "{" [Expr ":" Expr {"," Expr ":" Expr}] [","] "}"
         | INT | DECIMAL | STRING | "true" | "false" | "null"
```

Reserved words (rejected as identifiers): `as break const continue else for function if import let loop package
namespace return var void while`. Procedural constructs (loops, assignment) do not exist.

## 4. Operators

| Operator | Operands | Result |
|---|---|---|
| `a ? b : c` | `bool`, then two types that unify | unified type; only the chosen branch is evaluated |
| `\|\|`, `&&` | `bool` | `bool`. Short-circuit, with CEL error absorption (see §6) |
| `!` | `bool` | `bool` |
| `-` (unary) | `int`, `decimal`, `duration` | same |
| `+` | numbers; `string + string`; `list + list`; `timestamp + duration`; `duration + timestamp`; `duration + duration` | |
| `-` | numbers; `timestamp - timestamp` (gives `duration`); `timestamp - duration`; `duration - duration` | |
| `*`, `/` | numbers | `int` if both are `int` (integer division truncates toward zero), otherwise `decimal` |
| `%` | `int`, `int` | `int` (the sign follows the dividend) |
| `==`, `!=` | any comparable pair (same type, int/decimal, anything with `null`) | `bool`. Deep equality for lists, maps and objects |
| `<`, `<=`, `>`, `>=` | numbers, `string` (ordinal), `bool`, `date`, `timestamp`, `duration` | `bool` |
| `x in list`, `k in map` | element or key type | `bool` |
| `list[i]`, `map[k]`, `map.key` | | element or value. Out of range or missing key is an error |

## 5. Functions and macros

These are the adopted functions (`RuleLanguage.GlobalFunctions`, `MemberFunctions`, `Macros`). Every one has
conformance cases in `ConformanceTests`, and anything else fails compilation.

| Function | Signature | Notes |
|---|---|---|
| `has(x.f)` | object field or map key | `true` if the field is non-null or the key is present |
| `l.all(x, p)` / `l.exists(x, p)` / `l.exists_one(x, p)` | list or map (iterates keys) | `bool` |
| `l.map(x, f)` / `l.map(x, p, f)` / `l.filter(x, p)` | | `list` |
| `size(x)`, `x.size()` | `string` (Unicode code points), `list`, `map` | `int` |
| `int(x)` | `int`, `decimal` (truncates toward zero), `string` | `int` |
| `decimal(x)` | `int`, `decimal`, `string` (invariant, `-12.50`) | `decimal` |
| `string(x)` | scalars | invariant text (`10.50`, `2026-11-01`, `2026-11-01T10:00:00Z`, `3600s`) |
| `date(s)` / `date(ts)` | `yyyy-MM-dd` / UTC date of a timestamp | `date` |
| `timestamp(s)`, `duration(s)` | RFC 3339 / CEL duration text | literal arguments are parsed and checked at compile time |
| `round(x, places, mode)` | `places` 0..28; `mode` is a **string literal**: `HalfEven`, `HalfUp`, `HalfDown`, `Up`, `Down`, `Ceiling`, `Floor` | `decimal` with exactly `places` fraction digits (`round(15, 2, "HalfUp")` gives `15.00`) |
| `abs(x)` | number | same |
| `min(a, b, ...)`, `max(a, b, ...)`, `min(list)`, `max(list)` | orderable values | an empty list is an error |
| `sum(list)` | list of numbers | `0` (or `0` decimal) for an empty list |
| `ageAt(birth, asOf)` | `date, date` | whole years; an error if `asOf < birth` |
| `yearsBetween(a, b)`, `monthsBetween(a, b)` | `date, date` | whole years or months, negative if `b < a` |
| `daysBetween(a, b)` | `date, date` | `b - a` in days |
| `addDays(d, n)`, `addMonths(d, n)`, `addYears(d, n)` | `date, int` | month-end clamping (`addMonths(2026-01-31, 1)` gives `2026-02-28`) |
| `year(d)`, `month(d)` (1–12), `day(d)`, `dayOfWeek(d)` (ISO: Monday = 1 … Sunday = 7) | `date` | `int` |
| `s.startsWith(t)`, `s.endsWith(t)`, `s.contains(t)` | `string` | ordinal comparison |
| `s.matches(re)`, `matches(s, re)` | `re` must be a **string literal** | RE2-like linear-time engine (`RegexOptions.NonBacktracking`), search semantics, with a timeout |
| `s.lowerAscii()`, `s.upperAscii()`, `s.trim()` | `string` | culture-invariant |

**Whole years:** `ageAt` and `yearsBetween` count a year when `from.AddYears(n) <= to`. A 29 February birthday
therefore turns a year older on 28 February in common years. This convention is still to be confirmed with Legal
and the actuaries; some jurisdictions use 1 March.

## 6. Semantics

* **Pure and deterministic.** There is no I/O, clock, randomness or culture dependence. "Today" is always an
  explicit input from the time service. The names `now`, `today`, `random`, `uuid`, `clock`, … fail compilation
  with `RULE-NONDETERMINISTIC`. All parsing and formatting is culture-invariant (tests run under el-GR, de-DE,
  tr-TR and ar-SA).
* **Decimal arithmetic** (ruling D-ARC-10b): `+`, `-` and `*` are exact. If the exact result needs more than
  System.Decimal's 28–29 significant digits, evaluation fails with `RULE-PRECISION-LOSS` instead of rounding
  silently (fail closed); round an operand explicitly first. Overflow is `RULE-OVERFLOW`. **Division is the one
  rounding operation**: the quotient is System.Decimal's correctly rounded 28–29 significant digit result
  (`1 / 3.0` = `0.3333333333333333333333333333`), so a quotient must pass through an explicit `round(...)` (or a host
  rounding function such as `mkt.Rounding.apply`, REQ-MKT-006) before it becomes money.
* **Logical operators** short-circuit left to right: `false && x` and `true || x` never evaluate `x`. They follow
  CEL error absorption: if the left side fails and the right side decides the result (`err || true`,
  `err && false`), the result is that decision; otherwise the left error is raised. Cost-budget, size-limit and
  regex-timeout errors are never absorbed. `all`/`exists` absorb errors in the same way; `exists_one`, `map` and
  `filter` propagate them.
* **Null:** a `null` operand of arithmetic, ordering, field selection, a macro or a function is a
  `RULE-NULL-VALUE` error. `==` and `!=` with `null` are always allowed. Use `has(x.f)` or `x.f != null` to test.
* **Equality** is deep and numeric across `int` and `decimal` (`1 == 1.0`). Strings compare ordinally.

## 7. Errors

Compile errors (`RuleCompileError`, or `RuleCompileException` from `Compile`) and evaluation errors
(`RuleEvaluationError`) carry a `RuleErrorCode`, a stable text code (`CodeText`), a message, a `line:column`
position and, for decision tables, a context such as `rule 'R1' condition 'driverAge' (< 18)`.

| Code | When |
|---|---|
| `RULE-SYNTAX`, `RULE-RESERVED-WORD`, `RULE-INVALID-LITERAL` (also: lone UTF-16 surrogates in string literals, table ids, versions, rule ids or descriptions), `RULE-UNSUPPORTED` | parsing |
| `RULE-EXPRESSION-TOO-LONG`, `RULE-DEPTH-EXCEEDED` | limits at compile time |
| `RULE-UNKNOWN-IDENTIFIER` (names the undeclared input), `RULE-UNKNOWN-FIELD` (names the field path, for example `vehicle.colour`), `RULE-UNKNOWN-FUNCTION`, `RULE-NONDETERMINISTIC` | name resolution |
| `RULE-NO-MATCHING-OVERLOAD`, `RULE-TYPE-MISMATCH`, `RULE-RESULT-TYPE-MISMATCH`, `RULE-INVALID-ARGUMENT`, `RULE-INVALID-REGEX`, `RULE-DUPLICATE-KEY` | type checking |
| `RULE-DIVISION-BY-ZERO`, `RULE-OVERFLOW`, `RULE-NULL-VALUE`, `RULE-INDEX-OUT-OF-RANGE`, `RULE-NO-SUCH-KEY`, `RULE-DUPLICATE-KEY`, `RULE-INVALID-VALUE`, `RULE-HOST-FUNCTION-FAILED` | evaluation |
| `RULE-PRECISION-LOSS` | `+ - *` whose exact result does not fit a decimal |
| `RULE-COST-EXCEEDED`, `RULE-LIMIT-EXCEEDED`, `RULE-TIMEOUT`, `RULE-CANCELLED`, `RULE-REGEX-TIMEOUT` | resource limits (never absorbed by `&&`/`\|\|`) |
| `RULE-INPUT-INVALID` (`RuleInputException`, with the input path) | input binding |
| `PLT-ERR-TABLE-NOT-ACTIVE`, `PLT-ERR-HIT-POLICY-VIOLATION`, `RULE-INVALID-DEFINITION` | decision tables |

## 8. Limits

`RuleLimits` (defaults in brackets): `MaxExpressionLength` [8 192 characters], `MaxAstDepth` [200; at most
1 000], `MaxEvaluationSteps` [100 000], `MaxAllocatedElements` [1 000 000], `MaxEvaluationTime` [1 s],
`MaxCollectionSize` [10 000], `MaxStringLength` [65 536], `MaxRegexPatternLength` [512], `RegexTimeout` [50 ms],
`MaxTraceEntries` [10 000], `MaxTraceValueWeight` [1 000], `MaxInputDepth` [64; at most 1 000]. `EvaluationOptions.MaxSteps` can lower the budget and `EvaluationOptions.Cancellation`
cancels a single call.

**Cost model.** Steps are charged in proportion to the work done, not per node: one step per node evaluation and
per comprehension iteration, plus the *weight* of every value an operation traverses (`==`, `!=`, ordering, each
comparison inside `in`, `min`/`max`/`sum`, string scans at one step per 16 characters, run-time type checks).
A value's weight is the size of its tree counting shared sub-values once per occurrence, so values built by
sharing (whose size grows exponentially with linear steps) cannot be compared or scanned cheaply. Every element
created (list/map literals and results, concatenations, keys of iterated maps, widened copies, strings in
16-character chunks) counts towards `MaxAllocatedElements` and is also charged as steps. A wall-clock deadline
(`MaxEvaluationTime`) and the cancellation token are checked every 256 steps. Exceeding a bound gives
`RULE-COST-EXCEEDED`, `RULE-LIMIT-EXCEEDED`, `RULE-TIMEOUT` or `RULE-CANCELLED`.

**No heavy value leaves the engine.** Every collection the engine produces (literals, `map`/`filter` results,
concatenations, host-function results) is charged its full weight, so no value heavier than
`MaxAllocatedElements` can be built. As a second line of defence, every value that crosses the engine boundary
(an expression result, a decision-table variable, column or output) is checked against that budget
(`RULE-LIMIT-EXCEEDED`), inputs heavier than the budget are refused before evaluation, and input values heavier than
10 000 000 are refused when the inputs are built. `StepsUsed` is reported at most one past the budget;
`AttemptedSteps` shows the full work the breaking operation tried to charge. Value `ToString` renders at most
4 096 characters (then "..."), `GetHashCode` looks at a bounded prefix, and `Equals` remembers sub-values already
proven equal, so values that share structure compare in time proportional to their distinct parts. Decision-table
test cases whose expected values are heavier than the budget fail without being compared. Every decision-table output
value is charged against the evaluation's allocation budget, so the aggregate size of all matches is bounded.
Inputs and host results nested deeper than `MaxInputDepth` are refused (`RULE-LIMIT-EXCEEDED`); values nested deeper
than 1 000 are refused when inputs are built; deep equality is iterative, so no value can exhaust the call stack.

**Error messages never echo input content.** A string value is described by its length ("a string of length 12"),
other values by at most 64 characters, and dates of birth are not repeated; name the input path instead.

## 9. Explainability: traces and content hashes

* Values heavier than `MaxTraceValueWeight` are recorded in a trace as an `ElidedValue` placeholder (kind and
  weight only, for example `<List elided: weight 5001>`).
* With `EvaluationOptions { Trace = true }`, `EvaluationResult.Trace` lists, in evaluation order, each evaluated
  sub-expression (source text, position, node id) and its value, for example
  `["x = 7", "y = 2", "y * 2 = 4", "x + y * 2 = 11"]`. Short-circuited operands are absent. Literals and implicit
  conversions are omitted.
* `CanonicalText` example: `x + 1` gives `cel-subset/1.0:(call _+_ (id x) (int 1))`. Whitespace, comments,
  parentheses and quote style do not change it. Literal scale does (`1.0` and `1.00` hash differently because they
  produce differently scaled results). The content hash also covers the result type and `EnvironmentFingerprint`,
  so `p / 3` over `p: int` and over `p: decimal` hash differently. A host function's *implementation* cannot be
  hashed: change its name (or pass the configuration it depends on as an argument) when its behaviour changes.

## 10. Decision tables

`CompiledDecisionTable.Compile(DecisionTableDefinition, RuleEnvironment)` type-checks every expression of a table
version and reports **all** errors, each with its cell context.

* **Variables** (`TableVariable`, REQ-UW-033) are computed first, in order. Each may use the inputs and the earlier
  variables.
* **Input columns** (`InputColumn`) are typed expressions over the inputs and variables. Conditions and outputs can
  refer to inputs, variables and columns by name.
* **Output columns** (`OutputColumn`) are typed. Each rule gives one expression per output column.
* **Condition cells:**

  | Cell | Meaning |
  |---|---|
  | `-` or empty | any value |
  | `null` / `not null` | no value / has a value |
  | `< x`, `<= x`, `> x`, `>= x`, `== x` | positive comparison |
  | `!= x` | negative comparison |
  | `[a..b]`, `[a..b)`, `(a..b]`, `(a..b)` | positive range (`[` `]` inclusive, `(` `)` exclusive); numbers, dates, strings |
  | `in [a, b]` / `not in [a, b]` | positive / negative set membership |
  | `? <boolean expression>` | any predicate over inputs, variables and columns (null is the author's responsibility) |
  | anything else | positive equality with that expression's value |

  **Null rule:** a null column never matches a positive test and always matches a negative test (`!=`, `not in`),
  like SQL's `IS DISTINCT FROM`; `null` and `not null` test for it explicitly. A column that can be null must be
  declared with a nullable type (`RuleType.Int.Nullable()`); otherwise a null column value is `RULE-NULL-VALUE`.
  Compile errors in a cell carry positions in the cell text the author wrote, not in the generated expression.

* **Hit policies:** `First` (row order; later rows are not evaluated), `Unique` (more than one match gives
  `PLT-ERR-HIT-POLICY-VIOLATION`), `Priority` (highest `DecisionRule.Priority`; on a tie, the earlier row wins),
  `Collect` (all matches in row order). No match is a success with no matches.
* **Versioning** (`DecisionTableMetadata`): table id, version, status (`Draft … Retired`) and a half-open
  effective range `[EffectiveFrom, EffectiveTo)`. `Evaluate(inputs, asOf)` refuses with `PLT-ERR-TABLE-NOT-ACTIVE`
  unless the status is `Active` and `asOf` is inside the range.   `EvaluateForTesting` skips this check, for authoring
  and test runs. The `ContentHash` covers the content (hit policy, variables, columns, cells, outputs, priorities,
  and through each expression's hash the input schema and host signatures) and not the metadata. `CanonicalText`
  is a sequence of length-prefixed components (`length:text;`), so free-text rule ids cannot forge structure.
* **Trace** (`DecisionTrace`, REQ-PLT-177): table id, version, content hash, variable and column values, per-row
  cell outcomes (`Matched`, `NotMatched`, `Any`, `NotEvaluated`), the matched rules with their outputs and the steps
  used. `DetailedTrace = true` adds the sub-expression trace of every expression evaluated.
* **Test cases and activation gate** (REQ-PLT-176, REQ-UW-038): `RunTests(cases)` and
  `CheckActivationReadiness(cases, requireEveryRuleCovered)`. Activation is refused if there are no cases, if any
  case fails (the failing cases are listed), or, optionally, if some rule is not covered by any case.
* **Performance:** `tools/CoreIns.Rules.Benchmark` is the NFR-UW-001 spike (150 rules over 4 drivers and 2 vehicles
  with nested macros; budget p95 ≤ 150 ms, p99 ≤ 300 ms). Unit tests assert step counts, not wall-clock time.
* Persistence, approval workflow, maker-checker and DMN import/export belong to the PLT module (W1-PLT-04). They
  are not part of this library.

## 11. Host functions

`HostFunction(name, parameterTypes, returnType, implementation)` adds a typed, **pure** function. The name can be
qualified, for example `mkt.round(amount, "charge.line")` with the implementation bound over an MKT rounding
configuration snapshot. Names cannot shadow built-ins or non-deterministic names. Arguments are type-checked at
compile time. An exception thrown by the implementation becomes `RULE-HOST-FUNCTION-FAILED`, and so does a result
that does not match the declared type.

## 12. Differences from full CEL

* **No `double`, no `uint`, no `bytes`.** Numbers with a fraction are `decimal`, and the `double()`, `uint()`,
  `bytes()`, `dyn()` and `type()` functions are rejected. Exponent literals (`1e3`) and `u` suffixes are rejected.
  This follows engineering rule 2 (money is never binary floating point).
* **Numeric widening:** `int` op `decimal` gives `decimal`. In CEL, mixed `int`/`double` arithmetic is a type
  error. `int / int` stays integer division, as in CEL.
* **`date` type and functions** (`ageAt`, `addMonths`, …) are extensions. The CEL timestamp accessors
  (`getFullYear`, `getMonth` (0-based), …) and time zones are not adopted.
* **`round(x, places, mode)`, `min`, `max`, `sum`, `abs`, `trim`, `lowerAscii`, `upperAscii`** are extensions in
  the spirit of the cel-go `math`/`strings` extensions, with different names or signatures.
* **`matches` needs a literal pattern** and uses a linear-time engine with a timeout.
* **Not supported:** message construction (`Type{...}`), leading-dot names (`.a.b`), `optional` types, protobuf
  wrapper and well-known types, `getType`/type values, string indexing, string functions such as `split` and
  `substring`, and the bindings, encoders, sets and lists extension libraries.
* **Static typing:** expressions are always type-checked against a declared schema (checked CEL). `dyn` exists only
  for empty list literals and host results.
* **Evaluation order** of `&&`/`||` is left to right with short-circuiting. Results match CEL's commutative
  semantics, including error absorption, except that budget, allocation, deadline, cancellation and regex-timeout
  errors are never absorbed: if the left side failed with an ordinary error and the right side then exhausts a
  resource, the resource error is reported.
* **No silent decimal rounding** (`RULE-PRECISION-LOSS`) and **non-null expected types** are stricter than CEL.
