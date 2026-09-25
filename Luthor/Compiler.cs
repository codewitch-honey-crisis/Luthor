// Turns a codepoint DFA into a code-unit DFA for a given encoding, minimizes it,
// and flattens it into a single int[].
//
// Flat layout:
//   dfa[0]            newline code unit in this encoding (used by ^ and $), -1 if none
//   then per state, starting at offset 1 (the start state):
//     accept          rule index, or -1
//     bol             offset of the state reached by the ^ edge, or -1
//     eol             offset of the state reached by the $ edge, or -1
//     n               number of ranges
//     n x (min, max, target)   sorted by min, non-overlapping; target is an array offset

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Luthor;

internal static class Compiler
{
    internal static int[] Compile(IList<DfaState> cpDfa, string encoding = "UTF-8", bool minimize = true)
    {
        var (states, newline) = Transform(cpDfa, encoding);
        if (minimize) states = Minimize(states);
        return Flatten(states, newline);
    }

    // ---------------- encoding transform ----------------
    private static (List<DfaState> States, int Newline) Transform(IList<DfaState> cp, string encoding)
    {
        string e = encoding.ToUpperInvariant().Replace("-", "").Replace("_", "");
        switch (e)
        {
            case "UTF32": case "UTF32LE": case "UTF32BE":
                return (Copy(cp, keepMoves: true), '\n');
            case "UTF8":
                return (Sequenced(cp, Utf8Sequences), '\n');
            case "UTF16": case "UTF16LE": case "UTF16BE": case "UNICODE":
                return (Sequenced(cp, Utf16Sequences), '\n');
            default:
                return SingleByte(cp, encoding);
        }
    }

    static List<DfaState> Copy(IList<DfaState> cp, bool keepMoves) =>
        cp.Select(s => new DfaState { Accept = s.Accept, Bol = s.Bol, Eol = s.Eol, Moves = keepMoves ? s.Moves.ToList() : new() }).ToList();

    // Multi-unit encodings: every codepoint range becomes one or more sequences of
    // code-unit ranges; sequences leaving a state are merged into a trie of new states.
    static List<DfaState> Sequenced(IList<DfaState> cp, Func<int, int, List<(int Lo, int Hi)[]>> seqs)
    {
        var states = Copy(cp, keepMoves: false);
        var memo = new Dictionary<string, int>();
        for (int s = 0; s < cp.Count; s++)
        {
            var items = new List<((int Lo, int Hi)[] Seq, int To)>();
            foreach (var (lo, hi, to) in cp[s].Moves)
                foreach (var seq in seqs(lo, hi)) items.Add((seq, to));
            states[s].Moves = BuildTrie(items, 0, states, memo);
        }
        return states;
    }

    static List<(int Lo, int Hi, int To)> BuildTrie(List<((int Lo, int Hi)[] Seq, int To)> items, int depth,
        List<DfaState> states, Dictionary<string, int> memo)
    {
        var moves = new List<(int Lo, int Hi, int To)>();
        var points = items.SelectMany(t => new[] { t.Seq[depth].Lo, t.Seq[depth].Hi + 1 }).Distinct().OrderBy(x => x).ToList();
        for (int j = 0; j + 1 < points.Count; j++)
        {
            int lo = points[j], hi = points[j + 1] - 1;
            var group = items.Where(t => t.Seq[depth].Lo <= lo && hi <= t.Seq[depth].Hi).ToList();
            if (group.Count == 0) continue;
            int to;
            if (group.All(t => t.Seq.Length == depth + 1))
            {
                to = group[0].To;
                if (group.Any(t => t.To != to)) throw new InvalidOperationException("ambiguous encoding");
            }
            else
            {
                if (group.Any(t => t.Seq.Length == depth + 1)) throw new InvalidOperationException("mixed sequence lengths");
                // share identical suffix sub-tries
                string key = string.Join(";", group.Select(t =>
                    string.Join(",", t.Seq.Skip(depth + 1).Select(r => $"{r.Lo}-{r.Hi}")) + ">" + t.To).OrderBy(x => x, StringComparer.Ordinal));
                if (!memo.TryGetValue(key, out to))
                {
                    to = states.Count; states.Add(new DfaState()); memo[key] = to;
                    states[to].Moves = BuildTrie(group, depth + 1, states, memo);
                }
            }
            if (moves.Count > 0 && moves[^1].To == to && moves[^1].Hi + 1 == lo) moves[^1] = (moves[^1].Lo, hi, to);
            else moves.Add((lo, hi, to));
        }
        return moves;
    }

    // Splits [lo,hi] around the surrogate block, which is not encodable in UTF-8/UTF-16.
    static IEnumerable<(int Lo, int Hi)> NoSurrogates(int lo, int hi)
    {
        if (hi < 0xD800 || lo > 0xDFFF) { yield return (lo, hi); yield break; }
        if (lo < 0xD800) yield return (lo, 0xD7FF);
        if (hi > 0xDFFF) yield return (0xE000, hi);
    }

    private static List<(int Lo, int Hi)[]> Utf8Sequences(int lo, int hi)
    {
        var res = new List<(int, int)[]>();
        foreach (var (a, b) in NoSurrogates(lo, hi))
        {
            // split where the encoded length changes
            int s = a;
            foreach (int limit in new[] { 0x7F, 0x7FF, 0xFFFF, 0x10FFFF })
            {
                if (s > b) break;
                if (s > limit) continue;
                Utf8Split(s, Math.Min(b, limit), res);
                s = limit + 1;
            }
        }
        return res;
    }

    // Same encoded length assumed. Splits until every byte position is an independent range.
    static void Utf8Split(int lo, int hi, List<(int, int)[]> res)
    {
        int n = Utf8Encode(lo).Length;
        for (int k = 1; k < n; k++)
        {
            int m = (1 << (6 * k)) - 1;
            if ((lo & ~m) != (hi & ~m))
            {
                if ((lo & m) != 0) { Utf8Split(lo, lo | m, res); Utf8Split((lo | m) + 1, hi, res); return; }
                if ((hi & m) != m) { Utf8Split(lo, (hi & ~m) - 1, res); Utf8Split(hi & ~m, hi, res); return; }
            }
        }
        var x = Utf8Encode(lo); var y = Utf8Encode(hi);
        res.Add(x.Zip(y, (p, q) => (p, q)).ToArray());
    }

    static int[] Utf8Encode(int cp) => cp switch
    {
        < 0x80 => new[] { cp },
        < 0x800 => new[] { 0xC0 | cp >> 6, 0x80 | cp & 0x3F },
        < 0x10000 => new[] { 0xE0 | cp >> 12, 0x80 | cp >> 6 & 0x3F, 0x80 | cp & 0x3F },
        _ => new[] { 0xF0 | cp >> 18, 0x80 | cp >> 12 & 0x3F, 0x80 | cp >> 6 & 0x3F, 0x80 | cp & 0x3F },
    };

    private static List<(int Lo, int Hi)[]> Utf16Sequences(int lo, int hi)
    {
        var res = new List<(int, int)[]>();
        foreach (var (a, b) in NoSurrogates(lo, hi))
        {
            if (a <= 0xFFFF) res.Add(new[] { (a, Math.Min(b, 0xFFFF)) });
            if (b >= 0x10000) Utf16Split(Math.Max(a, 0x10000) - 0x10000, b - 0x10000, res);
        }
        return res;
    }

    static void Utf16Split(int lo, int hi, List<(int, int)[]> res)
    {
        const int m = 0x3FF;
        if ((lo & ~m) != (hi & ~m))
        {
            if ((lo & m) != 0) { Utf16Split(lo, lo | m, res); Utf16Split((lo | m) + 1, hi, res); return; }
            if ((hi & m) != m) { Utf16Split(lo, (hi & ~m) - 1, res); Utf16Split(hi & ~m, hi, res); return; }
        }
        res.Add(new[] { (0xD800 + (lo >> 10), 0xD800 + (hi >> 10)), (0xDC00 + (lo & m), 0xDC00 + (hi & m)) });
    }

    // Any single-byte .NET encoding (ASCII, ISO-8859-x, Windows-125x, EBCDIC code pages, ...)
    static (List<DfaState>, int) SingleByte(IList<DfaState> cp, string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var enc = int.TryParse(name, out int page)
            ? Encoding.GetEncoding(page, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
            : Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        if (!enc.IsSingleByte) throw new NotSupportedException($"{name} is not UTF-8/16/32 or a single-byte encoding");
        var cpOf = new int[256];
        for (int b = 0; b < 256; b++)
        {
            try
            {
                string s = enc.GetString(new[] { (byte)b });
                cpOf[b] = s.Length == 1 ? s[0] : -1;
            }
            catch (DecoderFallbackException) { cpOf[b] = -1; }
        }
        int newline = -1;
        try { var nl = enc.GetBytes("\n"); if (nl.Length == 1) newline = nl[0]; } catch (EncoderFallbackException) { }

        var states = Copy(cp, keepMoves: false);
        for (int s = 0; s < cp.Count; s++)
        {
            var moves = states[s].Moves;
            for (int b = 0; b < 256; b++)
            {
                if (cpOf[b] < 0) continue;
                int to = Lookup(cp[s].Moves, cpOf[b]);
                if (to < 0) continue;
                if (moves.Count > 0 && moves[^1].To == to && moves[^1].Hi + 1 == b) moves[^1] = (moves[^1].Lo, b, to);
                else moves.Add((b, b, to));
            }
        }
        return (states, newline);
    }

    static int Lookup(List<(int Lo, int Hi, int To)> moves, int c)
    {
        int lo = 0, hi = moves.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (c < moves[mid].Lo) hi = mid - 1; else if (c > moves[mid].Hi) lo = mid + 1; else return moves[mid].To;
        }
        return -1;
    }

    // ---------------- minimization (Moore partition refinement) ----------------
    private static List<DfaState> Minimize(List<DfaState> states)
    {
        int n = states.Count;
        var cls = new int[n];
        int count = 1;
        while (true)
        {
            var ids = new Dictionary<string, int>();
            var next = new int[n];
            var sb = new StringBuilder();
            for (int s = 0; s < n; s++)
            {
                var st = states[s];
                sb.Clear();
                sb.Append(cls[s]).Append('|').Append(st.Accept).Append('|')
                  .Append(st.Bol < 0 ? -1 : cls[st.Bol]).Append('|').Append(st.Eol < 0 ? -1 : cls[st.Eol]).Append('|');
                foreach (var (lo, hi, to) in MergeBy(st.Moves, t => cls[t])) sb.Append(lo).Append('-').Append(hi).Append('>').Append(to).Append(',');
                string key = sb.ToString();
                if (!ids.TryGetValue(key, out next[s])) ids[key] = next[s] = ids.Count;
            }
            cls = next;
            if (ids.Count == count) break;
            count = ids.Count;
        }
        // renumber in BFS order from the start state (drops unreachable states)
        var order = new Dictionary<int, int> { [cls[0]] = 0 };
        var rep = new List<int> { 0 };
        var result = new List<DfaState>();
        int Num(int s)
        {
            if (s < 0) return -1;
            if (!order.TryGetValue(cls[s], out int k)) { k = order[cls[s]] = rep.Count; rep.Add(s); }
            return k;
        }
        for (int k = 0; k < rep.Count; k++)
        {
            var st = states[rep[k]];
            var ns = new DfaState { Accept = st.Accept, Bol = Num(st.Bol), Eol = Num(st.Eol) };
            ns.Moves = MergeBy(st.Moves, Num);
            result.Add(ns);
        }
        return result;
    }

    static List<(int Lo, int Hi, int To)> MergeBy(List<(int Lo, int Hi, int To)> moves, Func<int, int> map)
    {
        var res = new List<(int Lo, int Hi, int To)>();
        foreach (var (lo, hi, to) in moves)
        {
            int t = map(to);
            if (res.Count > 0 && res[^1].To == t && res[^1].Hi + 1 == lo) res[^1] = (res[^1].Lo, hi, t);
            else res.Add((lo, hi, t));
        }
        return res;
    }

    // ---------------- flatten ----------------
    internal const int Header = 1; // ints before the start state

    private static int[] Flatten(List<DfaState> states, int newline)
    {
        var off = new int[states.Count];
        int size = Header;
        for (int s = 0; s < states.Count; s++) { off[s] = size; size += 4 + 3 * states[s].Moves.Count; }
        var dfa = new int[size];
        dfa[0] = newline;
        for (int s = 0; s < states.Count; s++)
        {
            var st = states[s]; int k = off[s];
            dfa[k++] = st.Accept;
            dfa[k++] = st.Bol < 0 ? -1 : off[st.Bol];
            dfa[k++] = st.Eol < 0 ? -1 : off[st.Eol];
            dfa[k++] = st.Moves.Count;
            foreach (var (lo, hi, to) in st.Moves) { dfa[k++] = lo; dfa[k++] = hi; dfa[k++] = off[to]; }
        }
        return dfa;
    }
}
