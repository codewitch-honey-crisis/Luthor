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
            var type = isPattern?"expression":"lexer";
            Console.Error.WriteLine($"Luthor {type} compiler");
            Console.Error.WriteLine();
            var patterns = new List<string>();
            if (!isPattern)
            {
                using var reader = new StreamReader(args[0], true);
                var first = true;
                foreach (var pattern in FileParser.ReadFrom(reader))
                {
                    patterns.Add(pattern);
                }
            }
            else
            {
                patterns.Add(arg0);
            }
            if(!isPattern)
            {
                Console.Error.WriteLine($"There are {patterns.Count} patterns.");
            }
            var dfa = Builder.Build(patterns,true);
            Console.Error.WriteLine($"{dfa.States.Count} states were built");
            
            var array = Compiler.Compile(dfa, args.Length == 2 ? args[1] : "UTF-8");
            int width = 8;
            for (var i = 0; i < array.Length; i++)
            {
                var n = array[i];
                if(width==8 &&n>127)
                {
                    width = 16;
                }
                if(width==16 && n > 32767)
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
            Console.Error.WriteLine();
            PrintUsage();
        }
    }
}
