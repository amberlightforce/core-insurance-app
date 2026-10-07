using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CoreIns.Rules.Syntax;

internal enum TokenKind
{
    Eof,
    Int,
    Decimal,
    String,
    Ident,
    True,
    False,
    Null,
    In,
    LParen,
    RParen,
    LBracket,
    RBracket,
    LBrace,
    RBrace,
    Dot,
    Comma,
    Colon,
    Question,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Bang,
    EqEq,
    NotEq,
    Lt,
    Le,
    Gt,
    Ge,
    AndAnd,
    OrOr,
}

/// <summary>A token. <see cref="StringValue"/> holds the decoded value of string literals.</summary>
internal readonly record struct Token(TokenKind Kind, int Start, int End, string Text, string? StringValue);

/// <summary>Tokeniser for the CEL subset. Culture-invariant; ASCII identifiers; <c>//</c> line comments.</summary>
internal sealed class Lexer
{
    private readonly string _src;
    private int _pos;

    private Lexer(string src) => _src = src;

    public static List<Token> Tokenize(string source)
    {
        var lexer = new Lexer(source);
        var tokens = new List<Token>();
        while (true)
        {
            var t = lexer.Next();
            tokens.Add(t);
            if (t.Kind == TokenKind.Eof)
            {
                return tokens;
            }
        }
    }

    private char Peek(int ahead) => _pos + ahead < _src.Length ? _src[_pos + ahead] : '\0';

    private bool Match(char c)
    {
        if (Peek(0) == c)
        {
            _pos++;
            return true;
        }

        return false;
    }

    private void SkipTrivia()
    {
        while (_pos < _src.Length)
        {
            char c = _src[_pos];
            if (c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v')
            {
                _pos++;
            }
            else if (c == '/' && Peek(1) == '/')
            {
                while (_pos < _src.Length && _src[_pos] != '\n')
                {
                    _pos++;
                }
            }
            else
            {
                return;
            }
        }
    }

    private Token Next()
    {
        SkipTrivia();
        if (_pos >= _src.Length)
        {
            return new Token(TokenKind.Eof, _pos, _pos, string.Empty, null);
        }

        int start = _pos;
        char c = _src[_pos];
        char next = Peek(1);
        if (c is 'r' or 'R' && next is '"' or '\'')
        {
            _pos++;
            return LexString(start, raw: true);
        }

        if (c is 'b' or 'B' && next is '"' or '\'')
        {
            throw new CompileFailure(RuleErrorCode.UnsupportedFeature, start, "bytes literals are outside the adopted CEL subset");
        }

        if (Identifiers.IsStart(c))
        {
            while (_pos < _src.Length && Identifiers.IsPart(_src[_pos]))
            {
                _pos++;
            }

            string text = _src[start.._pos];
            var kind = text switch
            {
                "true" => TokenKind.True,
                "false" => TokenKind.False,
                "null" => TokenKind.Null,
                "in" => TokenKind.In,
                _ => TokenKind.Ident,
            };
            return new Token(kind, start, _pos, text, null);
        }

        if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(next)))
        {
            return LexNumber(start);
        }

        if (c is '"' or '\'')
        {
            return LexString(start, raw: false);
        }

        _pos++;
        TokenKind k;
        switch (c)
        {
            case '(': k = TokenKind.LParen; break;
            case ')': k = TokenKind.RParen; break;
            case '[': k = TokenKind.LBracket; break;
            case ']': k = TokenKind.RBracket; break;
            case '{': k = TokenKind.LBrace; break;
            case '}': k = TokenKind.RBrace; break;
            case '.': k = TokenKind.Dot; break;
            case ',': k = TokenKind.Comma; break;
            case ':': k = TokenKind.Colon; break;
            case '?': k = TokenKind.Question; break;
            case '+': k = TokenKind.Plus; break;
            case '-': k = TokenKind.Minus; break;
            case '*': k = TokenKind.Star; break;
            case '/': k = TokenKind.Slash; break;
            case '%': k = TokenKind.Percent; break;
            case '!': k = Match('=') ? TokenKind.NotEq : TokenKind.Bang; break;
            case '<': k = Match('=') ? TokenKind.Le : TokenKind.Lt; break;
            case '>': k = Match('=') ? TokenKind.Ge : TokenKind.Gt; break;
            case '=':
                if (!Match('='))
                {
                    throw new CompileFailure(RuleErrorCode.Syntax, start, "unexpected '='; use '==' for equality");
                }

                k = TokenKind.EqEq;
                break;
            case '&':
                if (!Match('&'))
                {
                    throw new CompileFailure(RuleErrorCode.Syntax, start, "unexpected '&'; use '&&'");
                }

                k = TokenKind.AndAnd;
                break;
            case '|':
                if (!Match('|'))
                {
                    throw new CompileFailure(RuleErrorCode.Syntax, start, "unexpected '|'; use '||'");
                }

                k = TokenKind.OrOr;
                break;
            default:
                throw new CompileFailure(RuleErrorCode.Syntax, start, $"unexpected character '{c}'");
        }

        return new Token(k, start, _pos, _src[start.._pos], null);
    }

    private Token LexNumber(int start)
    {
        if (_src[_pos] == '0' && Peek(1) is 'x' or 'X')
        {
            _pos += 2;
            int digitsStart = _pos;
            while (_pos < _src.Length && char.IsAsciiHexDigit(_src[_pos]))
            {
                _pos++;
            }

            if (_pos == digitsStart)
            {
                throw new CompileFailure(RuleErrorCode.InvalidLiteral, start, "hexadecimal literal has no digits");
            }

            CheckNumberSuffix(start);
            return new Token(TokenKind.Int, start, _pos, _src[start.._pos], null);
        }

        while (_pos < _src.Length && char.IsAsciiDigit(_src[_pos]))
        {
            _pos++;
        }

        bool isDecimal = false;
        if (Peek(0) == '.' && char.IsAsciiDigit(Peek(1)))
        {
            isDecimal = true;
            _pos++;
            while (_pos < _src.Length && char.IsAsciiDigit(_src[_pos]))
            {
                _pos++;
            }
        }

        CheckNumberSuffix(start);
        return new Token(isDecimal ? TokenKind.Decimal : TokenKind.Int, start, _pos, _src[start.._pos], null);
    }

    private void CheckNumberSuffix(int start)
    {
        char c = Peek(0);
        if (c is 'e' or 'E')
        {
            throw new CompileFailure(RuleErrorCode.UnsupportedFeature, start, "exponent notation denotes a CEL double, which is not supported; write the decimal number in full");
        }

        if (c is 'u' or 'U')
        {
            throw new CompileFailure(RuleErrorCode.UnsupportedFeature, start, "unsigned integers (uint) are outside the adopted CEL subset");
        }

        if (Identifiers.IsPart(c))
        {
            throw new CompileFailure(RuleErrorCode.Syntax, start, "invalid number literal");
        }
    }

    private Token LexString(int start, bool raw)
    {
        char quote = _src[_pos];
        bool triple = Peek(1) == quote && Peek(2) == quote;
        _pos += triple ? 3 : 1;
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _src.Length)
            {
                throw new CompileFailure(RuleErrorCode.Syntax, start, "unterminated string literal");
            }

            char c = _src[_pos];
            if (triple)
            {
                if (c == quote && Peek(1) == quote && Peek(2) == quote)
                {
                    _pos += 3;
                    break;
                }
            }
            else
            {
                if (c == quote)
                {
                    _pos++;
                    break;
                }

                if (c is '\n' or '\r')
                {
                    throw new CompileFailure(RuleErrorCode.Syntax, _pos, "newline in string literal (use a triple-quoted string)");
                }
            }

            if (c == '\\' && !raw)
            {
                ReadEscape(sb);
                continue;
            }

            sb.Append(c);
            _pos++;
        }

        return new Token(TokenKind.String, start, _pos, _src[start.._pos], sb.ToString());
    }

    private void ReadEscape(StringBuilder sb)
    {
        int escStart = _pos;
        _pos++;
        if (_pos >= _src.Length)
        {
            throw new CompileFailure(RuleErrorCode.Syntax, escStart, "unterminated escape sequence");
        }

        char e = _src[_pos++];
        switch (e)
        {
            case 'a': sb.Append('\a'); return;
            case 'b': sb.Append('\b'); return;
            case 'f': sb.Append('\f'); return;
            case 'n': sb.Append('\n'); return;
            case 'r': sb.Append('\r'); return;
            case 't': sb.Append('\t'); return;
            case 'v': sb.Append('\v'); return;
            case '\\':
            case '\'':
            case '"':
            case '`':
            case '?':
                sb.Append(e);
                return;
            case 'x':
            case 'X':
                AppendCodePoint(sb, ReadHex(2, escStart), escStart);
                return;
            case 'u':
                AppendCodePoint(sb, ReadHex(4, escStart), escStart);
                return;
            case 'U':
                AppendCodePoint(sb, ReadHex(8, escStart), escStart);
                return;
            case >= '0' and <= '3':
            {
                int value = e - '0';
                for (int i = 0; i < 2; i++)
                {
                    char o = Peek(0);
                    if (o is < '0' or > '7')
                    {
                        throw new CompileFailure(RuleErrorCode.Syntax, escStart, "invalid octal escape sequence");
                    }

                    value = (value * 8) + (o - '0');
                    _pos++;
                }

                AppendCodePoint(sb, value, escStart);
                return;
            }

            default:
                throw new CompileFailure(RuleErrorCode.Syntax, escStart, $"invalid escape sequence '\\{e}'");
        }
    }

    private int ReadHex(int count, int escStart)
    {
        if (_pos + count > _src.Length)
        {
            throw new CompileFailure(RuleErrorCode.Syntax, escStart, "truncated hexadecimal escape sequence");
        }

        string hex = _src.Substring(_pos, count);
        if (!int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int value) || value < 0)
        {
            throw new CompileFailure(RuleErrorCode.Syntax, escStart, "invalid hexadecimal escape sequence");
        }

        _pos += count;
        return value;
    }

    private static void AppendCodePoint(StringBuilder sb, int codePoint, int escStart)
    {
        if (codePoint > 0x10FFFF || codePoint is >= 0xD800 and <= 0xDFFF)
        {
            throw new CompileFailure(RuleErrorCode.Syntax, escStart, "escape sequence is not a valid Unicode scalar value");
        }

        sb.Append(char.ConvertFromUtf32(codePoint));
    }
}
