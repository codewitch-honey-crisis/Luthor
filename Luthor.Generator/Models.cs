using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Luthor.Generator;

// Everything that flows between pipeline stages must be value-equatable and must not hold
// symbols or syntax nodes; otherwise the incremental cache never hits.

/// <summary>An ImmutableArray with structural equality.</summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
    where T : IEquatable<T>
{
    public static readonly EquatableArray<T> Empty = new(ImmutableArray<T>.Empty);

    private readonly ImmutableArray<T> _items;
    public EquatableArray(ImmutableArray<T> items) => _items = items;
    public EquatableArray(IEnumerable<T> items) => _items = items.ToImmutableArray();

    private ImmutableArray<T> Items => _items.IsDefault ? ImmutableArray<T>.Empty : _items;
    public int Count => Items.Length;
    public T this[int index] => Items[index];

    public bool Equals(EquatableArray<T> other) => Items.SequenceEqual(other.Items);
    public override bool Equals(object? obj) => obj is EquatableArray<T> o && Equals(o);
    public override int GetHashCode()
    {
        int h = 17;
        foreach (var item in Items) h = unchecked(h * 31 + (item?.GetHashCode() ?? 0));
        return h;
    }
    public static bool operator ==(EquatableArray<T> a, EquatableArray<T> b) => a.Equals(b);
    public static bool operator !=(EquatableArray<T> a, EquatableArray<T> b) => !a.Equals(b);

    public ImmutableArray<T>.Enumerator GetEnumerator() => Items.GetEnumerator();
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)Items).GetEnumerator();
}

/// <summary>A Location stripped down to values, so it can be cached and turned back into a Location.</summary>
internal sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
{
    public Location ToLocation() => Location.Create(FilePath, Span, LineSpan);

    public static LocationInfo? From(SyntaxNode? node) => From(node?.GetLocation());
    public static LocationInfo? From(Location? location) =>
        location is { SourceTree: not null }
            ? new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span)
            : null;
}

internal sealed record DiagnosticInfo(DiagnosticDescriptor Descriptor, LocationInfo? Location, EquatableArray<string> Args)
{
    public static DiagnosticInfo Create(DiagnosticDescriptor d, LocationInfo? location, params string[] args) =>
        new(d, location, new EquatableArray<string>(args));

    public Diagnostic ToDiagnostic() =>
        Diagnostic.Create(Descriptor, Location?.ToLocation(), Args.Cast<object>().ToArray());
}

/// <summary>One type declaration in the nesting chain: <c>{Keyword} {Name}{TypeParameters}</c>.</summary>
internal sealed record TypeDecl(string Keyword, string Name, string TypeParameters);

internal sealed record RuleModel(string Name, string Pattern, bool IsLiteral, LocationInfo? NameLocation, LocationInfo? PatternLocation);

/// <summary>One <c>[LexerStream]</c> attribute: an encoding to generate byte-level tokenizers for.</summary>
internal sealed record StreamModel(string Encoding, LocationInfo? Location);

internal sealed record LexerModel(
    string? Namespace,
    EquatableArray<TypeDecl> Containers,      // outermost first
    TypeDecl Type,
    string HintName,
    EquatableArray<RuleModel> Rules,
    bool ErrorRule,
    bool Unicode,
    EquatableArray<StreamModel> Streams,      // declaration order
    LocationInfo? TypeLocation,
    EquatableArray<DiagnosticInfo> Diagnostics); // found while reading the syntax; if any are errors, nothing is emitted