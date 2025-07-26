using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;
using System;

namespace Cli
{

	/// <summary>
	/// Represents an exception in the argument processesing
	/// </summary>
#if CALIB
	public
#endif
	class CmdException : Exception
	{
		/// <summary>
		/// The name of the named argument, if applicable
		/// </summary>
		public string Name { get; private set; }
		/// <summary>
		/// The ordinal of the ordinal argument, if applicable
		/// </summary>
		public int Ordinal { get; private set; }
		/// <summary>
		/// Constructs a new instance
		/// </summary>
		/// <param name="message">The message</param>
		/// <param name="ordinal">The ordinal</param>
		public CmdException(string message, int ordinal) : base(message)
		{
			Name = null;
			Ordinal = ordinal;
		}
		/// <summary>
		/// Constructs a new instance
		/// </summary>
		/// <param name="message">The message</param>
		/// <param name="name">The name</param>
		public CmdException(string message, string name) : base(message)
		{
			Name = name;
			Ordinal = -1;
		}
		/// <summary>
		/// Constructs a new instance
		/// </summary>
		/// <param name="message">The message</param>
		/// <param name="ordinal">The ordinal</param>
		/// <param name="innerException">The inner exception</param>
		public CmdException(string message, int ordinal, Exception innerException) : base(message, innerException)
		{

		}
		/// <summary>
		/// Constructs a new instance
		/// </summary>
		/// <param name="message">The message</param>
		/// <param name="name">The name</param>
		/// <param name="innerException">The inner exception</param>
		public CmdException(string message, string name, Exception innerException) : base(message, innerException)
		{

		}
	}
	/// <summary>
	/// The type of switch
	/// </summary>
#if CLILIB
	public
#endif
	enum CmdSwitchType
	{
		/// <summary>
		/// Just the switch itself
		/// </summary>
		Simple,
		/// <summary>
		/// Switch with single argument
		/// </summary>
		OneArg,
		/// <summary>
		/// Switch with a list of args
		/// </summary>
		List
	}
	/// <summary>
	/// Represents the result of parsing the command line arguments
	/// </summary>
#if CLILIB
	public
#endif
	class CmdParseResult : IDisposable
	{
		/// <summary>
		/// The group that the arguments are part of
		/// </summary>
		public string Group;
		/// <summary>
		/// The ordinal arguments
		/// </summary>
		public List<object> OrdinalArguments;
		/// <summary>
		/// The named arguments
		/// </summary>
		public Dictionary<string, object> NamedArguments;
		/// <summary>
		/// Disposes any disposable arguments
		/// </summary>
		public void Dispose()
		{
			for (int i = 0; i < OrdinalArguments.Count; i++)
			{
				_DisposeArg(OrdinalArguments[i]);
			}
			foreach (var de in NamedArguments)
			{
				_DisposeArg(de.Value);
			}
		}

		private static void _DisposeArg(object arg)
		{
			if (arg != null && arg.GetType().IsArray)
			{
				var arr = (Array)arg;
				if (arr.Rank == 1)
				{
					for (int j = 0; j < arr.Length; ++j)
					{
						var v = arr.GetValue(j);
						if (v == Console.In || v == Console.Out || v == Console.Error)
						{
							continue;
						}
						var d = v as IDisposable;
						if (d != null)
						{
							d.Dispose();
						}
					}
				}
			}
			else if (arg is IDisposable disp)
			{
				if (arg != Console.Out && arg != Console.Error && arg != Console.In)
				{
					disp.Dispose();
				}
			}
		}
	}
	/// <summary>
	/// Represents a command argument switch value
	/// </summary>
#if CLILIB
	public
#endif
	struct CmdSwitch
	{
		/// <summary>
		/// The name of the argument, if named
		/// </summary>
		public string Name;
		/// <summary>
		/// The ordinal of the argument, if ordinal, otherwise -1
		/// </summary>
		public int Ordinal;
		/// <summary>
		/// Indicates if the argument is optional
		/// </summary>
		public bool Optional;
		/// <summary>
		/// Indicates the default value
		/// </summary>
		public object Default;
		/// <summary>
		/// Indicates the type of switch
		/// </summary>
		public CmdSwitchType Type;
		/// <summary>
		/// Indicates the name of the element associated with the switch argument
		/// </summary>
		public string ElementName;
		/// <summary>
		/// Indicates the type of the element associated with the switch argument
		/// </summary>
		public Type ElementType;
		/// <summary>
		/// Indicates the <see cref="TypeConverter"/> to use to convert to and from an invariant string
		/// </summary>
		public TypeConverter ElementConverter;
		/// <summary>
		/// Indicates a description for the argument
		/// </summary>
		public string Description;
		/// <summary>
		/// Indicates which group of arguments this belongs to
		/// </summary>
		public string Group;
		/// <summary>
		/// Constructs a new instance
		/// </summary>
		/// <param name="name"><see cref="Name"/></param>
		/// <param name="ordinal"><see cref="Ordinal"/></param>
		/// <param name="optional"><see cref="Optional"/></param>
		/// <param name="default"><see cref="Default"/></param>
		/// <param name="type"><see cref="Type"/></param>
		/// <param name="elementName"><see cref="ElementName"/></param>
		/// <param name="elementType"><see cref="ElementType"/></param>
		/// <param name="elementConverter"><see cref="ElementConverter"/></param>
		/// <param name="description"><see cref="Description"/></param>
		/// <param name="group"><see cref="Group"/></param>
		public CmdSwitch(string name, int ordinal, bool optional, object @default, CmdSwitchType type, string elementName, Type elementType, TypeConverter elementConverter, string description, string group)
		{
			Name = name;
			Ordinal = ordinal;
			Optional = optional;
			Default = @default;
			Type = type;
			ElementName = elementName;
			ElementType = elementType;
			ElementConverter = elementConverter;
			Description = description;
			Group = group;
		}
		/// <summary>
		/// Returns a new empty instance
		/// </summary>
		public static CmdSwitch Empty { get => new CmdSwitch(null, -1, false, null, CmdSwitchType.OneArg, null, typeof(string), null, null, null); }
	}
	/// <summary>
	/// Indicates an attribute used to mark up static fields and properties to use as command line arguments
	/// </summary>
	[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
#if CLILIB
	public
#endif
	class CmdArgAttribute : Attribute
	{
		/// <summary>
		/// The name of the switch. Defaults to the member name
		/// </summary>
		public string Name { get; set; } = null;
		/// <summary>
		/// The ordinal. Default is named.
		/// </summary>
		public int Ordinal { get; set; } = -1;
		/// <summary>
		/// Indicates if the argument is optional
		/// </summary>
		public bool Optional { get; set; } = false;
		/// <summary>
		/// Indicates the name of the element associated with the argument
		/// </summary>
		public string ElementName { get; set; } = null;
		/// <summary>
		/// Indicates the <see cref="TypeConverter"/> used to convert elements to and from an invariant string
		/// </summary>
		public string ElementConverter { get; set; } = null;
		/// <summary>
		/// Indicates the description of the argument
		/// </summary>
		public string Description { get; set; } = null;
		/// <summary>
		/// Indicates the group of arguments this is a part of
		/// </summary>
		public string Group { get; set; } = null;
		/// <summary>
		/// Constructs a new instance
		/// </summary>
		/// <param name="name"><see cref="Name"/></param>
		/// <param name="ordinal"><see cref="Ordinal"/></param>
		/// <param name="optional"><see cref="Optional"/></param>
		/// <param name="elementName"><see cref="ElementName"/></param>
		/// <param name="elementConverter"><see cref="ElementConverter"/></param>
		/// <param name="description"><see cref="Description"/></param>
		/// <param name="group"><see cref="Group"/></param>
		public CmdArgAttribute(string name = null, int ordinal = -1, bool optional = false, string elementName = null, string elementConverter = null, string description = null, string group = null)
		{
			Name = name;
			Ordinal = ordinal;
			Optional = optional;
			ElementName = elementName;
			Description = description;
			Group = group;
		}
	}
	/// <summary>
	/// Provides command line argument parsing, stale file checking, usage screen generation and word wrapping facilities useful for CLI applications
	/// </summary>
#if CLILIB
	public
#endif
	static class CliUtility
	{
		#region _DeferredTextWriter
		private sealed class _DeferredTextWriter : TextWriter
		{
			readonly string _name;
			StreamWriter _writer = null;
			void EnsureWriter()
			{
				_writer ??= new StreamWriter(_name, false, Encoding.UTF8);

			}
			public override Encoding Encoding
			{
				get
				{
					if (_writer == null)
					{
						return Encoding.UTF8;
					}
					return _writer.Encoding;
				}
			}
			public _DeferredTextWriter(string path)
			{
				_name = path;
			}
			public string Name
			{
				get
				{
					return _name;
				}
			}
			protected override void Dispose(bool disposing)
			{
				if (disposing)
				{
					if (_writer != null)
					{
						_writer.Close();
						_writer = null;
					}
				}
				base.Dispose(disposing);
			}
			public override void Close()
			{
				if (_writer != null)
				{
					_writer.Close();
				}
				base.Close();
			}
			public override void Write(string value)
			{
				EnsureWriter();
				_writer.Write(value);
			}
			public override void Write(char ch)
			{
				EnsureWriter();
				_writer.Write(ch);
			}
			public override void WriteLine(string value)
			{
				EnsureWriter();
				_writer.WriteLine(value);
			}
		}
		#endregion // _DeferredTextWriter
		#region _StringCursor
		private class _StringCursor
		{
			public IEnumerator<char> Input = null;
			public long Position = -1;
			public int State = -2;
			public string Current = null;
			public void EnsureStarted()
			{
				if (State == -2)
				{
					Advance();
				}
			}
			public string Advance()
			{
				if (State == -1)
				{
					return null;
				}
				++Position;
				if (!Input.MoveNext())
				{
					State = -1;
					Current = null;
					return null;
				}
				char c = Input.Current;
				if (char.IsSurrogate(c))
				{
					if (!Input.MoveNext())
					{
						throw new Exception("Incomplete Unicode surrogate found. Unexpected end of string.");
					}
					++Position;
					char c2 = Input.Current;
					Current = new string(new char[] { c, c2 });
				}
				else
				{
					Current = c.ToString();
				}
				State = 0;
				return Current;
			}
		}
		#endregion
		#region _ArrayCursor
		private class _ArrayCursor
		{
			public IEnumerator<string> Input = null;
			public int State = -2;
			public string Current = null;
			public void EnsureStarted()
			{
				if (State == -2)
				{
					Advance();
				}
			}
			public string Advance()
			{
				if (State == -1)
				{
					return null;
				}
				if (!Input.MoveNext())
				{
					State = -1;
					Current = null;
					return null;
				}
				Current = Input.Current;
				State = 0;
				return Current;
			}
		}
		#endregion
		static (string Value, bool Quoted) _ParseWithQuoted(_StringCursor cur)
		{
			var sb = new StringBuilder();
			cur.EnsureStarted();
			var inQuotes = false;
			while (cur.Current != null && char.IsWhiteSpace(cur.Current, 0))
			{
				cur.Advance();
			}
			if (cur.Current == "\"")
			{
				inQuotes = true;
				cur.Advance();
			}
			var moved = false;
			while (cur.State != -1)
			{
				if (!inQuotes)
				{
					if (cur.Current == "\"")
					{
						return (sb.ToString(), false);
					}
					if (char.IsWhiteSpace(cur.Current, 0))
					{
						cur.Advance();
						return (sb.ToString(), false);
					}
					sb.Append(cur.Current);
					cur.Advance();
					moved = true;
				}
				else
				{
					if (cur.Current == "\"")
					{
						cur.Advance();
						if (cur.Current != "\"")
						{
							return (sb.ToString(), true);
						}
					}
					sb.Append(cur.Current);
					cur.Advance();
					moved = true;
				}
			}
			if (inQuotes)
			{
				throw new Exception("Unterminated quote");
			}
			return (moved ? sb.ToString() : null, false);
		}
		static object _ValueFromString(string value, Type type, TypeConverter converter)
		{
			if (converter != null)
			{

				return converter.ConvertFromInvariantString(value);

			}
			if (type == null || type == typeof(object) || type == typeof(string))
			{
				return value;
			}
			if (type == typeof(TextReader))
			{
				return new StreamReader(value);
			}
			else if (type == typeof(TextWriter))
			{
				return new _DeferredTextWriter(value);
			}
			if (type == typeof(Uri))
			{
				return new Uri(value);
			}
			if (type == typeof(DateTime))
			{
				return DateTime.Parse(value);
			}
			if (type == typeof(Guid))
			{
				return Guid.Parse(value);
			}
			if (type == typeof(TimeSpan))
			{
				return TimeSpan.Parse(value);
			}
			if (type == typeof(FileInfo))
			{
				return new FileInfo(value);
			}
			if (type == typeof(DirectoryInfo))
			{
				return new DirectoryInfo(value);
			}
			if(type.IsEnum)
			{
				return Enum.Parse(type, value,true);
			}
			return Convert.ChangeType(value, type);
		}
		static string _ValueToString(object value, Type type, TypeConverter converter)
		{
			if (converter != null)
			{
				return converter.ConvertToInvariantString(value);
			}
			if (type == null || type == typeof(string))
			{
				return value as string;
			}
			if (type == typeof(TextReader))
			{
				if (value == Console.In)
				{
					return "<stdin>";
				}
				return "<#file>";
			}
			else if (type == typeof(TextWriter))
			{
				if (value == Console.Out)
				{
					return "<stdout>";
				}
				if (value == Console.Error)
				{
					return "<stderr>";
				}
				return "<#file>";
			}
			if (type == typeof(Uri))
			{
				return ((uint)value).ToString();
			}
			if (type == typeof(DateTime))
			{
				return ((DateTime)value).ToString();
			}
			if (type == typeof(Guid))
			{
				return ((Guid)value).ToString();
			}
			if (type == typeof(TimeSpan))
			{
				return ((TimeSpan)value).ToString();
			}
			if (type == typeof(FileInfo))
			{
				return ((FileInfo)value).FullName;
			}
			if (type == typeof(DirectoryInfo))
			{
				return ((DirectoryInfo)value).FullName;
			}
			return value.ToString();
		}
		static Dictionary<string, List<CmdSwitch>> _GroupSwitches(List<CmdSwitch> switches)
		{
			var result = new Dictionary<string, List<CmdSwitch>>();
			foreach (var sw in switches)
			{
				List<CmdSwitch> group;
				if (result.TryGetValue(sw.Group, out group))
				{
					group.Add(sw);
				}
				else
				{
					var list = new List<CmdSwitch>() { sw };
					result.Add(sw.Group, list);
				}
			}
			return result;
		}
		static int _GetMinArgs(CmdSwitch sw)
		{
			if (sw.Optional) return 0;
			switch (sw.Type)
			{
				case CmdSwitchType.Simple:
					return 0;
				default:
					if (sw.Optional) return 0;
					return 1;
			}
		}
		static int _GetMaxArgs(CmdSwitch sw)
		{
			switch (sw.Type)
			{
				case CmdSwitchType.Simple:
					return 0;
				case CmdSwitchType.OneArg:
					return 1;
				default:
					return -1;
			}
		}
		static int _GetMinOrdinalArgs(IEnumerable<CmdSwitch> switches)
		{
			int result = 0;
			foreach (var sw in switches)
			{
				if (sw.Ordinal < 0)
				{
					break;
				}
				result += _GetMinArgs(sw);
			}
			return result;
		}
		static int _GetMaxOrdinalArgs(IEnumerable<CmdSwitch> switches)
		{
			int result = 0;
			foreach (var sw in switches)
			{
				if (sw.Ordinal < 0)
				{
					break;
				}
				var max = _GetMaxArgs(sw);
				if (max == -1)
				{
					return -1;
				}
				result += max;
			}
			return result;
		}
		static void _CheckUniqueSwitchees(List<CmdSwitch> lhs, List<CmdSwitch> rhs, string group)
		{
			var found_unique = false;
			var lhs_count = 0;
			for (var i = 0; i < lhs.Count; ++i)
			{
				if (!lhs[i].Optional && lhs[i].Ordinal < 0 && !string.IsNullOrEmpty(lhs[i].Name))
				{
					++lhs_count;
				}
			}
			var rhs_count = 0;
			for (var i = 0; i < rhs.Count; ++i)
			{
				if (!rhs[i].Optional && rhs[i].Ordinal < 0 && !string.IsNullOrEmpty(rhs[i].Name))
				{
					++rhs_count;
				}
			}
			if (lhs_count != rhs_count)
			{
				return;
			}
			foreach (var sw in lhs)
			{
				if (!sw.Optional && sw.Ordinal < 0 && !string.IsNullOrEmpty(sw.Name))
				{
					var found = false;
					var dup = false;
					foreach (var rsw in rhs)
					{
						if (rsw.Ordinal < 0 && !rsw.Optional && !string.IsNullOrEmpty(rsw.Name))
						{
							found = true;
							if (rsw.Name == sw.Name)
							{
								dup = true;
								break;
							}
						}
					}
					if (found && !dup)
					{
						found_unique = true;
						break;
					}
				}
			}
			if (!found_unique)
			{
				throw new ArgumentException("Switch required to disambiguate group \"" + group + "\".");
			}
		}
		/// <summary>
		/// Normalizes and validates a list of <see cref="CmdSwitch"/> instances
		/// </summary>
		/// <param name="switches">The switches to normalize</param>
		/// <exception cref="ArgumentException">Validation failed for one or more arguments</exception>
		/// <remarks>This is called by the framework automatically, but can be used by the user to perform validation earlier.</remarks>
		public static void NormalizeAndValidateSwitches(List<CmdSwitch> switches)
		{
			var i = 0;
			for (; i < switches.Count; i++)
			{
				var sw = switches[i];
				if (sw.Ordinal > -1 && !string.IsNullOrEmpty(sw.Name))
				{
					throw new ArgumentException("Both ordinal and name cannot be specified.", sw.Name);
				}
				if (sw.Type == CmdSwitchType.Simple)
				{
					if (sw.Ordinal != -1)
					{
						throw new ArgumentException("Switch type of Simple must be named.");
					}
				}

			}
			switches.Sort((lhs, rhs) =>
			{
				if (lhs.Ordinal < 0)
				{
					return rhs.Ordinal < 0 ? 0 : 1;
				}
				else
				{
					if (rhs.Ordinal < 0)
					{
						return -1;
					}
					if (lhs.Ordinal < rhs.Ordinal)
					{
						return -1;
					}
					else if (lhs.Ordinal > rhs.Ordinal)
					{
						return 1;
					}
				}
				return 0;
			});
			for (i = 0; i < switches.Count; ++i)
			{
				var sw = switches[i];
				if (sw.Ordinal < 0) break;
				sw.Group ??= "";
				sw.Ordinal = i;
				switches[i] = sw;
			}
			for (; i < switches.Count; ++i)
			{
				var sw = switches[i];
				sw.Group ??= "";
				switches[i] = sw;
			}
			int ordCount = 0;
			for (i = 0; i < switches.Count; ++i)
			{
				var sw = switches[i];
				if (sw.Ordinal < 0) break;
				ordCount++;
			}
			for (i = 0; i < switches.Count; ++i)
			{
				var sw = switches[i];
				if (sw.Ordinal < 0) break;
				if (sw.Type == CmdSwitchType.List)
				{
					if (i < ordCount - 1)
					{
						throw new ArgumentException("Ordinal position list must be last among unnamed arguments");
					}
				}
				if (sw.Optional)
				{
					if (i < ordCount - 1)
					{
						throw new ArgumentException("Ordinal position optional value must be last among unnamed arguments");
					}
				}
			}
			for (i = 0; i < switches.Count; ++i)
			{
				var sw = switches[i];
				if (string.IsNullOrEmpty(sw.ElementName) && sw.Type == CmdSwitchType.Simple)
				{
					sw.ElementName = sw.Name;
				}
				if (string.IsNullOrEmpty(sw.ElementName))
				{
					if (sw.ElementType == typeof(TextReader))
					{
						sw.ElementName = "inputfile";
					}
					else if (sw.ElementType == typeof(TextWriter))
					{
						sw.ElementName = "outputfile";
					}
					else
					{
						sw.ElementName = "item";
					}
				}
				if (string.IsNullOrEmpty(sw.Description))
				{
					if (sw.ElementType == typeof(TextReader))
					{
						sw.Description = "The input file";
					}
					else if (sw.ElementType == typeof(TextWriter))
					{
						sw.Description = "The output file";
					}
					else
					{
						sw.Description = "";
					}
				}
				switches[i] = sw;
			}
			var groups = _GroupSwitches(switches);
			foreach (var group in groups)
			{
				foreach (var group_cmp in groups)
				{
					if (group_cmp.Key == group.Key)
					{
						continue;
					}
					// check ordinals for non-overlapping ranges
					var min = _GetMinOrdinalArgs(group.Value);
					var max = _GetMaxOrdinalArgs(group.Value);
					var min_cmp = _GetMinOrdinalArgs(group_cmp.Value);
					var max_cmp = _GetMaxOrdinalArgs(group_cmp.Value);
					// overlap found, check switches
					if (min >= min_cmp && (((max == -1 && min > 0) || max <= max_cmp)))
					{
						// check switches for unique non optional
						_CheckUniqueSwitchees(group.Value, group_cmp.Value, group.Key);
					}

				}
			}


		}
		static object _ParseArgValue(CmdSwitch sw, _StringCursor cur)
		{
			var v = _ParseWithQuoted(cur);
			if (v.Value == null)
			{
				throw new ArgumentException("No value found");
			}
			return _ValueFromString(v.Value, sw.ElementType, sw.ElementConverter);

		}
		static object _ParseArgValue(CmdSwitch sw, _ArrayCursor cur)
		{
			cur.EnsureStarted();
			var v = cur.Current;
			if (v == null)
			{
				throw new ArgumentException("No value found");
			}
			var result = _ValueFromString(v, sw.ElementType, sw.ElementConverter);
			cur.Advance();
			return result;

		}
		static Array _ParseArgValues(CmdSwitch sw, _StringCursor cur, string switchPrefix)
		{
			var result = new List<object>();
			while (true)
			{
				while (char.IsWhiteSpace(cur.Current, 0))
				{
					cur.Advance();
				}
				if (cur.Current != null && cur.Current.StartsWith(switchPrefix))
				{
					break;
				}
				var v = _ParseWithQuoted(cur);
				if (v.Value == null)
				{
					break;
				}
				object o = _ValueFromString(v.Value, sw.ElementType, sw.ElementConverter);


				result.Add(o);
			}
			Type t = sw.ElementType;
			t ??= typeof(string);
			var arr = Array.CreateInstance(t, result.Count);
			for (int i = 0; i < arr.Length; ++i)
			{
				arr.SetValue(result[i], i);
			}
			return arr;
		}
		static Array _ParseArgValues(CmdSwitch sw, _ArrayCursor cur, string switchPrefix)
		{
			cur.EnsureStarted();
			var result = new List<object>();
			while (true)
			{
				if (cur.Current != null && cur.Current.StartsWith(switchPrefix))
				{
					break;
				}
				var v = cur.Current;
				if (v == null)
				{
					break;
				}
				object o = _ValueFromString(v, sw.ElementType, sw.ElementConverter);
				cur.Advance();


				result.Add(o);
			}
			Type t = sw.ElementType;
			t ??= typeof(string);
			var arr = Array.CreateInstance(t, result.Count);
			for (int i = 0; i < arr.Length; ++i)
			{
				arr.SetValue(result[i], i);
			}
			return arr;
		}
		static (int MinOrds, int MaxOrds, List<string> requiredNames) _GetGroupMetrics(List<CmdSwitch> groupSwitches)
		{
			int min = _GetMinOrdinalArgs(groupSwitches);
			int max = _GetMaxOrdinalArgs(groupSwitches);
			var req = new List<string>();
			foreach(var sw in groupSwitches)
			{
				if(sw.Ordinal<0 && !string.IsNullOrEmpty(sw.Name))
				{
					if(!sw.Optional)
					{
						req.Add(sw.Name);
					}
				}
			}
			return (min, max, req);
		}
		static bool _CheckGroup(int ords, List<string> swnames,List<CmdSwitch> groupSwitches)
		{
			var metrics = _GetGroupMetrics(groupSwitches);
			if (ords >= metrics.MinOrds && ((metrics.MaxOrds == -1) || (ords <= metrics.MaxOrds))) {
				foreach (var s in metrics.requiredNames)
				{
					if (!swnames.Contains(s))
					{
						return false;
					}
				}
				return true;
			}
			return false;
		}
		static string _MatchGroup(Dictionary<string, List<CmdSwitch>> groups, int ords, List<string> swnames)
		{
			var firstGroup = "";
			string result = null;
			foreach (var group in groups)
			{
				if (firstGroup.Length == 0)
				{
					firstGroup = group.Key;
				}
				var passed = _CheckGroup(ords, swnames, group.Value);
				if (passed)
				{
					var found = false;
					foreach (var gcmp in groups)
					{
						if (gcmp.Key != group.Key)
						{
							if (_CheckGroup(ords, swnames, gcmp.Value))
							{
								found = true;
								break;
							}
						}
					}
					if (!found)
					{
						if (result != null)
						{
							throw new ArgumentException("Invalid combination of arguments.");
						}
						result = group.Key;
					}
				}
			}
			return result != null ? result : firstGroup;
		}
		static string _DetectGroup(Dictionary<string, List<CmdSwitch>> groups, string commandLine, string switchPrefix)
		{
			var cur = new _StringCursor { Input = commandLine.GetEnumerator() };
			cur.EnsureStarted();
			_ParseWithQuoted(cur);
			var v = _ParseWithQuoted(cur);
			var ords = 0;
			var swnames = new List<string>();
			var passedOrds = false;
			while (v.Value != null)
			{
				if (!passedOrds && !v.Value.StartsWith(switchPrefix))
				{
					++ords;
				}
				else if (v.Value.StartsWith(switchPrefix))
				{
					passedOrds = true;
					swnames.Add(v.Value.Substring(switchPrefix.Length));
				}
				v = _ParseWithQuoted(cur);

			}
			return _MatchGroup(groups, ords, swnames);
		}

		static string _DetectGroup(Dictionary<string, List<CmdSwitch>> groups, IEnumerable<string> commandLine, string switchPrefix)
		{
			var cur = new _ArrayCursor { Input = commandLine.GetEnumerator() };
			cur.EnsureStarted();
			var v = cur.Current;
			var ords = 0;
			var swnames = new List<string>();
			var passedOrds = false;
			while (v != null)
			{
				if (!passedOrds && !v.StartsWith(switchPrefix))
				{
					++ords;
				}
				else if (v.StartsWith(switchPrefix))
				{
					passedOrds = true;
					swnames.Add(v.Substring(switchPrefix.Length));
				}
				cur.Advance();
				v = cur.Current;
			}

			return _MatchGroup(groups, ords, swnames);
		}
		/// <summary>
		/// Parses the executable path from the command line
		/// </summary>
		/// <param name="commandLine">The command line string</param>
		/// <returns>The executable path</returns>
		public static string ParseExePath(string commandLine)
		{
			_StringCursor cur = new _StringCursor() { Input = commandLine.GetEnumerator() };
			cur.EnsureStarted();
			return _ParseWithQuoted(cur).Value;

		}
		/// <summary>
		/// Parses command line arguments
		/// </summary>
		/// <param name="switches">A list of <see cref="CmdSwitch"/> instances describing the switches and arguments</param>
		/// <param name="commandLine">The command line to parse</param>
		/// <param name="switchPrefix">The prefix for switches</param>
		/// <returns>A <see cref="CmdParseResult"/> instance containing the parsed arguments</returns>
		public static CmdParseResult ParseArguments(List<CmdSwitch> switches, IEnumerable<string> commandLine, string switchPrefix = null)
		{
			if (string.IsNullOrEmpty(switchPrefix))
			{
				switchPrefix = SwitchPrefix;
			}
			NormalizeAndValidateSwitches(switches);
			var grps = _GroupSwitches(switches);
			var grp = _DetectGroup(grps, commandLine, switchPrefix);
			_ArrayCursor cur = new _ArrayCursor() { Input = commandLine.GetEnumerator() };
			cur.EnsureStarted();
			var ords = new List<object>();
			var named = new Dictionary<string, object>();

			var i = 0;
			switches = grps[grp];
			try
			{
				// process ordinal args
				for (; i < switches.Count; i++)
				{
					var sw = switches[i];

					var c = cur.Current;
					if (sw.Ordinal < 0)
					{
						break;
					}
					if (c == null || c.StartsWith(switchPrefix))
					{
						if (!sw.Optional)
						{
							throw new CmdException("At ordinal " + i.ToString() + ": Required argument missing", i);
						}
						else
						{
							ords.Add(sw.Default);
						}
						break;
					}
					switch (sw.Type)
					{
						case CmdSwitchType.OneArg:
							ords.Add(_ParseArgValue(sw, cur));
							break;
						case CmdSwitchType.List:
							ords.Add(_ParseArgValues(sw, cur, switchPrefix));
							break;

					}
				}
			}
			catch (CmdException)
			{
				throw;
			}
			catch (Exception ex)
			{
				throw new CmdException("At ordinal " + i.ToString() + ": " + ex.Message, i, ex);
			}
			var argt = cur.Current;
			while (argt != null)
			{
				if (!argt.StartsWith(switchPrefix))
				{
					throw new ArgumentException("Unexpected value when looking for a switch");
				}
				var name = argt.Substring(switchPrefix.Length);
				try
				{
					if (named.ContainsKey(name))
					{
						throw new CmdException("At switch " + name + ": Duplicate switch", name);
					}
					CmdSwitch sw = CmdSwitch.Empty;
					for (int j = 0; j < switches.Count; ++j)
					{
						sw = switches[j];

						if (sw.Name == name)
						{
							break;
						}

					}
					if (sw.Name == name)
					{
						cur.Advance();
						switch (sw.Type)
						{
							case CmdSwitchType.Simple:
								named.Add(name, true);
								break;
							case CmdSwitchType.OneArg:
								named.Add(name, _ParseArgValue(sw, cur));
								break;
							case CmdSwitchType.List:

								var v = _ParseArgValues(sw, cur, switchPrefix);
								if (v.Length == 0 && sw.Optional == false)
								{
									throw new CmdException("At switch " + sw.Name + ": Required argument not specified", sw.Name);
								}
								named.Add(name, v);
								break;

						}
					}
					else
					{
						throw new CmdException("At switch " + name + ": Invalid switch", sw.Name);
					}
					argt = cur.Current;
				}
				catch (CmdException)
				{
					throw;
				}
				catch (Exception ex)
				{
					throw new CmdException("At switch " + name + ": " + ex.Message, name, ex);
				}
			}
			for (i = 0; i < switches.Count; ++i)
			{
				var sw = switches[i];
				if (!string.IsNullOrEmpty(sw.Name))
				{
					if (sw.Optional == false && !named.ContainsKey(sw.Name))
					{
						throw new CmdException("At switch " + sw.Name + ": Required argument not specified", sw.Name);
					}
				}
			}
			for (i = 0; i < switches.Count; ++i)
			{
				var sw = switches[i];
				if (sw.Optional && sw.Ordinal < 0 && !string.IsNullOrEmpty(sw.Name))
				{
					if (!named.ContainsKey(sw.Name))
					{
						named.Add(sw.Name, sw.Default);
					}
				}
			}
			return new CmdParseResult() { Group = grp, OrdinalArguments = ords, NamedArguments = named };
		}
		/// <summary>
		/// Parses command line arguments
		/// </summary>
		/// <param name="switches">A list of <see cref="CmdSwitch"/> instances describing the switches and arguments</param>
		/// <param name="commandLine">The command line to parse</param>
		/// <param name="switchPrefix">The prefix for switches</param>
		/// <returns>A <see cref="CmdParseResult"/> instance containing the parsed arguments</returns>
		/// <exception cref="ArgumentException">One of the arguments or the switch configuration is invalid</exception>
		public static CmdParseResult ParseArguments(List<CmdSwitch> switches, string commandLine = null, string switchPrefix = null)
		{
			if (string.IsNullOrEmpty(commandLine))
			{
				commandLine = Environment.CommandLine;
			}

			if (string.IsNullOrEmpty(switchPrefix))
			{
				switchPrefix = SwitchPrefix;
			}
			NormalizeAndValidateSwitches(switches);
			var grps = _GroupSwitches(switches);
			var grp = _DetectGroup(grps, commandLine, switchPrefix);
			_StringCursor cur = new _StringCursor() { Input = commandLine.GetEnumerator() };
			cur.EnsureStarted();
			_ParseWithQuoted(cur);
			var ords = new List<object>();
			var named = new Dictionary<string, object>();
			switches = grps[grp];
			var i = 0;
			try
			{
				// process ordinal args
				for (; i < switches.Count; i++)
				{
					var sw = switches[i];
					
					var c = cur.Current;
					if (sw.Ordinal < 0)
					{
						break;
					}
					if (c == null || c.StartsWith(switchPrefix))
					{
						if (!sw.Optional)
						{
							throw new CmdException("At ordinal " + i.ToString() + ": Required argument missing", i);
						}
						else
						{
							ords.Add(sw.Default);
						}
						break;
					}
					switch (sw.Type)
					{
						case CmdSwitchType.OneArg:
							ords.Add(_ParseArgValue(sw, cur));
							break;
						case CmdSwitchType.List:
							ords.Add(_ParseArgValues(sw, cur, switchPrefix));
							break;

					}
				}
			}
			catch (CmdException)
			{
				throw;
			}
			catch (Exception ex)
			{
				throw new CmdException("At ordinal " + i.ToString() + ": " + ex.Message, i, ex);
			}
			var argt = _ParseWithQuoted(cur);
			while (argt.Value != null)
			{
				if (argt.Quoted || !argt.Value.StartsWith(switchPrefix))
				{
					throw new ArgumentException("Unexpected value when looking for a switch");
				}
				var name = argt.Value.Substring(switchPrefix.Length);
				try
				{
					if (named.ContainsKey(name))
					{
						throw new CmdException("At switch " + name + ": Duplicate switch", name);
					}
					CmdSwitch sw = CmdSwitch.Empty;
					for (int j = 0; j < switches.Count; ++j)
					{
						sw = switches[j];
						
						if (sw.Name == name)
						{
							break;
						}

					}
					if (sw.Name == name)
					{
						switch (sw.Type)
						{
							case CmdSwitchType.Simple:
								named.Add(name, true);
								break;
							case CmdSwitchType.OneArg:
								named.Add(name, _ParseArgValue(sw, cur));
								break;
							case CmdSwitchType.List:
								var v = _ParseArgValues(sw, cur, switchPrefix);
								if (v.Length == 0 && sw.Optional == false)
								{
									throw new CmdException("At switch " + sw.Name + ": Required argument not specified", sw.Name);
								}
								named.Add(name, v);
								break;

						}
					}
					else
					{
						throw new CmdException("At switch " + name + ": Invalid switch", sw.Name);
					}
					argt = _ParseWithQuoted(cur);
				}
				catch (CmdException)
				{
					throw;
				}
				catch (Exception ex)
				{
					throw new CmdException("At switch " + name + ": " + ex.Message, name, ex);
				}
			}
			for (i = 0; i < switches.Count; ++i)
			{
				var sw = switches[i];
				if (grp != sw.Group)
				{
					continue;
				}
				if (!string.IsNullOrEmpty(sw.Name))
				{
					if (sw.Optional == false && !named.ContainsKey(sw.Name))
					{
						throw new CmdException("At switch " + sw.Name + ": Required argument not specified", sw.Name);
					}
				}
			}
			for (i = 0; i < switches.Count; ++i)
			{
				var sw = switches[i];
				if (grp != sw.Group)
				{
					continue;
				}
				if (sw.Optional && sw.Ordinal < 0 && !string.IsNullOrEmpty(sw.Name))
				{
					if (!named.ContainsKey(sw.Name))
					{
						named.Add(sw.Name, sw.Default);
					}
				}
			}
			return new CmdParseResult() { Group = grp, OrdinalArguments = ords, NamedArguments = named };
		}
		#region WordWrap
		/// <summary>
		/// Performs word wrapping
		/// </summary>
		/// <param name="text">The text to wrap</param>
		/// <param name="width">The width of the display. Tries to approximate if zero</param>
		/// <param name="indent">The indent for successive lines, in number of spaces</param>
		/// <param name="startOffset">The starting offset of the first line where the text begins</param>
		/// <returns>Wrapped text</returns>
		/// <remarks>This routine accepts \u00A0 non-breaking spaces</remarks>
		public static string WordWrap(string text, int width = 0, int indent = 0, int startOffset = 0)
		{
			if (width == 0)
			{
				width = Console.WindowWidth;
			}
			if (indent < 0) throw new ArgumentOutOfRangeException(nameof(indent));
			if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
			if (width > 0 && width < indent)
			{
				throw new ArgumentOutOfRangeException(nameof(width));
			}
			string[] words = text.Split(new string[] { " " },
				StringSplitOptions.None);

			StringBuilder result = new StringBuilder();
			double actualWidth = startOffset;
			for (int i = 0; i < words.Length; i++)
			{
				var word = words[i];
				if (i > 0)
				{
					if (actualWidth + word.Length >= width)
					{
						result.Append(Environment.NewLine);
						if (indent > 0)
						{
							result.Append(new string(' ', indent));
						}
						actualWidth = indent;
					}
					else
					{
						result.Append(' ');
						++actualWidth;
					}
				}
				result.Append(word.Replace('\u00A0', ' '));
				actualWidth += word.Length;
			}
			return result.ToString();
		}

		#endregion // WordWrap
		/// <summary>
		/// Gets the command line argument portion of the usage information
		/// </summary>
		/// <param name="switches">A list of <see cref="CmdSwitch"/> instances</param>
		/// <param name="switchPrefix">The switch prefix to use</param>
		/// <param name="width">The width in characters</param>
		/// <param name="startOffset">The starting column where the arguments will be printed</param>
		/// <param name="nonBreaking">Returns with non-breaking spaces</param>
		/// <param name="group">The group name of the arguments</param>
		/// <returns>A string indicating the usage arguments</returns>
		public static string GetUsageArguments(List<CmdSwitch> switches, string switchPrefix = null, int width = 0, int startOffset = 0, bool nonBreaking = false, string group = null)
		{
			const int indent = 4;
			group ??= "";
			if (string.IsNullOrEmpty(switchPrefix))
			{
				switchPrefix = SwitchPrefix;
			}
			NormalizeAndValidateSwitches(switches);
			var sb = new StringBuilder();
			var first = true;
			for (int i = 0; i < switches.Count; ++i)
			{
				var sw = switches[i];
				if (group != sw.Group)
				{
					continue;
				}
				if (!first)
				{
					sb.Append(" ");
				}
				else
				{
					first = false;
				}

				if (sw.Optional)
				{
					if (nonBreaking)
					{
						sb.Append("[\u00A0");
					}
					else
					{
						sb.Append("[ ");
					}
				}
				if (!string.IsNullOrEmpty(sw.Name))
				{
					sb.Append(switchPrefix);
					sb.Append(sw.Name);
					if (sw.Type != CmdSwitchType.Simple)
					{
						if (nonBreaking)
						{
							sb.Append('\u00A0');
						}
						else
						{
							sb.Append(' ');
						}
					}
				}
				switch (sw.Type)
				{
					case CmdSwitchType.OneArg:
						sb.Append("<");
						sb.Append(sw.ElementName);
						sb.Append(">");
						break;
					case CmdSwitchType.List:
						if (nonBreaking)
						{
							sb.Append("{\u00A0<");
						}
						else
						{
							sb.Append("{ <");
						}
						sb.Append(sw.ElementName);
						sb.Append("1>, ");
						sb.Append(" <");
						sb.Append(sw.ElementName);
						if (nonBreaking)
						{
							sb.Append("2>, ...\u00A0}");
						}
						else
						{
							sb.Append("2>, ... }");
						}
						break;
				}
				if (sw.Optional)
				{
					if (nonBreaking)
					{
						sb.Append("\u00A0]");
					}
					else
					{
						sb.Append(" ]");
					}
				}
			}
			return WordWrap(sb.ToString(), width, indent, startOffset);
		}
		/// <summary>
		/// Returns the assembly title as set by the <see cref="AssemblyTitleAttribute" />
		/// </summary>
		public static string AssemblyTitle
		{
			get
			{
				var attr = Assembly.GetEntryAssembly().GetCustomAttribute<AssemblyTitleAttribute>();
				if (attr != null)
				{
					return attr.Title;
				}
				return null;
			}
		}
		/// <summary>
		/// Returns the assembly description as set by the <see cref="AssemblyDescriptionAttribute" />
		/// </summary>
		public static string AssemblyDescription
		{
			get
			{
				var attr = Assembly.GetEntryAssembly().GetCustomAttribute<AssemblyDescriptionAttribute>();
				if (attr != null)
				{
					return attr.Description;
				}
				return null;
			}
		}
		/// <summary>
		/// Indicates whether or not the operating system is Microsoft Windows based
		/// </summary>
		public static bool IsWindows
		{
			get
			{
				return (Environment.OSVersion.Platform == PlatformID.Win32NT || Environment.OSVersion.Platform == PlatformID.Win32Windows || Environment.OSVersion.Platform == PlatformID.Win32S);
			}
		}
		/// <summary>
		/// Gets the platform specific switch prefix
		/// </summary>
		public static string SwitchPrefix
		{
			get
			{
				return "--";
				
			}
		}
		/// <summary>
		/// Retrieves an array of all unique groups within the switch list
		/// </summary>
		/// <param name="switches">The switch list</param>
		/// <returns>An array of strings containing the group names</returns>
		static string[] GetGroups(List<CmdSwitch> switches)
		{
			var groups = new List<string>();
			for (var i = 0; i < switches.Count; ++i)
			{
				if (!groups.Contains(switches[i].Group))
				{
					groups.Add(switches[i].Group);
				}
			}
			return groups.ToArray();
		}
		/// <summary>
		/// Prints the usage screen
		/// </summary>
		/// <param name="switches">The list of switches</param>
		/// <param name="width">The width in characters to wrap to (defaults to console width)</param>
		/// <param name="writer">The writer to write to (defaults to stderr)</param>
		/// <param name="switchPrefix">The switch prefix to use</param>
		public static void PrintUsage(List<CmdSwitch> switches, int width = 0, TextWriter writer = null, string switchPrefix = null)
		{
			if (string.IsNullOrEmpty(switchPrefix))
			{
				switchPrefix = SwitchPrefix;
			}
			const int indent = 4;
			writer ??= Console.Error;

			var asm = Assembly.GetEntryAssembly();
			string desc = null;
			string ver = null;
			string name = asm.GetName().Name;
			var asmVer = asm.GetName().Version;
			ver = asmVer.ToString();
			var asmTitle = AssemblyTitle;
			var asmDesc = AssemblyDescription;
			desc = string.IsNullOrEmpty(asmDesc) ? null : asmDesc;

			if (!string.IsNullOrEmpty(asmTitle))
			{
				name = asmTitle;
			}

			writer.WriteLine(WordWrap(name + " v" + ver, width, indent));
			writer.WriteLine();
			if (!string.IsNullOrEmpty(desc))
			{
				writer.WriteLine(WordWrap(desc, width, indent));
				writer.WriteLine();
			}
			var path = ParseExePath(Environment.CommandLine);
			var grps = GetGroups(switches);
			if (grps.Length == 1) // simple usage
			{
				var str = "Usage: " + Path.GetFileNameWithoutExtension(path) + " ";
				writer.Write(str);
				writer.WriteLine(GetUsageArguments(switches, switchPrefix, width, str.Length, true, grps[0]));
				writer.WriteLine();
				writer.WriteLine(GetUsageCommandDescription(switches, switchPrefix, width, grps[0]));
			}
			else // grouped usage
			{
				var exe = Path.GetFileNameWithoutExtension(path);
				writer.WriteLine("Usage:");
				for (int i = 0; i < grps.Length; ++i)
				{
					writer.WriteLine();
					var str = exe + " ";
					writer.Write(str);
					writer.WriteLine(GetUsageArguments(switches, switchPrefix, width, str.Length, true, grps[i]));
					writer.WriteLine();
					writer.Write(GetUsageCommandDescription(switches, switchPrefix, width, grps[i]));
				}
			}
		}
		/// <summary>
		/// Retrieves the description portion of the usage information
		/// </summary>
		/// <param name="switches">The list if <see cref="CmdSwitch"/> instances</param>
		/// <param name="switchPrefix">The switch prefix to use</param>
		/// <param name="width">The width in characters</param>
		/// <param name="group">The group to emit the description for</param>
		/// <returns>A string wrapped to the width containing a description for each switch</returns>
		public static string GetUsageCommandDescription(List<CmdSwitch> switches, string switchPrefix, int width = 0, string group = null)
		{
			const int indent = 4;
			switchPrefix ??= SwitchPrefix;
			NormalizeAndValidateSwitches(switches);
			group ??= "";
			var left = new string(' ', indent);
			var max_len = 0;
			for (var i = 0; i < switches.Count; ++i)
			{
				var sw = switches[i];
				var len = sw.Type==CmdSwitchType.Simple?sw.Name.Length+switchPrefix.Length:sw.ElementName.Length+2;
				if (len > max_len)
				{
					max_len = len;
				}
			}
			var sb = new StringBuilder();
			var sbLine = new StringBuilder();
			for (var i = 0; i < switches.Count; ++i)
			{
				var sw = switches[i];
				if (sw.Group != group)
				{
					continue;
				}
				sbLine.Clear();
				sbLine.Append(left);
				string estr;
				if (sw.Type != CmdSwitchType.Simple)
				{
					estr = "<"+sw.ElementName+">";
				} else
				{
					estr = switchPrefix + sw.Name;
				}
				sb.Append(estr);
				sbLine.Append(new string(' ', max_len - estr.Length + 1));
				sbLine.Append(sw.Description);
				if (sw.Type != CmdSwitchType.Simple && sw.Default != null)
				{
					object val = sw.Default;
					if (sw.Type == CmdSwitchType.List)
					{
						if (val is Array arr)
						{
							if (arr != null && arr.Rank == 1 && arr.Length == 1)
							{
								val = arr.GetValue(0);
							}
						}
						else
						{
							var et = _GetListElementType(val.GetType());
							var col = val as System.Collections.ICollection;

							if (col != null && et != null && col.Count == 1)
							{
								foreach (var v in col)
								{
									val = v;
									break;
								}
							}
						}
					}
					if ((sw.Optional || sw.Type == CmdSwitchType.List) && (string.IsNullOrEmpty(sw.Description) || (sw.Description.IndexOf("default", StringComparison.InvariantCultureIgnoreCase) < 0)))
					{
						string str = _ValueToString(val, sw.ElementType, sw.ElementConverter);
						if (!string.IsNullOrEmpty(sw.Description) && !sw.Description.TrimEnd().EndsWith("."))
						{
							sbLine.Append('.');
						}
						if (!string.IsNullOrEmpty(sw.Description))
						{
							sbLine.Append(' ');
						}
						sbLine.Append("Defaults to ");
						sbLine.Append(str);
					}
				}
				sb.AppendLine(WordWrap(sbLine.ToString(), width, indent * 2));
			}
			return sb.ToString();
		}
		private static object _ReflGetValue(MemberInfo m,object instance)
		{
			if (m is PropertyInfo pi)
			{
				return pi.GetValue(instance);
			}
			else if (m is FieldInfo fi)
			{
				return fi.GetValue(instance);
			}
			return null;
		}
		private static void _ReflSetValue(MemberInfo m, object instance,object value)
		{
			if (m is PropertyInfo pi)
			{
				pi.SetValue(instance, value);
			}
			else if (m is FieldInfo fi)
			{
				fi.SetValue(instance, value);
			}
		}
		private static void _ListClear(object list, ref MethodInfo cm)
		{
			var t = list.GetType();
			if (cm != null)
			{
				cm.Invoke(list, null);
				return;
			}
			foreach (var m in t.GetMember("Clear"))
			{
				if (m is MethodInfo mt)
				{
					var pa = mt.GetParameters();
					if (pa.Length == 0)
					{
						cm = mt;
						mt.Invoke(list, null);
						return;
					}
				}
			}
			throw new Exception("Clear() method not found on list");
		}
		private static void _ListAdd(object list, object value, ref MethodInfo am)
		{
			var t = typeof(object);
			if (value != null)
			{
				t = value.GetType();
			}
			if (am != null)
			{
				am.Invoke(list, new object[] { value });
				return;
			}
			if (list is System.Collections.ICollection)
			{
				foreach (var mi in list.GetType().GetMember("Add"))
				{
					if (mi is MethodInfo mti)
					{
						var pa = mti.GetParameters();
						if (pa.Length == 1 && pa[0].ParameterType.IsAssignableFrom(t))
						{
							am = mti;
							mti.Invoke(list, new object[] { value });
							return;
						}
					}
				}
			}
			throw new Exception("Add method not found");
		}
		/// <summary>
		/// Sets the values from a parse result to the command arg static fields on the specified type
		/// </summary>
		/// <param name="switches">The list of <see cref="CmdSwitch"/> instances</param>
		/// <param name="result">The parse result</param>
		/// <param name="target">The target instance or null if static</param>
		/// <param name="targetType">The type to set the fields on</param>
		/// <param name="group">The group to set</param>
		public static void SetValues(List<CmdSwitch> switches, CmdParseResult result, object target = null, Type targetType = null,  string group = null)
		{
			targetType ??= target.GetType();
			group ??= "";
			var members = targetType.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Static);
			foreach (var member in members)
			{
				var cmdArg = member.GetCustomAttribute<CmdArgAttribute>();
				var found = false;
				object value = null;
				var cmdSw = CmdSwitch.Empty;
				Type mt = null;
				if (cmdArg != null)
				{
					mt = _GetMemberType(member);
					var g = cmdArg.Group;
					g ??= "";
					if (g != group)
					{
						continue;
					}
					if (cmdArg.Ordinal > -1)
					{
						for (var i = 0; i < switches.Count; ++i)
						{
							var sw = switches[i];
							if (cmdArg.Ordinal == sw.Ordinal)
							{
								cmdSw = sw;
								found = true; break;
							}
						}
					}
					else
					{
						var n = member.Name;
						if (!string.IsNullOrEmpty(cmdArg.Name))
						{
							n = cmdArg.Name;
						}
						for (int i = 0; i < switches.Count; ++i)
						{
							var sw = switches[i];

							if (n == sw.Name)
							{
								cmdSw = sw;
								found = true; break;
							}
						}
					}
				}
				if (found)
				{
					value = cmdSw.Default;
					if (cmdSw.Ordinal > -1)
					{
						value = result.OrdinalArguments[cmdSw.Ordinal];
					}
					else
					{
						value = result.NamedArguments[cmdSw.Name];
					}
					if (cmdSw.Type == CmdSwitchType.List)
					{
						if (mt.IsArray)
						{
							var newArr = Array.CreateInstance(mt.GetElementType(), ((Array)value).Length);
							var i = 0;
							foreach (var obj in (System.Collections.IEnumerable)value)
							{
								newArr.SetValue(obj, i);
								++i;
							}
							_ReflSetValue(member, target,newArr);
						}
						else
						{
							var et = _GetListElementType(mt);
							if (et == null)
							{
								throw new Exception("Invalid list member type");
							}
							var list = _GetMemberValue(member,target);
							if (list == null)
							{
								throw new Exception("List not set");
							}
							var arr = Array.CreateInstance(et, ((System.Collections.ICollection)value).Count);
							((System.Collections.ICollection)value).CopyTo(arr, 0);
							MethodInfo mci = null;
							MethodInfo mca = null;
							_ListClear(list, ref mci);
							var i = 0;
							foreach(var obj in (System.Collections.IEnumerable)arr)
							{
								_ListAdd(list, obj, ref mca);
								++i;
							}
							
						}
					}
					//else if (cmdSw.Type == CmdSwitchType.Simple)
					//{
					//	_ReflSetValue(member,target, true);
					//}
					else
					{
						_ReflSetValue(member, target,value);
					}
				}
			}
		}
		private static Type _GetMemberType(MemberInfo mi)
		{
			if (mi is PropertyInfo pi)
			{
				return pi.PropertyType;
			}
			else if (mi is FieldInfo fi)
			{
				return fi.FieldType;
			}
			return null;
		}
		private static object _GetMemberValue(MemberInfo mi, object instance)
		{
			if (mi is PropertyInfo pi)
			{
				return pi.GetValue(instance);
			}
			else if (mi is FieldInfo fi)
			{
				return fi.GetValue(instance);
			}
			return null;
		}
		private static Type _GetListElementType(Type type)
		{
			foreach (var it in type.GetInterfaces())
			{
				if (!it.IsGenericType) continue;
				var tdef = it.GetGenericTypeDefinition();
				if (typeof(ICollection<>) == tdef)
				{
					return type.GenericTypeArguments[0];
				}
			}
			if (typeof(System.Collections.ICollection).IsAssignableFrom(type))
			{
				foreach (var mi in type.GetMember("Add"))
				{
					if (mi is MethodInfo mti)
					{
						var pa = mti.GetParameters();
						if (pa.Length == 1)
						{
							return pa[0].ParameterType;
						}
					}
				}
			}
			return null;
		}
		/// <summary>
		/// Retrieves all the switches defined as static fields or properties on a type
		/// </summary>
		/// <param name="target">The target instance, or null if static</param>
		/// <param name="targetType">The type to reflect</param>
		/// <returns>A list of <see cref="CmdSwitch" /> instances based on the reflected members</returns>
		/// <exception cref="Exception">The attribute was on something other than a field or property</exception>
		public static List<CmdSwitch> GetSwitches(object target,Type targetType=null)
		{
			targetType ??= target.GetType();
			var result = new List<CmdSwitch>();
			var members = targetType.GetMembers(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (var member in members)
			{
				var cmdArg = member.GetCustomAttribute<CmdArgAttribute>();
				if (cmdArg != null)
				{
					CmdSwitch cmdSwitch = CmdSwitch.Empty;
					if (cmdArg.Ordinal > -1)
					{
						cmdSwitch.Ordinal = cmdArg.Ordinal;
					}
					else
					{
						cmdSwitch.Name = cmdArg.Name;
						if (string.IsNullOrEmpty(cmdSwitch.Name))
						{
							cmdSwitch.Name = member.Name;
						}
					}
					cmdSwitch.Description = cmdArg.Description;
					cmdSwitch.Optional = cmdArg.Optional;
					cmdSwitch.ElementName = cmdArg.ElementName;
					cmdSwitch.Group = cmdArg.Group;
					if (!string.IsNullOrEmpty(cmdArg.ElementConverter))
					{

						var t = Type.GetType(cmdArg.ElementConverter, false, false);
						t ??= Assembly.GetCallingAssembly().GetType(cmdArg.ElementConverter, true, false);

						cmdSwitch.ElementConverter = Activator.CreateInstance(t) as TypeConverter;

					}
					Type mtype = _GetMemberType(member);

					if (mtype != null)
					{
						if (mtype.IsArray)
						{
							cmdSwitch.Type = CmdSwitchType.List;
							cmdSwitch.ElementType = mtype.GetElementType();
						}
						else if (mtype == typeof(bool))
						{
							cmdSwitch.Type = CmdSwitchType.Simple;
							cmdSwitch.ElementType = typeof(bool);
						}
						else
						{
							var et = _GetListElementType(mtype);
							if (et != null)
							{
								cmdSwitch.Type = CmdSwitchType.List;
								cmdSwitch.ElementType = et;
							}
							else
							{
								cmdSwitch.Type = CmdSwitchType.OneArg;
								cmdSwitch.ElementType = mtype;
							}
						}
						cmdSwitch.Default = _GetMemberValue(member,target);
					}
					else
					{
						// shouldn't get here
						throw new Exception("Invalid attribute target");
					}
					result.Add(cmdSwitch);
				}

			}
			NormalizeAndValidateSwitches(result);
			return result;
		}
		/// <summary>
		/// Parses, validates and sets fields and properties with the command line and target type
		/// </summary>
		/// <param name="commandLine">The command line, or null to use the environment's current command line</param>
		/// <param name="target">The target instance or null if static</param>
		/// <param name="targetType">The type with the static fields and/or properties to set</param>
		/// <param name="width">The width in characters, or 0 to use the console window width</param>
		/// <param name="writer">The writer to write the help screen to or null to use stderr</param>
		/// <param name="switchPrefix">The switch prefix to use</param>
		/// <returns>The result of the parse</returns>
		public static CmdParseResult ParseAndSet(string commandLine, object target, Type targetType = null, int width = 0, TextWriter writer = null, string switchPrefix = null)
		{
			targetType??=target.GetType();
			List<CmdSwitch> switches = null;
			CmdParseResult result = null;
			try
			{
				switches = GetSwitches(targetType);
				result = ParseArguments(switches, commandLine, switchPrefix);
				SetValues(switches, result, target,targetType, result.Group);
				return result;
			}
			catch
			{
				if (switches != null)
				{
					writer ??= Console.Error;
					PrintUsage(switches, width, writer, switchPrefix);
					writer.WriteLine();
				}
				throw;
			}
		}
		/// <summary>
		/// Parses, validates and sets fields and properties with the command line and target type
		/// </summary>
		/// <param name="commandLine">The command line arguments</param>
		/// <param name="target">The target instance, or null if static</param>
		/// <param name="targetType">The type with the static fields and/or properties to set</param>
		/// <param name="width">The width in characters, or 0 to use the console window width</param>
		/// <param name="writer">The writer to write the help screen to or null to use stderr</param>
		/// <param name="switchPrefix">The switch prefix to use</param>
		/// <returns>The result of the parse</returns>
		public static CmdParseResult ParseAndSet(IEnumerable<string> commandLine, object target,Type targetType=null, int width = 0, TextWriter writer = null, string switchPrefix = null)
		{
			targetType ??= target.GetType();
			List<CmdSwitch> switches = null;
			CmdParseResult result = null;
			try
			{
				switches = GetSwitches(target,targetType);
				result = ParseArguments(switches, commandLine, switchPrefix);
				SetValues(switches, result, target,targetType, result.Group);
				return result;
			}
			catch
			{
				if (switches != null)
				{
					writer ??= Console.Error;
					PrintUsage(switches, width, writer, switchPrefix);
					writer.WriteLine();
				}
				throw;
			}
		}
		#region IsStale
		/// <summary>
		/// Indicates whether outputfile doesn't exist or is old
		/// </summary>
		/// <param name="inputfile">The master file to check the date of</param>
		/// <param name="outputfile">The output file which is compared against <paramref name="inputfile"/></param>
		/// <returns>True if <paramref name="outputfile"/> doesn't exist or is older than <paramref name="inputfile"/></returns>
		public static bool IsStale(string inputfile, string outputfile)
		{
			var result = true;
			// File.Exists doesn't always work right
			try
			{
				if (File.GetLastWriteTimeUtc(outputfile) >= File.GetLastWriteTimeUtc(inputfile))
					result = false;
			}
			catch { }
			return result;
		}
		/// <summary>
		/// Indicates whether outputfile doesn't exist or is old
		/// </summary>
		/// <param name="inputfile">The master file to check the date of</param>
		/// <param name="outputfile">The output file which is compared against <paramref name="inputfile"/></param>
		/// <returns>True if <paramref name="outputfile"/> doesn't exist or is older than <paramref name="inputfile"/></returns>
		public static bool IsStale(FileSystemInfo inputfile, FileSystemInfo outputfile)
		{
			var result = true;
			// File.Exists doesn't always work right
			try
			{
				if (File.GetLastWriteTimeUtc(outputfile.FullName) >= File.GetLastWriteTimeUtc(inputfile.FullName))
					result = false;
			}
			catch { }
			return result;
		}
		/// <summary>
		/// Indicates whether <paramref name="outputfile"/>'s file doesn't exist or is old
		/// </summary>
		/// <param name="inputfiles">The master files to check the date of</param>
		/// <param name="outputfile">The output file which is compared against each of the <paramref name="inputfiles"/></param>
		/// <returns>True if <paramref name="outputfile"/> doesn't exist or is older than <paramref name="inputfiles"/> or if any don't refer to a file</returns>
		public static bool IsStale(IEnumerable<FileSystemInfo> inputfiles, FileSystemInfo outputfile)
		{
			var result = true;
			foreach (var input in inputfiles)
			{
				result = false;
				if (IsStale(input, outputfile))
				{
					result = true;
					break;
				}
			}
			return result;
		}
		/// <summary>
		/// Indicates whether <paramref name="outputfile"/>'s file doesn't exist or is old
		/// </summary>
		/// <param name="inputfiles">The master files to check the date of</param>
		/// <param name="outputfile">The output file which is compared against each of the <paramref name="inputfiles"/></param>
		/// <returns>True if <paramref name="outputfile"/> doesn't exist or is older than <paramref name="inputfiles"/> or if any don't refer to a file</returns>
		public static bool IsStale(IEnumerable<FileInfo> inputfiles, FileInfo outputfile)
		{
			var result = true;
			foreach (var input in inputfiles)
			{
				result = false;
				if (IsStale(input, outputfile))
				{
					result = true;
					break;
				}
			}
			return result;
		}
		/// <summary>
		/// Indicates whether outputfile doesn't exist or is old
		/// </summary>
		/// <param name="input">The input reader to check the date of</param>
		/// <param name="output">The output writer which is compared against <paramref name="input"/></param>
		/// <returns>True if the file behind <paramref name="output"/> doesn't exist or is older than the file behind <paramref name="input"/> or if any are not files.</returns>
		public static bool IsStale(TextReader input, TextWriter output)
		{
			var result = true;
			var inputfile = GetFilename(input);
			if (inputfile == null)
			{
				return result;
			}
			var outputfile = GetFilename(output);
			if (outputfile == null)
			{
				return result;
			}
			// File.Exists doesn't always work right
			try
			{
				if (File.GetLastWriteTimeUtc(outputfile) >= File.GetLastWriteTimeUtc(inputfile))
					result = false;
			}
			catch { }
			return result;
		}
		/// <summary>
		/// Indicates whether <paramref name="output"/>'s file doesn't exist or is old
		/// </summary>
		/// <param name="inputs">The master files to check the date of</param>
		/// <param name="output">The output file which is compared against each of the <paramref name="inputs"/></param>
		/// <returns>True if <paramref name="output"/> doesn't exist or is older than <paramref name="inputs"/> or if any don't refer to a file</returns>
		public static bool IsStale(IEnumerable<TextReader> inputs, TextWriter output)
		{
			var result = true;
			foreach (var input in inputs)
			{
				result = false;
				if (IsStale(input, output))
				{
					result = true;
					break;
				}
			}
			return result;
		}
		#endregion // IsStale
		#region GetFilename
		/// <summary>
		/// Gets the filename for a <see cref="TextReader"/>if available
		/// </summary>
		/// <param name="t">The <see cref="TextReader"/> to examine</param>
		/// <returns>The filename, if available, or null</returns>
		public static string GetFilename(TextReader t)
		{
			var sr = t as StreamReader;
			string result = null;
			if (sr != null)
			{
				FileStream fstm = sr.BaseStream as FileStream;
				if (fstm != null)
				{
					result = fstm.Name;
				}
			}
			if (!string.IsNullOrEmpty(result))
			{
				return result;
			}
			return null;
		}
		/// <summary>
		/// Gets the filename for a <see cref="TextWriter"/>if available
		/// </summary>
		/// <param name="t">The <see cref="TextWriter"/> to examine</param>
		/// <returns>The filename, if available, or null</returns>
		public static string GetFilename(TextWriter t)
		{
			var dtw = t as _DeferredTextWriter;
			if (dtw != null)
			{
				return dtw.Name;
			}
			var sw = t as StreamWriter;
			string result = null;
			if (sw != null)
			{
				FileStream fstm = sw.BaseStream as FileStream;
				if (fstm != null)
				{
					result = fstm.Name;
				}
			}
			if (!string.IsNullOrEmpty(result))
			{
				return result;
			}
			return null;
		}
		#endregion GetFilename
		#region WriteProgressBar/WriteProgress
		const char _block = '■';
		const string _back = "\b\b\b\b\b\b\b\b\b\b\b\b\b\b\b\b\b";
		const string _twirl = "-\\|/";
		/// <summary>
		/// Writes a progress bar
		/// </summary>
		/// <param name="percent">The percentage from 0 to 100</param>
		/// <param name="update">False if this is the first call, otherwise true</param>
		/// <param name="writer">The writer to write to - defaults to <see cref="Console.Error"/></param>
		public static void WriteProgressBar(int percent, bool update = true, TextWriter writer = null)
		{
			writer ??= Console.Error;
			if (update)
				writer.Write(_back);
			writer.Write("[");
			var p = (int)((percent / 10f) + .5f);
			for (var i = 0; i < 10; ++i)
			{
				if (i >= p)
					writer.Write(' ');
				else
					writer.Write(_block);
			}
			writer.Write("] {0,3:##0}%", percent);
		}
		/// <summary>
		/// Writes an indeterminate progress indicator
		/// </summary>
		/// <param name="progress">An integer progress indicator. Keep incrementing this value as you progress,</param>
		/// <param name="update">False if this is the first call, otherwise true</param>
		/// <param name="writer">The writer to write to - defaults to <see cref="Console.Error"/></param>
		public static void WriteProgress(int progress, bool update = false, TextWriter writer = null)
		{
			writer ??= Console.Error;
			if (update)
				writer.Write("\b");
			writer.Write(_twirl[progress % _twirl.Length]);
		}
		#endregion

	}
}