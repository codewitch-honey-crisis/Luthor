// A small regex AST with simplifying ("smart") constructors, used as edge labels during state
// elimination. Every constructor in R normalizes as it builds, so expressions stay small while
// states are being removed. A null Rx means the empty language (no edge).
//
// Emitted text uses only syntax that Luthor.Builder parses, so the output can be re-parsed.

using System.Globalization;
using System.Text;

namespace Delazy;

internal abstract class Rx
{
    private string? _text;
    // Canonical text. Also used as structural identity: two nodes are equal iff their text is.
    public string Text => _text ??= Emit();
    public override string ToString() => Text;

    // 0 = alternation, 1 = concatenation, 2 = repetition, 3 = atom
    public abstract int Prec { get; }
    public abstract bool Nullable { get; }
    protected abstract string Emit();

    public bool Same(Rx o) => ReferenceEquals(this, o) || Text == o.Text;
}

internal sealed class RxEps : Rx
{
    public static readonly RxEps Instance = new();
    public override int Prec => 3;
    public override bool Nullable => true;
    protected override string Emit() => "()";
}

// '^' or '$'. Zero width, but conditional, so NOT nullable.
internal sealed class RxAnchor(bool bol) : Rx
{
    public readonly bool Bol = bol;
    public override int Prec => 3;
    public override bool Nullable => false;
    protected override string Emit() => Bol ? "^" : "$";
}

internal sealed class RxSet(List<(int Lo, int Hi)> ranges) : Rx
{
    public readonly List<(int Lo, int Hi)> Ranges = ranges; // normalized
    public override int Prec => 3;
    public override bool Nullable => false;
    protected override string Emit() => CharSets.Emit(Ranges);
}

internal sealed class RxCat(List<Rx> items) : Rx
{
    public readonly List<Rx> Items = items; // >= 2, none are RxCat or RxEps
    public override int Prec => 1;
    public override bool Nullable => Items.All(x => x.Nullable);
    protected override string Emit()
    {
        var sb = new StringBuilder();
        foreach (var x in Items)
            if (x.Prec < 1) sb.Append('(').Append(x.Text).Append(')'); else sb.Append(x.Text);
        return sb.ToString();
    }
}

internal sealed class RxAlt(List<Rx> items) : Rx
{
    public readonly List<Rx> Items = items; // >= 2, none are RxAlt or RxEps
    public override int Prec => 0;
    public override bool Nullable => Items.Any(x => x.Nullable);
    protected override string Emit() => string.Join("|", Items.Select(x => x.Text));
}

internal sealed class RxRep(Rx body, int min, int max) : Rx
{
    public const int Inf = -1;
    public readonly Rx Body = body;
    public readonly int Min = min, Max = max; // Max == Inf means unbounded
    public override int Prec => Expanded() is null ? 2 : 1;
    public override bool Nullable => Min == 0 || Body.Nullable;

    // Short counted repeats of an atom read better written out: "--" rather than "-{2}",
    // "aa+" rather than "a{2,}". Returns null when the counted form is shorter.
    string? Expanded()
    {
        if (Body.Prec < 3 || Min < 2) return null;
        string b = Body.Text;
        string? exp = Max == Min ? string.Concat(Enumerable.Repeat(b, Min))
                   : Max == Inf ? string.Concat(Enumerable.Repeat(b, Min)) + "+"   // a{2,} = aa+
                   : null;
        if (exp is null) return null;
        return exp.Length <= Counted().Length ? exp : null;
    }

    string Counted()
    {
        // Nested quantifiers are always parenthesized: "a*?" would re-parse as a LAZY star.
        string b = Body.Prec < 3 ? "(" + Body.Text + ")" : Body.Text;
        string q = (Min, Max) switch
        {
            (0, Inf) => "*",
            (1, Inf) => "+",
            (0, 1) => "?",
            (_, Inf) => $"{{{Min},}}",
            _ when Min == Max => $"{{{Min}}}",
            _ => $"{{{Min},{Max}}}",
        };
        return b + q;
    }

    protected override string Emit() => Expanded() ?? Counted();
}

internal static class R
{
    public static Rx Eps => RxEps.Instance;
    public static Rx Anchor(bool bol) => new RxAnchor(bol);
    public static Rx Set(IEnumerable<(int, int)> ranges) => new RxSet(CharSets.Normalize(ranges));

    // ---------------------------------------------------------------- concatenation
    public static Rx? Cat(params Rx?[] parts)
    {
        var items = new List<Rx>();
        foreach (var p in parts)
        {
            if (p is null) return null;              // anything . empty-language = empty-language
            if (p is RxCat c) items.AddRange(c.Items);
            else if (p is not RxEps) items.Add(p);
        }
        // Merge neighbours with the same base: x{a,b} x{c,d} = x{a+c,b+d}  (x x* = x+, x? x = x{1,2} ...)
        var outp = new List<Rx>();
        void Push(Rx it)
        {
            if (outp.Count > 0)
            {
                var (b1, m1, M1) = Split(outp[^1]);
                var (b2, m2, M2) = Split(it);
                Rx? merged = null;
                if (b1.Same(b2))
                {
                    int max = M1 == RxRep.Inf || M2 == RxRep.Inf ? RxRep.Inf : M1 + M2;
                    merged = Rep(b1, m1 + m2, max)!;
                }
                // x{m,M} (x{c,d})?  =  x{m,M+d}  when the two count ranges touch (c <= M-m+1)
                else merged = MergeOptional(outp[^1], it) ?? MergeOptional(it, outp[^1]) ?? MergeLoop(outp[^1], it);
                if (merged is not null)
                {
                    outp.RemoveAt(outp.Count - 1);
                    // the merged piece may now combine with its new left neighbour, so feed it back in
                    if (merged is RxCat mc) foreach (var x in mc.Items) Push(x); else if (merged is not RxEps) Push(merged);
                    return;
                }
            }
            outp.Add(it);
            while (FoldSequence(outp)) { }
        }
        foreach (var it in items) Push(it);
        return outp.Count switch { 0 => Eps, 1 => outp[0], _ => new RxCat(outp) };
    }

    // ab(ab)* = (ab)+   and   (ab)*ab = (ab)+ : a run of items equal to the body of a neighbouring
    // repeat is absorbed into it.
    static bool FoldSequence(List<Rx> outp)
    {
        // tail is  X1 .. Xk Rep(X1..Xk)
        // or       Rep(X1..Xk) X1 .. Xk
        if (outp[^1] is RxRep { Body: RxCat c } r1 && outp.Count > c.Items.Count
            && Enumerable.Range(0, c.Items.Count).All(j => outp[outp.Count - 1 - c.Items.Count + j].Same(c.Items[j])))
        {
            outp.RemoveRange(outp.Count - 1 - c.Items.Count, c.Items.Count + 1);
            outp.Add(Rep(c, r1.Min + 1, r1.Max == RxRep.Inf ? RxRep.Inf : r1.Max + 1)!);
            return true;
        }
        for (int k = 2; k < outp.Count; k++)
        {
            if (outp[^(k + 1)] is not RxRep { Body: RxCat c2 } r2 || c2.Items.Count != k) continue;
            if (!Enumerable.Range(0, k).All(j => outp[outp.Count - k + j].Same(c2.Items[j]))) continue;
            outp.RemoveRange(outp.Count - k - 1, k + 1);
            outp.Add(Rep(c2, r2.Min + 1, r2.Max == RxRep.Inf ? RxRep.Inf : r2.Max + 1)!);
            return true;
        }
        return false;
    }

    static (Rx Body, int Min, int Max) Split(Rx x) => x is RxRep r ? (r.Body, r.Min, r.Max) : (x, 1, 1);

    // (A*B)*A+ = (A|B)*A   and   A+(BA*)* = A(A|B)*      (holds for any languages A, B)
    static Rx? MergeLoop(Rx first, Rx second)
    {
        static bool IsStar(Rx x, out Rx body) { body = x is RxRep { Min: 0, Max: RxRep.Inf } r ? r.Body : x; return x is RxRep { Min: 0, Max: RxRep.Inf }; }
        static bool IsPlus(Rx x, out Rx body) { body = x is RxRep { Min: 1, Max: RxRep.Inf } r ? r.Body : x; return x is RxRep { Min: 1, Max: RxRep.Inf }; }

        if (IsStar(first, out var loop) && loop is RxCat c1 && IsPlus(second, out var a1)
            && IsStar(c1.Items[0], out var a0) && a0.Same(a1))
            return Cat(Star(Alt(a1, FromItems(c1.Items.Skip(1)))), a1);
        if (IsPlus(first, out var b1) && IsStar(second, out var loop2) && loop2 is RxCat c2
            && IsStar(c2.Items[^1], out var b0) && b0.Same(b1))
            return Cat(b1, Star(Alt(b1, FromItems(c2.Items.Take(c2.Items.Count - 1)))));
        return null;
    }

    // plain = x{m,M}, opt = (x{c,d})?. Counts m..M and m+c..M+d form one range when c <= M-m+1.
    static Rx? MergeOptional(Rx plain, Rx opt)
    {
        if (opt is not RxRep { Min: 0, Max: 1 } o) return null;
        var (b1, m, M) = Split(plain);
        var (b2, c, d) = Split(o.Body);
        if (!b1.Same(b2)) return null;
        if (M != RxRep.Inf && c > M - m + 1) return null;
        int max = M == RxRep.Inf || d == RxRep.Inf ? RxRep.Inf : M + d;
        return Rep(b1, m, max);
    }

    static List<Rx> CatItems(Rx x) => x is RxCat c ? c.Items : x is RxEps ? new() : new() { x };
    static Rx FromItems(IEnumerable<Rx> items) => Cat(items.Cast<Rx?>().ToArray())!;

    // ---------------------------------------------------------------- repetition
    public static Rx? Rep(Rx? body, int min, int max)
    {
        if (max != RxRep.Inf && max < min) throw new ArgumentException("bad repeat");
        if (body is null) return min == 0 ? Eps : null;
        if (body is RxEps || max == 0) return Eps;
        if (min == 1 && max == 1) return body;
        if (body.Nullable && min > 0) min = 0;                     // e in X  =>  X{m,M} = X{0,M}

        if (body is RxRep inner)
        {
            var a = inner.Body;
            switch (inner.Min, inner.Max)
            {
                case (0, 1): return Rep(a, 0, max == RxRep.Inf ? RxRep.Inf : max);          // (a?){m,M} = a{0,M}
                case (0, RxRep.Inf): return inner;                                           // (a*){m,M} = a*
                case (1, RxRep.Inf): return Rep(a, min == 0 ? 0 : min, RxRep.Inf);            // (a+){m,M} = a{m,}
            }
            if (inner.Min == inner.Max && min == max) return Rep(a, inner.Min * min, inner.Min * min); // (a{k}){m}
            if (inner.Min == 1 && min == 0 && max == 1) return Rep(a, 0, inner.Max);                  // (a{1,d})? = a{0,d}
        }

        if (max == RxRep.Inf && min <= 1)
        {
            // Inside a closure, nullable pieces can be unwrapped:
            //   (x*|y)* = (x|y)*        (x* y*)* = (x|y)*     (only when all of the cat is nullable)
            if (body is RxAlt alt && alt.Items.Any(x => x is RxRep { Min: 0, Max: RxRep.Inf }))
                return Rep(Alt(alt.Items.Select(x => x is RxRep { Min: 0, Max: RxRep.Inf } r ? r.Body : x).ToArray()), min, max);
            if (min == 0 && body is RxCat cat && cat.Items.All(x => x.Nullable))
                return Rep(Alt(cat.Items.Select(x => x is RxRep { Min: 0 } r && (r.Max == RxRep.Inf || r.Max == 1) ? r.Body : x).ToArray()), 0, RxRep.Inf);
        }
        return new RxRep(body, min, max);
    }

    public static Rx? Star(Rx? x) => Rep(x, 0, RxRep.Inf);

    // ---------------------------------------------------------------- alternation
    public static Rx? Alt(params Rx?[] parts)
    {
        var items = new List<Rx>();
        bool eps = false;
        void AddItem(Rx x)
        {
            if (x is RxAlt a) { foreach (var y in a.Items) AddItem(y); return; }
            if (x is RxEps) { eps = true; return; }
            if (!items.Any(y => y.Same(x))) items.Add(x);
        }
        foreach (var p in parts) if (p is not null) AddItem(p);
        if (items.Count == 0) return eps ? Eps : null;

        // 1. all single-character sets become one set
        var sets = items.OfType<RxSet>().ToList();
        if (sets.Count > 1)
        {
            int at = items.IndexOf(sets[0]);
            var merged = Set(sets.SelectMany(s => s.Ranges));
            items.RemoveAll(x => x is RxSet);
            items.Insert(Math.Min(at, items.Count), merged);
        }

        // 2. subsumption between repeats of the same thing: a | a+ = a+,  a? | a* = a*, set | set* ...
        for (int i = 0; i < items.Count; i++)
            for (int j = 0; j < items.Count; j++)
                if (i != j && Subsumes(items[j], items[i])) { items.RemoveAt(i); i = -1; break; }

        // 3. factor common prefixes, then common suffixes:  xy|xz = x(y|z),  yx|zx = (y|z)x
        items = Factor(items, prefix: true);
        items = Factor(items, prefix: false);

        // After factoring an alternative may have collapsed into a set, a nested alt or epsilon.
        var flat = new List<Rx>();
        foreach (var x in items)
        {
            if (x is RxEps) eps = true;
            else if (x is RxAlt a) flat.AddRange(a.Items);
            else flat.Add(x);
        }
        if (flat.Count != items.Count || flat.OfType<RxSet>().Count() > 1)
        {
            var again = Alt(flat.Cast<Rx?>().ToArray())!;
            return eps && !again.Nullable ? Opt(again) : again;
        }
        items = flat;

        Rx body = items.Count == 1 ? items[0] : new RxAlt(items);
        if (eps && !body.Nullable) return Opt(body);
        return body;
    }

    static Rx Opt(Rx x) => Rep(x, 0, 1)!;

    // does `big` contain every string of `small`? (conservative: only same-base repeats)
    static bool Subsumes(Rx big, Rx small)
    {
        if (small is RxEps) return big.Nullable;
        var (b1, m1, M1) = Split(big);
        var (b2, m2, M2) = Split(small);
        if (!b1.Same(b2))
        {
            // set ⊆ set  (e.g. [a-z] and a)
            if (small is RxSet s2 && big is RxSet s1) return CharSets.Contains(s1.Ranges, s2.Ranges);
            // x ⊆ y*   when x ⊆ y
            if (big is RxRep { Min: <= 1, Max: RxRep.Inf } br && small is RxSet ss && br.Body is RxSet bs)
                return CharSets.Contains(bs.Ranges, ss.Ranges);
            return false;
        }
        if (m1 > m2) return false;
        if (M1 == RxRep.Inf) return true;
        return M2 != RxRep.Inf && M2 <= M1 && (m1 <= m2);
    }

    static List<Rx> Factor(List<Rx> items, bool prefix)
    {
        var groups = new List<(Rx Key, List<Rx> Members)>();
        foreach (var x in items)
        {
            var parts = CatItems(x);
            var key = prefix ? parts[0] : parts[^1];
            var g = groups.FindIndex(t => t.Key.Same(key));
            if (g < 0) groups.Add((key, new List<Rx> { x })); else groups[g].Members.Add(x);
        }
        if (groups.All(g => g.Members.Count == 1)) return items;

        var res = new List<Rx>();
        foreach (var (key, members) in groups)
        {
            if (members.Count == 1) { res.Add(members[0]); continue; }
            var rests = members.Select(m =>
            {
                var p = CatItems(m);
                return (Rx?)FromItems(prefix ? p.Skip(1) : p.Take(p.Count - 1));
            }).ToArray();
            var mid = Alt(rests)!;
            res.Add(prefix ? Cat(key, mid)! : Cat(mid, key)!);
        }
        return res;
    }
}

internal static class CharSets
{
    public const int MaxCp = 0x10FFFF;
    static readonly List<(int, int)> Digit = new() { ('0', '9') };
    static readonly List<(int, int)> Word = new() { ('0', '9'), ('A', 'Z'), ('_', '_'), ('a', 'z') };
    static readonly List<(int, int)> Space = new() { ('\t', '\r'), (' ', ' ') };
    static readonly List<(int, int)> Dot = new() { (0, '\n' - 1), ('\n' + 1, MaxCp) };

    public static List<(int Lo, int Hi)> Normalize(IEnumerable<(int Lo, int Hi)> rs)
    {
        var res = new List<(int Lo, int Hi)>();
        foreach (var r in rs.OrderBy(r => r.Lo))
        {
            if (res.Count > 0 && r.Lo <= res[^1].Hi + 1) res[^1] = (res[^1].Lo, Math.Max(res[^1].Hi, r.Hi));
            else res.Add(r);
        }
        return res;
    }

    public static List<(int Lo, int Hi)> Complement(IEnumerable<(int Lo, int Hi)> rs)
    {
        var res = new List<(int Lo, int Hi)>(); int next = 0;
        foreach (var r in Normalize(rs)) { if (r.Lo > next) res.Add((next, r.Lo - 1)); next = r.Hi + 1; }
        if (next <= MaxCp) res.Add((next, MaxCp));
        return res;
    }

    static List<(int Lo, int Hi)> Subtract(List<(int Lo, int Hi)> a, List<(int, int)> b)
        => Complement(Complement(a).Concat(b));

    public static bool Contains(List<(int Lo, int Hi)> big, List<(int Lo, int Hi)> small)
        => Subtract(small, big).Count == 0;

    static bool Eq(List<(int Lo, int Hi)> a, List<(int, int)> b) => a.SequenceEqual(b);

    public static string Emit(List<(int Lo, int Hi)> set)
    {
        if (set.Count == 1 && set[0].Lo == set[0].Hi) return Literal(set[0].Lo, inClass: false);
        if (set.Count == 1 && set[0] == (0, MaxCp)) return @"[\s\S]";   // any codepoint, newline included
        if (Eq(set, Dot)) return ".";
        if (Eq(set, Digit)) return @"\d";
        if (Eq(set, Word)) return @"\w";
        if (Eq(set, Space)) return @"\s";
        var comp = Complement(set);
        if (Eq(comp, Digit)) return @"\D";
        if (Eq(comp, Word)) return @"\W";
        if (Eq(comp, Space)) return @"\S";
        string pos = Class(set, false), neg = Class(comp, true);
        return neg.Length < pos.Length ? neg : pos;
    }

    static string Class(List<(int Lo, int Hi)> set, bool negated)
    {
        var sb = new StringBuilder("[");
        if (negated) sb.Append('^');
        var rest = set;
        if (Contains(rest, Word) && Subtract(rest, Word).Count < rest.Count) { sb.Append(@"\w"); rest = Subtract(rest, Word); }
        if (Contains(rest, Space) && Subtract(rest, Space).Count < rest.Count) { sb.Append(@"\s"); rest = Subtract(rest, Space); }
        foreach (var (lo, hi) in rest)
        {
            sb.Append(Literal(lo, true));
            if (hi == lo + 1) sb.Append(Literal(hi, true));
            else if (hi > lo) sb.Append('-').Append(Literal(hi, true));
        }
        return sb.Append(']').ToString();
    }

    static string Literal(int cp, bool inClass)
    {
        switch (cp)
        {
            case '\n': return @"\n";
            case '\r': return @"\r";
            case '\t': return @"\t";
            case '\f': return @"\f";
            case '\v': return @"\v";
            case 0: return @"\x{0}"; // "\0" followed by a hex digit would still parse, but this is unambiguous
        }
        string special = inClass ? @"\]-[^" : @"\.[]()|*+?{}^$";
        if (cp < 0x80)
        {
            if (cp < 0x20 || cp == 0x7F) return $@"\x{{{cp:X}}}";
            char c = (char)cp;
            return special.Contains(c) ? "\\" + c : c.ToString();
        }
        if (cp <= 0xFFFF && !char.IsSurrogate((char)cp))
        {
            var cat = char.GetUnicodeCategory((char)cp);
            if (char.IsLetterOrDigit((char)cp) || char.IsPunctuation((char)cp) || char.IsSymbol((char)cp))
                if (cat != UnicodeCategory.Format && cat != UnicodeCategory.PrivateUse)
                    return ((char)cp).ToString();
        }
        return $@"\x{{{cp:X}}}";
    }
}