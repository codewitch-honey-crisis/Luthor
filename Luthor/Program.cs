using Cli;

using System.ComponentModel;
using System.Globalization;
using System.Text;
using Luthor;
public class EncodingConverter : TypeConverter
{
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if(value is string)
        {
            var ostr = (string)value;
            var str = ostr.ToLowerInvariant().Replace("-","");
            switch(str)
            {
                case "ascii":
                    return Encoding.ASCII;
                case "utf8":
                    return Encoding.UTF8;
                case "utf16":
                    return Encoding.Unicode;
                case "utf32":
                    return Encoding.UTF32;
                default:
                    return Encoding.GetEncoding(ostr);
            }
        }
        return base.ConvertFrom(context, culture, value);
    }
}
static class Program
{
    [CmdArg(Ordinal = 0, Description = "The input expression or file to use",ElementName="input")]
    static string? Input = null;
    [CmdArg(Name = "enc",Optional =true,ElementName ="encoding",ElementConverter ="EncodingConverter",Description ="The encoding to use (ASCII, UTF-8, UTF-16, or UTF-32, or a single byte encoding). Defaults to UTF-8")]
    static Encoding Enc = Encoding.UTF8;
    [CmdArg(Name = "graph", Optional = true, ElementName = "graph", Description = "Generate a DFA state graph to the specified file (requires GraphViz)")]
    static FileInfo Graph;
    [CmdArg(Name = "draft", Optional = true, ElementName = "draft", Description = "Generate a DFA state graph draft to the specified file (requires GraphViz)")]
    static FileInfo Draft;

    [CmdArg(Name="?", Group="Help",Optional = true,Description = "Displays this help screen")]
    static bool Help = false;
    static void PrintArray(int[] arr)
    {
        var num = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            if (i < arr.Length - 1)
            {
                Console.Write($"{arr[i]},");
            }
            else
            {
                Console.Write(arr[i]);
            }

            if (++num == 20)
            {
                num = 0;
                Console.WriteLine();
            }
            else
            {
                Console.Write(" ");
            }
        }
        Console.WriteLine();
    }
    private static int GetArrayWidth(int[] array)
    {
        int max = int.MinValue;
        for (int i = 0; i < array.Length; i++)
        {
            if (array[i] > max)
            {
                max = array[i];
            }
        }
        if (max <= sbyte.MaxValue)
        {
            return 1;
        }
        else if (max <= short.MaxValue)
        {
            return 2;
        }
        return 4;
    }
    static string SafePrint(string s)
    {
        if (s == null) return "<null>";
        if(s.Length > 40)
        {
            return s.Substring(0, 10) + "...<omitted>";
        }
        return s;
    }
    static void Main(string[] args)
    {
        using (var allArgs = CliUtility.ParseAndSet(args, null, typeof(Program), 0, null, "--"))
        {
            if(Help)
            {
                CliUtility.PrintUsage(CliUtility.GetSwitches(null, typeof(Program)), 0, null, "--");
                return;
            }
            if(File.Exists(Input))
            {
                Input = File.ReadAllText(Input);
            }
            Console.Error.WriteLine("Processing the following input:");
            //Console.Error.WriteLine(Input);
            Console.Error.WriteLine();
            var expr = RegexExpression.Parse(Input!);
            Console.Error.WriteLine(expr);
            var dfa = expr!.ToDfa();
            if (expr is RegexLexerExpression lexer)
            {
                Console.WriteLine("Individual rule expanded greedy expressions:");
                foreach (var rule in lexer.Rules)
                {
                    Console.WriteLine(SafePrint(rule.ToDfa().ToString()));
                }
                Console.Error.WriteLine();
                Console.WriteLine($"Amalgamated lexer greedy expression: {SafePrint(dfa.ToString())}");
                Console.Error.WriteLine();
            }
            else
            {
                Console.WriteLine("Expanded greedy expression");
                Console.WriteLine(SafePrint(dfa.ToString()));
                Console.Error.WriteLine();
            }
            Console.Error.WriteLine($"Created initial machine with {dfa.FillClosure().Count} states.");
            if (Graph != null)
            {
                if (Graph.Exists)
                {
                    try { Graph.Delete(); } catch { }
                }
                dfa.RenderToFile(Graph.FullName);
            }
            if (Draft != null)
            {
                if (Draft.Exists)
                {
                    try { Draft.Delete(); } catch { }
                }
                dfa.RenderToFile(Draft.FullName,true);
            }
            var len = dfa.GetArrayLength();
            Console.Error.WriteLine();
            Console.Error.Write("Minimizing...");
            dfa = dfa.ToMinimized();
            var mlen = dfa.GetArrayLength();
            Console.Error.WriteLine($"done! {100 - (mlen * 100 / len)}% size savings.");
            Console.Error.WriteLine($"Minimized machine has {dfa.FillClosure().Count} states.");
            var xformed = false;
            Console.Error.WriteLine();
            //dfa.RenderToFile(@"..\..\..\dfa.jpg");
            if (Enc != Encoding.UTF32)
            {
                Console.Error.Write($"Transforming to {Enc.EncodingName}...");
                dfa = DfaEncodingTransform.Transform(dfa, Enc);
                xformed = true;
            }
            //dfa.RenderToFile(@"..\..\..\xdfa.jpg");

            if (xformed)
            {
                var tlen = dfa.GetArrayLength();
                var finalSize = (tlen * 100 / mlen);
                var expansionCost = (tlen * 100 / mlen) - 100;
                string sizeChange = expansionCost >= 0
                    ? $"{expansionCost}% expansion cost"
                    : $"{Math.Abs(expansionCost)}% size reduction";

                Console.Error.WriteLine($"done! {sizeChange}.");
             
                Console.Error.WriteLine($"Net effect: {finalSize}% of original length*.");

            }
            Console.Error.WriteLine();
            var array = dfa.ToArray();

            var width = GetArrayWidth(array);
            var label = (width!=1)?"bytes":"byte";

            Console.Error.WriteLine($"The array takes a minimum of {width*array.Length} bytes to store");
            Console.Error.WriteLine();
            if (Dfa.IsRangeArray(array))
            {
                Console.Error.WriteLine($"Emitting ranged jump table array with a length of {array.Length} and an element width of {width} {label}");
            } else
            {
                Console.Error.WriteLine($"Emitting non-ranged jump table array with a length of {array.Length} and an element width of {width} {label}");
            }
            Console.Error.WriteLine();
            Console.Error.WriteLine("* values do not reflect the relative width of the elements, only the total length of the array.");
            Console.Error.WriteLine();
            PrintArray(array);
        }
    }
}

