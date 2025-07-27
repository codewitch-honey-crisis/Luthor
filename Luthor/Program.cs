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
            var str = ((string)value).ToLowerInvariant().Replace("-","");
            switch(str)
            {
                case "ascii": // can use utf-8 here because ascii is only 7-bit
                case "utf8":
                    return Encoding.UTF8;
                case "utf16":
                    return Encoding.Unicode;
                case "utf32":
                    return Encoding.UTF32;
            }
        }
        return base.ConvertFrom(context, culture, value);
    }
}
static class Program
{
    [CmdArg(Ordinal = 0, Description = "The input expression or file to use",ElementName="input")]
    static string? Input = null;
    [CmdArg(Name = "enc",Optional =true,ElementName ="encoding",ElementConverter ="EncodingConverter",Description ="The encoding to use (ASCII, UTF-8, UTF-16, or UTF-32). Defaults to UTF-8")]
    static Encoding Enc = Encoding.UTF8;
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
    static void Main(string[] args)
    {
        // if you use the DFAS in this code the order must be
        //       RegexExpression->ToDfa()       // Unicode codepoint DFA
        //         Dfa
        //          ->RenderToFile()            // Visualize human-readable version  
        //          ->UTF8 / UTF16 transformation // Split ranges, add intermediate states
        //          ->ToMinimized()             // Optimize the transformed structure
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
            Console.Error.WriteLine(Input);
            Console.Error.WriteLine();
            var expr = RegexExpression.Parse(Input!);
            var dfa = expr!.ToDfa();
            //dfa.RenderToFile(@"..\..\..\dfa.jpg");
            if (Enc== Encoding.UTF8)
            {
                Console.Error.WriteLine("Transforming to UTF-8");
                dfa = DfaUtf8Transformer.TransformToUtf8(dfa);
            } else if(Enc==Encoding.Unicode)
            {
                Console.Error.WriteLine("Transforming to UTF-16");
                dfa = DfaUtf16Transformer.TransformToUtf16(dfa);
            }
            Console.Error.WriteLine();
            var array = dfa.ToArray();
            if (Dfa.IsRangeArray(array))
            {
                Console.Error.WriteLine("Emitting ranged jump table array: ");
            } else
            {
                Console.Error.WriteLine("Emitting non-ranged jump table array: ");
            }
            Console.Error.WriteLine();
            PrintArray(array);
        }
    }
}

