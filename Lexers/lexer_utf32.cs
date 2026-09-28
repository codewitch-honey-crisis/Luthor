// Minimal lexer over a flat DFA table (UTF-32 table, input is an array of code points).
// The table was built with the error rule, so every position yields a token of length >= 1.
using System;
using System.Collections.Generic;
using System.Text;

static class Lexer
{
    static readonly int[] Dfa = {
        10, -1, 83, -1, 26, 0, 8, 165, 9, 10, 169, 11, 12, 165, 13, 13,
        169, 14, 31, 165, 32, 32, 169, 33, 34, 165, 35, 35, 182, 36, 39, 165,
        40, 43, 182, 44, 44, 165, 45, 45, 182, 46, 46, 165, 47, 47, 186, 48,
        57, 196, 58, 58, 165, 59, 59, 182, 60, 60, 165, 61, 61, 182, 62, 64,
        165, 65, 90, 203, 91, 94, 165, 95, 95, 203, 96, 96, 165, 97, 122, 203,
        123, 2147483647, 165, -1, -1, -1, 26, 0, 8, 165, 9, 10, 169, 11, 12, 165,
        13, 13, 169, 14, 31, 165, 32, 32, 169, 33, 34, 165, 35, 35, 219, 36,
        39, 165, 40, 43, 182, 44, 44, 165, 45, 45, 182, 46, 46, 165, 47, 47,
        186, 48, 57, 196, 58, 58, 165, 59, 59, 182, 60, 60, 165, 61, 61, 182,
        62, 64, 165, 65, 90, 203, 91, 94, 165, 95, 95, 203, 96, 96, 165, 97,
        122, 203, 123, 2147483647, 165, 7, -1, -1, 0, 5, -1, -1, 3, 9, 10, 169,
        13, 13, 169, 32, 32, 169, 6, -1, -1, 0, 6, -1, -1, 2, 42, 42,
        229, 47, 47, 242, 4, -1, -1, 1, 48, 57, 196, 3, -1, -1, 4, 48,
        57, 203, 65, 90, 203, 95, 95, 203, 97, 122, 203, 6, -1, 252, 2, 0,
        9, 262, 11, 1114111, 262, -1, -1, -1, 3, 0, 41, 229, 42, 42, 272, 43,
        1114111, 229, -1, -1, 291, 2, 0, 9, 242, 11, 1114111, 242, 0, -1, -1, 2,
        0, 9, 262, 11, 1114111, 262, -1, -1, 252, 2, 0, 9, 262, 11, 1114111, 262,
        -1, -1, -1, 5, 0, 41, 229, 42, 42, 272, 43, 46, 229, 47, 47, 301,
        48, 1114111, 229, 2, -1, -1, 2, 0, 9, 242, 11, 1114111, 242, 1, -1, -1,
        0
    };

    static readonly string[] Names = { "directive", "block", "line", "ident", "number", "ws", "op", "error" }; // "error": the built-in error rule

    // Longest match at text[pos..]. Returns (token id or -1, length).
    static (int Token, int Length) Match(int[] dfa, int[] text, int pos, bool atLineStart)
    {
        int state = 1, accept = -1, length = 0, i = pos;
        bool bol = atLineStart;
        while (true)
        {
            if (bol && dfa[state + 1] != -1) state = dfa[state + 1];                                // ^
            if ((i == text.Length || text[i] == dfa[0]) && dfa[state + 2] != -1) state = dfa[state + 2]; // $
            if (dfa[state] != -1) { accept = dfa[state]; length = i - pos; }
            if (i == text.Length) break;
            int c = text[i], next = -1, r = state + 4;   // (min, max, target) triples, sorted
            for (int k = 0; k < dfa[state + 3] && c >= dfa[r]; k++, r += 3)
                if (c <= dfa[r + 1]) { next = dfa[r + 2]; break; }
            if (next == -1) break;
            state = next;
            bol = c == dfa[0];
            i++;
        }
        return (accept, length);
    }

    // Code points [pos, pos + len) back to a string (non-scalar values become U+FFFD).
    static string Show(int[] text, int pos, int len)
    {
        var sb = new StringBuilder();
        for (int k = pos; k < pos + len; k++)
        {
            int c = text[k];
            sb.Append((c >= 0xD800 && c <= 0xDFFF) || c > 0x10FFFF || c < 0 ? "\uFFFD" : char.ConvertFromUtf32(c));
        }
        return sb.ToString();
    }

    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        string source = "#define X 1\nx = a1 + 42; /* héllo 😀 */ y /* z */\n  # not a directive\n// bye ✓\n@ café `\n";
        var codePoints = new List<int>();
        foreach (Rune rune in source.EnumerateRunes()) codePoints.Add(rune.Value);
        int[] text = codePoints.ToArray();
        for (int pos = 0; pos < text.Length;)
        {
            bool atLineStart = pos == 0 || text[pos - 1] == Dfa[0];
            var (tok, len) = Match(Dfa, text, pos, atLineStart);
            if (tok != 5) Console.WriteLine($"{Names[tok],-9} " + Show(text, pos, len).Replace("\n", "\\n"));
            pos += len;
        }
    }
}
