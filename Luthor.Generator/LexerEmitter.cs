using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Luthor.Generator;

internal static class LexerEmitter
{
    // Members the generator adds to the lexer type; rule names can't reuse them.
    static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "Tokenize", "_dfa", "_dfaBytes", "_encoding" };
    const string ErrorName = "ERROR";

    public static void Execute(SourceProductionContext spc, LexerModel m)
    {
        foreach (var d in m.Diagnostics) spc.ReportDiagnostic(d.ToDiagnostic());
        if (m.Diagnostics.Any(d => d.Descriptor.DefaultSeverity == DiagnosticSeverity.Error)) return;

        bool ok = true;
        void Error(DiagnosticDescriptor d, LocationInfo? loc, params string[] args)
        {
            spc.ReportDiagnostic(DiagnosticInfo.Create(d, loc, args).ToDiagnostic());
            if (d.DefaultSeverity == DiagnosticSeverity.Error) ok = false;
        }

        // ---- names ----
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in m.Rules)
        {
            string? problem =
                !SyntaxFacts.IsValidIdentifier(r.Name) ? "is not a valid C# identifier"
                : r.Name == m.Type.Name ? "is the same as the lexer type's name"
                : Reserved.Contains(r.Name) ? "conflicts with a generated member"
                : m.ErrorRule && r.Name == ErrorName ? "is reserved for the error rule (set [Lexer(ErrorRule = false)] to use it)"
                : !seen.Add(r.Name) ? "is declared more than once"
                : null;
            if (problem is not null) Error(Diagnostics.InvalidRuleName, r.NameLocation, r.Name, problem);
        }

        // ---- patterns, one at a time, so errors point at the right attribute ----
        var patterns = m.Rules.Select(r => r.IsLiteral ? EscapeLiteral(r.Pattern) : r.Pattern).ToList();
        for (int k = 0; k < patterns.Count; k++)
        {
            spc.CancellationToken.ThrowIfCancellationRequested();
            var r = m.Rules[k];
            if (r.IsLiteral && r.Pattern.Length == 0) { Error(Diagnostics.InvalidPattern, r.PatternLocation, r.Name, "literal is empty"); continue; }
            CodepointDfa single;
            try { single = Builder.Build(new[] { patterns[k] }); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Builder reports most syntax errors as FormatException; a pattern that ends early
                // (e.g. "a{2") can also run off the end of the string.
                string why = ex is FormatException ? ex.Message
                    : ex is IndexOutOfRangeException or ArgumentOutOfRangeException ? "pattern ends unexpectedly"
                    : $"malformed pattern ({ex.Message})";
                Error(Diagnostics.InvalidPattern, r.PatternLocation, r.Name, why);
                continue;
            }
            string? where = EmptyMatch(single);
            if (where is not null) Error(Diagnostics.EmptyMatch, r.PatternLocation, r.Name, where);
        }
        if (!ok) return;

        // ---- build ----
        int[] dfa;
        int[]? dfaBytes = null;
        EncodingKind? enc = null;
        try
        {
            var cp = Builder.Build(patterns, m.ErrorRule);
            dfa = Compiler.Compile(cp, "UTF-16");
            if (m.Encoding is not null)
            {
                try
                {
                    dfaBytes = Compiler.Compile(cp, m.Encoding);
                    enc = EncodingKind.Classify(m.Encoding);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    Error(Diagnostics.BadEncoding, m.EncodingLocation, m.Encoding, ex.Message);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error(Diagnostics.BuildFailed, m.TypeLocation, m.Type.Name, ex.Message);
            return;
        }

        spc.AddSource(m.HintName, SourceText.From(Render(m, dfa, dfaBytes, enc), Encoding.UTF8));
    }

    // Literal text -> pattern. Escaping every ASCII non-alphanumeric is always safe with Luthor's
    // parser (an unknown escape is the character itself); letters and digits must NOT be escaped
    // (\n, \d, \x, \0 mean something).
    internal static string EscapeLiteral(string s)
    {
        var sb = new StringBuilder(s.Length * 2);
        foreach (char c in s)
        {
            if (c < 128 && !char.IsLetterOrDigit(c)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // Can a single-rule DFA accept without consuming input? Check the start state and whatever
    // its zero-width ^ / $ edges reach.
    static string? EmptyMatch(CodepointDfa d)
    {
        var s = d.States;
        if (s[0].Accept >= 0) return "";
        var seen = new HashSet<int> { 0 };
        var work = new Stack<(int State, bool Bol, bool Eol)>();
        work.Push((0, false, false));
        while (work.Count > 0)
        {
            var (st, bol, eol) = work.Pop();
            if (s[st].Accept >= 0)
                return bol && eol ? " (on an empty line)" : bol ? " (at the start of a line)" : " (at the end of a line)";
            if (s[st].Bol >= 0 && seen.Add(s[st].Bol)) work.Push((s[st].Bol, true, eol));
            if (s[st].Eol >= 0 && seen.Add(s[st].Eol)) work.Push((s[st].Eol, bol, true));
        }
        return null;
    }

    // ---------------------------------------------------------------- source

    static string Render(LexerModel m, int[] dfa, int[]? dfaBytes, EncodingKind? enc)
    {
        var w = new Writer();
        w.Line("// <auto-generated/>");
        w.Line("// Generated by Luthor.Generator from the [Rule] attributes on " + m.Type.Name + ".");
        w.Line("#nullable enable");
        w.Line();
        if (m.Namespace is not null) { w.Line("namespace " + m.Namespace); w.Open(); }
        foreach (var c in m.Containers) { w.Line($"partial {c.Keyword} {c.Name}{c.TypeParameters}"); w.Open(); }
        w.Line($"partial {m.Type.Keyword} {m.Type.Name}{m.Type.TypeParameters}");
        w.Open();

        // symbols
        for (int k = 0; k < m.Rules.Count; k++)
        {
            var r = m.Rules[k];
            w.Line($"/// <summary>{(r.IsLiteral ? "Literal" : "Pattern")} <c>{Xml(Visible(r.Pattern))}</c></summary>");
            w.Line($"public const int {Identifier(r.Name)} = {k};");
        }
        if (m.ErrorRule)
        {
            w.Line("/// <summary>Any code unit no other rule matches (lowest priority).</summary>");
            w.Line($"public const int {ErrorName} = {m.Rules.Count};");
        }
        w.Line();

        // tables
        w.Line("// DFA over UTF-16 code units, used by Tokenize(string) and Tokenize(TextReader).");
        Table(w, "_dfa", dfa);
        if (dfaBytes is not null && enc is not null)
        {
            w.Line($"// DFA over {Visible(enc.Display)} code units, used by Tokenize(byte[]) and Tokenize(Stream).");
            Table(w, "_dfaBytes", dfaBytes);
            w.Line($"private static readonly global::Luthor.UnitEncoding _encoding = new global::Luthor.UnitEncoding({enc.EncodingExpression}, {enc.UnitSize}, {(enc.BigEndian ? "true" : "false")});");
            w.Line();
        }

        // entry points
        const string Ret = "global::System.Collections.Generic.IEnumerable<(long Position, int Symbol, string Text)>";
        string noMatch = m.ErrorRule
            ? $"Every code unit ends up in some token; input no rule matches is <see cref=\"{ErrorName}\"/>."
            : "Input no rule matches is returned one code unit at a time with symbol -1.";

        w.Line("/// <summary>Splits <paramref name=\"text\"/> into longest-match tokens. Position is a char index.</summary>");
        w.Line($"/// <remarks>{noMatch}</remarks>");
        w.Line($"public static {Ret} Tokenize(string text) => global::Luthor.LexerRuntime.Tokenize(_dfa, text);");
        w.Line();
        w.Line("/// <summary>Tokenizes a reader lazily, buffering only the current token. Position is a char offset.</summary>");
        w.Line($"/// <remarks>{noMatch}</remarks>");
        w.Line($"public static {Ret} Tokenize(global::System.IO.TextReader reader) => global::Luthor.LexerRuntime.Tokenize(_dfa, reader);");

        if (dfaBytes is not null && enc is not null)
        {
            string err = m.ErrorRule ? ErrorName : "-1";
            string display = Xml(Visible(enc.Display));
            string bom = enc.HasBom ? " A leading byte order mark is skipped." : "";
            w.Line();
            w.Line($"/// <summary>Tokenizes {display} bytes without decoding them first. Position is a byte offset.{bom}</summary>");
            w.Line($"/// <remarks>{noMatch}</remarks>");
            w.Line($"public static {Ret} Tokenize(byte[] data) => global::Luthor.LexerRuntime.Tokenize(_dfaBytes, data, _encoding, {err});");
            w.Line();
            w.Line($"/// <summary>Tokenizes a {display} stream lazily, buffering only the current token. Position is a byte offset.{bom}</summary>");
            w.Line($"/// <remarks>{noMatch}</remarks>");
            w.Line($"public static {Ret} Tokenize(global::System.IO.Stream stream) => global::Luthor.LexerRuntime.Tokenize(_dfaBytes, stream, _encoding, {err});");
        }

        w.Close();
        foreach (var _ in m.Containers) w.Close();
        if (m.Namespace is not null) w.Close();
        return w.ToString();
    }

    static void Table(Writer w, string name, int[] table)
    {
        w.Line($"private static readonly int[] {name} = new int[]");
        w.Open();
        for (int k = 0; k < table.Length; k += 16)
            w.Line(string.Join(", ", table.Skip(k).Take(16)) + ",");
        w.Close(";");
        w.Line();
    }

    static string Identifier(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) == SyntaxKind.UnderscoreToken
            ? "@" + name : name;

    // The pattern as written in a verbatim string, with control characters made visible.
    static string Visible(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(c < 0x20 || c == 0x7F ? $"\\u{(int)c:X4}" : c.ToString());
        return sb.ToString();
    }

    static string Xml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    sealed class Writer
    {
        readonly StringBuilder _sb = new();
        int _indent;
        public void Line(string s = "")
        {
            if (s.Length > 0) _sb.Append(' ', _indent * 4).Append(s);
            _sb.Append('\n');
        }
        public void Open() { Line("{"); _indent++; }
        public void Close(string suffix = "") { _indent--; Line("}" + suffix); }
        public override string ToString() => _sb.ToString();
    }
}

/// <summary>How the byte-level tokenizer reads code units for a given encoding name.</summary>
internal sealed record EncodingKind(string Display, string EncodingExpression, int UnitSize, bool BigEndian, bool HasBom)
{
    // Same name normalization as Compiler.Transform.
    public static EncodingKind Classify(string name)
    {
        string e = name.ToUpperInvariant().Replace("-", "").Replace("_", "");
        return e switch
        {
            "UTF8" => new("UTF-8", "new global::System.Text.UTF8Encoding(true)", 1, false, true),
            "UTF16" or "UTF16LE" or "UNICODE" => new("UTF-16LE", "new global::System.Text.UnicodeEncoding(false, true)", 2, false, true),
            "UTF16BE" => new("UTF-16BE", "new global::System.Text.UnicodeEncoding(true, true)", 2, true, true),
            "UTF32" or "UTF32LE" => new("UTF-32LE", "new global::System.Text.UTF32Encoding(false, true)", 4, false, true),
            "UTF32BE" => new("UTF-32BE", "new global::System.Text.UTF32Encoding(true, true)", 4, true, true),
            _ => new(name, "global::Luthor.UnitEncoding.CodePage(" + SymbolDisplay.FormatLiteral(name, true) + ")", 1, false, false),
        };
    }
}
