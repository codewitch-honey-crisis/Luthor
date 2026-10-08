using System.IO;
using System.Text;
namespace Luthor;

static class Program
{
    static void DumpArray(int[] array, int indent, TextWriter writer)
    {
        var spaces = new string(' ', indent);
        for (var i = 0; i < array.Length; i++)
        {
            if (i>0 && (i % 16 == 0))
            {
                writer.WriteLine();
                writer.Write(spaces);
            }
            writer.Write(array[i]);
            if (i < array.Length - 1)
            {
                writer.Write(", ");
            }
        }
    }   
    static string ReplaceTemplateArgs(string data, int width, string name)
    {
        data = data.Replace("%WIDTH%", width.ToString());
        data = data.Replace("%NAME%", name);
        return data;

    }
    static void Main(string[] args)
    {
        Options? toDispose = null;
        try
        {
            Console.OutputEncoding = Encoding.UTF8;

            if(!Options.TryCreate(args, out var options, out var error))
{
                if (!error!.IsHelpRequest)
                {
                    Console.Error.WriteLine("Error: " + error.Message);
                    Console.Error.WriteLine();
                }
                Options.PrintUsage();
                return;
            }
            toDispose = options;
            var isPattern = false;
            if (options.RulesFileOrPattern!.IndexOfAny(Path.GetInvalidPathChars()) > -1)
            {
                isPattern = true;
            }
            else if (!File.Exists(options.RulesFileOrPattern))
            {
                isPattern = true;
            }
            var type = isPattern ? "expression" : "lexer";
            Console.Error.WriteLine($"Luthor {type} compiler");
            Console.Error.WriteLine();
            var patterns = new List<string>();
            if (!isPattern)
            {
                using var reader = new StreamReader(options.RulesFileOrPattern, true);
                foreach (var pattern in FileParser.ReadFrom(reader))
                {
                    patterns.Add(pattern);
                }
            }
            else
            {
                patterns.Add(options.RulesFileOrPattern);
            }
            if (!isPattern)
            {
                Console.Error.WriteLine($"There are {patterns.Count} patterns.");
            }
            var dfa = Builder.Build(patterns, !options.NoError,options.Unicode);
            if (options.NoError)
            {
                Console.Error.WriteLine("The error rule was not generated.");
            }
            if (options.Unicode)
            {
                Console.Error.WriteLine("Unicode character groups are in use.");
            }
            Console.Error.WriteLine($"{dfa.States.Count} states were built.");
            if (options.Graph != null)
            {
                Graph.RenderToFile(dfa, options.Graph, new GraphOptions { Dpi = options.Dpi, Vertical = options.Vertical});
                Console.Error.WriteLine($"The graph was written to {options.Graph}.");
            }
            var array = Compiler.Compile(dfa, options!.Encoding==null?"UTF-8":options!.Encoding!.WebName!);
            Console.Error.WriteLine($"The array has {array.Length} elements.");
            int width = 8;
            for (var i = 0; i < array.Length; i++)
            {
                var n = array[i];
                if (width == 8 && n > 127)
                {
                    width = 16;
                }
                if (width == 16 && n > 32767)
                {
                    width = 32;
                }
            }
            Console.Error.WriteLine($"The array element width is {width} bits.");
            if (options.Template != null)
            {
                var data = options.Template.ReadToEnd();
                var oi = 0;
                int i = data.IndexOf("%TABLE%",0);
                int indent;
                while(i>-1)
                {
                    indent = 0;
                    for (var j=i-1;j>=0;--j)
                    {
                        if(data[j]=='\n')
                        {
                            indent = i - j - 1;
                            if(indent<0) indent = 0;
                            break;
                        }
                    }
                    if(i>oi)
                    {
                        options.Output.Write(ReplaceTemplateArgs(data.Substring(oi, i - oi),width,!isPattern?Path.GetFileNameWithoutExtension(options.RulesFileOrPattern):"expression"));
                    }
                    DumpArray(array, indent,options.Output);
                    oi = i+7;
                    i = data.IndexOf("%TABLE%", i + 1);
                }
                if(oi<data.Length+7)
                {
                    options.Output.Write(ReplaceTemplateArgs(data.Substring(oi, data.Length - oi), width, !isPattern ? Path.GetFileNameWithoutExtension(options.RulesFileOrPattern) : "expression"));
                }
            }
            else
            {
                Console.Error.WriteLine();
                DumpArray(array, 0,options.Output);
            }
            

        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            toDispose?.Dispose();
        }
    }
}