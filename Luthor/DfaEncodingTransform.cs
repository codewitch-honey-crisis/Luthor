using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;

namespace Luthor
{
    static class DfaEncodingTransform
    {
        /// <summary>
        /// Transforms a DFA to work with the specified encoding
        /// </summary>
        /// <param name="dfa">The source DFA (in Unicode codepoint space)</param>
        /// <param name="encoding">The target encoding</param>
        /// <returns>A transformed DFA that operates on the target encoding's byte space</returns>
        /// <exception cref="ArgumentException">Thrown for unsupported multi-byte encodings</exception>
        public static Dfa Transform(Dfa dfa, Encoding encoding)
        {
            if (dfa == null) throw new ArgumentNullException(nameof(dfa));
            if (encoding == null) throw new ArgumentNullException(nameof(encoding));

            return encoding switch
            {
                UTF8Encoding => DfaUtf8Transformer.TransformToUtf8(dfa),
                UnicodeEncoding => DfaUtf16Transformer.TransformToUtf16(dfa),
                UTF32Encoding => dfa, // Native format - no transformation needed
                _ when encoding.IsSingleByte => TransformToSingleByte(dfa, encoding),
                _ => throw new ArgumentException($"Encoding '{encoding.EncodingName}' is not supported. Only UTF-8, UTF-16, UTF-32, and single-byte encodings are supported.", nameof(encoding))
            };
        }

        /// <summary>
        /// Transforms a DFA to work with single-byte encoding
        /// </summary>
        private static Dfa TransformToSingleByte(Dfa dfa, Encoding encoding)
        {
            var closure = dfa.FillClosure();
            var stateMap = new Dictionary<Dfa, Dfa>();

            // Create new states for each existing state
            foreach (var state in closure)
            {
                var newState = new Dfa();
                newState.Attributes["AcceptSymbol"] = state.AcceptSymbol;

                // Copy anchor mask if present
                if (state.Attributes.ContainsKey("AnchorMask"))
                {
                    newState.Attributes["AnchorMask"] = state.Attributes["AnchorMask"];
                }

                stateMap[state] = newState;
            }

            // Transform transitions
            foreach (var state in closure)
            {
                var newState = stateMap[state];
                var transitionGroups = state.FillInputTransitionRangesGroupedByState();

                foreach (var group in transitionGroups)
                {
                    var targetState = stateMap[group.Key];
                    var mappableRanges = GetMappableByteRanges(group.Value, encoding);

                    // Add transitions for each mappable range
                    foreach (var range in mappableRanges)
                    {
                        newState.AddTransition(new DfaTransition(targetState, range.Min, range.Max));
                    }
                }
            }

            return stateMap[dfa];
        }

        /// <summary>
        /// Converts Unicode codepoint ranges to mappable byte ranges for the given encoding
        /// </summary>
        private static List<DfaRange> GetMappableByteRanges(IList<DfaRange> unicodeRanges, Encoding encoding)
        {
            var byteRanges = new List<DfaRange>();

            foreach (var range in unicodeRanges)
            {
                for (int codepoint = range.Min; codepoint <= range.Max; codepoint++)
                {
                    var byteValue = TryMapCodepointToByte(codepoint, encoding);
                    if (byteValue.HasValue)
                    {
                        byteRanges.Add(new DfaRange(byteValue.Value, byteValue.Value));
                    }
                    // Unmappable characters are removed (not added to ranges)
                }
            }

            return NormalizeByteRanges(byteRanges);
        }

        /// <summary>
        /// Attempts to map a Unicode codepoint to a single byte in the target encoding
        /// </summary>
        /// <param name="codepoint">The Unicode codepoint</param>
        /// <param name="encoding">The target encoding</param>
        /// <returns>The byte value if mappable, null if unmappable</returns>
        private static byte? TryMapCodepointToByte(int codepoint, Encoding encoding)
        {
            try
            {
                // Convert codepoint to string
                var str = char.ConvertFromUtf32(codepoint);

                // Encode to bytes
                var bytes = encoding.GetBytes(str);

                // Should be exactly one byte for single-byte encodings
                if (bytes.Length != 1)
                    return null;

                // Verify round-trip to ensure no data loss
                var decoded = encoding.GetString(bytes);
                if (decoded != str)
                    return null;

                return bytes[0];
            }
            catch
            {
                // Any encoding failure means unmappable
                return null;
            }
        }

        /// <summary>
        /// Normalizes and merges overlapping/adjacent byte ranges
        /// </summary>
        private static List<DfaRange> NormalizeByteRanges(List<DfaRange> ranges)
        {
            if (ranges.Count == 0)
                return ranges;

            // Sort by min value
            ranges.Sort((a, b) => a.Min.CompareTo(b.Min));

            var normalized = new List<DfaRange>();
            var current = ranges[0];

            for (int i = 1; i < ranges.Count; i++)
            {
                var next = ranges[i];

                // If ranges are adjacent or overlapping, merge them
                if (current.Max + 1 >= next.Min)
                {
                    current = new DfaRange(current.Min, Math.Max(current.Max, next.Max));
                }
                else
                {
                    normalized.Add(current);
                    current = next;
                }
            }

            normalized.Add(current);
            return normalized;
        }

        /// <summary>
        /// Gets the name of the encoding for diagnostic purposes
        /// </summary>
        public static string GetEncodingName(Encoding encoding)
        {
            return encoding switch
            {
                UTF8Encoding => "UTF-8",
                UnicodeEncoding => "UTF-16",
                UTF32Encoding => "UTF-32",
                _ when encoding.IsSingleByte => encoding.EncodingName,
                _ => $"{encoding.EncodingName} (unsupported)"
            };
        }

        /// <summary>
        /// Determines if an encoding is supported by this transformer
        /// </summary>
        public static bool IsEncodingSupported(Encoding encoding)
        {
            return encoding is UTF8Encoding or UnicodeEncoding or UTF32Encoding || encoding.IsSingleByte;
        }
    }
}