using Microsoft.CodeAnalysis;

namespace Luthor.Generator;

internal static class Diagnostics
{
    const string Category = "Luthor";

    public static readonly DiagnosticDescriptor NotPartial = new(
        "LUTH001", "Lexer type must be partial",
        "'{0}' has [Rule] attributes but is not declared partial", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor ContainerNotPartial = new(
        "LUTH002", "Containing type must be partial",
        "'{0}' contains the lexer '{1}' and must be declared partial", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor BadAttributeArgument = new(
        "LUTH003", "Invalid rule attribute",
        "Rule {0}: name and pattern must be non-null constant strings", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor InvalidRuleName = new(
        "LUTH004", "Invalid rule name",
        "Rule name '{0}' {1}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor InvalidPattern = new(
        "LUTH005", "Invalid rule pattern",
        "Rule '{0}': {1}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor EmptyMatch = new(
        "LUTH006", "Rule can match the empty string",
        "Rule '{0}' can match the empty string{1}; an empty match never produces a token", Category, DiagnosticSeverity.Warning, true);

    public static readonly DiagnosticDescriptor BadEncoding = new(
        "LUTH007", "Unsupported encoding",
        "Encoding '{0}' is not supported: {1}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor BuildFailed = new(
        "LUTH008", "DFA construction failed",
        "Building the DFA for '{0}' failed: {1}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor RulesSplitAcrossParts = new(
        "LUTH009", "Rules split across partial declarations",
        "'{0}' has [Rule] attributes on more than one partial declaration; rule priority then depends on file order. Put all rules on one declaration.",
        Category, DiagnosticSeverity.Warning, true);

    public static readonly DiagnosticDescriptor DuplicateEncoding = new(
        "LUTH010", "Duplicate stream encoding",
        "[LexerStream] encoding '{0}' {1}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor MissingEncoding = new(
        "LUTH011", "Missing stream encoding",
        "[LexerStream] needs Encoding set to a non-empty constant string", Category, DiagnosticSeverity.Error, true);
}