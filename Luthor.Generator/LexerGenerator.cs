using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Luthor.Generator;

/// <summary>
/// Generates a DFA lexer for every partial type marked with <c>[Luthor.Rule]</c>, plus one
/// shared runtime file (attributes, matcher, input windows) per assembly.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class LexerGenerator : IIncrementalGenerator
{
    internal const string RuleAttributeName = "Luthor.RuleAttribute";
    internal const string LexerAttributeName = "Luthor.LexerAttribute";
    internal const string LexerStreamAttributeName = "Luthor.LexerStreamAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // The attributes must exist before user code binds, so they come from post-init output.
        context.RegisterPostInitializationOutput(static c =>
            c.AddSource("LuthorShared.g.cs", SourceText.From(SharedSource.Value, Encoding.UTF8)));

        // Stage 1 (runs on every edit of an annotated type, so it stays cheap): syntax -> value model.
        var lexers = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                RuleAttributeName,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, ct) => ModelReader.Read(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        // Stage 2 (runs only when a model actually changes): build the DFA and emit.
        context.RegisterSourceOutput(lexers, static (spc, model) => LexerEmitter.Execute(spc, model));
    }

    static readonly Lazy<string> SharedSource = new(() =>
    {
        using var stream = typeof(LexerGenerator).Assembly.GetManifestResourceStream("Luthor.Generator.LuthorShared.cs")
            ?? throw new InvalidOperationException("LuthorShared.cs resource is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
}

internal static class ModelReader
{
    static readonly SymbolDisplayFormat NamespaceFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public static LexerModel? Read(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type || ctx.TargetNode is not TypeDeclarationSyntax node)
            return null;

        // [Rule]s may sit on several partial declarations; ForAttributeWithMetadataName then calls us
        // once per declaration. Produce the model from the first one only, using ALL the type's rules.
        var all = type.GetAttributes();
        var ruleAttrs = all.Where(a => a.AttributeClass?.ToDisplayString() == LexerGenerator.RuleAttributeName).ToList();
        var partsWithRules = type.DeclaringSyntaxReferences
            .Where(r => ruleAttrs.Any(a => Contains(r, a.ApplicationSyntaxReference)))
            .ToList();
        if (partsWithRules.Count == 0 || !SameNode(partsWithRules[0], node)) return null;

        var diags = new List<DiagnosticInfo>();
        var typeLocation = LocationInfo.From(node.Identifier.GetLocation());
        if (partsWithRules.Count > 1)
            diags.Add(DiagnosticInfo.Create(Diagnostics.RulesSplitAcrossParts, typeLocation, type.Name));

        if (!node.Modifiers.Any(SyntaxKind.PartialKeyword))
            diags.Add(DiagnosticInfo.Create(Diagnostics.NotPartial, typeLocation, type.Name));

        var containers = new List<TypeDecl>();
        for (var parent = node.Parent as TypeDeclarationSyntax; parent is not null; parent = parent.Parent as TypeDeclarationSyntax)
        {
            if (!parent.Modifiers.Any(SyntaxKind.PartialKeyword))
                diags.Add(DiagnosticInfo.Create(Diagnostics.ContainerNotPartial, LocationInfo.From(parent.Identifier.GetLocation()),
                    parent.Identifier.ValueText, type.Name));
            containers.Insert(0, Decl(parent));
        }

        var rules = new List<RuleModel>();
        foreach (var attr in ruleAttrs)
        {
            ct.ThrowIfCancellationRequested();
            var syntax = attr.ApplicationSyntaxReference?.GetSyntax(ct) as AttributeSyntax;
            var args = attr.ConstructorArguments;
            if (args.Length < 2 || args[0].Value is not string name || args[1].Value is not string pattern)
            {
                diags.Add(DiagnosticInfo.Create(Diagnostics.BadAttributeArgument, LocationInfo.From(syntax),
                    (rules.Count + 1).ToString()));
                continue;
            }
            bool isLiteral = args.Length > 2 && args[2].Value is true;
            var argList = syntax?.ArgumentList?.Arguments;
            rules.Add(new RuleModel(name, pattern, isLiteral,
                LocationInfo.From(argList is { Count: > 0 } ? argList.Value[0] : syntax),
                LocationInfo.From(argList is { Count: > 1 } ? argList.Value[1] : syntax)));
        }

        bool errorRule = true;
        bool unicode = false;
        var lexerAttr = all.FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == LexerGenerator.LexerAttributeName);
        if (lexerAttr is not null)
        {
            foreach (var kv in lexerAttr.NamedArguments)
            {
                if (kv.Key == "ErrorRule" && kv.Value.Value is bool b) errorRule = b;
                else if (kv.Key == "Unicode" && kv.Value.Value is bool u) unicode = u;
            }
        }

        // [LexerStream(Encoding = ...)], any number, on any partial declaration. Duplicates are
        // detected later, in the emitter, where encodings are resolved to a canonical identity.
        var streams = new List<StreamModel>();
        foreach (var attr in all.Where(a => a.AttributeClass?.ToDisplayString() == LexerGenerator.LexerStreamAttributeName))
        {
            ct.ThrowIfCancellationRequested();
            var syntax = attr.ApplicationSyntaxReference?.GetSyntax(ct) as AttributeSyntax;
            var encodingArg = syntax?.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "Encoding");
            var location = LocationInfo.From((SyntaxNode?)encodingArg ?? syntax);

            string? encoding = null;
            foreach (var kv in attr.NamedArguments)
                if (kv.Key == "Encoding" && kv.Value.Value is string s) encoding = s.Trim();

            if (string.IsNullOrEmpty(encoding))
                diags.Add(DiagnosticInfo.Create(Diagnostics.MissingEncoding, location));
            else
                streams.Add(new StreamModel(encoding!, location));
        }

        string? ns = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(NamespaceFormat);
        return new LexerModel(
            ns,
            new EquatableArray<TypeDecl>(containers),
            Decl(node),
            HintName(type),
            new EquatableArray<RuleModel>(rules),
            errorRule,
            unicode,
            new EquatableArray<StreamModel>(streams),
            typeLocation,
            new EquatableArray<DiagnosticInfo>(diags));
    }

    static bool Contains(SyntaxReference decl, SyntaxReference? attr) =>
        attr is not null && attr.SyntaxTree == decl.SyntaxTree && decl.Span.Contains(attr.Span);

    static bool SameNode(SyntaxReference r, SyntaxNode node) => r.SyntaxTree == node.SyntaxTree && r.Span == node.Span;

    static TypeDecl Decl(TypeDeclarationSyntax t)
    {
        string keyword = t is RecordDeclarationSyntax r && !r.ClassOrStructKeyword.IsKind(SyntaxKind.None)
            ? $"record {r.ClassOrStructKeyword.ValueText}"
            : t.Keyword.ValueText;
        string typeParams = t.TypeParameterList is { Parameters.Count: > 0 } list
            ? "<" + string.Join(", ", list.Parameters.Select(p =>
                (p.VarianceKeyword.IsKind(SyntaxKind.None) ? "" : p.VarianceKeyword.ValueText + " ") + p.Identifier.Text)) + ">"
            : "";
        return new TypeDecl(keyword, t.Identifier.Text, typeParams);
    }

    static string HintName(INamedTypeSymbol type)
    {
        var parts = new List<string>();
        for (ISymbol? s = type; s is INamedTypeSymbol t; s = t.ContainingType)
            parts.Insert(0, t.Arity > 0 ? $"{t.Name}_{t.Arity}" : t.Name);
        if (!type.ContainingNamespace.IsGlobalNamespace) parts.Insert(0, type.ContainingNamespace.ToDisplayString());
        var sb = new StringBuilder();
        foreach (char c in string.Join(".", parts)) sb.Append(char.IsLetterOrDigit(c) || c is '.' or '_' ? c : '_');
        return sb.Append(".g.cs").ToString();
    }
}