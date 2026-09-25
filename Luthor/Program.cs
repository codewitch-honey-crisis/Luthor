using System.IO;
using System.Text;
namespace Luthor;
static class Program
{
    static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: Luthor <rules-file|pattern> [encoding]");
        Console.Error.WriteLine("  rules-file: text file containing regex rules, one per line, in the format 'name = pattern' or '# comment' at the start of each line");
        Console.Error.WriteLine("  pattern: a single pattern to match");
        Console.Error.WriteLine("  encoding: character encoding to use (e.g., UTF-8, UTF-16). default is UTF-8");
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
            if (args.Length > 2)
            {
                throw new ArgumentException("Too many arguments provided.");
            }
            var arg0 = args[0];
            if(arg0=="-?" || arg0.ToLowerInvariant()=="--help")
            {
                PrintUsage();
                return;
            }
            var isPattern = false;
            if(arg0.IndexOfAny(Path.GetInvalidPathChars())>-1) {
                isPattern = true;
            } else if (!File.Exists(arg0)) {
                isPattern= true;
            }
            var patterns = new List<string>();
            if (!isPattern)
            {
                using var reader = new StreamReader(args[0], true);
                var first = true;
                foreach (var rule in FileParser.ReadFrom(reader))
                {
                    if (first) { first = false; } else { Console.WriteLine(", "); }
                    Console.Write($"\"{rule.Name.Replace("\"", "\\\"")}\"");
                    patterns.Add(rule.Pattern);
                }
                Console.WriteLine();
            }
            else
            {
                patterns.Add(arg0);
            }
            var states = Builder.Build(patterns,true);
            var dfa = Compiler.Compile(states, args.Length == 2 ? args[1] : "UTF-8");

            for (var i = 0; i < dfa.Length; i++)
            {
                if (i % 16 == 0)
                {
                    Console.WriteLine();
                }
                Console.Write(dfa[i]);
                if (i < dfa.Length - 1)
                {
                    Console.Write(", ");
                }
            }
            Console.WriteLine();
            
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine();
            PrintUsage();
        }
    }
}
