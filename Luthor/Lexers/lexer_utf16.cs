// Minimal lexer over a flat DFA table (UTF-16 table, input is a string of UTF-16 chars).
// The table was built with the error rule, so every position yields a token of length >= 1.
using System;

static class Lexer
{
    static readonly int[] Dfa = {
        10, -1, 89, -1, 28, 0, 8, 177, 9, 10, 181, 11, 12, 177, 13, 13,
        181, 14, 31, 177, 32, 32, 181, 33, 34, 177, 35, 35, 194, 36, 39, 177,
        40, 43, 194, 44, 44, 177, 45, 45, 194, 46, 46, 177, 47, 47, 198, 48,
        57, 208, 58, 58, 177, 59, 59, 194, 60, 60, 177, 61, 61, 194, 62, 64,
        177, 65, 90, 215, 91, 94, 177, 95, 95, 215, 96, 96, 177, 97, 122, 215,
        123, 55295, 177, 55296, 56319, 231, 56320, 65535, 177, -1, -1, -1, 28, 0, 8, 177,
        9, 10, 181, 11, 12, 177, 13, 13, 181, 14, 31, 177, 32, 32, 181, 33,
        34, 177, 35, 35, 238, 36, 39, 177, 40, 43, 194, 44, 44, 177, 45, 45,
        194, 46, 46, 177, 47, 47, 198, 48, 57, 208, 58, 58, 177, 59, 59, 194,
        60, 60, 177, 61, 61, 194, 62, 64, 177, 65, 90, 215, 91, 94, 177, 95,
        95, 215, 96, 96, 177, 97, 122, 215, 123, 55295, 177, 55296, 56319, 231, 56320, 65535,
        177, 7, -1, -1, 0, 5, -1, -1, 3, 9, 10, 181, 13, 13, 181, 32,
        32, 181, 6, -1, -1, 0, 6, -1, -1, 2, 42, 42, 254, 47, 47, 273,
        4, -1, -1, 1, 48, 57, 208, 3, -1, -1, 4, 48, 57, 215, 65, 90,
        215, 95, 95, 215, 97, 122, 215, 7, -1, -1, 1, 56320, 57343, 177, 6, -1,
        289, 4, 0, 9, 305, 11, 55295, 305, 55296, 56319, 321, 57344, 65535, 305, -1, -1,
        -1, 5, 0, 41, 254, 42, 42, 328, 43, 55295, 254, 55296, 56319, 353, 57344, 65535,
        254, -1, -1, 360, 4, 0, 9, 273, 11, 55295, 273, 55296, 56319, 376, 57344, 65535,
        273, 0, -1, -1, 4, 0, 9, 305, 11, 55295, 305, 55296, 56319, 321, 57344, 65535,
        305, -1, -1, 289, 4, 0, 9, 305, 11, 55295, 305, 55296, 56319, 321, 57344, 65535,
        305, -1, -1, -1, 1, 56320, 57343, 305, -1, -1, -1, 7, 0, 41, 254, 42,
        42, 328, 43, 46, 254, 47, 47, 383, 48, 55295, 254, 55296, 56319, 353, 57344, 65535,
        254, -1, -1, -1, 1, 56320, 57343, 254, 2, -1, -1, 4, 0, 9, 273, 11,
        55295, 273, 55296, 56319, 376, 57344, 65535, 273, -1, -1, -1, 1, 56320, 57343, 273, 1,
        -1, -1, 0
    };

    static readonly string[] Names = { "directive", "block", "line", "ident", "number", "ws", "op", "error" }; // "error": the built-in error rule

    // Longest match at text[pos..]. Returns (token id or -1, length).
    static (int Token, int Length) Match(int[] dfa, string text, int pos, bool atLineStart)
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

    static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string text = "#define X 1\nx = a1 + 42; /* héllo 😀 */ y /* z */\n  # not a directive\n// bye ✓\n@ café `\n";
        for (int pos = 0; pos < text.Length;)
        {
            bool atLineStart = pos == 0 || text[pos - 1] == Dfa[0];
            var (tok, len) = Match(Dfa, text, pos, atLineStart);
            if (tok != 5) Console.WriteLine($"{Names[tok],-9} " + text.Substring(pos, len).Replace("\n", "\\n"));
            pos += len;
        }
    }
}