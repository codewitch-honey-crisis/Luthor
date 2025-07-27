using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Luthor
{
    class DfaAttributes : Dictionary<string, object>, IEquatable<DfaAttributes>
    {
        public bool Equals(DfaAttributes? other)
        {
            if (object.ReferenceEquals(this, other)) return true;
            if (object.ReferenceEquals(other, null)) return false;
            if (this.Count != other.Count) return false;
            foreach (var attr in this)
            {
                object? val;
                if (!other.TryGetValue(attr.Key, out val)) return false;
                var en = false;
                if (attr.Value is System.Collections.ICollection e)
                {
                    if (!(val is System.Collections.ICollection))
                    {
                        return false;
                    }
                    en = true;
                }
                if (!en)
                {
                    if (!object.Equals(attr.Value, val)) return false;
                } else
                {
                    var list1 = attr.Value as System.Collections.IList;
                    var list2 = val as System.Collections.IList;
                    if (list1 != null && list2 != null)
                    {
                        if(list1.Count != list2.Count) return false;
                        for(int i = 0;i < list1.Count;i++)
                        {
                            if (!object.Equals(list1[i], list2[i])) return false;
                        }
                    }
                    else
                    {
                        var col1 = attr.Value as System.Collections.ICollection;
                        var col2 = val as System.Collections.ICollection;
                        if (col1!.Count != col2!.Count) return false;
                        foreach (var v1 in col1)
                        {
                            var found = false;
                            foreach (var v2 in col2)
                            {
                                if (object.Equals(v1, v2))
                                {
                                    found = true; break;
                                }
                            }
                            if (!found) return false;
                        }
                    }
                }

            }
            return true;
        }
        public override bool Equals(object? obj)
        {
            if (object.ReferenceEquals(this, obj)) return true;
            if (object.ReferenceEquals(obj, null)) return false;
            return Equals(obj as DfaAttributes);
        }
        public override int GetHashCode()
        {
            int result = 0;
            foreach (var attr in this)
            {
                result ^= attr.Key.GetHashCode();
                if (attr.Value != null)
                {
                    result ^= attr.Value.GetHashCode();
                }
            }
            return result;

        }
    }
    /// <summary>
    /// Represents a range of codepoints
    /// </summary>

    struct DfaRange : IEquatable<DfaRange>
    {
        /// <summary>
        /// The minimum codepoint
        /// </summary>
        public int Min;
        /// <summary>
        /// The maximum codepoint
        /// </summary>
        public int Max;
        /// <summary>
        /// Constructs an instance
        /// </summary>
        /// <param name="min">The minimum codepoint</param>
        /// <param name="max">The maximum codepoint</param>
        public DfaRange(int min, int max)
        {
            Min = min;
            Max = max;
        }
        /// <summary>
        /// Indicates whether this range intersects with another range
        /// </summary>
        /// <param name="rhs">The range to compare</param>
        /// <returns></returns>
        public bool Intersects(DfaRange rhs)
        {
            return (rhs.Min >= Min && rhs.Min <= Max) ||
                rhs.Max >= Min && rhs.Max <= Max;
        }
        /// <summary>
        /// Indicates whether or not this codepoint intersects this range
        /// </summary>
        /// <param name="codepoint">The codepoint</param>
        /// <returns>True if the codepoint is part of the range, otherwise false</returns>
        public bool Intersects(int codepoint)
        {
            return codepoint >= Min && codepoint <= Max;
        }
        /// <summary>
        /// Turns packed ranges into unpacked ranges
        /// </summary>
        /// <param name="packedRanges">The ranges to unpack</param>
        /// <returns>The unpacked ranges</returns>
        public static DfaRange[] ToUnpacked(int[] packedRanges)
        {
            var result = new DfaRange[packedRanges.Length / 2];
            for (var i = 0; i < result.Length; ++i)
            {
                var j = i * 2;
                result[i] = new DfaRange(packedRanges[j], packedRanges[j + 1]);
            }
            return result;
        }
        /// <summary>
        /// Packs a series of ranges
        /// </summary>
        /// <param name="pairs">The ranges to pack</param>
        /// <returns>The packed ranges</returns>
        public static int[] ToPacked(IList<DfaRange> pairs)
        {
            var result = new int[pairs.Count * 2];
            for (int ic = pairs.Count, i = 0; i < ic; ++i)
            {
                var pair = pairs[i];
                var j = i * 2;
                result[j] = pair.Min;
                result[j + 1] = pair.Max;
            }
            return result;
        }
        /// <summary>
        /// Inverts a set of unpacked ranges
        /// </summary>
        /// <param name="ranges">The ranges to invert</param>
        /// <returns>The inverted ranges</returns>
        public static IEnumerable<DfaRange> ToNotRanges(IEnumerable<DfaRange> ranges)
        {
            // expects ranges to be normalized
            var last = 0x10ffff;
            using (var e = ranges.GetEnumerator())
            {
                if (!e.MoveNext())
                {
                    yield return new DfaRange(0x0, 0x10ffff);
                    yield break;
                }
                if (e.Current.Min > 0)
                {
                    yield return new DfaRange(0, unchecked(e.Current.Min - 1));
                    last = e.Current.Max;
                    if (0x10ffff <= last)
                        yield break;
                }
                else if (e.Current.Min == 0)
                {
                    last = e.Current.Max;
                    if (0x10ffff <= last)
                        yield break;
                }
                while (e.MoveNext())
                {
                    if (0x10ffff <= last)
                        yield break;
                    if (unchecked(last + 1) < e.Current.Min)
                        yield return new DfaRange(unchecked(last + 1), unchecked((e.Current.Min - 1)));
                    last = e.Current.Max;
                }
                if (0x10ffff >= last)
                    yield return new DfaRange(unchecked((last + 1)), 0x10ffff);

            }
        }
        /// <summary>
        /// Returns a string representation of the range
        /// </summary>
        /// <returns>A string representing the range</returns>
        public override string ToString()
        {
            if (Min == Max)
            {
                return string.Concat("[", char.ConvertFromUtf32(Min), "]");
            }
            return string.Concat("[", char.ConvertFromUtf32(Min), "-", char.ConvertFromUtf32(Max), "]");
        }
        /// <summary>
        /// Value equality
        /// </summary>
        /// <param name="rhs">The range to compare</param>
        /// <returns>True if they are equal, otherwise false</returns>
        public bool Equals(DfaRange rhs)
        {
            return rhs.Min == Min && rhs.Max == Max;
        }
        /// <summary>
        /// Value equality
        /// </summary>
        /// <param name="rhs">The object to compare</param>
        /// <returns>True if they are equal, otherwise false</returns>
        public override bool Equals(object? rhs)
        {
            if (ReferenceEquals(null, rhs)) return false;
            if (rhs is DfaRange)
            {
                return Equals((DfaRange)rhs);
            }
            return base.Equals(rhs);
        }
        /// <summary>
        /// Retrieves a hash code for the instance
        /// </summary>
        /// <returns>A hash code</returns>
        public override int GetHashCode()
        {
            return Min.GetHashCode() ^ Max.GetHashCode();
        }
    }

    /// <summary>
    /// Represents a transition in an FSM
    /// </summary>

    struct DfaTransition : IEquatable<DfaTransition>
    {
        /// <summary>
        /// The minimum codepoint of the range
        /// </summary>
        public int Min;
        /// <summary>
        /// The maximum codepoint of the range
        /// </summary>
        public int Max;
        /// <summary>
        /// The destination state
        /// </summary>
        public Dfa To;
        /// <summary>
        /// Constructs a new instance
        /// </summary>
        /// <param name="to">The destination state</param>
        /// <param name="min">The minimum codepoint</param>
        /// <param name="max">The maximum codepoint</param>
        public DfaTransition(Dfa to, int min = -1, int max = -1)
        {
            Min = min;
            Max = max;
            To = to;
        }
        /// <summary>
        /// Indicates whether or not this is an epsilon transition
        /// </summary>
        /// <remarks>Not used for direct DFA construction, but kept for traditional DFA construction</remarks>
        public bool IsEpsilon { get { return Min == -1 || Max == -1; } }
        /// <summary>
        /// Provides a string representation of the transition
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            if (IsEpsilon)
            {
                return string.Concat("-> ", To.ToString());
            }
            if (Min == Max)
            {
                return string.Concat("[", char.ConvertFromUtf32(Min), "]-> ", To.ToString());
            }
            return string.Concat("[", char.ConvertFromUtf32(Min), "-", char.ConvertFromUtf32(Max), "]-> ", To.ToString());
        }
        /// <summary>
        /// Value equality
        /// </summary>
        /// <param name="rhs">The transition to compare</param>
        /// <returns></returns>
        public bool Equals(DfaTransition rhs)
        {
            return To == rhs.To && Min == rhs.Min && Max == rhs.Max;
        }
        /// <summary>
        /// Returns a hashcode for the transition
        /// </summary>
        /// <returns>A hashcode</returns>
        public override int GetHashCode()
        {
            if (To == null)
            {
                return Min.GetHashCode() ^ Max.GetHashCode();
            }
            return Min.GetHashCode() ^ Max.GetHashCode() ^ To.GetHashCode();
        }
        /// <summary>
        /// Value equality
        /// </summary>
        /// <param name="obj">The object to compare</param>
        /// <returns></returns>
        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(obj, null)) return false;
            if (!(obj is DfaTransition)) return false;
            DfaTransition rhs = (DfaTransition)obj;
            // ref compare on To so its attributes will always be the same
            return To == rhs.To && Min == rhs.Min && Max == rhs.Max;
        }
    }


    sealed class DfaParseException : DfaException
    {
        /// <summary>
        /// Indicates the strings that were expected
        /// </summary>
        public string[] Expecting { get; } = null;
        /// <summary>
        /// Indicates the position where the error occurred
        /// </summary>
        public int Position { get; }
        /// <summary>
        /// Constructs a new instance
        /// </summary>
        /// <param name="message">The error</param>
        /// <param name="position">The position</param>
        public DfaParseException(string message, int position) : base(message)
        {
            Position = position;
        }
        /// <summary>
        /// Constructs a new instance
        /// </summary>
        /// <param name="message">The error</param>
        /// <param name="position">The position</param>
        /// <param name="innerException">The inner exception</param>
        public DfaParseException(string message, int position, Exception innerException) : base(message, innerException)
        {
            Position = position;
        }
        /// <summary>
        /// Constructs a new instance
        /// </summary>
        /// <param name="message">The error</param>
        /// <param name="position">The position</param>
        /// <param name="expecting">The strings that were expected</param>
        public DfaParseException(string message, int position, string[] expecting) : base(message)
        {
            Position = position;
            Expecting = expecting;
        }
    }
    /// <summary>
    /// Represents an exception in the FA engine
    /// </summary>

    class DfaException : Exception
    {
        /// <summary>
        /// Constructs a new instance
        /// </summary>
        /// <param name="message">The message</param>
        public DfaException(string message) : base(message)
        {

        }
        /// <summary>
        /// Constructs a new instance
        /// </summary>
        /// <param name="message">The message</param>
        /// <param name="innerException">The inner exception</param>
        public DfaException(string message, Exception innerException) : base(message, innerException)
        {

        }
    }

    internal partial class Dfa
    {
        internal int _MinimizationTag { get; set; } = -1;
        public IList<Dfa> FillClosure(IList<Dfa> result = null)
        {
            if (null == result) result = new List<Dfa>();
            else if (result.Contains(this)) return result;
            result.Add(this);
            foreach (var trn in Transitions)
            {
                if (trn.To != null)
                {
                    trn.To.FillClosure(result);
                }
            }
            return result;
        }
        public readonly DfaAttributes Attributes = new DfaAttributes();
        readonly List<DfaTransition> _transitions = new List<DfaTransition>(); // TODO: wrap this with IReadOnlyList for a public property, and add AddTransition
        public IReadOnlyList<DfaTransition> Transitions { get { return _transitions; } }
        public bool IsAccept
        {
            get
            {
                object result;
                if (Attributes.TryGetValue("AcceptSymbol", out result))
                {
                    if (result is int val && val != -1)
                    {
                        return true;
                    }
                }
                return false;
            }
        }
        public int AcceptSymbol
        {
            get
            {
                object result;
                if (Attributes.TryGetValue("AcceptSymbol", out result))
                {
                    if (result is int val)
                    {
                        return val;
                    }
                }
                return -1;
            }
        }
        public static int GetFirstAcceptSymbol(IEnumerable<Dfa> states)
        {
            if (states == null) return -1;
            foreach (var state in states)
            {
                int acc = state.AcceptSymbol;
                if (acc!=-1)
                {
                    return acc;
                }
            }
            return -1;
        }
        static bool _FsmHasAnyVirtualCharTransitionsOrAcceptingStates(IEnumerable<Dfa> states)
        {
            if (states == null) return false;
            foreach (var state in states)
            {
                
                if (state.IsAccept)
                {
                    return true;
                }
                foreach(var fat in state._transitions)
                {
                    if (fat.Min < -1) return true;
                }
            }
            return false;
        }
        public void RemoveTransition(DfaTransition trn)
        {
            _transitions.Remove(trn);
        }
        public void AddTransition(DfaTransition transition)
        {
            foreach (var trn in _transitions)
            {
                if (trn.Equals(transition))
                    return; // found
            }
            _transitions.Add(transition);
        }
        public IDictionary<Dfa, IList<DfaRange>> FillInputTransitionRangesGroupedByState(IDictionary<Dfa, IList<DfaRange>>? result = null)
        {
            if (null == result)
                result = new Dictionary<Dfa, IList<DfaRange>>();

            foreach (var trns in Transitions)
            {
                if (trns.IsEpsilon)
                {
                    continue;
                }
                IList<DfaRange> l;
                if (!result.TryGetValue(trns.To, out l))
                {
                    l = new List<DfaRange>();
                    result.Add(trns.To, l);
                }
                l.Add(new DfaRange(trns.Min, trns.Max));
            }
            foreach (var item in result)
            {
                ((List<DfaRange>)item.Value).Sort((x, y) => { var c = x.Min.CompareTo(y.Min); if (0 != c) return c; return x.Max.CompareTo(y.Max); });
                _NormalizeSortedRangeList(item.Value);
            }
            return result;
        }
        static void _NormalizeSortedRangeList(IList<DfaRange> pairs)
        {
            for (int i = 1; i < pairs.Count; ++i)
            {
                if (pairs[i - 1].Max + 1 >= pairs[i].Min)
                {
                    var nr = new DfaRange(pairs[i - 1].Min, pairs[i].Max);
                    pairs[i - 1] = nr;
                    pairs.RemoveAt(i);
                    --i; // compensated for by ++i in for loop
                }
            }
        }
        static IEnumerable<DfaRange> _InvertRanges(IEnumerable<DfaRange> ranges)
        {
            if (ranges == null)
            {
                yield break;
            }
            var last = 0x10ffff;

            using (var e = ranges.GetEnumerator())
            {
                if (!e.MoveNext())
                {
                    DfaRange range;
                    range.Min = 0;
                    range.Max = 0x10ffff;
                    yield return range;
                    yield break;
                }
                if (e.Current.Min > 0)
                {
                    DfaRange range;
                    range.Min = 0;
                    range.Max = e.Current.Min - 1;
                    yield return range;
                    last = e.Current.Max;
                    if (0x10ffff <= last)
                        yield break;
                }
                else if (e.Current.Min == 0)
                {
                    last = e.Current.Max;
                    if (0x10ffff <= last)
                        yield break;
                }
                while (e.MoveNext())
                {
                    if (0x10ffff <= last)
                        yield break;
                    if (unchecked(last + 1) < e.Current.Min)
                    {
                        DfaRange range;
                        range.Min = unchecked(last + 1);
                        range.Max = unchecked((e.Current.Min - 1));
                        yield return range;
                    }
                    last = e.Current.Max;
                }
                if (0x10ffff > last)
                {
                    DfaRange range;
                    range.Min = unchecked((last + 1));
                    range.Max = 0x10ffff;
                    yield return range;
                }

            }
        }
        static void _AppendRangeTo(StringBuilder builder, IList<DfaRange> ranges)
        {
            for (int i = 0; i < ranges.Count; ++i)
            {
                _AppendRangeTo(builder, ranges, i);
            }
        }
        static void _AppendRangeTo(StringBuilder builder, IList<DfaRange> ranges, int index)
        {
            var first = ranges[index].Min;
            var last = ranges[index].Max;
            _AppendRangeCharTo(builder, first);
            if (0 == last.CompareTo(first)) return;
            if (last == first + 1) // spit out 1 and 2 length ranges as flat chars
            {
                _AppendRangeCharTo(builder, last);
                return;
            }
            else if (last == first + 2)
            {
                _AppendRangeCharTo(builder, first + 1);
                _AppendRangeCharTo(builder, last);
                return;
            }
            builder.Append('-');
            _AppendRangeCharTo(builder, last);
        }
        static void _AppendCharTo(StringBuilder builder, int @char)
        {
            switch (@char)
            {
                case '.':
                case '[':
                case ']':
                case '^':
                case '-':
                case '+':
                case '?':
                case '(':
                case ')':
                case '\\':
                    builder.Append('\\');
                    builder.Append(char.ConvertFromUtf32(@char));
                    return;
                case '\t':
                    builder.Append("\\t");
                    return;
                case '\n':
                    builder.Append("\\n");
                    return;
                case '\r':
                    builder.Append("\\r");
                    return;
                case '\0':
                    builder.Append("\\0");
                    return;
                case '\f':
                    builder.Append("\\f");
                    return;
                case '\v':
                    builder.Append("\\v");
                    return;
                case '\b':
                    builder.Append("\\b");
                    return;
                default:
                    var s = char.ConvertFromUtf32(@char);
                    if (!char.IsLetterOrDigit(s, 0) && !char.IsSeparator(s, 0) && !char.IsPunctuation(s, 0) && !char.IsSymbol(s, 0))
                    {
                        if (s.Length == 1)
                        {
                            builder.Append("\\u");
                            builder.Append(unchecked((ushort)@char).ToString("x4"));
                        }
                        else
                        {
                            builder.Append("\\U");
                            builder.Append(@char.ToString("x8"));
                        }

                    }
                    else
                        builder.Append(s);
                    break;
            }
        }

        static void _AppendRangeCharTo(StringBuilder builder, int rangeChar)
        {
            switch (rangeChar)
            {
                case '.':
                case '[':
                case ']':
                case '^':
                case '-':
                case '(':
                case ')':
                case '{':
                case '}':
                case '\\':
                    builder.Append('\\');
                    builder.Append(char.ConvertFromUtf32(rangeChar));
                    return;
                case '\t':
                    builder.Append("\\t");
                    return;
                case '\n':
                    builder.Append("\\n");
                    return;
                case '\r':
                    builder.Append("\\r");
                    return;
                case '\0':
                    builder.Append("\\0");
                    return;
                case '\f':
                    builder.Append("\\f");
                    return;
                case '\v':
                    builder.Append("\\v");
                    return;
                case '\b':
                    builder.Append("\\b");
                    return;
                default:
                    var s = char.ConvertFromUtf32(rangeChar);
                    if (!char.IsLetterOrDigit(s, 0) && !char.IsSeparator(s, 0) && !char.IsPunctuation(s, 0) && !char.IsSymbol(s, 0))
                    {
                        if (s.Length == 1)
                        {
                            builder.Append("\\u");
                            builder.Append(unchecked((ushort)rangeChar).ToString("x4"));
                        }
                        else
                        {
                            builder.Append("\\U");
                            builder.Append(rangeChar.ToString("x8"));
                        }

                    }
                    else
                        builder.Append(s);
                    break;
            }
        }
        static string _EscapeLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return label;

            string result = label.Replace("\\", @"\\");
            result = result.Replace("\"", "\\\"");
            result = result.Replace("\n", "\\n");
            result = result.Replace("\r", "\\r");
            result = result.Replace("\0", "\\0");
            result = result.Replace("\v", "\\v");
            result = result.Replace("\t", "\\t");
            result = result.Replace("\f", "\\f");
            return result;
        }
        static bool _IsPrintable(int cp)
        {
            try
            {


                var str = char.ConvertFromUtf32(cp);
                if (!char.IsWhiteSpace(str, 0) && char.IsSymbol(str, 0))
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
        void _WriteDotTo(TextWriter writer)
        {
            writer.WriteLine("digraph FA {");
            writer.WriteLine("rankdir=LR");
            writer.WriteLine("node [shape=circle]");
            var closure = FillClosure();
            var accepting = new List<Dfa>();
            var finals = new List<Dfa>();
            foreach (var ffa in closure)
            {
                if (ffa.IsAccept)
                {
                    accepting.Add(ffa);
                }
                else if (ffa.Transitions.Count == 0)
                {
                    finals.Add(ffa);
                }
            }
            int i = 0;
            foreach (var ffa in closure)
            {
                var rngGrps = ffa.FillInputTransitionRangesGroupedByState();
                foreach (var rngGrp in rngGrps)
                {
                    var di = closure.IndexOf(rngGrp.Key);
                    writer.Write("q");
                    writer.Write(i);
                    writer.Write("->q");
                    writer.Write(di.ToString());
                    writer.Write(" [label=\"");
                    var sb = new StringBuilder();
                    // gather the specials and remove them from the standard transitions
                    var specials = new List<DfaRange>();
                    for(var j = 0;j<rngGrp.Value.Count;++j)
                    {
                        var v = rngGrp.Value[j];
                        if (v.Min < -1 || v.Max < -1) {
                            specials.Add(v);
                            rngGrp.Value.RemoveAt(j);
                            --j;
                        }
                    }
                    foreach(var spec in specials)
                    {
                        if(spec.Min==-2)
                        {
                            writer.Write(_EscapeLabel("{^}"));
                        } else if(spec.Min==-3)
                        {
                            writer.Write(_EscapeLabel("{$}"));
                        }
                        
                    }
                    if (rngGrp.Value.Count > 0)
                    {
                        //var notRanges = new List<FARange>(FARange.ToNotRanges(rngGrp.Value));
                        var notRanges = new List<DfaRange>(_InvertRanges(rngGrp.Value));
                        var hasNonPrint = false;
                        foreach(var v in rngGrp.Value)
                        {
                            if(!_IsPrintable(v.Min) || !_IsPrintable(v.Max)) {
                                hasNonPrint = true; break;
                            }
                        }
                        var hasNotNonPrint = false;
                        foreach (var v in notRanges)
                        {
                            if (!_IsPrintable(v.Min) || !_IsPrintable(v.Max)) {
                                hasNotNonPrint = true; break;
                            }
                        }
                        if ((hasNotNonPrint && !hasNonPrint)  || (hasNonPrint==hasNotNonPrint && notRanges.Count * 1.5 > rngGrp.Value.Count))
                        {
                            _AppendRangeTo(sb, rngGrp.Value);
                        }
                        else
                        {
                            if (notRanges.Count == 0)
                            {
                                sb.Append(".\\n");
                            }
                            else
                            {
                                sb.Append("^");
                                _AppendRangeTo(sb, notRanges);
                            }
                        }
                        if (sb.Length != 1 || " " == sb.ToString())
                        {
                            writer.Write('[');
                            if (sb.Length > 16)
                            {
                                sb.Length = 16;
                                sb.Append("...");
                            }
                            writer.Write(_EscapeLabel(sb.ToString()));
                            writer.Write(']');
                        }
                        else
                        {
                            writer.Write(_EscapeLabel(sb.ToString()));
                        }
                    }
                    writer.WriteLine("\"]");

                }
                ++i;
            }
            i = 0;
            var delim = "";
            foreach (var ffa in closure)
            {
                writer.Write("q");
                writer.Write(i);
                writer.Write(" [");
                
                writer.Write("label=<");
                writer.Write("<TABLE BORDER=\"0\"><TR><TD>");
                writer.Write("q");
                writer.Write("<SUB>");
                writer.Write(i);
                writer.Write("</SUB></TD></TR>");

                if (ffa.IsAccept)
                {
                    writer.Write("<TR><TD>");
                    string acc = Convert.ToString(ffa.AcceptSymbol);
                    
                    writer.Write(acc.Replace("\"", "&quot;"));
                    writer.Write("</TD></TR>");
                }
                writer.Write("</TABLE>");
                writer.Write(">");
                bool isfinal = false;
                if (accepting.Contains(ffa))
                    writer.Write(",shape=doublecircle");
                else if (isfinal)
                {
                    writer.Write(",color=gray");
                    
                }
                writer.WriteLine("]");
                ++i;
            }
            delim = "";
            if (0 < accepting.Count)
            {
                foreach (var ntfa in accepting)
                {
                    writer.Write(delim);
                    writer.Write("q");
                    writer.Write(closure.IndexOf(ntfa));
                    delim = ",";

                }
                if (delim != "")
                {
                    writer.WriteLine(" [shape=doublecircle]");
                }
            }
            writer.WriteLine("}");
        }
        /// <summary>
		/// Creates a packed state table as a series of integers
		/// </summary>
		/// <returns>An integer array representing the machine</returns>
		public int[] ToRangeArray()
        {
            var working = new List<int>();
            var closure = new List<Dfa>();
            FillClosure(closure);
            var stateIndices = new int[closure.Count];
            // fill in the state information
            for (var i = 0; i < stateIndices.Length; ++i)
            {
                var cfa = closure[i];
                stateIndices[i] = working.Count;
                // add the accept
                working.Add(cfa.IsAccept ? cfa.AcceptSymbol : -1);
                var itrgp = cfa.FillInputTransitionRangesGroupedByState();
                // add the number of transitions
                working.Add(itrgp.Count);
                foreach (var itr in itrgp)
                {
                    // We have to fill in the following after the fact
                    // We don't have enough info here
                    // for now just drop the state index as a placeholder
                    working.Add(closure.IndexOf(itr.Key));
                    // add the number of packed ranges
                    working.Add(itr.Value.Count);
                    // add the packed ranges
                    working.AddRange(DfaRange.ToPacked(itr.Value));
                }
            }
            var result = working.ToArray();
            var state = 0;
            // now fill in the state indices
            while (state < result.Length)
            {
                ++state;
                var tlen = result[state++];
                for (var i = 0; i < tlen; ++i)
                {
                    // patch the destination
                    result[state] = stateIndices[result[state]];
                    ++state;
                    var prlen = result[state++];
                    state += prlen * 2;
                }
            }
            return result;
        }
        /// <summary>
		/// Creates a packed state table as a series of integers
		/// </summary>
		/// <returns>An integer array representing the machine</returns>
		public int[] ToNonRangeArray()
        {
            var working = new List<int>();
            var codepoints = new List<int>();
            var closure = new List<Dfa>();
            FillClosure(closure);
            var stateIndices = new int[closure.Count];
            // fill in the state information
            for (var i = 0; i < stateIndices.Length; ++i)
            {
                var cfa = closure[i];
                stateIndices[i] = working.Count;
                // add the accept
                working.Add(cfa.IsAccept ? cfa.AcceptSymbol : -1);
                var itrgp = cfa.FillInputTransitionRangesGroupedByState();
                // add the number of transitions
                working.Add(itrgp.Count);
                foreach (var itr in itrgp)
                {
                    // We have to fill in the following after the fact
                    // We don't have enough info here
                    // for now just drop the state index as a placeholder
                    working.Add(closure.IndexOf(itr.Key));
                    // add the number of packed ranges
                    // add the packed ranges
                    codepoints.Clear();
                    foreach(var range in itr.Value)
                    {
                        for(var j = range.Min; j <= range.Max; ++j)
                        {
                            codepoints.Add(j);
                        }
                    }
                    working.Add(codepoints.Count);
                    working.AddRange(codepoints);
                }
            }
            // force the array to be odd. This is how we mark it as a non-range array for FromArray
            // range arrays are *always* even
            if (0 == (working.Count % 2))
            {
                working.Add(-1);
            }
            var result = working.ToArray();
            var state = 0;
            // now fill in the state indices
            while (state < result.Length)
            {
                ++state;
                if (state >= result.Length) break;
                var tlen = result[state++];
                for (var i = 0; i < tlen; ++i)
                {
                    // patch the destination
                    result[state] = stateIndices[result[state]];
                    ++state;
                    var prlen = result[state++];
                    state += prlen;
                }
            }
            
            return result;
        }
        public static bool IsRangeArray(int[] fa)
        {
            return fa.Length % 2 == 0;
        }
        public int[] ToArray()
        {
            var rangeArray = ToRangeArray();
            var nonRangeArray = ToNonRangeArray();
            if(rangeArray.Length<nonRangeArray.Length)
            {
                return rangeArray;
            }
            return nonRangeArray;
        }
        public static Dfa FromArray(int[] fa)
        {
            if (null == fa) throw new ArgumentNullException(nameof(fa));
            if (fa.Length == 0)
            {
                var result = new Dfa();
                return result;
            }
            var isRangeArray = (0 == fa.Length % 2);
            // create the states and build a map
            // of state indices in the array to
            // new FA instances
            var si = 0;
            var indexToStateMap = new Dictionary<int, Dfa>();
            while (si < fa.Length)
            {
                var newfa = new Dfa();
                indexToStateMap.Add(si, newfa);
                newfa.Attributes["AcceptSymbol"] = fa[si++];
                // skip to the next state
                if (si >= fa.Length)
                {
                    break;
                }

                var tlen = fa[si++];
                for (var i = 0; i < tlen; ++i)
                {
                    ++si; // tto
                    var prlen = fa[si++];
                    if (isRangeArray)
                        si += prlen * 2;
                    else
                        si += prlen;
                }
            }
            // walk the array
            si = 0;
            var sid = 0;
            while (si < fa.Length)
            {
                // get the current state
                var newfa = indexToStateMap[si];
                // already set above:
                // newfa.AcceptSymbol = fa[si++];
                ++si;
                if (si >= fa.Length)
                {
                    break;
                }
                // transitions length
                var tlen = fa[si++];
                for (var i = 0; i < tlen; ++i)
                {
                    // destination state index
                    var tto = fa[si++];
                    // destination state instance
                    var to = indexToStateMap[tto];
                    // range count
                    var prlen = fa[si++];
                    for (var j = 0; j < prlen; ++j)
                    {
                        int pmin, pmax;
                        if (isRangeArray)
                        {
                            pmin = fa[si++];
                            pmax = fa[si++];
                        } else
                        {
                            pmin = pmax = fa[si++];
                        }

                        newfa.AddTransition(new DfaTransition(to, pmin, pmax));

                    }
                }
                ++sid;
            }
            return indexToStateMap[0];
        }

        public void RenderToFile(string filename)
        {
            string args = "-T";
            string ext = Path.GetExtension(filename);
            if (0 == string.Compare(".dot",
                ext,
                StringComparison.InvariantCultureIgnoreCase))
            {
                using (var writer = new StreamWriter(filename, false))
                {
                    _WriteDotTo(writer);
                    return;
                }
            }
            else if (0 == string.Compare(".png",
                ext,
                StringComparison.InvariantCultureIgnoreCase))
                args += "png";
            else if (0 == string.Compare(".jpg",
                ext,
                StringComparison.InvariantCultureIgnoreCase))
                args += "jpg";
            else if (0 == string.Compare(".bmp",
                ext,
                StringComparison.InvariantCultureIgnoreCase))
                args += "bmp";
            else if (0 == string.Compare(".svg",
                ext,
                StringComparison.InvariantCultureIgnoreCase))
                args += "svg";
            args += " -Gdpi=300";
            args += " -o\"" + filename + "\"";

            var psi = new ProcessStartInfo("dot", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true
            };
            using (var proc = Process.Start(psi))
            {
                if (proc == null)
                {
                    throw new NotSupportedException(
                        "Graphviz \"dot\" application is either not installed or not in the system PATH");
                }
                _WriteDotTo(proc.StandardInput);
                proc.StandardInput.Close();
                proc.WaitForExit();
            }
        }
    }
}