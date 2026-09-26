// Lazy-quantifier DFA construction in the style of RE/flex (Dr. Robert van Engelen).
// Direct followpos construction; lazy quantifiers are handled by tagging positions
// with a lazy index and trimming DFA states during subset construction.
// LazyAsu builds a DFA over Unicode codepoints; DfaEncoder turns it into a code-unit table.
//
// Syntax: literals, escapes (\n \r \t \f \v \0 \xHH \x{H..} \uHHHH \d \D \w \W \s \S),
// '.', [...] / [^...], ( ), |, *, +, ?, {n}, {n,}, {n,m}, lazy forms of all quantifiers,
// and the anchors ^ (line start) and $ (line end, i.e. before '\n' or end of input).
// Not supported: lookahead, captures, word boundaries, case-insensitivity.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Luthor
{
    // A DFA state, used both for the codepoint DFA and for the code-unit DFA.
    // Bol / Eol are zero-width edges taken when at a line start / before '\n' or end of input.
    internal sealed class DfaState
    {
        internal int Accept = -1, Bol = -1, Eol = -1;
        internal List<(int Lo, int Hi, int To)> Moves = new(); // sorted, non-overlapping
    }

    // Output of LazyAsu.Build: codepoint-level states (state 0 is the start state) and the
    // accept id of the error rule, or -1 if there is none.
    internal sealed record CodepointDfa(List<DfaState> States, int ErrorId);

    // Lazy-aware Aho-Sethi-Ullman (followpos) DFA construction.
    internal sealed class Builder
    {
        // A position is a leaf in the regex, plus a lazy tag. Accept "positions" use Id = rule index.
        // The same leaf with different lazy tags is a DIFFERENT position.
        private readonly record struct Pos(int Id, byte Lazy, bool Accept) : IComparable<Pos>
        {
            internal Pos WithLazy(byte l) => this with { Lazy = l };
            int IComparable<Pos>.CompareTo(Pos o)
            {
                int c = Accept.CompareTo(o.Accept); if (c != 0) return c;
                c = Lazy.CompareTo(o.Lazy); if (c != 0) return c;
                return Id.CompareTo(o.Id);
            }
            public override string ToString() => (Accept ? $"#{Id}" : $"{Id}") + (Lazy != 0 ? $"?{Lazy}" : "");
        }

        // One lazy quantifier: its index and its source location (the '?' character).
        private readonly record struct LazyQ(byte Index, int Loc);

        private enum LeafKind { Char, LineStart, LineEnd }

        private sealed class Leaf
        {
            internal LeafKind Kind;
            internal List<(int Lo, int Hi)> Set = new(); // codepoint ranges (Char leaves only)
            internal int Loc;                             // source location; {n,m} copies share it
        }

        private sealed class Frag
        {
            internal List<Pos> First = new(), Last = new();
            internal bool Nullable;
            internal List<LazyQ> Lazies = new(); // lazy quantifiers active in this subexpression
        }

        private static class Ranges
        {
            internal const int MaxCp = 0x10FFFF;
            internal static List<(int Lo, int Hi)> Normalize(IEnumerable<(int Lo, int Hi)> rs)
            {
                var res = new List<(int Lo, int Hi)>();
                foreach (var r in rs.OrderBy(r => r.Lo))
                {
                    if (res.Count > 0 && r.Lo <= res[^1].Hi + 1) res[^1] = (res[^1].Lo, Math.Max(res[^1].Hi, r.Hi));
                    else res.Add(r);
                }
                return res;
            }
            internal static List<(int Lo, int Hi)> Complement(IEnumerable<(int Lo, int Hi)> rs)
            {
                var res = new List<(int Lo, int Hi)>(); int next = 0;
                foreach (var r in Normalize(rs)) { if (r.Lo > next) res.Add((next, r.Lo - 1)); next = r.Hi + 1; }
                if (next <= MaxCp) res.Add((next, MaxCp));
                return res;
            }
            internal static readonly (int, int)[] Digit = { ('0', '9') };
            internal static readonly (int, int)[] Word = { ('0', '9'), ('A', 'Z'), ('_', '_'), ('a', 'z') };
            internal static readonly (int, int)[] Space = { ('\t', '\r'), (' ', ' ') };
        }

        private readonly List<Leaf> Leaves = new();
        private readonly Dictionary<int, List<Pos>> Follow = new();     // leaf id -> followpos
        private readonly List<LazyQ> AllLazies = new();
        byte lazyIdx = 0;
        string re = ""; int i, offset;   // offset makes source locations global across rules

        static void Add(List<Pos> s, Pos p) { if (!s.Contains(p)) s.Add(p); }
        static void AddAll(List<Pos> s, IEnumerable<Pos> ps) { foreach (var p in ps) Add(s, p); }
        List<Pos> F(int id) => Follow.TryGetValue(id, out var l) ? l : Follow[id] = new List<Pos>();

        // followpos of k, with k's lazy tag propagated along the path
        IEnumerable<Pos> FollowOf(Pos k) => F(k.Id).Select(p => k.Lazy != 0 ? p.WithLazy(k.Lazy) : p);

        // ================= parsing: alternation > concatenation > postfix > atom =================
        private Frag ParseRule(string pattern)
        {
            re = pattern; i = 0;
            var f = Alt();
            if (i != re.Length) throw new FormatException($"unexpected '{re[i]}' at {i}");
            offset += pattern.Length + 1;
            return f;
        }

        Frag Alt()
        {
            var f = Concat();
            while (i < re.Length && re[i] == '|')
            {
                i++;
                var g = Concat();
                AddAll(f.First, g.First); AddAll(f.Last, g.Last);
                f.Nullable |= g.Nullable; f.Lazies.AddRange(g.Lazies);
            }
            return f;
        }

        Frag Concat()
        {
            var f = new Frag { Nullable = true };
            bool first = true;
            while (i < re.Length && re[i] != '|' && re[i] != ')')
            {
                var g = Postfix();
                if (first) { f = g; first = false; continue; }
                if (f.Nullable) AddAll(f.First, g.First);
                foreach (var p in f.Last) AddAll(F(p.Id), g.First);
                if (g.Nullable) AddAll(f.Last, g.Last); else { f.Last = g.Last; f.Nullable = false; }
                f.Lazies.AddRange(g.Lazies);
            }
            return f;
        }

        bool AtRepeat() => i + 1 < re.Length && re[i] == '{' && char.IsDigit(re[i + 1]);

        // stopAt limits parsing when re-parsing an operand to make a {n,m} copy
        Frag Postfix(int stopAt = int.MaxValue)
        {
            int start = i;
            byte lazyIdx0 = lazyIdx;
            var f = Atom();
            while (i < re.Length && i < stopAt && (re[i] == '*' || re[i] == '+' || re[i] == '?' || AtRepeat()))
            {
                if (re[i] == '{') { f = Repeat(f, start, lazyIdx0); continue; }
                char c = re[i++];
                if (c != '+') f.Nullable = true;
                if (i < re.Length && re[i] == '?')
                {
                    // NEW LAZY QUANTIFIER: tag the entry points (firstpos) with a fresh index.
                    var q = NewLazy(offset + i++);
                    f.Lazies.Add(q);
                    f.First = f.First.Select(p => p.WithLazy(q.Index)).Distinct().ToList();
                }
                else if (c != '?' && f.Lazies.Count > 0)
                {
                    // greedy loop around something containing a lazy quantifier: entries become greedy
                    f.First = f.First.Select(p => p.WithLazy(0)).Distinct().ToList();
                }
                if (c != '?')
                    foreach (var p in f.Last) AddAll(F(p.Id), f.First); // loop back
            }
            return f;
        }

        LazyQ NewLazy(int loc)
        {
            if (lazyIdx == 255) throw new FormatException("too many lazy quantifiers (max 255)");
            var q = new LazyQ(++lazyIdx, loc);
            if (!AllLazies.Contains(q)) AllLazies.Add(q);
            return q;
        }

        // X{n}, X{n,}, X{n,m}, optionally lazy. f is the already-parsed first copy of X,
        // whose source text is re[start .. i).  Copies 2..m are made by re-parsing that text.
        Frag Repeat(Frag f, int start, byte lazyIdx0)
        {
            int qpos = i++; // at '{'
            int n = Num(), m = n; bool unlimited = false;
            if (re[i] == ',') { i++; if (char.IsDigit(re[i])) m = Num(); else unlimited = true; }
            if (i >= re.Length || re[i++] != '}') throw new FormatException($"bad repeat at {qpos}");
            if (n > m && !unlimited) throw new FormatException($"bad repeat {n}>{m}");
            int after = i;
            bool lazy = i < re.Length && re[i] == '?';
            if (lazy) i++;

            if (!unlimited && m == 0) return new Frag { Nullable = true };      // X{0} matches empty
            if (unlimited && n == 0) m = 1;                                      // X{0,} is X*

            // copies 1..m-1; re-parsing reuses the same lazy indexes for lazy quantifiers
            // inside X, like RE/flex's virtual copies do
            byte lazyIdxAfter = lazyIdx;
            var copies = new List<Frag> { f };
            for (int k = 1; k < m; k++)
            {
                i = start; lazyIdx = lazyIdx0;
                copies.Add(Postfix(stopAt: qpos));
            }
            lazyIdx = lazyIdxAfter; i = after + (lazy ? 1 : 0);

            if (lazy)
            {
                var q = NewLazy(offset + after);
                f.Lazies.Add(q);
                foreach (var c in copies) c.First = c.First.Select(p => p.WithLazy(q.Index)).Distinct().ToList();
            }

            bool xNullable = f.Nullable;
            var r = new Frag { Nullable = xNullable || n == 0, Lazies = f.Lazies };
            for (int k = 0; k + 1 < copies.Count; k++)                       // copy k -> copy k+1
                foreach (var p in copies[k].Last) AddAll(F(p.Id), copies[k + 1].First);
            if (unlimited)                                                    // last copy loops
                foreach (var p in copies[^1].Last) AddAll(F(p.Id), copies[^1].First);
            AddAll(r.First, copies[0].First);
            if (xNullable) for (int k = 1; k < copies.Count; k++) AddAll(r.First, copies[k].First);
            for (int k = r.Nullable ? 0 : n - 1; k < copies.Count; k++) AddAll(r.Last, copies[k].Last);
            return r;
        }

        int Num()
        {
            int s = i;
            while (i < re.Length && char.IsDigit(re[i])) i++;
            if (s == i) throw new FormatException($"expected number at {s}");
            return int.Parse(re.AsSpan(s, i - s));
        }

        Frag NewLeaf(LeafKind kind, List<(int, int)> set, int loc)
        {
            int id = Leaves.Count;
            Leaves.Add(new Leaf { Kind = kind, Set = set, Loc = offset + loc });
            var p = new Pos(id, 0, false);
            return new Frag { First = { p }, Last = { p }, Nullable = false };
        }

        Frag Atom()
        {
            int loc = i;
            char c = re[i];
            switch (c)
            {
                case '(':
                    i++;
                    if (i + 1 < re.Length && re[i] == '?' && re[i + 1] == ':') i += 2; // (?: ) = ( )
                    var f = Alt();
                    if (i >= re.Length || re[i++] != ')') throw new FormatException("missing )");
                    return f;
                case '^': i++; return NewLeaf(LeafKind.LineStart, new(), loc);
                case '$': i++; return NewLeaf(LeafKind.LineEnd, new(), loc);
                case '.': i++; return NewLeaf(LeafKind.Char, new() { (0, '\n' - 1), ('\n' + 1, Ranges.MaxCp) }, loc);
                case '[': return NewLeaf(LeafKind.Char, ParseClass(), loc);
                case '*':
                case '+':
                case '?':
                case ')':
                    throw new FormatException($"unexpected '{c}' at {i}");
                case '\\': return NewLeaf(LeafKind.Char, ParseEscape(out _), loc);
                default:
                    int cp = NextCodepoint();
                    return NewLeaf(LeafKind.Char, new() { (cp, cp) }, loc);
            }
        }

        int NextCodepoint()
        {
            if (char.IsHighSurrogate(re[i]) && i + 1 < re.Length && char.IsLowSurrogate(re[i + 1]))
            { int cp = char.ConvertToUtf32(re[i], re[i + 1]); i += 2; return cp; }
            return re[i++];
        }

        // at '\'; returns the set; isClass is true for \d \w \s and their negations
        List<(int, int)> ParseEscape(out bool isClass)
        {
            i++; isClass = false;
            if (i >= re.Length) throw new FormatException("trailing backslash");
            char c = re[i++];
            List<(int, int)> One(int cp) => new() { (cp, cp) };
            switch (c)
            {
                case 'n': return One('\n');
                case 'r': return One('\r');
                case 't': return One('\t');
                case 'f': return One('\f');
                case 'v': return One('\v');
                case '0': return One(0);
                case 'd': isClass = true; return Ranges.Digit.ToList();
                case 'w': isClass = true; return Ranges.Word.ToList();
                case 's': isClass = true; return Ranges.Space.ToList();
                case 'D': isClass = true; return Ranges.Complement(Ranges.Digit);
                case 'W': isClass = true; return Ranges.Complement(Ranges.Word);
                case 'S': isClass = true; return Ranges.Complement(Ranges.Space);
                case 'x':
                    if (i < re.Length && re[i] == '{')
                    {
                        int e = re.IndexOf('}', i);
                        if (e < 0) throw new FormatException("bad \\x{...}");
                        int v = Convert.ToInt32(re.Substring(i + 1, e - i - 1), 16); i = e + 1;
                        return One(v);
                    }
                    return One(Hex(2));
                case 'u': return One(Hex(4));
                default: i--; return One(NextCodepoint());
            }
        }

        int Hex(int digits)
        {
            if (i + digits > re.Length) throw new FormatException("bad hex escape");
            int v = Convert.ToInt32(re.Substring(i, digits), 16); i += digits; return v;
        }

        List<(int, int)> ParseClass()
        {
            i++; // '['
            bool neg = i < re.Length && re[i] == '^'; if (neg) i++;
            var set = new List<(int, int)>();
            bool first = true;
            while (true)
            {
                if (i >= re.Length) throw new FormatException("missing ]");
                if (re[i] == ']' && !first) { i++; break; }
                first = false;
                int lo;
                if (re[i] == '\\')
                {
                    var e = ParseEscape(out bool isClass);
                    if (isClass) { set.AddRange(e); continue; }
                    lo = e[0].Item1;
                }
                else lo = NextCodepoint();
                int hi = lo;
                if (i + 1 < re.Length && re[i] == '-' && re[i + 1] != ']')
                {
                    i++;
                    hi = re[i] == '\\' ? ParseEscape(out _)[0].Item1 : NextCodepoint();
                    if (hi < lo) throw new FormatException("bad range in class");
                }
                set.Add((lo, hi));
            }
            return neg ? Ranges.Complement(set) : Ranges.Normalize(set);
        }

        // ================= the heart of it: trim a DFA state =================
        private void TrimLazy(List<Pos> s)
        {
            // 1. If some position tagged l is an accept, the lazy quantifier l has "succeeded":
            //    kill every other thread that carries tag l (cuts the lazy loop edges).
            for (int k = 0; k < s.Count; k++)
            {
                var p = s[k];
                if (p.Lazy != 0 && p.Accept)
                {
                    byte l = p.Lazy;
                    s.RemoveAll(q => q.Lazy == l && !(q.Accept && q.Id == p.Id));
                    s[s.IndexOf(p)] = p.WithLazy(0);
                    k = -1; // restart scan; list changed
                }
            }
            s.Sort(); DedupSorted(s);
            // 2. If every remaining thread is lazy, positions past the relevant lazy quantifier(s)
            //    drop their tag (normalization; mirrors RE/flex trim_lazy's second half).
            if (s.Count > 0 && s.All(p => p.Lazy != 0))
            {
                int max = -1;
                foreach (var q in AllLazies)
                    if (s.Any(p => p.Lazy == q.Index) && q.Loc > max) max = q.Loc;
                if (max >= 0)
                    for (int k = 0; k < s.Count; k++)
                        if (!s[k].Accept && Leaves[s[k].Id].Loc > max) s[k] = s[k].WithLazy(0);
                s.Sort(); DedupSorted(s);
            }
        }
        static void DedupSorted(List<Pos> s) { for (int k = s.Count - 1; k > 0; k--) if (s[k].Equals(s[k - 1])) s.RemoveAt(k); }

        // ================= subset construction over codepoints =================
        // Rule 0 has the highest priority when several rules accept the same length.
        // errorRule adds a catch-all rule with the lowest priority (accept id = rules.Count) that
        // matches any single character, so every position of the input produces a token. The
        // encoder extends it to invalid code units (see DfaEncoder).
        internal static CodepointDfa Build(IReadOnlyList<string> rules, bool errorRule = false)
        {
            var builder = new Builder();
            if (errorRule) rules = rules.Append(@"[\x{0}-\x{10FFFF}]").ToList();
            var start = new List<Pos>();
            for (int r = 0; r < rules.Count; r++)
            {
                var f = builder.ParseRule(rules[r]);
                AddAll(start, f.First);
                if (f.Nullable) Add(start, new Pos(r, 0, true));
                // accept positions carry the rule's lazy tags, so a path that SKIPS a lazy loop
                // still cuts the loop when it accepts
                var accepts = f.Lazies.Count == 0
                    ? new List<Pos> { new Pos(r, 0, true) }
                    : f.Lazies.Select(q => new Pos(r, q.Index, true)).ToList();
                foreach (var p in f.Last) AddAll(builder.F(p.Id), accepts);
            }
            builder.TrimLazy(start);

            var sets = new List<List<Pos>> { start };
            var index = new Dictionary<string, int> { [Key(start)] = 0 };
            int Intern(List<Pos> s)
            {
                string key = Key(s);
                if (!index.TryGetValue(key, out int t)) { t = sets.Count; sets.Add(s); index[key] = t; }
                return t;
            }

            var result = new List<DfaState>();
            for (int si = 0; si < sets.Count; si++)
            {
                var S = sets[si];
                var st = new DfaState();
                st.Accept = S.Where(p => p.Accept).Select(p => p.Id).DefaultIfEmpty(-1).Min();

                // --- character moves: split all leaf ranges into disjoint elementary intervals ---
                var items = new List<(int Lo, int Hi, List<Pos> Follow)>();
                foreach (var k in S)
                {
                    if (k.Accept || builder.Leaves[k.Id].Kind != LeafKind.Char) continue;
                    var follow = builder.FollowOf(k).ToList();
                    foreach (var (lo, hi) in builder.Leaves[k.Id].Set) items.Add((lo, hi, follow));
                }
                var points = items.SelectMany(t => new[] { t.Lo, t.Hi + 1 }).Distinct().OrderBy(x => x).ToList();
                var buckets = new List<Pos>?[Math.Max(0, points.Count - 1)];
                foreach (var (lo, hi, follow) in items)
                    for (int j = points.BinarySearch(lo); j < buckets.Length && points[j] <= hi; j++)
                        AddAll(buckets[j] ??= new List<Pos>(), follow);
                for (int j = 0; j < buckets.Length; j++)
                {
                    if (buckets[j] is not { } target) continue;
                    builder.TrimLazy(target);
                    if (target.Count == 0) continue;
                    int t = Intern(target);
                    int lo = points[j], hi = points[j + 1] - 1;
                    if (st.Moves.Count > 0 && st.Moves[^1].To == t && st.Moves[^1].Hi + 1 == lo)
                        st.Moves[^1] = (st.Moves[^1].Lo, hi, t);
                    else st.Moves.Add((lo, hi, t));
                }

                // --- zero-width anchor edges ---
                st.Bol = builder.AnchorEdge(S, LeafKind.LineStart, Intern);
                st.Eol = builder.AnchorEdge(S, LeafKind.LineEnd, Intern);
                result.Add(st);
            }
            return new CodepointDfa(result, errorRule ? rules.Count - 1 : -1);
        }

        // Taking an anchor edge: anchor threads advance past the anchor, all other threads stay
        // where they are. The result goes through the same lazy trimming as any other move.
        int AnchorEdge(List<Pos> S, LeafKind kind, Func<List<Pos>, int> intern)
        {
            bool IsAnchor(Pos p) => !p.Accept && Leaves[p.Id].Kind == kind;
            if (!S.Any(IsAnchor)) return -1;
            var work = new List<Pos>(S);
            var done = new HashSet<Pos>();
            while (work.Any(IsAnchor))
            {
                var next = new List<Pos>();
                foreach (var p in work)
                {
                    if (!IsAnchor(p)) Add(next, p);
                    else if (done.Add(p)) AddAll(next, FollowOf(p));
                }
                work = next;
            }
            TrimLazy(work);
            return intern(work);
        }

        static string Key(List<Pos> s) => string.Join(",", s.OrderBy(p => p));
    }
}