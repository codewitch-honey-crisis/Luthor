using System.Text;
using Example;

const string source = """
    #include "stdio.h"
    /* multi-line
       comment */ while (x <= 10) { x = x + 1.5; } // done
    s = "café \"quoted\"" @ 😀
    """;

string[] names = typeof(Lexer).GetFields()
    .Where(f => f.IsLiteral && f.FieldType == typeof(int))
    .OrderBy(f => (int)f.GetRawConstantValue()!)
    .Select(f => f.Name).ToArray();
string Name(int s) => s >= 0 && s < names.Length ? names[s] : "#" + s;

Console.WriteLine("-- Tokenize(string)");
foreach (var (pos, sym, text) in Lexer.Tokenize(source))
    if (sym != Lexer.Ws) Console.WriteLine($"{pos,4} {Name(sym),-12} {Show(text)}");

// The byte overloads run a second DFA directly over UTF-8, so positions are byte offsets.
Console.WriteLine("-- Tokenize(Stream), UTF-8 with BOM");
using var stream = new MemoryStream(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(source)).ToArray());
foreach (var (pos, sym, text) in Lexer.Tokenize(stream))
    if (sym != Lexer.Ws && pos >= 90) Console.WriteLine($"{pos,4} {Name(sym),-12} {Show(text)}");

static string Show(string s) => s.Replace("\n", "\\n");
