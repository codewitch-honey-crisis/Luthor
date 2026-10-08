using Luthor;

namespace Example;

// Rules are tried in order: on a tie (same longest length) the earlier rule wins,
// so keywords go before Ident.
[Rule("Directive", @"^#[^\n]*")]
[Rule("LineComment", @"//[^\n]*")]
[Rule("BlockComment", @"/\*(.|\n)*?\*/")]   // lazy: stops at the first */
[Rule("If", "if", true)]
[Rule("While", "while", true)]
[Rule("Arrow", "=>", true)]
[Rule("Ident", @"[a-zA-Z_][a-zA-Z0-9_]*")]
[Rule("Number", @"[0-9]+(\.[0-9]+)?")]
[Rule("String", @"""([^""\\\n]|\\.)*""")]
[Rule("Ws", @"\p{Space}+")]
[Rule("Op", @"[-+*/=<>!;,(){}]")]
[Lexer(Unicode=true)]
[LexerStream(Encoding="UTF-8")]
[LexerStream(Encoding = "latin1")]
partial class Lexer
{
}
