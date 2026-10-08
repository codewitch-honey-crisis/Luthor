using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Luthor.Generator;

internal static class LexerEmitter
{
    // Members the generator always adds to the lexer type; rule names can't reuse them.
    // Per-encoding members (TokenizeX, _dfaX, _encodingX) are added per lexer in Execute.
    static readonly string[] Reserved = { "Tokenize", "_dfa" };
    const string ErrorName = "ERROR";
    const string Utf16Table = "_dfa";

    /// <summary>One byte-level tokenizer to generate: <c>Tokenize{Suffix}</c> over <c>{TableField}</c>.</summary>
    sealed record StreamPlan(string Suffix, EncodingKind Kind, string TableField);

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
        var generated = new HashSet<string>(Reserved, StringComparer.Ordinal);
        foreach (var s in m.Streams)
        {
            string suffix = EncodingKind.MethodSuffix(s.Encoding);
            generated.Add("Tokenize" + suffix);
            generated.Add("_dfa" + suffix);
            generated.Add("_encoding" + suffix);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in m.Rules)
        {
            string? problem =
                !SyntaxFacts.IsValidIdentifier(r.Name) ? "is not a valid C# identifier"
                : r.Name == m.Type.Name ? "is the same as the lexer type's name"
                : generated.Contains(r.Name) ? "conflicts with a generated member"
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
        var streams = new List<StreamPlan>();
        var extraTables = new List<(string Field, int[] Table)>();   // in first-use order, excluding _dfa
        try
        {
            var cp = Builder.Build(patterns, m.ErrorRule, m.Unicode);
            dfa = Compiler.Compile(cp, "UTF-16");

            // Every table emitted so far, by content, so identical tables are emitted once. This
            // catches the obvious cases (UTF-32LE/BE) and the incidental ones (two code pages that
            // agree on every character the rules can match).
            var pool = new Dictionary<int[], string>(IntArrayComparer.Instance) { [dfa] = Utf16Table };
            var byIdentity = new Dictionary<string, string>(StringComparer.Ordinal);   // canonical id -> encoding as written
            var bySuffix = new Dictionary<string, string>(StringComparer.Ordinal);     // method suffix -> encoding as written

            foreach (var s in m.Streams)
            {
                spc.CancellationToken.ThrowIfCancellationRequested();

                int[] table;
                if (EncodingKind.IsUtf16(s.Encoding))
                {
                    // The table works on code units, and UTF-16 code units are chars: _dfa already is
                    // the UTF-16 table. Byte order is handled by UnitEncoding when reading units.
                    table = dfa;
                }
                else
                {
                    try { table = Compiler.Compile(cp, s.Encoding); }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                    {
                        Error(Diagnostics.BadEncoding, s.Location, s.Encoding, ex.Message);
                        continue;
                    }
                }

                // Classified after compiling: Compiler.SingleByte registers the code pages provider,
                // which resolving names like "windows-1252" to a code page number needs on .NET Core.
                var kind = EncodingKind.Classify(s.Encoding);
                if (byIdentity.TryGetValue(kind.Identity, out var first))
                {
                    Error(Diagnostics.DuplicateEncoding, s.Location, s.Encoding,
                        string.Equals(first, s.Encoding, StringComparison.Ordinal)
                            ? "is declared more than once"
                            : $"is the same encoding as '{first}', which is already declared");
                    continue;
                }
                byIdentity.Add(kind.Identity, s.Encoding);

                string suffix = EncodingKind.MethodSuffix(s.Encoding);
                if (bySuffix.TryGetValue(suffix, out var clash))
                {
                    Error(Diagnostics.DuplicateEncoding, s.Location, s.Encoding,
                        $"would generate Tokenize{suffix}, which '{clash}' already generates");
                    continue;
                }
                bySuffix.Add(suffix, s.Encoding);

                if (!pool.TryGetValue(table, out var field))
                {
                    field = "_dfa" + suffix;
                    pool.Add(table, field);
                    extraTables.Add((field, table));
                }
                streams.Add(new StreamPlan(suffix, kind, field));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error(Diagnostics.BuildFailed, m.TypeLocation, m.Type.Name, ex.Message);
            return;
        }
        if (!ok) return;

        spc.AddSource(m.HintName, SourceText.From(Render(m, dfa, extraTables, streams), Encoding.UTF8));
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

    static string Render(LexerModel m, int[] dfa, List<(string Field, int[] Table)> extraTables, List<StreamPlan> streams)
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
        string UsedBy(string field, params string[] always) =>
            string.Join(", ", always.Concat(streams.Where(p => p.TableField == field).Select(p => "Tokenize" + p.Suffix)));

        w.Line($"// DFA over UTF-16 code units, used by {UsedBy(Utf16Table, "Tokenize(string)", "Tokenize(TextReader)")}.");
        Table(w, Utf16Table, dfa);
        foreach (var (field, table) in extraTables)
        {
            string encodings = string.Join(" / ", streams.Where(p => p.TableField == field).Select(p => p.Kind.Display).Distinct());
            w.Line($"// DFA over {Visible(encodings)} code units, used by {UsedBy(field)}.");
            Table(w, field, table);
        }
        foreach (var p in streams)
            w.Line($"private static readonly global::Luthor.UnitEncoding _encoding{p.Suffix} = new global::Luthor.UnitEncoding({p.Kind.EncodingExpression}, {p.Kind.UnitSize}, {(p.Kind.BigEndian ? "true" : "false")});");
        if (streams.Count > 0) w.Line();

        // entry points
        const string Ret = "global::System.Collections.Generic.IEnumerable<(long Position, int Symbol, string Text)>";
        string noMatch = m.ErrorRule
            ? $"Every code unit ends up in some token; input no rule matches is <see cref=\"{ErrorName}\"/>."
            : "Input no rule matches is returned one code unit at a time with symbol -1.";

        w.Line("/// <summary>Splits <paramref name=\"text\"/> into longest-match tokens. Position is a char index.</summary>");
        w.Line($"/// <remarks>{noMatch}</remarks>");
        w.Line($"public static {Ret} Tokenize(string text) => global::Luthor.LexerRuntime.Tokenize({Utf16Table}, text);");
        w.Line();
        w.Line("/// <summary>Tokenizes a reader lazily, buffering only the current token. Position is a char offset.</summary>");
        w.Line($"/// <remarks>{noMatch}</remarks>");
        w.Line($"public static {Ret} Tokenize(global::System.IO.TextReader reader) => global::Luthor.LexerRuntime.Tokenize({Utf16Table}, reader);");

        string err = m.ErrorRule ? ErrorName : "-1";
        foreach (var p in streams)
        {
            string display = Xml(Visible(p.Kind.Display));
            string bom = p.Kind.HasBom ? " A leading byte order mark is skipped." : "";
            string name = "Tokenize" + p.Suffix;
            w.Line();
            w.Line($"/// <summary>Tokenizes {display} bytes without decoding them first. Position is a byte offset.{bom}</summary>");
            w.Line($"/// <remarks>{noMatch}</remarks>");
            w.Line($"public static {Ret} {name}(byte[] data) => global::Luthor.LexerRuntime.Tokenize({p.TableField}, data, _encoding{p.Suffix}, {err});");
            w.Line();
            w.Line($"/// <summary>Tokenizes a {display} stream lazily, buffering only the current token. Position is a byte offset.{bom}</summary>");
            w.Line($"/// <remarks>{noMatch}</remarks>");
            w.Line($"public static {Ret} {name}(global::System.IO.Stream stream) => global::Luthor.LexerRuntime.Tokenize({p.TableField}, stream, _encoding{p.Suffix}, {err});");
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

/// <summary>Compares DFA tables by content, so identical tables can share one field.</summary>
internal sealed class IntArrayComparer : IEqualityComparer<int[]>
{
    public static readonly IntArrayComparer Instance = new();

    public bool Equals(int[]? a, int[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Length != b.Length) return false;
        for (int k = 0; k < a.Length; k++) if (a[k] != b[k]) return false;
        return true;
    }

    public int GetHashCode(int[] a)
    {
        unchecked
        {
            int h = a.Length;
            foreach (int x in a) h = h * 31 + x;
            return h;
        }
    }
}

/// <summary>How the byte-level tokenizer reads code units for a given encoding name.</summary>
/// <param name="Identity">Canonical identity: two names with the same identity are the same encoding.</param>
internal sealed record EncodingKind(string Display, string Identity, string EncodingExpression, int UnitSize, bool BigEndian, bool HasBom)
{
    // Same name normalization as Compiler.Transform.
    static string Normalize(string name) => name.ToUpperInvariant().Replace("-", "").Replace("_", "");

    public static bool IsUtf16(string name) => Normalize(name) is "UTF16" or "UTF16LE" or "UTF16BE" or "UNICODE";

    public static EncodingKind Classify(string name)
    {
        return Normalize(name) switch
        {
            "UTF8" => Utf("UTF-8", "new global::System.Text.UTF8Encoding(true)", 1, false),
            "UTF16" or "UTF16LE" or "UNICODE" => Utf("UTF-16LE", "new global::System.Text.UnicodeEncoding(false, true)", 2, false),
            "UTF16BE" => Utf("UTF-16BE", "new global::System.Text.UnicodeEncoding(true, true)", 2, true),
            "UTF32" or "UTF32LE" => Utf("UTF-32LE", "new global::System.Text.UTF32Encoding(false, true)", 4, false),
            "UTF32BE" => Utf("UTF-32BE", "new global::System.Text.UTF32Encoding(true, true)", 4, true),
            _ => new(name, CodePageIdentity(name),
                "global::Luthor.UnitEncoding.CodePage(" + SymbolDisplay.FormatLiteral(name, true) + ")", 1, false, false),
        };

        static EncodingKind Utf(string display, string expr, int unit, bool bigEndian) => new(display, display, expr, unit, bigEndian, true);
    }

    // "latin1", "iso-8859-1" and "28591" are all code page 28591. If the name can't be resolved
    // here, fall back to the normalized name, which still catches differences in case and dashes.
    static string CodePageIdentity(string name)
    {
        if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int page)) return "CP" + page;
        try { return "CP" + System.Text.Encoding.GetEncoding(name).CodePage; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return "NAME:" + Normalize(name); }
    }

    /// <summary>
    /// The part of the method name after <c>Tokenize</c>, from the encoding as written:
    /// "UTF-8" -> Utf8, "UTF-16BE" -> Utf16BE, "windows-1252" -> Windows1252,
    /// "iso-8859-1" -> Iso8859_1, "437" -> Cp437.
    /// </summary>
    public static string MethodSuffix(string name)
    {
        switch (Normalize(name))
        {
            case "UTF8": return "Utf8";
            case "UTF16": return "Utf16";
            case "UTF16LE": return "Utf16LE";
            case "UTF16BE": return "Utf16BE";
            case "UNICODE": return "Unicode";
            case "UTF32": return "Utf32";
            case "UTF32LE": return "Utf32LE";
            case "UTF32BE": return "Utf32BE";
        }

        // Pascal-case the ASCII alphanumeric runs; keep digit runs apart with '_' so that
        // "8859-1" doesn't become "88591".
        var sb = new StringBuilder();
        int k = 0;
        while (k < name.Length)
        {
            if (!IsAsciiAlnum(name[k])) { k++; continue; }
            int start = k;
            while (k < name.Length && IsAsciiAlnum(name[k])) k++;
            string part = name.Substring(start, k - start);
            if (sb.Length > 0 && char.IsDigit(sb[sb.Length - 1]) && char.IsDigit(part[0])) sb.Append('_');
            sb.Append(char.ToUpperInvariant(part[0])).Append(part.Substring(1).ToLowerInvariant());
        }
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, "Cp");
        return sb.ToString();

        static bool IsAsciiAlnum(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
    }
}