// Minimal lexer over a flat DFA table (UTF-16 table, input is a string of UTF-16 chars).
using System;

static class Lexer
{
    static readonly int[] Dfa = {
        10, -1, 44, -1, 13, 9, 10, 87, 13, 13, 87, 32, 32, 87, 35, 35,
        100, 40, 43, 100, 45, 45, 100, 47, 47, 104, 48, 57, 114, 59, 59, 100,
        61, 61, 100, 65, 90, 121, 95, 95, 121, 97, 122, 121, -1, -1, -1, 13,
        9, 10, 87, 13, 13, 87, 32, 32, 87, 35, 35, 137, 40, 43, 100, 45,
        45, 100, 47, 47, 104, 48, 57, 114, 59, 59, 100, 61, 61, 100, 65, 90,
        121, 95, 95, 121, 97, 122, 121, 5, -1, -1, 3, 9, 10, 87, 13, 13,
        87, 32, 32, 87, 6, -1, -1, 0, 6, -1, -1, 2, 42, 42, 153, 47,
        47, 172, 4, -1, -1, 1, 48, 57, 114, 3, -1, -1, 4, 48, 57, 121,
        65, 90, 121, 95, 95, 121, 97, 122, 121, 6, -1, 188, 4, 0, 9, 204,
        11, 55295, 204, 55296, 56319, 220, 57344, 65535, 204, -1, -1, -1, 5, 0, 41, 153,
        42, 42, 227, 43, 55295, 153, 55296, 56319, 252, 57344, 65535, 153, -1, -1, 259, 4,
        0, 9, 172, 11, 55295, 172, 55296, 56319, 275, 57344, 65535, 172, 0, -1, -1, 4,
        0, 9, 204, 11, 55295, 204, 55296, 56319, 220, 57344, 65535, 204, -1, -1, 188, 4,
        0, 9, 204, 11, 55295, 204, 55296, 56319, 220, 57344, 65535, 204, -1, -1, -1, 1,
        56320, 57343, 204, -1, -1, -1, 7, 0, 41, 153, 42, 42, 227, 43, 46, 153,
        47, 47, 282, 48, 55295, 153, 55296, 56319, 252, 57344, 65535, 153, -1, -1, -1, 1,
        56320, 57343, 153, 2, -1, -1, 4, 0, 9, 172, 11, 55295, 172, 55296, 56319, 275,
        57344, 65535, 172, -1, -1, -1, 1, 56320, 57343, 172, 1, -1, -1, 0
    };

    static readonly string[] Names = { "directive", "block", "line", "ident", "number", "ws", "op" };

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
        string text = "#define X 1\nx = a1 + 42; /* héllo 😀 */ y /* z */\n  # not a directive\n// bye ✓\n";
        for (int pos = 0; pos < text.Length;)
        {
            bool atLineStart = pos == 0 || text[pos - 1] == Dfa[0];
            var (tok, len) = Match(Dfa, text, pos, atLineStart);
            if (len == 0) { Console.WriteLine($"error at {pos}"); pos++; continue; }
            if (tok != 5) Console.WriteLine($"{Names[tok],-9} " + text.Substring(pos, len).Replace("\n", "\\n"));
            pos += len;
        }
    }
}
