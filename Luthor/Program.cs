using System.IO;
using System.Text;
namespace Luthor;

static class Program
{
  
    static void Main(string[] args)
    {
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
                if (error!.IsHelpRequest) return;
            }

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
            for (var i = 0; i < array.Length; i++)
            {
                if (i % 16 == 0)
                {
                    Console.WriteLine();
                }
                Console.Write(array[i]);
                if (i < array.Length - 1)
                {
                    Console.Write(", ");
                }
            }
            Console.WriteLine();

        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
        }
    }
}