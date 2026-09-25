using System;
using System.IO;
namespace Luthor;
static class FileParser
{
	internal sealed record Rule
	{
		public string Name;
		public string Pattern;
		public Rule(string name, string pattern)
		{
			Name = name;
            Pattern = pattern;
        }
    }
	internal static IEnumerable<Rule> ReadFrom(TextReader reader)
	{
		string? line;
		int lineNumber = 1;
        while ((line = reader.ReadLine()) != null)
		{
			if(line.StartsWith("#"))
			{
				++lineNumber;
                continue;
            }
            int i = line.IndexOf('=');
			if(i==-1)
			{
				throw new FormatException("Invalid rule format on line: " + line);
            }
			++lineNumber;
            yield return new Rule(line.Substring(0, i).Trim(), line.Substring(i + 1).Trim());
        }
	}
}
