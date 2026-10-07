// netstandard2.0 shims so Builder.cs and Compiler.cs compile here without edits.

using System.ComponentModel;

namespace System.Runtime.CompilerServices
{
    // records / init accessors
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit { }
}

namespace System
{
    // Enough of System.Index for `list[^1]` (the compiler lowers it to list[list.Count - 1]).
    internal readonly struct Index : IEquatable<Index>
    {
        private readonly int _value; // negative (~n) means "from end"

        public Index(int value, bool fromEnd = false)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _value = fromEnd ? ~value : value;
        }

        public static Index Start => new(0);
        public static Index End => new(0, fromEnd: true);
        public static Index FromStart(int value) => new(value);
        public static Index FromEnd(int value) => new(value, fromEnd: true);
        public int Value => _value < 0 ? ~_value : _value;
        public bool IsFromEnd => _value < 0;
        public int GetOffset(int length) => _value < 0 ? length + _value + 1 : _value;
        public static implicit operator Index(int value) => FromStart(value);
        public bool Equals(Index other) => _value == other._value;
        public override bool Equals(object? obj) => obj is Index i && Equals(i);
        public override int GetHashCode() => _value;
        public override string ToString() => IsFromEnd ? "^" + Value : Value.ToString();
    }
}

namespace Luthor
{
    internal static class NetStandardShims
    {
        // Builder.Num() calls int.Parse(re.AsSpan(s, n)). netstandard2.0 has no
        // int.Parse(ReadOnlySpan<char>), so inside namespace Luthor `AsSpan` binds here
        // (inner namespace wins over MemoryExtensions) and returns a string instead.
        internal static string AsSpan(this string s, int start, int length) => s.Substring(start, length);
    }
}
