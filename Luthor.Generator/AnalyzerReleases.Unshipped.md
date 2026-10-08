; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
LUTH001 | Luthor | Error | Lexer type must be partial
LUTH002 | Luthor | Error | Containing type must be partial
LUTH003 | Luthor | Error | Invalid rule attribute
LUTH004 | Luthor | Error | Invalid rule name
LUTH005 | Luthor | Error | Invalid rule pattern
LUTH006 | Luthor | Warning | Rule can match the empty string
LUTH007 | Luthor | Error | Unsupported encoding
LUTH008 | Luthor | Error | DFA construction failed
LUTH009 | Luthor | Warning | Rules split across partial declarations
LUTH010 | Luthor | Error | Duplicate stream encoding
LUTH011 | Luthor | Error | Missing stream encoding