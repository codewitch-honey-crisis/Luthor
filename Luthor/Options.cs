using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;
using Cli;
namespace Luthor
{
    class EncodingConverter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        {
            return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
        }
        public override object? ConvertFrom(ITypeDescriptorContext? context, System.Globalization.CultureInfo? culture, object value)
        {
            if (value is string s)
            {
                try
                {
                    return Encoding.GetEncoding(s);
                }
                catch (ArgumentException ex)
                {
                    throw new FormatException($"Invalid encoding: {s}", ex);
                }
            }
            return base.ConvertFrom(context, culture, value);
        }
    }
    [CliArgs]
    internal partial class Options
    {
        [CmdArg(0, Description ="text file containing regex rules, one per line, in the format 'name = pattern' or '# comment' at the start of each line, or a single pattern to match",ValueName = "rules-file|pattern",Required =true)]
        public string? RulesFileOrPattern { get; set; } = null;
        [CmdArg(Description ="the encoding to use",ValueName ="encoding",ShortName ='e', Converter =typeof(EncodingConverter))]
        public Encoding Encoding { get; set; } = Encoding.UTF8!;
        [CmdArg(Description ="do not generate the error rule",ValueName ="noerror",ShortName ='n')]
        public bool NoError { get; set; } = false;
        [CmdArg(Description = "use Unicode character groups", ValueName = "unicode",ShortName ='u')]
        public bool Unicode { get; set; } = false;
        [CmdArg(Description = "Emit the output to the specified file", ValueName = "output-file", ShortName = 'o')]
        public TextWriter Output { get; set; } = Console.Out;
        [CmdArg(Description = "Emit the output using the specified template file", ValueName = "template-file", ShortName = 't')]
        public TextReader? Template { get; set; } = null;
        [CmdArg(Description = "generate a DFA graph (requires GraphViz in your PATH)", ValueName = "graph-file", ShortName = 'g')]
        public string? Graph { get; set; } = null;
        [CmdArg(Description = "use vertical DFA graphs", ValueName = "vertical",ShortName ='v')]
        public bool Vertical { get; set; } = false;
        [CmdArg(Description = "use the indicated DPI for graphs", ValueName = "dpi", ShortName = 'd')]
        public ushort Dpi { get; set; } = 300;


    }
}
