using System;
using System.IO;
namespace Luthor;
static class FileParser
{
	internal static IEnumerable<string> ReadFrom(TextReader reader)
	{
		string? line;
		int lineNumber = 1;
        while ((line = reader.ReadLine()) != null)
		{
			if(line.StartsWith("#") || line.Trim().Length==0)
			{
				++lineNumber;
                continue;
            }
			yield return line.Trim();
            ++lineNumber;

        }
    }
}
