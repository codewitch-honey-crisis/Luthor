// Traversal of the flat int[] produced by Compier. Works on any code-unit span:
// ReadOnlySpan<byte> for UTF-8 and single-byte encodings, ReadOnlySpan<char> for UTF-16,
// ReadOnlySpan<int> for UTF-32.

using System;
using System.Numerics;

namespace Luthor;

internal static class Matcher
{
    // Next state offset for code unit c, or -1.
    private static int Next(int[] dfa, int state, int c)
    {
        int n = dfa[state + 3], b = state + 4;
        if (n <= 8)
        {
            for (int k = 0; k < n; k++, b += 3)
            {
                if (c < dfa[b]) return -1;          // ranges are sorted: no later one can match
                if (c <= dfa[b + 1]) return dfa[b + 2];
            }
            return -1;
        }
        int lo = 0, hi = n - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1, t = b + 3 * mid;
            if (c < dfa[t]) hi = mid - 1;
            else if (c > dfa[t + 1]) lo = mid + 1;
            else return dfa[t + 2];
        }
        return -1;
    }

    // Longest match at the start of input. Returns the rule index (-1 if none) and the
    // match length in code units. atLineStart: whether input[0] begins a line.
    internal static (int Accept, int Length) Match<T>(int[] dfa, ReadOnlySpan<T> input, bool atLineStart = true)
        where T : unmanaged, IBinaryInteger<T>
    {
        int newline = dfa[0];
        int s = Compiler.Header, accept = -1, length = 0, i = 0;
        bool bol = atLineStart;
        while (true)
        {
            if (bol && dfa[s + 1] >= 0) s = dfa[s + 1];                        // ^ edge
            bool eol = i == input.Length || int.CreateTruncating(input[i]) == newline;
            if (eol && dfa[s + 2] >= 0) s = dfa[s + 2];                        // $ edge
            if (dfa[s] >= 0) { accept = dfa[s]; length = i; }
            if (i == input.Length) break;
            int c = int.CreateTruncating(input[i]);
            s = Next(dfa, s, c);
            if (s < 0) break;
            i++;
            bol = c == newline;
        }
        return (accept, length);
    }
}
