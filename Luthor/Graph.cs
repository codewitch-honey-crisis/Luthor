using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Luthor
{
    sealed class GraphOptions
    {
        /// <summary>
        /// The resolution, in dots-per-inch to render at
        /// </summary>
        public int Dpi { get; set; } = 300;
        /// <summary>
        /// The prefix used for state labels
        /// </summary>
        public string StatePrefix { get; set; } = "q";
        /// <summary>
        /// True to hide the accept symbol ids on accepting states. Symbol names, when provided,
        /// are still shown.
        /// </summary>
        public bool HideSymbolIds { get; set; } = false;
        /// <summary>
        /// Maps accept ids to names for display in the graph
        /// </summary>
        public string[]? SymbolNames { get; set; } = null;
        /// <summary>
        /// True to generate vertical output. Otherwise it will be left to right.
        /// </summary>
        public bool Vertical { get; set; } = false;
    }

    /// <summary>
    /// Renders a codepoint DFA produced by <see cref="Builder.Build"/> as a Graphviz graph.
    /// </summary>
    internal static class Graph
    {
        const int MaxCp = 0x10FFFF;

        /// <summary>
        /// Writes Graphviz dot source for the DFA to the specified writer
        /// </summary>
        /// <param name="dfa">The DFA to graph</param>
        /// <param name="writer">The writer to write to</param>
        /// <param name="options">A <see cref="GraphOptions"/> instance with any options, or null to use the defaults</param>
        public static void WriteTo(CodepointDfa dfa, TextWriter writer, GraphOptions? options = null)
        {
            options ??= new GraphOptions();
            var states = dfa.States;

            writer.WriteLine("digraph FA {");
            writer.WriteLine(options.Vertical ? "\trankdir=TB;" : "\trankdir=LR;");
            writer.WriteLine("\tnode [shape=circle];");

            // --- states ---
            for (int s = 0; s < states.Count; s++)
            {
                var st = states[s];
                var label = new StringBuilder();
                label.Append(Html(options.StatePrefix)).Append("<SUB>").Append(s).Append("</SUB>");
                string? symbol = st.Accept >= 0 ? SymbolText(st.Accept, options) : null;
                if (symbol != null) label.Append("<BR/>").Append(Html(symbol));
                writer.Write($"\t{Node(s)} [label=<{label}>");
                if (st.Accept >= 0) writer.Write(", shape=doublecircle");
                writer.WriteLine("];");
            }

            // --- character moves: one edge per target, all ranges to that target merged into one label ---
            for (int s = 0; s < states.Count; s++)
            {
                var st = states[s];
                var byTarget = new Dictionary<int, List<(int Lo, int Hi)>>();
                var order = new List<int>();
                foreach (var (lo, hi, to) in st.Moves)
                {
                    if (!byTarget.TryGetValue(to, out var list))
                    {
                        byTarget[to] = list = new List<(int Lo, int Hi)>();
                        order.Add(to);
                    }
                    list.Add((lo, hi));
                }
                foreach (int to in order)
                    writer.WriteLine($"\t{Node(s)} -> {Node(to)} [label=<{Html(RangeLabel(byTarget[to]))}>];");

                // --- zero-width anchor edges ---
                if (st.Bol >= 0)
                    writer.WriteLine($"\t{Node(s)} -> {Node(st.Bol)} [label=<^>, style=dashed, color=gray, fontcolor=gray];");
                if (st.Eol >= 0)
                    writer.WriteLine($"\t{Node(s)} -> {Node(st.Eol)} [label=<$>, style=dashed, color=gray, fontcolor=gray];");
            }
            writer.WriteLine("}");
        }

        /// <summary>
        /// Renders Graphviz output for the DFA to the specified file
        /// </summary>
        /// <param name="dfa">The DFA to graph</param>
        /// <param name="filename">The output filename. The format to render is indicated by the file extension (.dot writes the dot source; anything else, such as .png, .jpg, .svg or .pdf, is rendered by Graphviz).</param>
        /// <param name="options">A <see cref="GraphOptions"/> instance with any options, or null to use the defaults</param>
        public static void RenderToFile(CodepointDfa dfa, string filename, GraphOptions? options = null)
        {
            options ??= new GraphOptions();
            string ext = Path.GetExtension(filename).TrimStart('.').ToLowerInvariant();
            if (ext.Length == 0)
                throw new ArgumentException("The output filename needs an extension to indicate the format", nameof(filename));

            if (ext == "dot")
            {
                using var writer = new StreamWriter(filename, false, new UTF8Encoding(false));
                WriteTo(dfa, writer, options);
                return;
            }

            var psi = new ProcessStartInfo("dot")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                // labels can contain any Unicode character, and dot reads UTF-8
                StandardInputEncoding = new UTF8Encoding(false),
            };
            psi.ArgumentList.Add("-T" + ext);
            if (0 < options.Dpi) psi.ArgumentList.Add("-Gdpi=" + options.Dpi.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-o" + filename);

            Process? proc;
            try { proc = Process.Start(psi); }
            catch (Win32Exception ex)
            {
                throw new NotSupportedException(
                    "Graphviz \"dot\" application is either not installed or not in the system PATH", ex);
            }
            if (proc == null)
                throw new NotSupportedException(
                    "Graphviz \"dot\" application is either not installed or not in the system PATH");
            using (proc)
            {
                var stderr = proc.StandardError.ReadToEndAsync();
                WriteTo(dfa, proc.StandardInput, options);
                proc.StandardInput.Close();
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                    throw new InvalidOperationException($"Graphviz \"dot\" failed: {stderr.Result.Trim()}");
            }
        }

        // ---------------- labels ----------------

        static string Node(int s) => "s" + s.ToString(CultureInfo.InvariantCulture);

        // The name if one is provided, otherwise the id unless ids are hidden.
        static string? SymbolText(int id, GraphOptions options)
        {
            var names = options.SymbolNames;
            if (names != null && id < names.Length && !string.IsNullOrEmpty(names[id])) return names[id];
            return options.HideSymbolIds ? null : id.ToString(CultureInfo.InvariantCulture);
        }

        // Regex-style label for a set of codepoint ranges: a single character, a class, or a negated
        // class, whichever is shorter.
        static string RangeLabel(List<(int Lo, int Hi)> ranges)
        {
            var set = Normalize(ranges);
            if (set.Count == 1 && set[0].Lo == set[0].Hi) return Escape(set[0].Lo, inClass: false);
            var comp = Complement(set);
            if (comp.Count == 0) return "any";
            string pos = "[" + ClassBody(set) + "]";
            string neg = "[^" + ClassBody(comp) + "]";
            return neg.Length < pos.Length ? neg : pos;
        }

        static string ClassBody(List<(int Lo, int Hi)> set)
        {
            var sb = new StringBuilder();
            foreach (var (lo, hi) in set)
            {
                sb.Append(Escape(lo, inClass: true));
                if (hi == lo) continue;
                if (hi != lo + 1) sb.Append('-');   // two adjacent characters read better as "ab" than "a-b"
                sb.Append(Escape(hi, inClass: true));
            }
            return sb.ToString();
        }

        static List<(int Lo, int Hi)> Normalize(IEnumerable<(int Lo, int Hi)> rs)
        {
            var res = new List<(int Lo, int Hi)>();
            foreach (var r in rs.OrderBy(r => r.Lo))
            {
                if (res.Count > 0 && r.Lo <= res[^1].Hi + 1) res[^1] = (res[^1].Lo, Math.Max(res[^1].Hi, r.Hi));
                else res.Add(r);
            }
            return res;
        }

        static List<(int Lo, int Hi)> Complement(List<(int Lo, int Hi)> set)
        {
            var res = new List<(int Lo, int Hi)>();
            int next = 0;
            foreach (var (lo, hi) in set) { if (lo > next) res.Add((next, lo - 1)); next = hi + 1; }
            if (next <= MaxCp) res.Add((next, MaxCp));
            return res;
        }

        // Escapes a codepoint using the same escape syntax Builder accepts.
        static string Escape(int cp, bool inClass)
        {
            switch (cp)
            {
                case '\n': return @"\n";
                case '\r': return @"\r";
                case '\t': return @"\t";
                case '\f': return @"\f";
                case '\v': return @"\v";
                case 0: return @"\0";
            }
            string meta = inClass ? @"\]^-[" : @"\.*+?()|[]{}^$";
            if (cp < 0x80 && meta.IndexOf((char)cp) >= 0) return "\\" + (char)cp;
            if (IsVisible(cp)) return char.ConvertFromUtf32(cp);
            if (cp <= 0xFF) return $"\\x{cp:X2}";
            if (cp <= 0xFFFF) return $"\\u{cp:X4}";
            return $"\\x{{{cp:X}}}";
        }

        static bool IsVisible(int cp)
        {
            if (cp >= 0xD800 && cp <= 0xDFFF) return false;   // lone surrogates can't be written out
            switch (CharUnicodeInfo.GetUnicodeCategory(cp))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                case UnicodeCategory.SpaceSeparator:          // includes ' ', which is invisible as a label
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                    return false;
                default:
                    return true;
            }
        }

        // Escapes text for a Graphviz HTML-like label.
        static string Html(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}