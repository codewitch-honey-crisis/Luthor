using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Luthor
{
    #region StringCursor
    internal sealed class StringCursor
    {
        const int BeforeInput = -2;
        const int EndOfInput = -1;
        public string Input = null;
        public int Position = -1;
        public int Codepoint = -2;
        public int StartPosition = 0;
        int StringPosition
        {
            get
            {
                return Position - StartPosition;
            }
            set
            {
                Position = value + StartPosition;
            }
        }
        public StringBuilder CaptureBuffer { get; } = new StringBuilder();
        public void Capture()
        {
            if (Codepoint < 0)
            {
                return;
            }
            CaptureBuffer.Append(char.ConvertFromUtf32(Codepoint));
        }
        public string GetCapture(int index = 0, int length = -1)
        {
            if (length == -1)
            {
                if (index == 0)
                {
                    return CaptureBuffer.ToString();
                }
                return CaptureBuffer.ToString(index, CaptureBuffer.Length - index);
            }
            return CaptureBuffer.ToString(index, length);
        }
        public void EnsureStarted()
        {
            if (Codepoint == -2)
            {
                Advance();
            }
        }
        public int Advance()
        {
            if (Input == null)
            {
                Codepoint = -1;
                return -1;
            }
            if (++StringPosition >= Input.Length)
            {
                Codepoint = -1;
                return -1;
            }
            Codepoint = Input[StringPosition];
            if (Codepoint <= 0xFFFF && char.IsHighSurrogate((char)Codepoint))
            {
                if (++StringPosition >= Input.Length)
                {
                    throw new IOException("Unexpected end of input in Unicode stream");
                }
                var tmp = Input[StringPosition];
                if (tmp > 0xFFFF || !char.IsLowSurrogate((char)tmp))
                {
                    throw new IOException("Unterminated surrogate Unicode stream");
                }
                Codepoint = char.ConvertToUtf32((char)Codepoint, (char)tmp);
            }
            return Codepoint;
        }
        public void Expecting(params int[] codepoints)
        {
            if (BeforeInput == Codepoint)
                throw new DfaParseException("The cursor is before the beginning of the input", Position);
            switch (codepoints.Length)
            {
                case 0:
                    if (EndOfInput == Codepoint)
                        throw new DfaParseException("Unexpected end of input", Position);
                    break;
                case 1:
                    if (codepoints[0] != Codepoint)
                        throw new DfaParseException(_GetErrorMessage(codepoints), Position, _GetErrorExpecting(codepoints));
                    break;
                default:
                    if (0 > Array.IndexOf(codepoints, Codepoint))
                        throw new DfaParseException(_GetErrorMessage(codepoints), Position, _GetErrorExpecting(codepoints));
                    break;
            }
        }
        string[] _GetErrorExpecting(int[] codepoints)
        {
            var result = new string[codepoints.Length];
            for (var i = 0; i < codepoints.Length; i++)
            {
                if (-1 != codepoints[i])
                    result[i] = char.ConvertFromUtf32(codepoints[i]);
                else
                    result[i] = "end of input";
            }
            return result;
        }
        string _GetErrorMessage(int[] expecting)
        {
            StringBuilder sb = null;
            switch (expecting.Length)
            {
                case 0:
                    break;
                case 1:
                    sb = new StringBuilder();
                    if (-1 == expecting[0])
                        sb.Append("end of input");
                    else
                    {
                        sb.Append("\"");
                        sb.Append(char.ConvertFromUtf32(expecting[0]));
                        sb.Append("\"");
                    }
                    break;
                case 2:
                    sb = new StringBuilder();
                    if (-1 == expecting[0])
                        sb.Append("end of input");
                    else
                    {
                        sb.Append("\"");
                        sb.Append(char.ConvertFromUtf32(expecting[0]));
                        sb.Append("\"");
                    }
                    sb.Append(" or ");
                    if (-1 == expecting[1])
                        sb.Append("end of input");
                    else
                    {
                        sb.Append("\"");
                        sb.Append(char.ConvertFromUtf32(expecting[1]));
                        sb.Append("\"");
                    }
                    break;
                default: // length > 2
                    sb = new StringBuilder();
                    if (-1 == expecting[0])
                        sb.Append("end of input");
                    else
                    {
                        sb.Append("\"");
                        sb.Append(char.ConvertFromUtf32(expecting[0]));
                        sb.Append("\"");
                    }
                    int l = expecting.Length - 1;
                    int i = 1;
                    for (; i < l; ++i)
                    {
                        sb.Append(", ");
                        if (-1 == expecting[i])
                            sb.Append("end of input");
                        else
                        {
                            sb.Append("\"");
                            sb.Append(char.ConvertFromUtf32(expecting[1]));
                            sb.Append("\"");
                        }
                    }
                    sb.Append(", or ");
                    if (-1 == expecting[i])
                        sb.Append("end of input");
                    else
                    {
                        sb.Append("\"");
                        sb.Append(char.ConvertFromUtf32(expecting[i]));
                        sb.Append("\"");
                    }
                    break;
            }
            if (-1 == Codepoint)
            {
                if (0 == expecting.Length)
                    return "Unexpected end of input";
                System.Diagnostics.Debug.Assert(sb != null); // shut up code analysis
                return string.Concat("Unexpected end of input. Expecting ", sb.ToString());
            }
            if (0 == expecting.Length)
                return string.Concat("Unexpected character \"", char.ConvertFromUtf32(Codepoint), "\" in input");
            System.Diagnostics.Debug.Assert(sb != null); // shut up code analysis
            return string.Concat("Unexpected character \"", char.ConvertFromUtf32(Codepoint), "\" in input. Expecting ", sb.ToString());
        }
        public bool TrySkipWhiteSpace()
        {
            EnsureStarted();
            if (Input == null || StringPosition >= Input.Length) return false;
            if (!char.IsWhiteSpace(Input, StringPosition))
                return false;
            Advance();
            if (StringPosition < Input.Length && char.IsLowSurrogate(Input,  StringPosition)) ++StringPosition;
            while (StringPosition < Input.Length && char.IsWhiteSpace(Input, StringPosition))
            {
                Advance();
            }
            return true;
        }
        public bool TryReadDigits()
        {
            EnsureStarted();
            if (Input == null || StringPosition >= Input.Length) return false;
            if (!char.IsDigit(Input, StringPosition))
                return false;
            Capture();
            Advance();
            while (StringPosition < Input.Length && char.IsDigit(Input, StringPosition))
            {
                Capture();
                Advance();
            }
            return true;
        }
        public bool TryReadUntil(int character, bool readCharacter = true)
        {
            EnsureStarted();
            if (0 > character) character = -1;
            Capture();

            if (Codepoint == character)
            {
                return true;
            }
            while (-1 != Advance() && Codepoint != character)
                Capture();
            //
            if (Codepoint == character)
            {
                if (readCharacter)
                {
                    Capture();
                    Advance();
                }
                return true;
            }
            return false;
        }
    }
    #endregion StringCursor

    enum RegexAnchorType
    {
        LineStart = 0,
        LineEnd
    }
    /// <summary>
    /// Indicates an action to take when a node is visited
    /// </summary>
    /// <param name="parent">The parent node</param>
    /// <param name="expression">The current expression</param>
    /// <param name="childIndex">The index of the expression within the parent</param>
    /// <param name="level">The nexting level</param>
    /// <returns></returns>

    delegate bool RegexVisitAction(RegexExpression parent, RegexExpression expression, int childIndex, int level);
    /// <summary>
    /// Represents the common functionality of all regular expression elements
    /// </summary>

    abstract partial class RegexExpression : ICloneable
    {
        /// <summary>
        /// Indicates the 0 based position on which the regular expression was found
        /// </summary>
        public long Position { get; set; } = -1;
        /// <summary>
        /// A user defined, application specific value to associate with this expression
        /// </summary>
        public object Tag { get; set; } = null;
        /// <summary>
        /// Indicates whether or not this statement is a empty element or not
        /// </summary>
        public abstract bool IsEmptyElement { get; }
        /// <summary>
        /// Indicates whether or not this statement is a single element or not
        /// </summary>
        /// <remarks>If false, this statement will be wrapped in parentheses if necessary</remarks>
        public abstract bool IsSingleElement { get; }
        /// <summary>
        /// Sets the location information for the expression
        /// </summary>
        /// <param name="position">The 0 based position where the expression appears</param>
        public void SetLocation(long position)
        {
            Position = position;
        }
        public List<RegexComment> Comments { get; } = new List<RegexComment> ();

        public virtual bool IsLeaf { get; } = false;
        public virtual IList<DfaRange> GetRanges()
        {
            return new DfaRange[0];
        }
        /// <summary>
		/// Converts a series of characters into a series of UTF-32 codepoints
		/// </summary>
		/// <param name="string">The series of characters to convert</param>
		/// <returns>The series of UTF-32 codepoints</returns>
		/// <exception cref="IOException">The characters had a sequence that was not valid unicode</exception>
		public static IEnumerable<int> ToUtf32(IEnumerable<char> @string)
        {
            int chh = -1;
            foreach (var ch in @string)
            {
                if (char.IsHighSurrogate(ch))
                {
                    chh = ch;
                    continue;
                }
                else
                    chh = -1;
                if (-1 != chh)
                {
                    if (!char.IsLowSurrogate(ch))
                        throw new IOException("Unterminated Unicode surrogate pair found in string.");
                    yield return char.ConvertToUtf32(unchecked((char)chh), ch);
                    chh = -1;
                    continue;
                }
                yield return ch;
            }
        }
        static RegexExpression _ExpandRepeats(RegexExpression start)
        {
            start = start.Clone();
            start.Visit((parent, expr, index, level) =>
            {
                if (expr is RegexRepeatExpression repeat)
                {
                    expr = repeat.ExpandRepeats();
                    if (parent is RegexUnaryExpression unary)
                    {
                        unary.Expression = expr;
                    }
                    else if (parent is RegexBinaryExpression binary)
                    {
                        if (index == 0)
                        {
                            binary.Left = expr;
                        }
                        else
                        {
                            binary.Right = expr;
                        }
                    }
                }
                return true;
            });
            return start;
        }
        public string ToString(string format)
        {
            if(format=="x")
            {
                return _ExpandRepeats(this).ToString();
            } else if(string.IsNullOrEmpty(format))
            {
                return this.ToString();
            }
            throw new FormatException($"Unrecognized format specifier \"{format}\".");
        }
        private bool _Visit(RegexExpression parent, RegexVisitAction action, int childIndex, int level)
        {
            if (action(parent, this, childIndex, level))
            {
                var lexer = this as RegexLexerExpression;
                if (lexer != null)
                {
                    for (int i = 0; i < lexer.Rules.Count; i++)
                    {
                        lexer.Rules[i]._Visit(this, action, i, level + 1);
                    }
                    return true;
                }
                var unary = this as RegexUnaryExpression;
                if (unary != null && unary.Expression != null)
                {
                    return unary.Expression._Visit(this, action, 0, level + 1);
                }
                var binary = this as RegexBinaryExpression;
                if (binary != null)
                {
                    int i = 0;
                    if (binary.Left != null)
                    {
                        binary.Left._Visit(this, action, i++, level + 1);
                    }
                    if (binary.Right != null)
                    {
                        binary.Right._Visit(this, action, i, level + 1);
                    }
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Visits each element in the AST
        /// </summary>
        /// <param name="action">The anonymous method to call for each element</param>
        public void Visit(RegexVisitAction action)
        {
            _Visit(null, action, 0, 0);
        }

        /// <summary>
        /// Creates a copy of the expression
        /// </summary>
        /// <returns>A copy of the expression</returns>
        protected abstract RegexExpression CloneImpl();
        object ICloneable.Clone() => CloneImpl();
        /// <summary>
        /// Clones the expression
        /// </summary>
        /// <returns>A deep copy of the expression</returns>
        public RegexExpression Clone()
        {
            return CloneImpl();
        }

        /// <summary>
        /// Appends the textual representation to a <see cref="StringBuilder"/>
        /// </summary>
        /// <param name="sb">The string builder to use</param>
        /// <remarks>Used by ToString()</remarks>
        protected internal abstract void AppendTo(StringBuilder sb);
        /// <summary>
        /// Gets a textual representation of the expression
        /// </summary>
        /// <returns>A string representing the expression</returns>
        public override string ToString()
        {
            var result = new StringBuilder();
            AppendTo(result);
            return result.ToString();
        }
        public Dfa ToDfa()
        {
            return DfaBuilder.BuildDfa(this);
        }
        /// <summary>
        /// Parses a regular expression from the specified string
        /// </summary>
        /// <param name="expression">The expression to parse</param>
        /// <returns>A new abstract syntax tree representing the expression</returns>
        public static RegexExpression? Parse(string expression)
        {
            StringCursor pc = new StringCursor();
            pc.Input = expression;
            return _Parse(pc);
        }
        
        private static RegexExpression? _Parse(StringCursor pc)
        {
            var result = new RegexLexerExpression();
            pc.EnsureStarted();
            result.SetLocation(pc.Position);
            var comments = new List<RegexComment>();
            RegexExpression? expr;
            var sc = new StringCursor();

            do
            {
                pc.CaptureBuffer.Clear();
                int pos = pc.Position;
                pc.TryReadUntil('\n');
                var line = pc.CaptureBuffer.ToString();
                if (line.EndsWith('\n'))
                {
                    line = line.Substring(0, line.Length - 1);
                }
                if (line.EndsWith('\r'))
                {
                    line = line.Substring(0, line.Length - 1);
                }
                if (line.StartsWith("#") && (line.Length == 1 || char.IsWhiteSpace(line[1]))) // is comment
                {
                    RegexComment comment = new RegexComment();
                    comment.SetPosition(pos);
                    if (line.Length > 1)
                    {
                        comment.Text = line.Substring(2);
                    }
                    comments.Add(comment);
                }
                else // is expression
                {
                    sc.Codepoint = -2;
                    sc.StartPosition = pos;
                    sc.Position = pos - 1;
                    sc.Input = line;
                    expr = _ParseExpr(sc);
                    if (expr != null)
                    {
                        expr.Comments.AddRange(comments);
                        comments.Clear();
                        result.Rules.Add(expr);
                    }
                }
            } while (pc.Codepoint != -1);

            return result.Rules.Count switch
            {
                0 => null,
                1 => result.Rules[0],
                _ => result,
            };
        }
        private static RegexExpression? _ParseExpr(StringCursor pc)
        {
            RegexExpression? result = null, next = null;
            int ich;
            bool cap;
            string nmgrp=null;
            pc.EnsureStarted();
            var position = pc.Position;
            while (true)
            {
                switch (pc.Codepoint)
                {
                    case '^':
                        pc.Advance();
                        var lsanch = new RegexAnchorExpression(RegexAnchorType.LineStart);
                        lsanch.SetLocation(position);
                        if (null == result)
                            result = lsanch;
                        else
                        {
                            result = new RegexConcatExpression(result, lsanch);
                            result.SetLocation(position);

                        }
                        break;
                    case '$':
                        pc.Advance();
                        var leanch = new RegexAnchorExpression(RegexAnchorType.LineEnd);
                        leanch.SetLocation(position);
                        if (null == result)
                            result = leanch;
                        else
                        {
                            result = new RegexConcatExpression(result, leanch);
                            result.SetLocation(position);

                        }
                        break;
                    case '\n':
                        pc.Advance();
                        return result;
                    case -1:
                        return result;
                    case '.':
                        RegexExpression nset = new RegexCharsetExpression(new RegexCharsetEntry[] { new RegexCharsetRangeEntry(0, ((int)'\n') - 1), new RegexCharsetRangeEntry('\n' + 1, 0x10ffff) }, false);
                        nset.SetLocation(position);
                        pc.Advance();
                        nset = _ParseModifier(nset, pc);
                        if (null == result)
                            result = nset;
                        else
                        {
                            result = new RegexConcatExpression(result, nset);
                            result.SetLocation(position);
                        }
                        position = pc.Position;
                        break;
                    case '\\':

                        pc.Advance();
                        pc.Expecting();
                        switch (pc.Codepoint)
                        {
                            case 'd':
                                next = new RegexCharsetExpression(new RegexCharsetEntry[] { new RegexCharsetClassEntry("digit") });
                                pc.Advance();
                                break;
                            case 'D':
                                next = new RegexCharsetExpression(new RegexCharsetEntry[] { new RegexCharsetClassEntry("digit") }, true);
                                pc.Advance();
                                break;
                            case 'h':
                                next = new RegexCharsetExpression(new RegexCharsetEntry[] { new RegexCharsetClassEntry("blank") });
                                pc.Advance();
                                break;
                            case 'l':
                                next = new RegexCharsetExpression(new RegexCharsetEntry[] { new RegexCharsetClassEntry("lower") });
                                pc.Advance();
                                break;
                            case 's':
                                next = new RegexCharsetExpression(new RegexCharsetEntry[] { new RegexCharsetClassEntry("space") });
                                pc.Advance();
                                break;
                            case 'S':
                                next = new RegexCharsetExpression(new RegexCharsetEntry[] { new RegexCharsetClassEntry("space") }, true);
                                pc.Advance();
                                break;
                            case 'u':
                                next = new RegexCharsetExpression(new RegexCharsetEntry[] { new RegexCharsetClassEntry("upper") });
                                pc.Advance();
                                break;
                            case 'w':
                                next = new RegexCharsetExpression(new RegexCharsetEntry[] { new RegexCharsetClassEntry("word") });
                                pc.Advance();
                                break;
                            case 'W':
                                next = new RegexCharsetExpression(new RegexCharsetEntry[] { new RegexCharsetClassEntry("word") }, true);
                                pc.Advance();
                                break;
                            default:
                                if (-1 != (ich = _ParseEscapePart(pc)))
                                {

                                    next = new RegexLiteralExpression(ich);

                                }
                                else
                                {
                                    pc.Expecting(); // throw an error
                                    return null; // doesn't execute
                                }
                                break;
                        }
                        if (next != null)
                        {
                            next.SetLocation(position);
                            next = _ParseModifier(next, pc);
                        }
                        if (null != result)
                        {
                            result = new RegexConcatExpression(result, next);
                            result.SetLocation(position);
                        }
                        else
                            result = next;
                        position = pc.Position;
                        break;
                    case ')':
                        return result;
                    case '(':
                        pc.Advance();
                        pc.Expecting();
                        cap = true;
                        if(pc.Codepoint=='?')
                        {
                            pc.Advance();
                            if(pc.Codepoint==':')
                            {
                                pc.Advance();
                                pc.Expecting();
                                cap = false;
                            } else
                            {
                                pc.Expecting('<');
                                pc.Advance();
                                pc.CaptureBuffer.Clear();
                                pc.TryReadUntil('>', false);
                                pc.Advance();
                                nmgrp = pc.CaptureBuffer.ToString();
                                if(nmgrp.Length==0)
                                {
                                    nmgrp = null;
                                }
                            }
                        }
                        next = _ParseExpr(pc);
                        pc.Expecting(')');
                        pc.Advance();
                        next = _ParseModifier(next, pc);
                        
                        if (null == result)
                            result = next;
                        else
                        {
                            result = new RegexConcatExpression(result, next);
                            result.SetLocation(position);
                        }
                        position = pc.Position;
                        break;
                    case '|':
                        if (-1 != pc.Advance())
                        {
                            next = _ParseExpr(pc);
                            result = new RegexOrExpression(result, next);
                            result.SetLocation(position);
                        }
                        else
                        {
                            result = new RegexOrExpression(result, null);
                            result.SetLocation(position);
                        }
                        position = pc.Position;
                        break;
                    case '[':
                        pc.CaptureBuffer.Clear();
                        pc.Advance();
                        pc.Expecting();
                        bool not = false;


                        if ('^' == pc.Codepoint)
                        {
                            not = true;
                            pc.Advance();
                            pc.Expecting();
                        }
                        var ranges = _ParseRanges(pc);

                        pc.Expecting(']');
                        pc.Advance();
                        next = new RegexCharsetExpression(ranges, not);
                        next.SetLocation(position);
                        next = _ParseModifier(next, pc);

                        if (null == result)
                            result = next;
                        else
                        {
                            result = new RegexConcatExpression(result, next);
                            result.SetLocation(pc.Position);
                        }
                        position = pc.Position;
                        break;
                    default:
                        ich = pc.Codepoint;

                        next = new RegexLiteralExpression(ich);
                        next.SetLocation(position);

                        pc.Advance();
                        if (next != null)
                        {
                            next = _ParseModifier(next, pc);
                        }
                        if (null == result)
                            result = next;
                        else
                        {
                            if (next != null)
                            {
                                result = new RegexConcatExpression(result, next);
                            }
                            result.SetLocation(position);
                        }
                        position = pc.Position;
                        break;
                }
            }
        }
        static IList<RegexCharsetEntry> _ParseRanges(StringCursor pc)
        {
            pc.EnsureStarted();
            var result = new List<RegexCharsetEntry>();
            RegexCharsetEntry next = null;
            bool readDash = false;
            while (-1 != pc.Codepoint && ']' != pc.Codepoint)
            {
                switch (pc.Codepoint)
                {
                    case '[': // char partial class 
                        if (null != next)
                        {
                            result.Add(next);
                            if (readDash)
                                result.Add(new RegexCharsetCharEntry('-'));
                            result.Add(new RegexCharsetCharEntry('-'));
                        }
                        pc.Advance();
                        pc.Expecting(':');
                        pc.Advance();
                        var l = pc.CaptureBuffer.Length;
                        pc.TryReadUntil(':', false);
                        var n = pc.GetCapture(l);
                        pc.Advance();
                        pc.Expecting(']');
                        pc.Advance();
                        result.Add(new RegexCharsetClassEntry(n));
                        readDash = false;
                        next = null;
                        break;
                    case '\\':
                        pc.Advance();
                        pc.Expecting();
                        switch (pc.Codepoint)
                        {
                            case 'h':
                                _ParseCharClassEscape(pc, "space", result, ref next, ref readDash);
                                break;
                            case 'd':
                                _ParseCharClassEscape(pc, "digit", result, ref next, ref readDash);
                                break;
                            case 'D':
                                _ParseCharClassEscape(pc, "^digit", result, ref next, ref readDash);
                                break;
                            case 'l':
                                _ParseCharClassEscape(pc, "lower", result, ref next, ref readDash);
                                break;
                            case 's':
                                _ParseCharClassEscape(pc, "space", result, ref next, ref readDash);
                                break;
                            case 'S':
                                _ParseCharClassEscape(pc, "^space", result, ref next, ref readDash);
                                break;
                            case 'u':
                                _ParseCharClassEscape(pc, "upper", result, ref next, ref readDash);
                                break;
                            case 'w':
                                _ParseCharClassEscape(pc, "word", result, ref next, ref readDash);
                                break;
                            case 'W':
                                _ParseCharClassEscape(pc, "^word", result, ref next, ref readDash);
                                break;
                            default:
                                var ch = _ParseRangeEscapePart(pc);
                                if (null == next)
                                    next = new RegexCharsetCharEntry(ch);
                                else if (readDash)
                                {
                                    result.Add(new RegexCharsetRangeEntry(((RegexCharsetCharEntry)next).Codepoint, ch));
                                    next = null;
                                    readDash = false;
                                }
                                else
                                {
                                    result.Add(next);
                                    next = new RegexCharsetCharEntry(ch);
                                }

                                break;
                        }

                        break;
                    case '-':
                        pc.Advance();
                        if (null == next)
                        {
                            next = new RegexCharsetCharEntry('-');
                            readDash = false;
                        }
                        else
                        {
                            if (readDash)
                                result.Add(next);
                            readDash = true;
                        }
                        break;
                    default:
                        if (null == next)
                        {
                            next = new RegexCharsetCharEntry(pc.Codepoint);
                        }
                        else
                        {
                            if (readDash)
                            {
                                result.Add(new RegexCharsetRangeEntry(((RegexCharsetCharEntry)next).Codepoint, pc.Codepoint));
                                next = null;
                                readDash = false;
                            }
                            else
                            {
                                result.Add(next);
                                next = new RegexCharsetCharEntry(pc.Codepoint);
                            }
                        }
                        pc.Advance();
                        break;
                }
            }
            if (null != next)
            {
                result.Add(next);
                if (readDash)
                {
                    next = new RegexCharsetCharEntry('-');
                    result.Add(next);
                }
            }
            return result;
        }

        static void _ParseCharClassEscape(StringCursor pc, string cls, List<RegexCharsetEntry> result, ref RegexCharsetEntry next, ref bool readDash)
        {
            if (null != next)
            {
                result.Add(next);
                if (readDash)
                    result.Add(new RegexCharsetCharEntry('-'));
                result.Add(new RegexCharsetCharEntry('-'));
            }
            pc.Advance();
            result.Add(new RegexCharsetClassEntry(cls));
            next = null;
            readDash = false;
        }

        static RegexExpression _ParseModifier(RegexExpression expr, StringCursor pc)
        {
            var position = pc.Position;
            RegexRepeatExpression? rep = null;
            switch (pc.Codepoint)
            {
                case '*':
                    rep = new RegexRepeatExpression(expr);
                    expr = rep;
                    expr.SetLocation(position);
                    pc.Advance();
                    break;
                case '+':
                    rep= new RegexRepeatExpression(expr, 1);
                    expr = rep;
                    expr.SetLocation(position);
                    pc.Advance();
                    break;
                case '?':
                    rep = new RegexRepeatExpression(expr, 0, 1);
                    expr = rep;
                    expr.SetLocation(position);
                    pc.Advance();
                    break;
                case '{':
                    pc.Advance();
                    pc.TrySkipWhiteSpace();
                    pc.Expecting('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', ',', '}');
                    var min = -1;
                    var max = -1;
                    if (',' != pc.Codepoint && '}' != pc.Codepoint)
                    {
                        var l = pc.CaptureBuffer.Length;
                        pc.TryReadDigits();
                        min = int.Parse(pc.GetCapture(l), CultureInfo.InvariantCulture.NumberFormat);
                        pc.TrySkipWhiteSpace();
                    }
                    if (',' == pc.Codepoint)
                    {
                        pc.Advance();
                        pc.TrySkipWhiteSpace();
                        pc.Expecting('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '}');
                        if ('}' != pc.Codepoint)
                        {
                            var l = pc.CaptureBuffer.Length;
                            pc.TryReadDigits();
                            max = int.Parse(pc.GetCapture(l), CultureInfo.InvariantCulture.NumberFormat);
                            pc.TrySkipWhiteSpace();
                        }
                    }
                    else { max = min; }
                    pc.Expecting('}');
                    pc.Advance();
                    rep = new RegexRepeatExpression(expr, min, max);
                    expr = rep;
                    expr.SetLocation(position);
                    break;
            }
            if (pc.Codepoint == '?' && rep!=null)
            {
                rep.IsLazy = true;
                pc.Advance();
            }
            return expr;
        }
        /// <summary>
        /// Appends a character escape to the specified <see cref="StringBuilder"/>
        /// </summary>
        /// <param name="character">The character to escape</param>
        /// <param name="builder">The string builder to append to</param>
        internal static void AppendEscapedChar(string character, StringBuilder builder)
        {
            int codepoint = char.ConvertToUtf32(character, 0);
            switch (codepoint)
            {
                case '.':
                case '/': // js expects this
                case '(':
                case ')':
                case '[':
                case ']':
                case '<': // flex expects this
                case '>':
                case '|':
                case ';': // flex expects this
                case '\'': // pck expects this
                case '\"':
                case '{':
                case '}':
                case '?':
                case '*':
                case '+':
                case '$':
                case '^':
                case '\\':
                    builder.Append('\\');
                    builder.Append(character);
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
                    if (!char.IsLetterOrDigit(character, 0) && !char.IsSeparator(character, 0) && !char.IsPunctuation(character, 0) && !char.IsSymbol(character, 0))
                    {

                        builder.Append("\\u");
                        builder.Append(unchecked(codepoint.ToString("x4")));
                    }
                    else
                        builder.Append(character);
                    break;
            }

        }
        /// <summary>
        /// Escapes the specified character
        /// </summary>
        /// <param name="character">The character to escape</param>
        /// <returns>A string representing the escaped character</returns>
        internal static string EscapeChar(string character)
        {
            var codepoint = char.ConvertToUtf32(character, 0);
            switch (codepoint)
            {
                case '.':
                case '/': // js expects this
                case '(':
                case ')':
                case '[':
                case ']':
                case '<': // flex expects this
                case '>':
                case '|':
                case ';': // flex expects this
                case '\'': // pck expects this
                case '\"':
                case '{':
                case '}':
                case '?':
                case '*':
                case '+':
                case '$':
                case '^':
                case '\\':
                    return string.Concat("\\", character);
                case '\t':
                    return "\\t";
                case '\n':
                    return "\\n";
                case '\r':
                    return "\\r";
                case '\0':
                    return "\\0";
                case '\f':
                    return "\\f";
                case '\v':
                    return "\\v";
                case '\b':
                    return "\\b";
                default:
                    if (!char.IsLetterOrDigit(character, 0) && !char.IsSeparator(character, 0) && !char.IsPunctuation(character, 0) && !char.IsSymbol(character, 0))
                    {

                        return string.Concat("\\x", unchecked(codepoint).ToString("x4"));

                    }
                    else
                        return string.Concat(character);
            }
        }
        /// <summary>
        /// Appends an escaped range character to the specified <see cref="StringBuilder"/>
        /// </summary>
        /// <param name="rangeCharacter">The range character to escape</param>
        /// <param name="builder">The string builder to append to</param>
        internal static void AppendEscapedRangeChar(string rangeCharacter, StringBuilder builder)
        {
            var codepoint = char.ConvertToUtf32(rangeCharacter, 0);
            switch (codepoint)
            {
                case '.':
                case '/': // js expects this
                case '(':
                case ')':
                case '[':
                case ']':
                case '<': // flex expects this
                case '>':
                case '|':
                case ':': // expected by posix (sort of, Posix doesn't allow escapes in ranges, but standard extensions do)
                case ';': // flex expects this
                case '\'': // pck expects this
                case '\"':
                case '{':
                case '}':
                case '?':
                case '*':
                case '+':
                case '$':
                case '^':
                case '-':
                case '\\':
                    builder.Append('\\');
                    builder.Append(rangeCharacter);
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
                    if (!char.IsLetterOrDigit(rangeCharacter, 0) && !char.IsSeparator(rangeCharacter, 0) && !char.IsPunctuation(rangeCharacter, 0) && !char.IsSymbol(rangeCharacter, 0))
                    {

                        builder.Append("\\u");
                        builder.Append(unchecked(codepoint).ToString("x4"));

                    }
                    else
                        builder.Append(rangeCharacter);
                    break;
            }
        }
        /// <summary>
        /// Escapes a range character
        /// </summary>
        /// <param name="character">The character to escape</param>
        /// <returns>A string containing the escaped character</returns>
        internal static string EscapeRangeChar(string character)
        {
            var codepoint = char.ConvertToUtf32(character, 0);
            switch (codepoint)
            {
                case '.':
                case '/': // js expects this
                case '(':
                case ')':
                case '[':
                case ']':
                case '<': // flex expects this
                case '>':
                case '|':
                case ':': // expected by posix (sort of, Posix doesn't allow escapes in ranges, but standard extensions do)
                case ';': // flex expects this
                case '\'': // pck expects this
                case '\"':
                case '{':
                case '}':
                case '?':
                case '*':
                case '+':
                case '$':
                case '^':
                case '-':
                case '\\':
                    return string.Concat("\\", character);
                case '\t':
                    return "\\t";
                case '\n':
                    return "\\n";
                case '\r':
                    return "\\r";
                case '\0':
                    return "\\0";
                case '\f':
                    return "\\f";
                case '\v':
                    return "\\v";
                case '\b':
                    return "\\b";
                default:
                    if (!char.IsLetterOrDigit(character, 0) && !char.IsSeparator(character, 0) && !char.IsPunctuation(character, 0) && !char.IsSymbol(character, 0))
                    {

                        return string.Concat("\\x", unchecked(codepoint).ToString("x4"));

                    }
                    else
                        return string.Concat(character);
            }
        }
        static byte _FromHexChar(int hex)
        {
            if (':' > hex && '/' < hex)
                return (byte)(hex - '0');
            if ('G' > hex && '@' < hex)
                return (byte)(hex - '7'); // 'A'-10
            if ('g' > hex && '`' < hex)
                return (byte)(hex - 'W'); // 'a'-10
            throw new ArgumentException("The value was not hex.", "hex");
        }
        static bool _IsHexChar(int hex)
        {
            if (':' > hex && '/' < hex)
                return true;
            if ('G' > hex && '@' < hex)
                return true;
            if ('g' > hex && '`' < hex)
                return true;
            return false;
        }
        // return type is either char or ranges. this is kind of a union return type.
        static int _ParseEscapePart(StringCursor pc)
        {
            if (-1 == pc.Codepoint) return -1;
            switch (pc.Codepoint)
            {
                case 'f':
                    pc.Advance();
                    return '\f';
                case 'v':
                    pc.Advance();
                    return '\v';
                case 't':
                    pc.Advance();
                    return '\t';
                case 'n':
                    pc.Advance();
                    return '\n';
                case 'r':
                    pc.Advance();
                    return '\r';
                case 'x':
                    if (-1 == pc.Advance() || !_IsHexChar(pc.Codepoint))
                        return 'x';
                    byte b = _FromHexChar(pc.Codepoint);
                    if (-1 == pc.Advance() || !_IsHexChar(pc.Codepoint))
                        return unchecked(b);
                    b <<= 4;
                    b |= _FromHexChar(pc.Codepoint);
                    if (-1 == pc.Advance() || !_IsHexChar(pc.Codepoint))
                        return unchecked(b);
                    b <<= 4;
                    b |= _FromHexChar(pc.Codepoint);
                    if (-1 == pc.Advance() || !_IsHexChar(pc.Codepoint))
                        return unchecked(b);
                    b <<= 4;
                    b |= _FromHexChar(pc.Codepoint);
                    return unchecked(b);
                case 'u':
                    if (-1 == pc.Advance())
                        return 'u';
                    ushort u = _FromHexChar(pc.Codepoint);
                    u <<= 4;
                    if (-1 == pc.Advance())
                        return unchecked(u);
                    u |= _FromHexChar(pc.Codepoint);
                    u <<= 4;
                    if (-1 == pc.Advance())
                        return unchecked(u);
                    u |= _FromHexChar(pc.Codepoint);
                    u <<= 4;
                    if (-1 == pc.Advance())
                        return unchecked(u);
                    u |= _FromHexChar(pc.Codepoint);
                    return unchecked(u);
                default:
                    int i = pc.Codepoint;
                    pc.Advance();
                    return i;
            }
        }
        static int _ParseRangeEscapePart(StringCursor pc)
        {
            if (-1 == pc.Codepoint)
                return -1;
            switch (pc.Codepoint)
            {
                case 'f':
                    pc.Advance();
                    return '\f';
                case 'v':
                    pc.Advance();
                    return '\v';
                case 't':
                    pc.Advance();
                    return '\t';
                case 'n':
                    pc.Advance();
                    return '\n';
                case 'r':
                    pc.Advance();
                    return '\r';
                case 'x':
                    if (-1 == pc.Advance() || !_IsHexChar(pc.Codepoint))
                        return 'x';
                    byte b = _FromHexChar(pc.Codepoint);
                    if (-1 == pc.Advance() || !_IsHexChar(pc.Codepoint))
                        return unchecked(b);
                    b <<= 4;
                    b |= _FromHexChar(pc.Codepoint);
                    if (-1 == pc.Advance() || !_IsHexChar(pc.Codepoint))
                        return unchecked(b);
                    b <<= 4;
                    b |= _FromHexChar(pc.Codepoint);
                    if (-1 == pc.Advance() || !_IsHexChar(pc.Codepoint))
                        return unchecked(b);
                    b <<= 4;
                    b |= _FromHexChar(pc.Codepoint);
                    return unchecked(b);
                case 'u':
                    if (-1 == pc.Advance())
                        return 'u';
                    ushort u = _FromHexChar(pc.Codepoint);
                    u <<= 4;
                    if (-1 == pc.Advance())
                        return unchecked(u);
                    u |= _FromHexChar(pc.Codepoint);
                    u <<= 4;
                    if (-1 == pc.Advance())
                        return unchecked(u);
                    u |= _FromHexChar(pc.Codepoint);
                    u <<= 4;
                    if (-1 == pc.Advance())
                        return unchecked(u);
                    u |= _FromHexChar(pc.Codepoint);
                    return unchecked(u);
                default:
                    int i = pc.Codepoint;
                    pc.Advance();
                    return i;
            }
        }
    }
    /// <summary>
    /// Represents a special terminator node
    /// </summary>
    partial class RegexTerminatorExpression : RegexExpression
    {
        public override bool IsLeaf => true;
        /// <summary>
        /// Indicates whether or not this statement is a single element or not
        /// </summary>
        /// <remarks>If false, this statement will be wrapped in parentheses if necessary</remarks>
        public override bool IsSingleElement => true;
        /// <summary>
        /// Indicates whether or not this statement is a empty element or not
        /// </summary>
        public override bool IsEmptyElement => false;

        /// <summary>
        /// Creates a terminator expression with the specified codepoint
        /// </summary>
        public RegexTerminatorExpression() { }

        /// <summary>
        /// Appends the textual representation to a <see cref="StringBuilder"/>
        /// </summary>
        /// <param name="sb">The string builder to use</param>
        /// <remarks>Used by ToString()</remarks>
        protected internal override void AppendTo(StringBuilder sb)
        {
            sb.Append("<<END>>");

        }

        /// <summary>
        /// Creates a new copy of this expression
        /// </summary>
        /// <returns>A new copy of this expression</returns>
        protected override RegexExpression CloneImpl()
            => Clone();
        /// <summary>
        /// Creates a new copy of this expression
        /// </summary>
        /// <returns>A new copy of this expression</returns>
        public new RegexTerminatorExpression Clone()
        {
            return new RegexTerminatorExpression();
        }

    }
    /// <summary>
    /// Represents a comment node
    /// </summary>
    partial class RegexComment : ICloneable, IEquatable<RegexComment>
    {
        public long Position { get; private set; } = -1;
        public void SetPosition(long position)
        {
            Position = position;
        }
        public string? Text { get; set; } = null;
        /// <summary>
        /// Creates a terminator expression with the specified codepoint
        /// </summary>
        public RegexComment(string? text = null) {
            Text = text;
        }

        /// <summary>
        /// Provides a string representation of the comment
        /// </summary>
        public override string ToString()
        {
            if(string.IsNullOrEmpty(Text))
            {
                return $"#{Environment.NewLine}";
            }
            var sr = new StringReader(Text);
            var sb = new StringBuilder((int)(Text.Length*1.5));
            for(var line = sr.ReadLine();line!=null;line = sr.ReadLine())
            {
                sb.AppendLine($"# {line}");
            }
            return sb.ToString();  
        }
        /// <summary>
        /// Creates a new copy of this expression
        /// </summary>
        /// <returns>A new copy of this expression</returns>
        public RegexComment Clone()
        {
            return new RegexComment(Text);
        }
        object ICloneable.Clone()
        {
            return Clone();
        }
        #region Value semantics
        /// <summary>
        /// Indicates whether this expression is the same as the right hand expression
        /// </summary>
        /// <param name="rhs">The expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public bool Equals(RegexComment? rhs)
        {
            if (ReferenceEquals(rhs, this)) return true;
            if (ReferenceEquals(rhs, null)) return false;
            if (Position != rhs.Position) return false;
            return (string.IsNullOrEmpty(Text) && string.IsNullOrEmpty(rhs.Text)) || 0==string.CompareOrdinal(Text, rhs.Text);
        }
        /// <summary>
        /// Indicates whether this expression is the same as the right hand expression
        /// </summary>
        /// <param name="rhs">The expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public override bool Equals(object? rhs)
            => Equals(rhs as RegexLiteralExpression);
        /// <summary>
        /// Computes a hash code for this expression
        /// </summary>
        /// <returns>A hash code for this expression</returns>
        public override int GetHashCode()
            => Position.GetHashCode() ^ (!string.IsNullOrEmpty(Text) ? Text.GetHashCode() : 0);
        /// <summary>
        /// Indicates whether or not two expression are the same
        /// </summary>
        /// <param name="lhs">The left hand expression to compare</param>
        /// <param name="rhs">The right hand expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public static bool operator ==(RegexComment lhs, RegexComment rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return true;
            if (ReferenceEquals(lhs, null)) return false;
            return lhs.Equals(rhs);
        }
        /// <summary>
        /// Indicates whether or not two expression are different
        /// </summary>
        /// <param name="lhs">The left hand expression to compare</param>
        /// <param name="rhs">The right hand expression to compare</param>
        /// <returns>True if the expressions are different, otherwise false</returns>
        public static bool operator !=(RegexComment lhs, RegexComment rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return false;
            if (ReferenceEquals(lhs, null)) return true;
            return !lhs.Equals(rhs);
        }
        #endregion

    }
    /// <summary>
    /// Represents a binary expression
    /// </summary>

    abstract partial class RegexBinaryExpression : RegexExpression
    {
        /// <summary>
        /// Indicates the left expression
        /// </summary>
        public RegexExpression? Left { get; set; } = null;
        public RegexExpression? Right { get; set; } = null;
    }
    /// <summary>
    /// Represents an expression with a single target expression
    /// </summary>

    abstract partial class RegexUnaryExpression : RegexExpression
    {
        /// <summary>
        /// Indicates the target expression
        /// </summary>
        public RegexExpression? Expression { get; set; } = null;

    }
    /// <summary>
    /// Represents a single character literal
    /// </summary>
    partial class RegexLexerExpression : RegexExpression, IEquatable<RegexLexerExpression>
    {
        /// <summary>
        /// Indicates whether or not this statement is a single element or not
        /// </summary>
        /// <remarks>If false, this statement will be wrapped in parentheses if necessary</remarks>
        public override bool IsSingleElement => Rules.Count == 1 && Rules[0].IsSingleElement;
        /// <summary>
        /// Indicates whether or not this statement is a empty element or not
        /// </summary>
        public override bool IsEmptyElement => Rules.Count == 0;
        public override bool IsLeaf => false;
        /// <summary>
        /// Indicates the rules in this expression
        /// </summary>
        public List<RegexExpression> Rules { get; } = new List<RegexExpression>();
        
        /// <summary>
        /// Creates a lexer expression with the specified rules
        /// </summary>
        /// <param name="rules">The rules/param>
        public RegexLexerExpression(IEnumerable<RegexExpression> rules) { Rules.AddRange(rules); }
        /// <summary>
        /// Creates a lexer expression with the specified rules
        /// </summary>
        /// <param name="rules">The rules/param>
        public RegexLexerExpression(params RegexExpression[] rules) { if(rules.Length>0) Rules.AddRange(rules); }

        /// <summary>
        /// Appends the textual representation to a <see cref="StringBuilder"/>
        /// </summary>
        /// <param name="sb">The string builder to use</param>
        /// <remarks>Used by ToString()</remarks>
        protected internal override void AppendTo(StringBuilder sb)
        {
            foreach(var expr in Rules)
            {
                expr.AppendTo(sb);
                sb.AppendLine();
            }
        }

        /// <summary>
        /// Creates a new copy of this expression
        /// </summary>
        /// <returns>A new copy of this expression</returns>
        protected override RegexExpression CloneImpl()
            => Clone();
        /// <summary>
        /// Creates a new copy of this expression
        /// </summary>
        /// <returns>A new copy of this expression</returns>
        public new RegexLexerExpression Clone()
        {
            var rules = new List<RegexExpression>(Rules.Count);
            foreach(var rule in Rules)
            {
                rules.Add(rule.Clone());
            }
            return new RegexLexerExpression(rules);
        }

        #region Value semantics
        /// <summary>
        /// Indicates whether this expression is the same as the right hand expression
        /// </summary>
        /// <param name="rhs">The expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public bool Equals(RegexLexerExpression? rhs)
        {
            if (ReferenceEquals(rhs, this)) return true;
            if (ReferenceEquals(rhs, null)) return false;
            if (Position != rhs.Position) return false;
            if(Rules.Count != rhs.Rules.Count) return false;
            for(int i = 0;i< Rules.Count;i++)
            {
                if (!Rules[i].Equals(rhs.Rules[i])) return false;
            }
            return true;
        }
        /// <summary>
        /// Indicates whether this expression is the same as the right hand expression
        /// </summary>
        /// <param name="rhs">The expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public override bool Equals(object? rhs)
            => Equals(rhs as RegexLexerExpression);
        /// <summary>
        /// Computes a hash code for this expression
        /// </summary>
        /// <returns>A hash code for this expression</returns>
        public override int GetHashCode()
        {
            var result = Position.GetHashCode();
            foreach(var rule in Rules)
            {
                result ^= rule.GetHashCode();
            }
            return result;
        }
        /// <summary>
        /// Indicates whether or not two expression are the same
        /// </summary>
        /// <param name="lhs">The left hand expression to compare</param>
        /// <param name="rhs">The right hand expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public static bool operator ==(RegexLexerExpression lhs, RegexLexerExpression rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return true;
            if (ReferenceEquals(lhs, null)) return false;
            return lhs.Equals(rhs);
        }
        /// <summary>
        /// Indicates whether or not two expression are different
        /// </summary>
        /// <param name="lhs">The left hand expression to compare</param>
        /// <param name="rhs">The right hand expression to compare</param>
        /// <returns>True if the expressions are different, otherwise false</returns>
        public static bool operator !=(RegexLexerExpression lhs, RegexLexerExpression rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return false;
            if (ReferenceEquals(lhs, null)) return true;
            return !lhs.Equals(rhs);
        }
        #endregion

    }
    /// <summary>
    /// Represents a single character literal
    /// </summary>
    partial class RegexLiteralExpression : RegexExpression, IEquatable<RegexLiteralExpression>
    {
        /// <summary>
        /// Indicates whether or not this statement is a single element or not
        /// </summary>
        /// <remarks>If false, this statement will be wrapped in parentheses if necessary</remarks>
        public override bool IsSingleElement => Codepoint != -1;
        /// <summary>
        /// Indicates whether or not this statement is a empty element or not
        /// </summary>
        public override bool IsEmptyElement => Codepoint == -1;
        public override bool IsLeaf => true;
        /// <summary>
        /// Indicates the codepoint in this expression
        /// </summary>
        public int Codepoint { get; set; } = -1;
        /// <summary>
        /// Indicates the string literal of this expression
        /// </summary>
        public string Value
        {
            get
            {
                if (Codepoint == -1)
                {
                    return "";
                }
                return char.ConvertFromUtf32(Codepoint);
            }
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    Codepoint = -1;
                    return;
                }
                Codepoint = ToUtf32(value).First();
            }
        }
        public override IList<DfaRange> GetRanges()
        {
            if (Codepoint == -1) return base.GetRanges();
            return new DfaRange[] { new DfaRange(Codepoint, Codepoint) };
        }
        /// <summary>
        /// Creates a literal expression with the specified codepoint
        /// </summary>
        /// <param name="codepoint">The codepoint to represent</param>
        public RegexLiteralExpression(int codepoint) { Codepoint = codepoint; }

        /// <summary>
        /// Creates a literal expression with the specified string
        /// </summary>
        /// <param name="string">The string to represent</param>
        public RegexLiteralExpression(string @string) { Value = @string; }
        /// <summary>
        /// Creates a default instance of the expression
        /// </summary>
        public RegexLiteralExpression() { }

        public static RegexExpression CreateString(IEnumerable<char> value)
        {
            var exprs = new List<RegexLiteralExpression>();
            foreach(var cp in ToUtf32(value))
            {
                exprs.Add(new RegexLiteralExpression(cp));
            }
            return RegexConcatExpression.CreateChain(exprs.ToArray());
        }
        /// <summary>
        /// Appends the textual representation to a <see cref="StringBuilder"/>
        /// </summary>
        /// <param name="sb">The string builder to use</param>
        /// <remarks>Used by ToString()</remarks>
        protected internal override void AppendTo(StringBuilder sb)
        {
            if (Codepoint == -1)
            {
                return;
            }
            AppendEscapedChar(char.ConvertFromUtf32(Codepoint), sb);
            
        }
       
        /// <summary>
        /// Creates a new copy of this expression
        /// </summary>
        /// <returns>A new copy of this expression</returns>
        protected override RegexExpression CloneImpl()
            => Clone();
        /// <summary>
        /// Creates a new copy of this expression
        /// </summary>
        /// <returns>A new copy of this expression</returns>
        public new RegexLiteralExpression Clone()
        {
            return new RegexLiteralExpression(Value);
        }

        #region Value semantics
        /// <summary>
        /// Indicates whether this expression is the same as the right hand expression
        /// </summary>
        /// <param name="rhs">The expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public bool Equals(RegexLiteralExpression? rhs)
        {
            if (ReferenceEquals(rhs, this)) return true;
            if (ReferenceEquals(rhs, null)) return false;
            if(Position!=rhs.Position) return false;
            return Codepoint == rhs.Codepoint;
        }
        /// <summary>
        /// Indicates whether this expression is the same as the right hand expression
        /// </summary>
        /// <param name="rhs">The expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public override bool Equals(object? rhs)
            => Equals(rhs as RegexLiteralExpression);
        /// <summary>
        /// Computes a hash code for this expression
        /// </summary>
        /// <returns>A hash code for this expression</returns>
        public override int GetHashCode()
            => Position.GetHashCode() ^ ((Value != null) ? Value.GetHashCode() : 0);
        /// <summary>
        /// Indicates whether or not two expression are the same
        /// </summary>
        /// <param name="lhs">The left hand expression to compare</param>
        /// <param name="rhs">The right hand expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public static bool operator ==(RegexLiteralExpression lhs, RegexLiteralExpression rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return true;
            if (ReferenceEquals(lhs, null)) return false;
            return lhs.Equals(rhs);
        }
        /// <summary>
        /// Indicates whether or not two expression are different
        /// </summary>
        /// <param name="lhs">The left hand expression to compare</param>
        /// <param name="rhs">The right hand expression to compare</param>
        /// <returns>True if the expressions are different, otherwise false</returns>
        public static bool operator !=(RegexLiteralExpression lhs, RegexLiteralExpression rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return false;
            if (ReferenceEquals(lhs, null)) return true;
            return !lhs.Equals(rhs);
        }
        #endregion

    }

    /// <summary>
    /// Represents an anchor
    /// </summary>
    partial class RegexAnchorExpression : RegexExpression, IEquatable<RegexAnchorExpression>
    {
        const int START_ANCHOR = -2;   // ^
        const int END_ANCHOR = -3;     // $
        /// <summary>
        /// Indicates whether or not this statement is a single element or not
        /// </summary>
        /// <remarks>If false, this statement will be wrapped in parentheses if necessary</remarks>
        public override bool IsSingleElement => true;
        /// <summary>
        /// Indicates whether or not this statement is a empty element or not
        /// </summary>
        public override bool IsEmptyElement => false;
        public override bool IsLeaf => true;
        /// <summary>
        /// Indicates the anchor type of this expression
        /// </summary>
        public RegexAnchorType Type { get; set; } = RegexAnchorType.LineStart;
       
        /// <summary>
        /// Creates a literal expression with the specified codepoint
        /// </summary>
        /// <param name="type">The anchor type to represent</param>
        public RegexAnchorExpression(RegexAnchorType type= RegexAnchorType.LineStart) { Type = type; }

        /// <summary>
        /// Appends the textual representation to a <see cref="StringBuilder"/>
        /// </summary>
        /// <param name="sb">The string builder to use</param>
        /// <remarks>Used by ToString()</remarks>
        protected internal override void AppendTo(StringBuilder sb)
        {
            switch(Type)
            {
                case RegexAnchorType.LineStart:
                    sb.Append('^');
                    break;
                case RegexAnchorType.LineEnd:
                    sb.Append('$');
                    break;
            }
        }
        public int GetVirtualCodepoint()
        {
            switch (Type)
            {
                case RegexAnchorType.LineStart:
                    return START_ANCHOR;
                case RegexAnchorType.LineEnd:
                    return END_ANCHOR;
            }
            return -1;
        }
        /// <summary>
        /// Creates a new copy of this expression
        /// </summary>
        /// <returns>A new copy of this expression</returns>
        protected override RegexExpression CloneImpl()
            => Clone();
        /// <summary>
        /// Creates a new copy of this expression
        /// </summary>
        /// <returns>A new copy of this expression</returns>
        public new RegexAnchorExpression Clone()
        {
            return new RegexAnchorExpression(Type);
        }

        #region Value semantics
        /// <summary>
        /// Indicates whether this expression is the same as the right hand expression
        /// </summary>
        /// <param name="rhs">The expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public bool Equals(RegexAnchorExpression? rhs)
        {
            if (ReferenceEquals(rhs, this)) return true;
            if (ReferenceEquals(rhs, null)) return false;
            if (Position != rhs.Position) return false;
            return Type == rhs.Type;
        }
        /// <summary>
        /// Indicates whether this expression is the same as the right hand expression
        /// </summary>
        /// <param name="rhs">The expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public override bool Equals(object? rhs)
            => Equals(rhs as RegexAnchorExpression);
        /// <summary>
        /// Computes a hash code for this expression
        /// </summary>
        /// <returns>A hash code for this expression</returns>
        public override int GetHashCode()
            => Position.GetHashCode() ^ Type.GetHashCode();
        /// <summary>
        /// Indicates whether or not two expression are the same
        /// </summary>
        /// <param name="lhs">The left hand expression to compare</param>
        /// <param name="rhs">The right hand expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public static bool operator ==(RegexAnchorExpression lhs, RegexAnchorExpression rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return true;
            if (ReferenceEquals(lhs, null)) return false;
            return lhs.Equals(rhs);
        }
        /// <summary>
        /// Indicates whether or not two expression are different
        /// </summary>
        /// <param name="lhs">The left hand expression to compare</param>
        /// <param name="rhs">The right hand expression to compare</param>
        /// <returns>True if the expressions are different, otherwise false</returns>
        public static bool operator !=(RegexAnchorExpression lhs, RegexAnchorExpression rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return false;
            if (ReferenceEquals(lhs, null)) return true;
            return !lhs.Equals(rhs);
        }
        #endregion

    }
    /// <summary>
    /// Represents the base partial class for regex charset entries
    /// </summary>
    abstract partial class RegexCharsetEntry : ICloneable
    {
        /// <summary>
        /// Initializes the charset entry
        /// </summary>
        internal RegexCharsetEntry() { } // nobody can make new derivations
        /// <summary>
        /// Implements the clone method
        /// </summary>
        /// <returns>A copy of the charset entry</returns>
        protected abstract RegexCharsetEntry CloneImpl();
        /// <summary>
        /// Creates a copy of the charset entry
        /// </summary>
        /// <returns>A new copy of the charset entry</returns>
        object ICloneable.Clone() => CloneImpl();
    }
    /// <summary>
    /// Represents a character partial class charset entry
    /// </summary>
#if FALIB
	public
#endif
    partial class RegexCharsetClassEntry : RegexCharsetEntry
    {
        /// <summary>
        /// Initializes a partial class entry with the specified character partial class
        /// </summary>
        /// <param name="name">The name of the character partial class</param>
        public RegexCharsetClassEntry(string name)
        {
            Name = name;
        }
        /// <summary>
        /// Initializes a default instance of the charset entry
        /// </summary>
        public RegexCharsetClassEntry() { }
        /// <summary>
        /// Indicates the name of the character partial class
        /// </summary>
        public string? Name { get; set; }
        /// <summary>
        /// Gets a string representation of this instance
        /// </summary>
        /// <returns>The string representation of this character partial class</returns>
        public override string ToString()
        {
            return string.Concat("[:", Name, ":]");
        }
        /// <summary>
        /// Clones the object
        /// </summary>
        /// <returns>A new copy of the charset entry</returns>
        protected override RegexCharsetEntry CloneImpl()
            => Clone();
        /// <summary>
        /// Clones the object
        /// </summary>
        /// <returns>A new copy of the charset entry</returns>
        public RegexCharsetClassEntry Clone()
        {
            return new RegexCharsetClassEntry(Name);
        }

        #region Value semantics
        /// <summary>
        /// Indicates whether this charset entry is the same as the right hand charset entry
        /// </summary>
        /// <param name="rhs">The charset entry to compare</param>
        /// <returns>True if the charset entries are the same, otherwise false</returns>
        public bool Equals(RegexCharsetClassEntry? rhs)
        {
            if (ReferenceEquals(rhs, this)) return true;
            if (ReferenceEquals(rhs, null)) return false;
            return Name == rhs.Name;
        }
        /// <summary>
        /// Indicates whether this charset entry is the same as the right hand charset entry
        /// </summary>
        /// <param name="rhs">The charset entry to compare</param>
        /// <returns>True if the charset entries are the same, otherwise false</returns>
        public override bool Equals(object? rhs)
            => Equals(rhs as RegexCharsetClassEntry);
        /// <summary>
        /// Computes a hash code for this charset entry
        /// </summary>
        /// <returns>A hash code for this charset entry</returns>
        public override int GetHashCode()
        {
            if (!string.IsNullOrEmpty(Name)) return Name.GetHashCode();
            return 0;
        }
        /// <summary>
        /// Indicates whether or not two charset entries are the same
        /// </summary>
        /// <param name="lhs">The left hand charset entry to compare</param>
        /// <param name="rhs">The right hand charset entry to compare</param>
        /// <returns>True if the charset entries are the same, otherwise false</returns>
        public static bool operator ==(RegexCharsetClassEntry lhs, RegexCharsetClassEntry rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return true;
            if (ReferenceEquals(lhs, null)) return false;
            return lhs.Equals(rhs);
        }
        /// <summary>
        /// Indicates whether or not two charset entries are different
        /// </summary>
        /// <param name="lhs">The left hand charset entry to compare</param>
        /// <param name="rhs">The right hand charset entry to compare</param>
        /// <returns>True if the charset entries are different, otherwise false</returns>
        public static bool operator !=(RegexCharsetClassEntry lhs, RegexCharsetClassEntry rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return false;
            if (ReferenceEquals(lhs, null)) return true;
            return !lhs.Equals(rhs);
        }
        #endregion
    }
    /// <summary>
    /// Represents a single character charset entry
    /// </summary>

    partial class RegexCharsetCharEntry : RegexCharsetEntry, IEquatable<RegexCharsetCharEntry>
    {
        /// <summary>
        /// Initializes the entry with a character
        /// </summary>
        /// <param name="codepoint">The character to use</param>
        public RegexCharsetCharEntry(int codepoint)
        {
            Codepoint = codepoint;
        }
        /// <summary>
        /// Initializes the entry with a character
        /// </summary>
        /// <param name="character">The character to use</param>
        public RegexCharsetCharEntry(string character)
        {
            Value = character;
        }
        /// <summary>
        /// Initializes a default instance of the charset entry
        /// </summary>
        public RegexCharsetCharEntry() { }
        /// <summary>
        /// Indicates the character the charset entry represents
        /// </summary>
        public int Codepoint { get; set; }
        /// <summary>
        /// Indicates the character literal of this expression
        /// </summary>
        public string Value
        {
            get
            {
                return char.ConvertFromUtf32(Codepoint);
            }
            set
            {
                if (value == null) throw new NullReferenceException();
                if (value.Length == 0 || value.Length > 2) throw new InvalidOperationException();
                Codepoint = char.ConvertToUtf32(value, 0);
            }
        }
        /// <summary>
        /// Gets a string representation of the charset entry
        /// </summary>
        /// <returns>The string representation of this charset entry</returns>
        public override string ToString()
        {
            return RegexExpression.EscapeRangeChar(Value);
        }
        /// <summary>
        /// Clones the object
        /// </summary>
        /// <returns>A new copy of the charset entry</returns>
        protected override RegexCharsetEntry CloneImpl()
            => Clone();
        /// <summary>
        /// Clones the object
        /// </summary>
        /// <returns>A new copy of the charset entry</returns>
        public RegexCharsetCharEntry Clone()
        {
            return new RegexCharsetCharEntry(Codepoint);
        }

        #region Value semantics
        /// <summary>
        /// Indicates whether this charset entry is the same as the right hand charset entry
        /// </summary>
        /// <param name="rhs">The charset entry to compare</param>
        /// <returns>True if the charset entries are the same, otherwise false</returns>
        public bool Equals(RegexCharsetCharEntry? rhs)
        {
            if (ReferenceEquals(rhs, this)) return true;
            if (ReferenceEquals(rhs, null)) return false;
            return Codepoint == rhs.Codepoint;
        }
        /// <summary>
        /// Indicates whether this charset entry is the same as the right hand charset entry
        /// </summary>
        /// <param name="rhs">The charset entry to compare</param>
        /// <returns>True if the charset entries are the same, otherwise false</returns>
        public override bool Equals(object? rhs)
            => Equals(rhs as RegexCharsetCharEntry);
        /// <summary>
        /// Computes a hash code for this charset entry
        /// </summary>
        /// <returns>A hash code for this charset entry</returns>
        public override int GetHashCode()
            => Codepoint.GetHashCode();
        /// <summary>
        /// Indicates whether or not two charset entries are the same
        /// </summary>
        /// <param name="lhs">The left hand charset entry to compare</param>
        /// <param name="rhs">The right hand charset entry to compare</param>
        /// <returns>True if the charset entries are the same, otherwise false</returns>
        public static bool operator ==(RegexCharsetCharEntry lhs, RegexCharsetCharEntry rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return true;
            if (ReferenceEquals(lhs, null)) return false;
            return lhs.Equals(rhs);
        }
        /// <summary>
        /// Indicates whether or not two charset entries are different
        /// </summary>
        /// <param name="lhs">The left hand charset entry to compare</param>
        /// <param name="rhs">The right hand charset entry to compare</param>
        /// <returns>True if the charset entries are different, otherwise false</returns>
        public static bool operator !=(RegexCharsetCharEntry lhs, RegexCharsetCharEntry rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return false;
            if (ReferenceEquals(lhs, null)) return true;
            return !lhs.Equals(rhs);
        }
        #endregion
    }
    /// <summary>
    /// Represents a character set range entry
    /// </summary>
    partial class RegexCharsetRangeEntry : RegexCharsetEntry
    {
        /// <summary>
        /// Creates a new range entry with the specified first and last characters
        /// </summary>
        /// <param name="firstCodepoint">The first character in the range</param>
        /// <param name="lastCodepoint">The last character in the range</param>
        public RegexCharsetRangeEntry(int firstCodepoint, int lastCodepoint)
        {
            FirstCodepoint = firstCodepoint;
            LastCodepoint = lastCodepoint;
        }
        /// <summary>
        /// Creates a new range entry with the specified first and last characters
        /// </summary>
        /// <param name="first">The first character in the range</param>
        /// <param name="last">The last character in the range</param>
        public RegexCharsetRangeEntry(string first, string last)
        {
            First = first;
            Last = last;
        }
        /// <summary>
        /// Creates a default instance of the range entry
        /// </summary>
        public RegexCharsetRangeEntry()
        {
        }
        /// <summary>
        /// Indicates the first character in the range
        /// </summary>
        public int FirstCodepoint { get; set; }
        /// <summary>
        /// Indicates the first character literal of this expression
        /// </summary>
        public string First
        {
            get
            {
                return char.ConvertFromUtf32(FirstCodepoint);
            }
            set
            {
                if (value == null) throw new NullReferenceException();
                if (value.Length == 0 || value.Length > 2) throw new InvalidOperationException();
                FirstCodepoint = char.ConvertToUtf32(value, 0);
            }
        }
        /// <summary>
        /// Indicates the last character in the range
        /// </summary>
        public int LastCodepoint { get; set; }
        /// <summary>
        /// Indicates the last character literal of this expression
        /// </summary>
        public string Last
        {
            get
            {
                return char.ConvertFromUtf32(LastCodepoint);
            }
            set
            {
                if (value == null) throw new NullReferenceException();
                if (value.Length == 0 || value.Length > 2) throw new InvalidOperationException();
                LastCodepoint = char.ConvertToUtf32(value, 0);
            }
        }
        /// <summary>
        /// Clones the object
        /// </summary>
        /// <returns>A new copy of the charset entry</returns>
        protected override RegexCharsetEntry CloneImpl()
            => Clone();
        /// <summary>
        /// Clones the object
        /// </summary>
        /// <returns>A new copy of the charset entry</returns>
        public RegexCharsetRangeEntry Clone()
        {
            return new RegexCharsetRangeEntry(FirstCodepoint, LastCodepoint);
        }
        /// <summary>
        /// Gets a string representation of the charset entry
        /// </summary>
        /// <returns>The string representation of this charset entry</returns>
        public override string ToString()
        {
            return string.Concat(RegexExpression.EscapeRangeChar(First), "-", RegexExpression.EscapeRangeChar(Last));
        }
        #region Value semantics
        /// <summary>
        /// Indicates whether this charset entry is the same as the right hand charset entry
        /// </summary>
        /// <param name="rhs">The charset entry to compare</param>
        /// <returns>True if the charset entries are the same, otherwise false</returns>
        public bool Equals(RegexCharsetRangeEntry? rhs)
        {
            if (ReferenceEquals(rhs, this)) return true;
            if (ReferenceEquals(rhs, null)) return false;
            return FirstCodepoint == rhs.FirstCodepoint && LastCodepoint == rhs.LastCodepoint;
        }
        /// <summary>
        /// Indicates whether this charset entry is the same as the right hand charset entry
        /// </summary>
        /// <param name="rhs">The charset entry to compare</param>
        /// <returns>True if the charset entries are the same, otherwise false</returns>
        public override bool Equals(object? rhs)
            => Equals(rhs as RegexCharsetRangeEntry);
        /// <summary>
        /// Computes a hash code for this charset entry
        /// </summary>
        /// <returns>A hash code for this charset entry</returns>
        public override int GetHashCode()
            => FirstCodepoint.GetHashCode() ^ LastCodepoint.GetHashCode();
        /// <summary>
        /// Indicates whether or not two charset entries are the same
        /// </summary>
        /// <param name="lhs">The left hand charset entry to compare</param>
        /// <param name="rhs">The right hand charset entry to compare</param>
        /// <returns>True if the charset entries are the same, otherwise false</returns>
        public static bool operator ==(RegexCharsetRangeEntry lhs, RegexCharsetRangeEntry rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return true;
            if (ReferenceEquals(lhs, null)) return false;
            return lhs.Equals(rhs);
        }
        /// <summary>
        /// Indicates whether or not two charset entries are different
        /// </summary>
        /// <param name="lhs">The left hand charset entry to compare</param>
        /// <param name="rhs">The right hand charset entry to compare</param>
        /// <returns>True if the charset entries are different, otherwise false</returns>
        public static bool operator !=(RegexCharsetRangeEntry lhs, RegexCharsetRangeEntry rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return false;
            if (ReferenceEquals(lhs, null)) return true;
            return !lhs.Equals(rhs);
        }
        #endregion
    }
    /// <summary>
    /// Indicates a charset expression
    /// </summary>
    /// <remarks>Represented by [] in regular expression syntax</remarks>

    partial class RegexCharsetExpression : RegexExpression, IEquatable<RegexCharsetExpression>
    {
        public override bool IsLeaf => true;
        /// <summary>
        /// Indicates whether or not this statement is a empty element or not
        /// </summary>
        public override bool IsEmptyElement => Entries.Count == 0;
        /// <summary>
        /// Indicates the <see cref="RegexCharsetEntry"/> entries in the character set
        /// </summary>
        public IList<RegexCharsetEntry> Entries { get; } = new List<RegexCharsetEntry>();
        /// <summary>
        /// Creates a new charset expression with the specified entries and optionally negated
        /// </summary>
        /// <param name="entries">The entries to initialize the charset with</param>
        /// <param name="hasNegatedRanges">True if the range is a "not range" like [^], otherwise false</param>
        public RegexCharsetExpression(IEnumerable<RegexCharsetEntry> entries, bool hasNegatedRanges = false)
        {
            foreach (var entry in entries)
                Entries.Add(entry);
            HasNegatedRanges = hasNegatedRanges;
        }
        /// <summary>
        /// Creates a default instance of the expression
        /// </summary>
        public RegexCharsetExpression() { }
        /// <summary>
        /// Retrieve the codepoint ranges for the character set
        /// </summary>
        /// <returns></returns>
        public override IList<DfaRange> GetRanges()
        {
            var result = new List<DfaRange>();
            for (int ic = Entries.Count, i = 0; i < ic; ++i)
            {
                var entry = Entries[i];
                var crc = entry as RegexCharsetCharEntry;
                if (crc != null)
                    result.Add(new DfaRange(crc.Codepoint, crc.Codepoint));
                var crr = entry as RegexCharsetRangeEntry;
                if (null != crr)
                    result.Add(new DfaRange(crr.FirstCodepoint, crr.LastCodepoint));
                var crcl = entry as RegexCharsetClassEntry;
                if (null != crcl)
                {
                    var known = CharacterClasses.Known[crcl.Name];
                    for (int j = 0; j < known.Length; j += 2)
                    {
                        result.Add(new DfaRange(known[j], known[j + 1]));
                    }
                }
            }
            if (HasNegatedRanges)
            {
                return new List<DfaRange>(DfaRange.ToNotRanges(result));
            }
            return result;
        }
        /// <summary>
        /// Indicates whether the range is a "not range"
        /// </summary>
        /// <remarks>This is represented by the [^] regular expression syntax</remarks>
        public bool HasNegatedRanges { get; set; } = false;
        /// <summary>
        /// Indicates whether or not this statement is a single element or not
        /// </summary>
        /// <remarks>If false, this statement will be wrapped in parentheses if necessary</remarks>
        public override bool IsSingleElement => true;
        /// <summary>
        /// Appends the textual representation to a <see cref="StringBuilder"/>
        /// </summary>
        /// <param name="sb">The string builder to use</param>
        /// <remarks>Used by ToString()</remarks>
        protected internal override void AppendTo(StringBuilder sb)
        {
            // special case for "."
            if (1 == Entries.Count)
            {
                var dotE = Entries[0] as RegexCharsetRangeEntry;
                if (!HasNegatedRanges && null != dotE && dotE.FirstCodepoint == char.MinValue && dotE.LastCodepoint == char.MaxValue)
                {
                    sb.Append(".");
                    return;
                }
                var cls = Entries[0] as RegexCharsetClassEntry;
                if (null != cls)
                {
                    switch (cls.Name)
                    {
                        case "blank":
                            if (!HasNegatedRanges)
                                sb.Append(@"\h");
                            return;
                        case "digit":
                            if (!HasNegatedRanges)
                                sb.Append(@"\d");
                            else
                                sb.Append(@"\D");
                            return;
                        case "lower":
                            if (!HasNegatedRanges)
                                sb.Append(@"\l");
                            return;
                        case "space":
                            if (!HasNegatedRanges)
                                sb.Append(@"\s");
                            else
                                sb.Append(@"\S");
                            return;
                        case "upper":
                            if (!HasNegatedRanges)
                                sb.Append(@"\u");
                            return;
                        case "word":
                            if (!HasNegatedRanges)
                                sb.Append(@"\w");
                            else
                                sb.Append(@"\W");
                            return;

                    }
                }
            }

            sb.Append('[');
            if (HasNegatedRanges)
                sb.Append('^');
            for (int ic = Entries.Count, i = 0; i < ic; ++i)
                sb.Append(Entries[i]);
            sb.Append(']');
        }
        /// <summary>
        /// Creates a new copy of this expression
        /// </summary>
        /// <returns>A new copy of this expression</returns>
        protected override RegexExpression CloneImpl()
            => Clone();
        /// <summary>
        /// Creates a new copy of this expression
        /// </summary>
        /// <returns>A new copy of this expression</returns>
        public new RegexCharsetExpression Clone()
        {
            return new RegexCharsetExpression(Entries, HasNegatedRanges);
        }
        #region Value semantics
        /// <summary>
        /// Indicates whether this expression is the same as the right hand expression
        /// </summary>
        /// <param name="rhs">The expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public bool Equals(RegexCharsetExpression rhs)
        {
            if (ReferenceEquals(rhs, this)) return true;
            if (ReferenceEquals(rhs, null)) return false;
            if(Position!= rhs.Position) return false;
            if (HasNegatedRanges == rhs.HasNegatedRanges && rhs.Entries.Count == Entries.Count)
            {
                for (int ic = Entries.Count, i = 0; i < ic; ++i)
                {
                    if (!Entries[i].Equals(rhs.Entries[i]))
                    {
                        return false;
                    }
                }
                return true;
            }
            return false;
        }
        /// <summary>
        /// Indicates whether this expression is the same as the right hand expression
        /// </summary>
        /// <param name="rhs">The expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public override bool Equals(object rhs)
            => Equals(rhs as RegexCharsetExpression);
        /// <summary>
        /// Computes a hash code for this expression
        /// </summary>
        /// <returns>A hash code for this expression</returns>
        public override int GetHashCode()
        {
            var result = HasNegatedRanges.GetHashCode();
            result ^= Position.GetHashCode();
            for (int ic = Entries.Count, i = 0; i < ic; ++i)
                result ^= Entries[i].GetHashCode();
            return result;
        }
        /// <summary>
        /// Indicates whether or not two expression are the same
        /// </summary>
        /// <param name="lhs">The left hand expression to compare</param>
        /// <param name="rhs">The right hand expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public static bool operator ==(RegexCharsetExpression lhs, RegexCharsetExpression rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return true;
            if (ReferenceEquals(lhs, null)) return false;
            return lhs.Equals(rhs);
        }
        /// <summary>
        /// Indicates whether or not two expression are different
        /// </summary>
        /// <param name="lhs">The left hand expression to compare</param>
        /// <param name="rhs">The right hand expression to compare</param>
        /// <returns>True if the expressions are different, otherwise false</returns>
        public static bool operator !=(RegexCharsetExpression lhs, RegexCharsetExpression rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return false;
            if (ReferenceEquals(lhs, null)) return true;
            return !lhs.Equals(rhs);
        }
        #endregion
    }
    /// <summary>
    /// Represents a concatenation between two expression. This has no operator as it is implicit.
    /// </summary>
    partial class RegexConcatExpression : RegexBinaryExpression, IEquatable<RegexConcatExpression>
    {
        /// <summary>
        /// Indicates whether or not this statement is a single element or not
        /// </summary>
        /// <remarks>If false, this statement will be wrapped in parentheses if necessary</remarks>
        public override bool IsSingleElement
        {
            get
            {
                return ((Left != null && Right == null && Left.IsSingleElement) || (Left == null && Right != null && Right.IsSingleElement));
            }
        }
        /// <summary>
        /// Indicates whether or not this statement is a empty element or not
        /// </summary>
        public override bool IsEmptyElement => (Left == null || Left.IsEmptyElement) && (Right == null || Right.IsEmptyElement);
        /// <summary>
        /// Creates a new expression with the specified left and right hand sides
        /// </summary>
        /// <param name="expressions">The right expressions</param>
        public RegexConcatExpression(RegexExpression? left, RegexExpression? right)
        {
            Left = left;
            Right = right;
        }
        /// <summary>
        /// Creates a default instance of the expression
        /// </summary>
        public RegexConcatExpression() { }

        public static RegexExpression CreateChain(params RegexExpression[] exprs)
        {
            var result = new RegexConcatExpression();
            if (exprs.Length == 0) return result;
            if (exprs.Length == 1) { return exprs[0]; }
            var current = result;
            for (int i = 0; i < exprs.Length; i++)
            {
                if (current.Left == null)
                {
                    current.Left = exprs[i];
                }
                else if (current.Right == null)
                {
                    if (i < exprs.Length - 1)
                    {
                        current.Right = new RegexConcatExpression(exprs[i], null);
                        current = (RegexConcatExpression)current.Right;
                    } else
                    {
                        current.Right = exprs[i];
                    }
                }
            }
            return result;
        }
        /// <summary>
        /// Creates a new copy of this expression
        /// </summary>
        /// <returns>A new copy of this expression</returns>
        protected override RegexExpression CloneImpl()
            => Clone();
        /// <summary>
        /// Creates a new copy of this expression
        /// </summary>
        /// <returns>A new copy of this expression</returns>
        public new RegexConcatExpression Clone()
        {
            var result = new RegexConcatExpression();
            if (Left != null)
            {
                result.Left = Left.Clone();
            }
            if (Right != null)
            {
                result.Right = Right.Clone();
            }
            return result;
        }
        #region Value semantics
        /// <summary>
        /// Indicates whether this expression is the same as the right hand expression
        /// </summary>
        /// <param name="rhs">The expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public bool Equals(RegexConcatExpression? rhs)
        {
            if (ReferenceEquals(rhs, this)) return true;
            if (ReferenceEquals(rhs, null)) return false;
            if (Position != rhs.Position) return false;
            if ((Left == null && rhs.Left == null)||(Left!=null && Left.Equals(rhs.Left))) {
                return ((Right == null && rhs.Right == null) || (Right != null && Right.Equals(rhs.Right)));
            }
            return false;
        }
        /// <summary>
        /// Indicates whether this expression is the same as the right hand expression
        /// </summary>
        /// <param name="rhs">The expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public override bool Equals(object? rhs)
            => Equals(rhs as RegexConcatExpression);
        /// <summary>
        /// Computes a hash code for this expression
        /// </summary>
        /// <returns>A hash code for this expression</returns>
        public override int GetHashCode()
        {
            var result = Position.GetHashCode();
            if (Left != null)
            {
                result ^= Left.GetHashCode();
            }
            if (Right != null)
            {
                result ^= Right.GetHashCode();
            }
            return result;
        }
        /// <summary>
        /// Indicates whether or not two expression are the same
        /// </summary>
        /// <param name="lhs">The left hand expression to compare</param>
        /// <param name="rhs">The right hand expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public static bool operator ==(RegexConcatExpression lhs, RegexConcatExpression rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return true;
            if (ReferenceEquals(lhs, null)) return false;
            return lhs.Equals(rhs);
        }
        /// <summary>
        /// Indicates whether or not two expression are different
        /// </summary>
        /// <param name="lhs">The left hand expression to compare</param>
        /// <param name="rhs">The right hand expression to compare</param>
        /// <returns>True if the expressions are different, otherwise false</returns>
        public static bool operator !=(RegexConcatExpression lhs, RegexConcatExpression rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return false;
            if (ReferenceEquals(lhs, null)) return true;
            return !lhs.Equals(rhs);
        }
        #endregion
        /// <summary>
        /// Appends the textual representation to a <see cref="StringBuilder"/>
        /// </summary>
        /// <param name="sb">The string builder to use</param>
        /// <remarks>Used by ToString()</remarks>
        protected internal override void AppendTo(StringBuilder sb)
        {
            if (Left != null)
            {
                var oe = Left as RegexOrExpression;
                if (oe != null)
                {
                    sb.Append("(?:");
                    Left.AppendTo(sb);
                    sb.Append(")");
                }
                else
                {
                    Left.AppendTo(sb);
                }
            }
            if (Right != null)
            {
                var oe = Right as RegexOrExpression;
                if (oe != null)
                {
                    sb.Append("(?:");
                    Right.AppendTo(sb);
                    sb.Append(")");
                }
                else
                {
                    Right.AppendTo(sb);
                }
            }
        }
    }
    /// <summary>
    /// Represents an "or" regular expression as indicated by |
    /// </summary>
    partial class RegexOrExpression : RegexBinaryExpression, IEquatable<RegexOrExpression>
    {
        /// <summary>
        /// Indicates whether or not this statement is a single element or not
        /// </summary>
        /// <remarks>If false, this statement will be wrapped in parentheses if necessary</remarks>
        public override bool IsSingleElement
        {
            get
            {
                return ((Left != null && Right == null && Left.IsSingleElement) || (Left == null && Right != null && Right.IsSingleElement));
            }
        }
        /// <summary>
        /// Indicates whether or not this statement is a empty element or not
        /// </summary>
        public override bool IsEmptyElement => (Left == null || Left.IsEmptyElement) && (Right == null || Right.IsEmptyElement);
        /// <summary>
        /// Creates a new instance from a list of expressions
        /// </summary>
        /// <param name="expressions">The expressions</param>
        /// <exception cref="ArgumentNullException"><paramref name="expressions"/> was null</exception>
        /// <exception cref="ArgumentException"><paramref name="expressions"/> was empty</exception>
        public RegexOrExpression(RegexExpression? left, RegexExpression? right)
        {
            Left = left;
            Right = right;
        }

        /// <summary>
        /// Creates a default instance of the expression
        /// </summary>
        public RegexOrExpression() { }
        public static RegexExpression CreateChain(params RegexExpression[] exprs)
        {
            var result = new RegexOrExpression();
            if (exprs.Length == 0) return result;
            if (exprs.Length == 1) { return exprs[0]; }
            var current = result;
            for (int i = 0; i < exprs.Length; i++)
            {
                if (current.Left == null)
                {
                    current.Left = exprs[i];
                }
                else if (current.Right == null)
                {
                    if (i < exprs.Length - 1)
                    {
                        current.Right = new RegexOrExpression(exprs[i], null);
                        current = (RegexOrExpression)current.Right;
                    }
                    else
                    {
                        current.Right = exprs[i];
                    }
                }
            }
            return result;
        }
        /// <summary>
        /// Appends the textual representation to a <see cref="StringBuilder"/>
        /// </summary>
        /// <param name="sb">The string builder to use</param>
        /// <remarks>Used by ToString()</remarks>
        protected internal override void AppendTo(StringBuilder sb)
        {
            bool hasNull = false;
            if (Left != null && !Left.IsEmptyElement)
            {
                Left.AppendTo(sb);
            } else
            {
                hasNull = true;
            }
            if(Right != null && !Right.IsEmptyElement)
            {
                sb.Append("|");
                Right.AppendTo(sb);
            } else
            {
                hasNull = true;
            }
            if (hasNull)
            {
                sb.Append("|");
            }
        }
        /// <summary>
        /// Creates a new copy of this expression
        /// </summary>
        /// <returns>A new copy of this expression</returns>
        protected override RegexExpression CloneImpl()
            => Clone();
        /// <summary>
        /// Creates a new copy of this expression
        /// </summary>
        /// <returns>A new copy of this expression</returns>
        public new RegexOrExpression Clone()
        {
            var result = new RegexOrExpression();
            if (Left != null)
            {
                result.Left = Left.Clone();
            }
            if (Right != null)
            {
                result.Right = Right.Clone();
            }
            return result;
        }
        #region Value semantics
        private bool _Equals(RegexOrExpression? rhs)
        {
            if (ReferenceEquals(rhs, this)) return true;
            if (ReferenceEquals(rhs, null)) return false;
            if(Position!=rhs.Position) return false;
            if ((Left == null && rhs.Left == null) || (Left != null && Left.Equals(rhs.Left)))
            {
                return ((Right == null && rhs.Right == null) || (Right != null && Right.Equals(rhs.Right)));
            }
            return false;

        }
        /// <summary>
        /// Indicates whether this expression is the same as the right hand expression
        /// </summary>
        /// <param name="rhs">The expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public bool Equals(RegexOrExpression? rhs)
        {
            if (_Equals(rhs)) return true;
            // swap values and check
            var swapped = new RegexOrExpression(Right, Left);
            return swapped._Equals(rhs);
        }
        /// <summary>
        /// Indicates whether this expression is the same as the right hand expression
        /// </summary>
        /// <param name="rhs">The expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public override bool Equals(object? rhs)
            => Equals(rhs as RegexOrExpression);
        /// <summary>
        /// Computes a hash code for this expression
        /// </summary>
        /// <returns>A hash code for this expression</returns>
        public override int GetHashCode()
        {
            var result = Position.GetHashCode();
            if (Left != null)
            {
                result ^= Left.GetHashCode();
            }
            if (Right != null)
            {
                result ^= Right.GetHashCode();
            }
            return result;
        }
        /// <summary>
        /// Indicates whether or not two expression are the same
        /// </summary>
        /// <param name="lhs">The left hand expression to compare</param>
        /// <param name="rhs">The right hand expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public static bool operator ==(RegexOrExpression lhs, RegexOrExpression rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return true;
            if (ReferenceEquals(lhs, null)) return false;
            return lhs.Equals(rhs);
        }
        /// <summary>
        /// Indicates whether or not two expression are different
        /// </summary>
        /// <param name="lhs">The left hand expression to compare</param>
        /// <param name="rhs">The right hand expression to compare</param>
        /// <returns>True if the expressions are different, otherwise false</returns>
        public static bool operator !=(RegexOrExpression lhs, RegexOrExpression rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return false;
            if (ReferenceEquals(lhs, null)) return true;
            return !lhs.Equals(rhs);
        }
        #endregion

    }

    /// <summary>
    /// Represents a repeat regular expression as indicated by *, +, or {min,max}
    /// </summary>
    partial class RegexRepeatExpression : RegexUnaryExpression, IEquatable<RegexRepeatExpression>
    {
        /// <summary>
        /// Indicates whether or not this statement is a single element or not
        /// </summary>
        /// <remarks>If false, this statement will be wrapped in parentheses if necessary</remarks>
        public override bool IsSingleElement => true;
        /// <summary>
        /// Indicates whether or not this statement is a empty element or not
        /// </summary>
        public override bool IsEmptyElement => Expression == null || Expression.IsEmptyElement;
        /// <summary>
        /// Creates a repeat expression with the specifed target expression, and minimum and maximum occurances
        /// </summary>
        /// <param name="expression">The target expression</param>
        /// <param name="minOccurs">The minimum number of times the target expression can occur or -1</param>
        /// <param name="maxOccurs">The maximum number of times the target expression can occur or -1</param>
        public RegexRepeatExpression(RegexExpression? expression, int minOccurs = -1, int maxOccurs = -1, bool isLazy = false)
        {
            Expression = expression;
            MinOccurs = minOccurs;
            MaxOccurs = maxOccurs;
            IsLazy = isLazy;
        }
        /// <summary>
        /// Creates a default instance of the expression
        /// </summary>
        public RegexRepeatExpression() { }
        /// <summary>
        /// Indicates the minimum number of times the target expression can occur, or 0 or -1 for no minimum
        /// </summary>
        public int MinOccurs { get; set; } = -1;
        /// <summary>
        /// Indicates the maximum number of times the target expression can occur, or 0 or -1 for no maximum
        /// </summary>
        public int MaxOccurs { get; set; } = -1; // kleene by default

        /// <summary>
        /// Indicates whether or not this is a lazy match
        /// </summary>
        public bool IsLazy { get; set; } = false;
        /// <summary>
        /// Appends the textual representation to a <see cref="StringBuilder"/>
        /// </summary>
        /// <param name="sb">The string builder to use</param>
        /// <remarks>Used by ToString()</remarks>
        protected internal override void AppendTo(StringBuilder sb)
        {
            if(Expression == null || Expression.IsEmptyElement)
            {
                return;
            }
            var ise = Expression.IsSingleElement;
            if (!ise)
                sb.Append("(?:");
            if (null != Expression)
                Expression.AppendTo(sb);
            if (!ise)
                sb.Append(')');

            switch (MinOccurs)
            {
                case -1:
                case 0:
                    switch (MaxOccurs)
                    {
                        case -1:
                        case 0:
                            sb.Append('*');
                            break;
                        case 1:
                            sb.Append('?');
                            break;
                        default:
                            sb.Append('{');
                            if (-1 != MinOccurs)
                                sb.Append(MinOccurs);
                            sb.Append(',');
                            sb.Append(MaxOccurs);
                            sb.Append('}');
                            break;
                    }
                    break;
                case 1:
                    switch (MaxOccurs)
                    {
                        case -1:
                        case 0:
                            sb.Append('+');
                            break;
                        default:
                            sb.Append("{1,");
                            sb.Append(MaxOccurs);
                            sb.Append('}');
                            break;
                    }
                    break;
                default:
                    sb.Append('{');
                    if (MaxOccurs != MinOccurs)
                    {
                        if (-1 != MinOccurs)
                            sb.Append(MinOccurs);
                        sb.Append(',');
                        if (-1 != MaxOccurs)
                            sb.Append(MaxOccurs);
                    }
                    else
                    {
                        sb.Append(MinOccurs);
                    }
                    sb.Append('}');
                    break;
            }
            if(IsLazy)
            {
                sb.Append('?');
            }
        }
        public RegexExpression ExpandRepeats()
        {
            if ((MinOccurs < 2 && MaxOccurs<=MinOccurs) || Expression == null || Expression.IsEmptyElement)
            {
                return this;
            }

            // Handle fixed repeats (minOccurs == maxOccurs)
            if (MinOccurs == MaxOccurs)
            {
                var exprs = new List<RegexExpression>();
                for (int i = 0; i < MinOccurs; ++i)
                {
                    exprs.Add(this.Expression.Clone());
                }
                return RegexConcatExpression.CreateChain(exprs.ToArray());
            }

            // Handle variable repeats (minOccurs < maxOccurs, both > 1)
            // Create disjunction: minOccurs copies | (minOccurs+1) copies | ... | maxOccurs copies
            var alternatives = new List<RegexExpression>();

            for (int count = MinOccurs; count <= MaxOccurs; ++count)
            {
                var exprs = new List<RegexExpression>();
                for (int i = 0; i < count; ++i)
                {
                    var expr = this.Expression;
                    if (expr is RegexRepeatExpression repeat)
                    {
                        expr = repeat.ExpandRepeats();
                    } else
                    {
                        expr = expr.Clone();
                    }
                    exprs.Add(expr);
                }
                alternatives.Add(RegexConcatExpression.CreateChain(exprs.ToArray()));
            }

            // Create OR expression from all alternatives
            return RegexOrExpression.CreateChain(alternatives.ToArray());
        }
        /// <summary>
        /// Creates a new copy of this expression
        /// </summary>
        /// <returns>A new copy of this expression</returns>
        protected override RegexExpression CloneImpl()
            => Clone();
        /// <summary>
        /// Creates a new copy of this expression
        /// </summary>
        /// <returns>A new copy of this expression</returns>
        public new RegexRepeatExpression Clone()
        {
            return new RegexRepeatExpression(Expression, MinOccurs, MaxOccurs, IsLazy);
        }

        /// <summary>
        /// Indicates whether this expression is the same as the right hand expression
        /// </summary>
        /// <param name="rhs">The expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public bool Equals(RegexRepeatExpression? rhs)
        {
            if (ReferenceEquals(rhs, this)) return true;
            if (ReferenceEquals(rhs, null)) return false;
            if (Position != rhs.Position) return false;
            if(IsLazy!=rhs.IsLazy) return false;
            if (Equals(Expression, rhs.Expression))
            {
                var lmio = Math.Max(0, MinOccurs);
                var lmao = Math.Max(0, MaxOccurs);
                var rmio = Math.Max(0, rhs.MinOccurs);
                var rmao = Math.Max(0, rhs.MaxOccurs);
                return lmio == rmio && lmao == rmao;
            }
            return false;
        }
        /// <summary>
        /// Indicates whether this expression is the same as the right hand expression
        /// </summary>
        /// <param name="rhs">The expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public override bool Equals(object? rhs)
            => Equals(rhs as RegexRepeatExpression);
        /// <summary>
        /// Computes a hash code for this expression
        /// </summary>
        /// <returns>A hash code for this expression</returns>
        public override int GetHashCode()
        {
            var result = Position.GetHashCode() ^ Math.Max(MinOccurs,0) ^ Math.Max(MaxOccurs,0) ^ IsLazy.GetHashCode();
            if (null != Expression)
                return result ^ Expression.GetHashCode();
            return result;
        }
        /// <summary>
        /// Indicates whether or not two expression are the same
        /// </summary>
        /// <param name="lhs">The left hand expression to compare</param>
        /// <param name="rhs">The right hand expression to compare</param>
        /// <returns>True if the expressions are the same, otherwise false</returns>
        public static bool operator ==(RegexRepeatExpression lhs, RegexRepeatExpression rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return true;
            if (ReferenceEquals(lhs, null)) return false;
            return lhs.Equals(rhs);
        }
        /// <summary>
        /// Indicates whether or not two expression are different
        /// </summary>
        /// <param name="lhs">The left hand expression to compare</param>
        /// <param name="rhs">The right hand expression to compare</param>
        /// <returns>True if the expressions are different, otherwise false</returns>
        public static bool operator !=(RegexRepeatExpression lhs, RegexRepeatExpression rhs)
        {
            if (ReferenceEquals(lhs, rhs)) return false;
            if (ReferenceEquals(lhs, null)) return true;
            return !lhs.Equals(rhs);
        }
    }
}

