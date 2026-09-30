using Luthor;
using System.Text;

namespace Delazy;

// delazy: rewrites a regular expression that uses lazy quantifiers into an equivalent one that
// uses only greedy quantifiers.
//
//   1. Luthor.Builder turns the expression into a DFA. Lazy quantifiers are resolved during that
//      construction (trimmed states), so the DFA itself is an ordinary, "pure" DFA.
//   2. The DFA is trimmed, split by line context where ^ / $ are involved, and minimized.
//   3. State elimination turns the DFA back into a regex, using a simplifying AST for edge labels.
//      Several elimination orders are tried; with anchors, a split into A | ^B | C$ | ^D$ is also
//      tried (see DfaTools.ContextSplit).
//   4. Candidates are re-parsed with Builder and checked for equivalence with the original DFA
//      (RE/flex semantics for ^ and $), shortest first, so a result is only printed if it is known
//      to be correct. The winner then gets a verified clean-up pass that deletes redundant pieces.
internal class Program
{
    static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: delazy [-v] <expression>");
        Console.Error.WriteLine("  -v   print statistics and every candidate to stderr");
        Console.Error.WriteLine();
    }

    static int Main(string[] args)
    {
        bool verbose = args.Length == 2 && args[0] == "-v";
        if (args.Length != 1 && !verbose)
        {
            PrintUsage();
            return 1;
        }
        Console.OutputEncoding = Encoding.UTF8;
        var expression = args[^1];

        try
        {
            var result = Delazy(expression, verbose ? Console.Error : null);
            if (result is null) return 2;
            Console.WriteLine(result);
            return 0;
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    // Returns the greedy expression, or null (with a message on stderr) if none could be produced.
    internal static string? Delazy(string expression, TextWriter? log)
    {
        var original = Builder.Build([expression], false).States;
        if (log is not null)
        {
            log.WriteLine("Builder DFA:");
            for (int s = 0; s < original.Count; s++)
            {
                var st = original[s];
                log.Write($"  {s}{(st.Accept >= 0 ? " accept" : "")}:");
                if (st.Bol >= 0) log.Write($" ^->{st.Bol}");
                if (st.Eol >= 0) log.Write($" $->{st.Eol}");
                foreach (var (lo, hi, to) in st.Moves) log.Write($" {CharSets.Emit(new() { (lo, hi) })}->{to}");
                log.WriteLine();
            }
        }
        var trimmed = DfaTools.Trim(original);
        if (trimmed is not null) trimmed = DfaTools.Trim(DfaTools.SplitAnchorContext(trimmed));
        if (trimmed is null)
        {
            Console.Error.WriteLine("error: the expression matches nothing");
            return null;
        }
        var dfa = DfaTools.Minimize(trimmed);
        log?.WriteLine($"DFA: {original.Count} states, {dfa.Count} after trimming and minimizing");

        string? firstFailure = null;
        Rx? best = null;
        string? Check(string text)
        {
            try { return DfaTools.Difference(original, Builder.Build([text], false).States); }
            catch (FormatException ex) { return "does not re-parse: " + ex.Message; }
        }
        // Candidates: state elimination in a few orders, plus (with anchors) a line-context split.
        var candidates = new List<(string Name, Rx Rx)>();
        foreach (var order in Enum.GetValues<DfaTools.Order>())
            if (DfaTools.ToRegex(dfa, order) is { } rx) candidates.Add((order.ToString(), rx));
        string? inexpressible = null;
        if (original.Any(s => s.Bol >= 0 || s.Eol >= 0))
        {
            // A | ^B | C$ | ^D$ is exact where the direct conversion can't be (lazy trimming next to ^ or $)
            if (DfaTools.ContextSplit(original, out inexpressible) is { } rx) candidates.Add(("Context", rx));
            else log?.WriteLine($"  Context    impossible: {inexpressible}");
        }

        // Verify shortest first; the first equivalent one wins. (Some orders blow up badly, and
        // re-parsing a multi-megabyte candidate just to reject it would dominate the run time.)
        foreach (var (name, rx) in candidates.OrderBy(c => c.Rx.Text.Length))
        {
            string? diff = Check(rx.Text);
            log?.WriteLine($"  {name,-10} {(diff is null ? "ok  " : "FAIL")} {Clip(rx.Text)}");
            if (diff is null) { best = rx; break; }
            firstFailure ??= $"{Clip(rx.Text)}\n  differs on input {diff}";
        }
        if (best is not null)
        {
            var polished = Polish(best, t => Check(t) is null, TimeSpan.FromSeconds(best.Text.Length > 400 ? 0.5 : 2));
            if (polished.Text != best.Text) log?.WriteLine($"  {"polished",-10} ok   {polished.Text}");
            best = polished;
        }

        if (best is null)
        {
            Console.Error.WriteLine("error: could not produce an equivalent expression.");
            if (inexpressible is not null) Console.Error.WriteLine("reason: " + inexpressible);
            else if (firstFailure is not null) Console.Error.WriteLine("closest candidate: " + firstFailure);
        }
        return best?.Text;
    }

    static string Clip(string s) => s.Length <= 2000 ? s : s[..2000] + $"... ({s.Length} chars)";

    // Greedy clean-up: repeatedly try deleting one alternative or one range of a character set, and
    // keep the first shorter expression that is still equivalent. This removes redundancy that
    // state elimination can't see, mostly around ^ and $ (e.g. "b|^[ab]" becomes "^a|b").
    static Rx Polish(Rx rx, Func<string, bool> equivalent, TimeSpan limit)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool improved = true;
        while (improved && sw.Elapsed < limit)
        {
            improved = false;
            foreach (var v in Variants(rx))
            {
                if (sw.Elapsed >= limit) break;
                if (v is null || v.Text.Length >= rx.Text.Length) continue;
                if (equivalent(v.Text)) { rx = v; improved = true; break; }
            }
        }
        return rx;
    }

    // Every tree obtained from x by one deletion (null = the empty language).
    static IEnumerable<Rx?> Variants(Rx x)
    {
        switch (x)
        {
            case RxAlt a:
                for (int i = 0; i < a.Items.Count; i++)
                    yield return R.Alt(a.Items.Where((_, j) => j != i).ToArray());
                for (int i = 0; i < a.Items.Count; i++)
                    foreach (var v in Variants(a.Items[i]))
                        yield return R.Alt(a.Items.Select((y, j) => j == i ? v : y).ToArray());
                break;
            case RxCat c:
                for (int i = 0; i < c.Items.Count; i++)
                    foreach (var v in Variants(c.Items[i]))
                        yield return R.Cat(c.Items.Select((y, j) => j == i ? v : y).ToArray());
                break;
            case RxRep r:
                foreach (var v in Variants(r.Body)) yield return R.Rep(v, r.Min, r.Max);
                break;
            case RxSet s:
                if (s.Ranges.Count > 1)
                    for (int i = 0; i < s.Ranges.Count; i++)
                        yield return R.Set(s.Ranges.Where((_, j) => j != i));
                for (int i = 0; i < s.Ranges.Count; i++)   // shave one codepoint off either end
                {
                    var (lo, hi) = s.Ranges[i];
                    if (hi == lo) continue;
                    yield return R.Set(s.Ranges.Select((g, j) => j == i ? (lo + 1, hi) : g));
                    yield return R.Set(s.Ranges.Select((g, j) => j == i ? (lo, hi - 1) : g));
                }
                break;
        }
    }
}