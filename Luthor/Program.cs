using System.IO;
using System.Text;
namespace Luthor;

static class Program
{
    static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: Luthor <rules-file|pattern> [encoding] [--noerror] [--graph <path> [--vertical]]");
        Console.Error.WriteLine("  rules-file: text file containing regex rules, one per line, in the format 'name = pattern' or '# comment' at the start of each line");
        Console.Error.WriteLine("  pattern: a single pattern to match");
        Console.Error.WriteLine("  encoding: character encoding to use (e.g., UTF-8, UTF-16). default is UTF-8");
        Console.Error.WriteLine("  noerror: do not generate the error rule");
        Console.Error.WriteLine("  graph: path to save the generated graph (dot,jpg,png,svg)");
        Console.Error.WriteLine("  --vertical: lay the graph out top to bottom instead of left to right");
    }
    static void Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length < 1)
            {
                throw new ArgumentException("The rules file or a pattern is required.");
            }
            var arg0 = args[0];
            if (arg0 == "-?" || arg0.ToLowerInvariant() == "--help")
            {
                PrintUsage();
                return;
            }

            // options after the rules file or pattern, in any order:
            // [encoding] [--noerror] [--graph <path> [--vertical]]
            string? enc = null;
            var noerror = false;
            string? graphPath = null;
            var vertical = false;
            for (var i = 1; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg == "-n" || arg == "--noerror")
                {
                    noerror = true;
                }
                else if (arg == "-g" || arg == "--graph")
                {
                    if (graphPath != null)
                    {
                        throw new ArgumentException("--graph was specified more than once.");
                    }
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("-"))
                    {
                        throw new ArgumentException("--graph requires an output path.");
                    }
                    graphPath = args[++i];
                }
                else if (arg == "-v" || arg == "--vertical")
                {
                    vertical = true;
                }
                else if (enc == null && !arg.StartsWith("-"))
                {
                    enc = arg.ToUpperInvariant();
                }
                else
                {
                    throw new ArgumentException($"Unexpected argument '{arg}'.");
                }
            }
            enc ??= "UTF-8";
            if (vertical && graphPath == null)
            {
                throw new ArgumentException("--vertical requires --graph.");
            }

            var isPattern = false;
            if (arg0.IndexOfAny(Path.GetInvalidPathChars()) > -1)
            {
                isPattern = true;
            }
            else if (!File.Exists(arg0))
            {
                isPattern = true;
            }
            var type = isPattern ? "expression" : "lexer";
            Console.Error.WriteLine($"Luthor {type} compiler");
            Console.Error.WriteLine();
            var patterns = new List<string>();
            if (!isPattern)
            {
                using var reader = new StreamReader(args[0], true);
                foreach (var pattern in FileParser.ReadFrom(reader))
                {
                    patterns.Add(pattern);
                }
            }
            else
            {
                patterns.Add(arg0);
            }
            if (!isPattern)
            {
                Console.Error.WriteLine($"There are {patterns.Count} patterns.");
            }
            var dfa = Builder.Build(patterns, !noerror);
            if (noerror)
            {
                Console.Error.WriteLine("The error rule was not generated.");
            }
            Console.Error.WriteLine($"{dfa.States.Count} states were built.");
            if (graphPath != null)
            {
                Graph.RenderToFile(dfa, graphPath, new GraphOptions { Dpi = 600, Vertical = vertical });
                Console.Error.WriteLine($"The graph was written to {graphPath}.");
            }
            var array = Compiler.Compile(dfa, enc);
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
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine();
            PrintUsage();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
        }
    }
}