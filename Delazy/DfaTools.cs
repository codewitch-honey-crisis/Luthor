// DFA utilities: trimming, minimization, RE/flex-style semantic equivalence, and state elimination.

using Luthor;

using System.Runtime.InteropServices;

namespace Delazy;

internal static class DfaTools
{
    static IEnumerable<int> Succ(DfaState s)
    {
        foreach (var m in s.Moves) yield return m.To;
        if (s.Bol >= 0) yield return s.Bol;
        if (s.Eol >= 0) yield return s.Eol;
    }

    static DfaState Clone(DfaState s) => new() { Accept = s.Accept, Bol = s.Bol, Eol = s.Eol, Moves = new(s.Moves) };

    // Remove states that are unreachable from the start or can never reach an accepting state.
    // Returns null if the language is empty. State 0 stays the start state.
    public static List<DfaState>? Trim(List<DfaState> states)
    {
        int n = states.Count;
        var reach = new bool[n];
        var stack = new Stack<int>(); stack.Push(0); reach[0] = true;
        while (stack.Count > 0) foreach (var t in Succ(states[stack.Pop()])) if (!reach[t]) { reach[t] = true; stack.Push(t); }

        var live = new bool[n];
        bool changed = true;
        for (int s = 0; s < n; s++) live[s] = states[s].Accept >= 0;
        while (changed)
        {
            changed = false;
            for (int s = 0; s < n; s++)
                if (!live[s] && Succ(states[s]).Any(t => live[t])) { live[s] = true; changed = true; }
        }
        if (!live[0]) return null;

        var map = new int[n]; int k = 0;
        for (int s = 0; s < n; s++) map[s] = reach[s] && live[s] ? k++ : -1;
        var res = new List<DfaState>();
        for (int s = 0; s < n; s++)
        {
            if (map[s] < 0) continue;
            var o = states[s];
            res.Add(new DfaState
            {
                Accept = o.Accept,
                Bol = o.Bol >= 0 ? map[o.Bol] : -1,
                Eol = o.Eol >= 0 ? map[o.Eol] : -1,
                Moves = o.Moves.Where(m => map[m.To] >= 0).Select(m => (m.Lo, m.Hi, map[m.To])).ToList(),
            });
        }
        return res;
    }

    // Moore partition refinement. Anchor edges are treated as two extra input symbols, which is
    // sound under any interpretation of them. Expects a trimmed DFA (no dead states).
    public static List<DfaState> Minimize(List<DfaState> states)
    {
        int n = states.Count;
        var cls = states.Select(s => s.Accept + 1).ToArray();
        int count = cls.Distinct().Count();
        while (true)
        {
            var ids = new Dictionary<string, int>();
            var next = new int[n];
            for (int s = 0; s < n; s++)
            {
                var st = states[s];
                var sig = new System.Text.StringBuilder();
                sig.Append(cls[s]).Append('|').Append(st.Bol < 0 ? -1 : cls[st.Bol]).Append('|').Append(st.Eol < 0 ? -1 : cls[st.Eol]);
                foreach (var (lo, hi, to) in MergeMoves(st.Moves.Select(m => (m.Lo, m.Hi, cls[m.To]))))
                    sig.Append('|').Append(lo).Append('-').Append(hi).Append(':').Append(to);
                string key = sig.ToString();
                if (!ids.TryGetValue(key, out int id)) ids[key] = id = ids.Count;
                next[s] = id;
            }
            cls = next;
            if (ids.Count == count) break;
            count = ids.Count;
        }

        // renumber classes in BFS order from the start state, so the start is 0 and output is stable
        var order = new int[count]; Array.Fill(order, -1);
        var rep = new int[count];
        var q = new Queue<int>(); order[cls[0]] = 0; rep[0] = 0; q.Enqueue(0); int k = 1;
        while (q.Count > 0)
            foreach (var t in Succ(states[q.Dequeue()]))
                if (order[cls[t]] < 0) { order[cls[t]] = k; rep[k] = t; k++; q.Enqueue(t); }
        var res = new List<DfaState>();
        for (int c = 0; c < k; c++)
        {
            var st = states[rep[c]];
            res.Add(new DfaState
            {
                Accept = st.Accept,
                Bol = st.Bol < 0 ? -1 : order[cls[st.Bol]],
                Eol = st.Eol < 0 ? -1 : order[cls[st.Eol]],
                Moves = MergeMoves(st.Moves.Select(m => (m.Lo, m.Hi, order[cls[m.To]]))).ToList(),
            });
        }
        return res;
    }

    static IEnumerable<(int Lo, int Hi, int To)> MergeMoves(IEnumerable<(int Lo, int Hi, int To)> moves)
    {
        (int Lo, int Hi, int To)? cur = null;
        foreach (var m in moves.OrderBy(m => m.Lo))
        {
            if (cur is { } c && c.To == m.To && c.Hi + 1 == m.Lo) cur = (c.Lo, m.Hi, c.To);
            else { if (cur is { } d) yield return d; cur = m; }
        }
        if (cur is { } e) yield return e;
    }

    // ------------------------------------------------------------------ anchor context
    // Splits each state by what is known about the line context there:
    //   bolOk  - the previous character was '\n' (or no character was read), so a ^ edge can fire
    //   eolHit - a $ edge was just taken, so the next character can only be '\n'
    // Edges that can never fire in their context are dropped. The result has the same meaning but
    // no impossible paths such as "$x" or "x^", which otherwise clutter the regex.
    public static List<DfaState> SplitAnchorContext(List<DfaState> states)
    {
        if (states.All(s => s.Bol < 0 && s.Eol < 0)) return states;
        var ids = new Dictionary<(int, bool, bool), int>();
        var keys = new List<(int S, bool BolOk, bool EolHit)>();
        int Id((int, bool, bool) k)
        {
            if (!ids.TryGetValue(k, out int id)) { ids[k] = id = keys.Count; keys.Add(k); }
            return id;
        }
        Id((0, true, false));
        var res = new List<DfaState>();
        for (int i = 0; i < keys.Count; i++)
        {
            var (s, bolOk, eolHit) = keys[i];
            var o = states[s];
            var st = new DfaState { Accept = o.Accept };
            foreach (var (lo, hi, to) in o.Moves)
            {
                // split the range around '\n', since reading '\n' changes the context
                foreach (var (a, b) in new[] { (lo, Math.Min(hi, '\n' - 1)), (Math.Max(lo, '\n'), Math.Min(hi, '\n')), (Math.Max(lo, '\n' + 1), hi) })
                {
                    if (a > b) continue;
                    bool nl = a == '\n';
                    if (eolHit && !nl) continue;            // after $, only '\n' can follow
                    st.Moves.Add((a, b, Id((to, nl, false))));
                }
            }
            if (o.Bol >= 0 && bolOk) st.Bol = Id((o.Bol, true, eolHit));
            if (o.Eol >= 0) st.Eol = Id((o.Eol, bolOk, true));
            st.Moves = MergeMoves(st.Moves).ToList();
            res.Add(st);
        }
        return res;
    }

    // ------------------------------------------------------------------ context split
    // Inside a match, ^ and $ are decided by the matched characters themselves (the previous / next
    // character is or isn't '\n'). Only the context OUTSIDE the match is external: whether it starts
    // at a line start, and whether it ends before '\n' / end of input. So the matches are described
    // by four anchor-free languages, one per context:
    //     mid-line, not at eol: Lmn      line start, not at eol: Lbn
    //     mid-line, at eol:     Lme      line start, at eol:     Lbe
    // A regex can only ADD matches by using ^ or $, so this works iff Lmn ⊆ Lbn ⊆ Lbe and Lmn ⊆ Lme ⊆ Lbe.
    // Then the result is  A | ^B | C$ | ^D$  with A = Lmn, B = Lbn-Lmn, C = Lme-Lmn, D = Lbe-(Lbn∪Lme).
    // Returns null (and a reason) if the containments fail, i.e. no regex in this syntax exists.
    public static Rx? ContextSplit(List<DfaState> A, out string? reason)
    {
        reason = null;
        // product of two runs of A: one started mid-line, one started at a line start
        var ids = new Dictionary<(int, bool, int, bool), int>();
        var keys = new List<(int S1, bool B1, int S2, bool B2)>();
        var parent = new List<(int P, int C)>();
        int Id((int, bool, int, bool) k, int p, int c)
        {
            if (!ids.TryGetValue(k, out int id)) { ids[k] = id = keys.Count; keys.Add(k); parent.Add((p, c)); }
            return id;
        }
        Id((0, false, 0, true), -1, -1);
        var moves = new List<List<(int Lo, int Hi, int To)>>();
        var bits = new List<(bool MN, bool ME, bool BN, bool BE)>();
        for (int i = 0; i < keys.Count; i++)
        {
            var (s1, b1, s2, b2) = keys[i];
            int m0 = Effective(A, s1, b1, false), m1 = Effective(A, s1, b1, true);
            int n0 = Effective(A, s2, b2, false), n1 = Effective(A, s2, b2, true);
            var bit = (Acc(A, m0), Acc(A, m1), Acc(A, n0), Acc(A, n1));
            bits.Add(bit);
            if ((bit.Item1 && !(bit.Item2 && bit.Item3)) || ((bit.Item2 || bit.Item3) && !bit.Item4))
            {
                var chars = new List<int>();
                for (int k = i; parent[k].P >= 0; k = parent[k].P) chars.Add(parent[k].C);
                chars.Reverse();
                string show = string.Concat(chars.Select(c => c == '\n' ? "\\n" : c < 0x20 || c > 0x7E ? $"\\x{{{c:X}}}" : ((char)c).ToString()));
                reason = $"\"{show}\" is matched in a narrower line context but not a wider one " +
                         $"(mid-line/eol={bit.Item2}, mid-line/no-eol={bit.Item1}, line-start/no-eol={bit.Item3}, line-start/eol={bit.Item4}); " +
                         "a regex can't express that without lookaround";
                return null;
            }
            var points = new SortedSet<int> { 0, '\n', '\n' + 1 };
            foreach (var m in Moves(A, m0).Concat(Moves(A, m1)).Concat(Moves(A, n0)).Concat(Moves(A, n1)))
            { points.Add(m.Lo); if (m.Hi < CharSets.MaxCp) points.Add(m.Hi + 1); }
            var pts = points.ToList(); pts.Add(CharSets.MaxCp + 1);
            var mv = new List<(int, int, int)>();
            for (int j = 0; j + 1 < pts.Count; j++)
            {
                int c = pts[j];
                bool nl = c == '\n';
                int t1 = Move(A, nl ? m1 : m0, c), t2 = Move(A, nl ? n1 : n0, c);
                if (t1 < 0 && t2 < 0) continue;
                mv.Add((c, pts[j + 1] - 1, Id((t1, nl, t2, nl), i, c)));
            }
            moves.Add(mv);
        }

        Rx? Piece(Func<(bool MN, bool ME, bool BN, bool BE), bool> accept)
        {
            var d = new List<DfaState>();
            for (int i = 0; i < keys.Count; i++)
                d.Add(new DfaState { Accept = accept(bits[i]) ? 0 : -1, Moves = MergeMoves(moves[i]).ToList() });
            var t = Trim(d);
            if (t is null) return null;
            t = Minimize(t);
            return Enum.GetValues<Order>().Select(o => ToRegex(t, o)).Where(r => r is not null).MinBy(r => r!.Text.Length);
        }

        var a = Piece(b => b.MN);
        var bb = Piece(b => b.BN && !b.MN);
        var cc = Piece(b => b.ME && !b.MN);
        var d = Piece(b => b.BE && !b.BN && !b.ME);
        return R.Alt(a, R.Cat(R.Anchor(true), bb), R.Cat(cc, R.Anchor(false)), R.Cat(R.Anchor(true), d, R.Anchor(false)));
    }

    // ------------------------------------------------------------------ semantics
    // RE/flex-style execution: before reading the next character, if at a line start and the state
    // has a Bol edge, it is taken; then if before '\n' / end of input and the state has an Eol edge,
    // it is taken. These edges are mandatory, not optional.
    static int Effective(List<DfaState> S, int s, bool bol, bool eol)
    {
        for (int guard = 0; guard < 16 && s >= 0; guard++)
        {
            var st = S[s];
            if (bol && st.Bol >= 0 && st.Bol != s) { s = st.Bol; continue; }
            if (eol && st.Eol >= 0 && st.Eol != s) { s = st.Eol; continue; }
            break;
        }
        return s;
    }

    static int Move(List<DfaState> S, int s, int c)
    {
        if (s < 0) return -1;
        foreach (var (lo, hi, to) in S[s].Moves) if (c >= lo && c <= hi) return to; else if (lo > c) break;
        return -1;
    }

    static bool Acc(List<DfaState> S, int s) => s >= 0 && S[s].Accept >= 0;

    // Returns null if the two DFAs match exactly the same strings in every line context, otherwise
    // a short description of a distinguishing input.
    public static string? Difference(List<DfaState> A, List<DfaState> B)
    {
        var seen = new Dictionary<(int, int, bool), (int Parent, int Ch)>();
        var keys = new List<(int, int, bool)>();
        var q = new Queue<int>();
        void Visit((int, int, bool) key, int parent, int ch)
        {
            if (seen.ContainsKey(key)) return;
            seen[key] = (parent, ch); keys.Add(key); q.Enqueue(keys.Count - 1);
        }
        string Path(int idx, string tail)
        {
            var chars = new List<int>();
            bool atBol = true;
            while (idx >= 0) { var (p, ch) = seen[keys[idx]]; if (p >= 0) chars.Add(ch); else atBol = keys[idx].Item3; idx = p; }
            chars.Reverse();
            string Show(int c) => c == '\n' ? "\\n" : c < 0x20 || c > 0x7E ? $"\\x{{{c:X}}}" : ((char)c).ToString();
            return "\"" + string.Concat(chars.Select(Show)) + "\"" + tail + (atBol ? " (starting at a line start)" : " (starting mid-line)");
        }

        Visit((0, 0, true), -1, -1);   // a match can start at a line start...
        Visit((0, 0, false), -1, -1);  // ...or in the middle of a line
        while (q.Count > 0)
        {
            int idx = q.Dequeue();
            var (a, b, bol) = keys[idx];
            int a0 = Effective(A, a, bol, false), a1 = Effective(A, a, bol, true);
            int b0 = Effective(B, b, bol, false), b1 = Effective(B, b, bol, true);

            if (Acc(A, a1) != Acc(B, b1)) return Path(idx, " followed by end of input or \\n");

            var points = new SortedSet<int> { 0, '\n', '\n' + 1 };
            foreach (var m in Moves(A, a0).Concat(Moves(A, a1)).Concat(Moves(B, b0)).Concat(Moves(B, b1)))
            { points.Add(m.Lo); if (m.Hi < CharSets.MaxCp) points.Add(m.Hi + 1); }

            foreach (int c in points)
            {
                bool eol = c == '\n';
                int ea = eol ? a1 : a0, eb = eol ? b1 : b0;
                if (Acc(A, ea) != Acc(B, eb)) return Path(idx, $" followed by a character like U+{c:X4}");
                int na = Move(A, ea, c), nb = Move(B, eb, c);
                if (na < 0 && nb < 0) continue;
                Visit((na, nb, c == '\n'), idx, c);
            }
        }
        return null;
    }

    static IEnumerable<(int Lo, int Hi, int To)> Moves(List<DfaState> S, int s) => s < 0 ? Enumerable.Empty<(int, int, int)>() : S[s].Moves;

    // ------------------------------------------------------------------ state elimination
    public enum Order { Heuristic, Ascending, Descending }

    // Converts the DFA to a regex by state elimination. Anchor edges become ^ / $ edges. This reads
    // them as optional, which agrees with the mandatory reading whenever the anchor target contains
    // all of the source's threads, as Builder.AnchorEdge produces. The caller verifies the result.
    public static Rx? ToRegex(List<DfaState> states, Order order)
    {
        int n = states.Count, S = n, F = n + 1, N = n + 2;
        var E = new Rx?[N, N];
        E[S, 0] = R.Eps;
        for (int s = 0; s < n; s++)
        {
            var st = states[s];
            if (st.Accept >= 0) E[s, F] = R.Eps;
            foreach (var g in st.Moves.GroupBy(m => m.To))
                E[s, g.Key] = R.Alt(E[s, g.Key], R.Set(g.Select(m => (m.Lo, m.Hi))));
            if (st.Bol >= 0) E[s, st.Bol] = R.Alt(E[s, st.Bol], R.Anchor(true));
            if (st.Eol >= 0) E[s, st.Eol] = R.Alt(E[s, st.Eol], R.Anchor(false));
        }

        var remaining = Enumerable.Range(0, n).ToList();
        if (order == Order.Descending) remaining.Reverse();
        while (remaining.Count > 0)
        {
            int q = order == Order.Heuristic ? remaining.MinBy(x => Weight(E, x, N)) : remaining[0];
            remaining.Remove(q);
            var loop = R.Star(E[q, q]);
            var ins = Enumerable.Range(0, N).Where(p => p != q && E[p, q] is not null).ToList();
            var outs = Enumerable.Range(0, N).Where(r => r != q && E[q, r] is not null).ToList();
            foreach (var p in ins)
                foreach (var r in outs)
                    E[p, r] = R.Alt(E[p, r], R.Cat(E[p, q], loop, E[q, r]));
            for (int x = 0; x < N; x++) { E[x, q] = null; E[q, x] = null; }
        }
        return E[S, F];
    }

    // Classic heuristic: how much text removing q adds (size of copied labels).
    static long Weight(Rx?[,] E, int q, int N)
    {
        var ins = new List<int>(); var outs = new List<int>();
        for (int x = 0; x < N; x++)
        {
            if (x == q) continue;
            if (E[x, q] is not null) ins.Add(E[x, q]!.Text.Length);
            if (E[q, x] is not null) outs.Add(E[q, x]!.Text.Length);
        }
        long loop = E[q, q]?.Text.Length ?? 0;
        return ins.Sum() * (long)(outs.Count - 1) + outs.Sum() * (long)(ins.Count - 1) + loop * (ins.Count * outs.Count - 1);
    }
}