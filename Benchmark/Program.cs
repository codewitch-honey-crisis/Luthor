namespace Benchmark;

using Luthor;
using System;
using System.Security.Cryptography;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

public class LuthorBenchmark
{
    private string lexer = @"# C example
# directive
^#.*$
# block comment
/\*(.|\n)*?\*/
# line comment
//.*$
# identifier
[A-Za-z_\u00C0-\uFFFF][A-Za-z0-9_\u00C0-\uFFFF]*
# number
[0-9]+
# string
""(\\.|[^""\\\n])*?""
# whitespace
[ \t\r\n]+
# operator
[-+*/=;#]
";
    private string text = @"#include <stddef.h>
/* Longest match at s[0..n). Returns the token id (-1 if none); *len gets the match length. */
static int match(const int* dfa, const unsigned char* s, size_t n, int at_line_start, size_t* len)
{
    int state = 1, accept = -1, bol = at_line_start;
    size_t i = 0;
    *len = 0;
    for (;;) {
        if (bol && dfa[state + 1] != -1) state = dfa[state + 1];                       // ^
        if ((i == n || s[i] == dfa[0]) && dfa[state + 2] != -1) state = dfa[state + 2]; // $
        if (dfa[state] != -1) { accept = dfa[state]; *len = i; }
        if (i == n) break;
        int c = s[i], next = -1;
        const int* r = dfa + state + 4;             // (min, max, target) triples, sorted
        for (int k = 0; k < dfa[state + 3] && c >= r[0]; k++, r += 3)
            if (c <= r[1]) { next = r[2]; break; }
        if (next == -1) break;
        state = next;
        bol = (c == dfa[0]);
        i++;
    }
    return accept;
}
";
    private readonly int[] lexerArray;

    public LuthorBenchmark()
    {
        var patterns = new List<string>();
        using var reader = new StringReader(lexer);
        foreach (var pattern in FileParser.ReadFrom(reader))
        {
            patterns.Add(pattern);
        }
        var dfa = Builder.Build(patterns, true);
        lexerArray = Compiler.Compile(dfa);
    }
    static void Tokenize<T>(int[] dfa, T[] input, Func<T[], string> decode) where T : unmanaged, System.Numerics.IBinaryInteger<T>
    {
        int newline = dfa[0];
        for (int pos = 0; pos < input.Length;)
        {
            bool atLineStart = pos == 0 || int.CreateTruncating(input[pos - 1]) == newline;
            var (tok, len) = Matcher.Match<T>(dfa, input.AsSpan(pos), atLineStart);
            pos += len;
        }
    }

    [Benchmark]
    public void Generate()
    {
        var patterns = new List<string>();
        using var reader = new StringReader(lexer);
        foreach (var pattern in FileParser.ReadFrom(reader))
        {
            patterns.Add(pattern);
        }
        var dfa = Builder.Build(patterns, true);
        Compiler.Compile(dfa);
    }

    [Benchmark]
    public void Match()
    {
        Tokenize(lexerArray, text.ToCharArray(), s => new string(s));
    }
}

public class Program
{
    public static void Main(string[] args)
    {
        var summary = BenchmarkRunner.Run<LuthorBenchmark>();

    }
}
    