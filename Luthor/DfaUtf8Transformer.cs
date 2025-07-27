using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Luthor
{

    /// <summary>
    /// Transforms a Unicode codepoint-based DFA into a UTF-8 byte-based DFA
    /// </summary>
    class DfaUtf8Transformer
    {

        public static Dfa TransformToUtf8(Dfa unicodeDfa)
        {
            var stateMap = new Dictionary<Dfa, Dfa>();
            var intermediateStates = new Dictionary<string, Dfa>();


            // Get all states in the original DFA
            var allStates = unicodeDfa.FillClosure();

            // Pre-create all mapped states
            foreach (var originalState in allStates)
            {
                GetMappedState(originalState, stateMap);
            }

            // Transform each state's transitions
            foreach (var originalState in allStates)
            {
                TransformState(originalState, stateMap, intermediateStates);
            }

            // Return the mapped start state
            return GetMappedState(unicodeDfa, stateMap);
        }

        private static void TransformState(Dfa originalState, Dictionary<Dfa, Dfa> stateMap, Dictionary<string, Dfa> intermediateStates)
        {
            var utf8State = GetMappedState(originalState, stateMap);

            // Group transitions by destination state for processing
            var transitionGroups = originalState.FillInputTransitionRangesGroupedByState();

            foreach (var group in transitionGroups)
            {
                var destState = group.Key;
                var ranges = group.Value;

                // Convert each range to UTF-8 transitions
                foreach (var range in ranges)
                {
                    CreateUtf8Transitions(utf8State, destState, range.Min, range.Max, stateMap, intermediateStates);
                }
            }
        }

        private static void CreateUtf8Transitions(Dfa fromState, Dfa originalDestState, int minCodepoint, int maxCodepoint, Dictionary<Dfa, Dfa> stateMap, Dictionary<string, Dfa> intermediateStates)
        {
            // Handle special anchor codepoints (negative values) - pass through unchanged
            if (minCodepoint < 0 || maxCodepoint < 0)
            {
                var destState = GetMappedState(originalDestState, stateMap);
                fromState.AddTransition(new DfaTransition(destState, minCodepoint, maxCodepoint));
                return;
            }

            // Convert the codepoint range to UTF-8 patterns
            var utf8Patterns = ConvertCodepointRangeToUtf8Patterns(minCodepoint, maxCodepoint);

            // For each pattern, create the necessary state chain
            foreach (var pattern in utf8Patterns)
            {
                CreateUtf8StateChain(fromState, originalDestState, pattern, stateMap, intermediateStates);
            }
        }

        private static void CreateUtf8StateChain(Dfa fromState, Dfa originalDestState, Utf8BytePattern pattern, Dictionary<Dfa, Dfa> stateMap, Dictionary<string, Dfa> intermediateStates)
        {
            var currentState = fromState;

            // Create intermediate states for all but the last byte
            for (int i = 0; i < pattern.ByteRanges.Count - 1; i++)
            {
                var byteRange = pattern.ByteRanges[i];

                // Create a unique key for this intermediate state
                var stateKey = CreateIntermediateStateKey(fromState, originalDestState, pattern, i);

                Dfa nextState;
                if (!intermediateStates.TryGetValue(stateKey, out nextState))
                {
                    // Create new intermediate state
                    nextState = new Dfa();
                    // Mark as intermediate (not accepting)
                    nextState.Attributes["AcceptSymbol"] = -1;
                    nextState.Attributes["IsIntermediate"] = true;
                    nextState.Attributes["IntermediateFor"] = $"{fromState.GetHashCode()}->{originalDestState.GetHashCode()}";

                    intermediateStates[stateKey] = nextState;
                }

                // Add transition for this byte range
                currentState.AddTransition(new DfaTransition(nextState, byteRange.Min, byteRange.Max));
                currentState = nextState;
            }

            // Final transition to the mapped destination state
            if (pattern.ByteRanges.Count > 0)
            {
                var finalByteRange = pattern.ByteRanges.Last();
                var finalDestState = GetMappedState(originalDestState, stateMap);
                currentState.AddTransition(new DfaTransition(finalDestState, finalByteRange.Min, finalByteRange.Max));
            }
        }

        private static string CreateIntermediateStateKey(Dfa fromState, Dfa originalDestState, Utf8BytePattern pattern, int byteIndex)
        {
            // Create a unique key that identifies this specific intermediate state
            var patternStr = string.Join(",", pattern.ByteRanges.Take(byteIndex + 1).Select(br => $"{br.Min:X2}-{br.Max:X2}"));
            return $"{fromState.GetHashCode()}->{originalDestState.GetHashCode()}:byte{byteIndex}:{patternStr}";
        }

        private static Dfa GetMappedState(Dfa originalState, Dictionary<Dfa, Dfa> stateMap)
        {
            if (!stateMap.ContainsKey(originalState))
            {
                stateMap[originalState] = CreateMappedState(originalState);
            }
            return stateMap[originalState];
        }

        private static Dfa CreateMappedState(Dfa originalState)
        {
            var newState = new Dfa();

            // Copy accept symbol and other important attributes
            foreach (var attr in originalState.Attributes)
            {
                newState.Attributes[attr.Key] = attr.Value;
            }

            return newState;
        }

        private static List<Utf8BytePattern> ConvertCodepointRangeToUtf8Patterns(int minCodepoint, int maxCodepoint)
        {
            var patterns = new List<Utf8BytePattern>();

            var currentMin = minCodepoint;

            while (currentMin <= maxCodepoint)
            {
                // PROBLEM: Need to skip surrogate range here
                if (currentMin >= 0xD800 && currentMin <= 0xDFFF)
                {
                    currentMin = 0xE000; // Skip to after surrogate range
                    if (currentMin > maxCodepoint) break;
                }

                int utf8Length = GetUtf8Length(currentMin);
                int maxForLength = GetMaxCodepointForUtf8Length(utf8Length);
                int currentMax = Math.Min(maxCodepoint, maxForLength);

                // Also need to check if currentMax falls in surrogate range
                if (currentMax >= 0xD800 && currentMax <= 0xDFFF)
                {
                    currentMax = 0xD7FF; // End before surrogate range
                    if (currentMax < currentMin)
                    {
                        currentMin = 0xE000;
                        continue;
                    }
                }

                // Generate pattern for this UTF-8 length range
                var subPatterns = GenerateUtf8PatternsForRange(currentMin, currentMax, utf8Length);
                patterns.AddRange(subPatterns);

                currentMin = currentMax + 1;
            }

            return patterns;
        }

        private static List<Utf8BytePattern> GenerateUtf8PatternsForRange(int minCodepoint, int maxCodepoint, int utf8Length)
        {

            if ((minCodepoint >= 0xD800 && minCodepoint <= 0xDFFF) ||
    (maxCodepoint >= 0xD800 && maxCodepoint <= 0xDFFF))
            {
                throw new InvalidOperationException($"Surrogate codepoints are not valid: [{minCodepoint:X}-{maxCodepoint:X}]");
            }

            var patterns = new List<Utf8BytePattern>();


            // For large ranges, we might need to break them down further
            // to avoid overly broad byte ranges

            if (utf8Length == 1)
            {
                // Simple case: ASCII range maps directly
                var pattern = new Utf8BytePattern();
                pattern.ByteRanges.Add(new Utf8ByteRange((byte)minCodepoint, (byte)maxCodepoint));
                patterns.Add(pattern);
            }
            else
            {
                // Complex case: multi-byte UTF-8
                // We need to be more careful about the byte ranges
                patterns.Add(GenerateMultiByteUtf8Pattern(minCodepoint, maxCodepoint, utf8Length));
            }

            return patterns;
        }

        private static Utf8BytePattern GenerateMultiByteUtf8Pattern(int minCodepoint, int maxCodepoint, int utf8Length)
        {
            var pattern = new Utf8BytePattern();

            // Get the UTF-8 encoding for the range endpoints
            var minBytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(minCodepoint));
            var maxBytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(maxCodepoint));

            // Ensure consistent encoding length
            if (minBytes.Length != utf8Length || maxBytes.Length != utf8Length)
            {
                throw new InvalidOperationException($"Inconsistent UTF-8 encoding lengths for range [{minCodepoint:X}-{maxCodepoint:X}]");
            }

            // Calculate byte ranges for each position
            for (int bytePos = 0; bytePos < utf8Length; bytePos++)
            {
                var (minByte, maxByte) = CalculateByteRangeForPosition(minCodepoint, maxCodepoint, bytePos, utf8Length);
                pattern.ByteRanges.Add(new Utf8ByteRange(minByte, maxByte));
            }

            return pattern;
        }

        private static (byte min, byte max) CalculateByteRangeForPosition(int minCodepoint, int maxCodepoint, int bytePos, int utf8Length)
        {
            // Get the UTF-8 encoding for the range endpoints
            var minBytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(minCodepoint));
            var maxBytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(maxCodepoint));

            if (minBytes.Length != utf8Length || maxBytes.Length != utf8Length)
            {
                throw new InvalidOperationException($"Inconsistent UTF-8 encoding lengths for range [{minCodepoint:X}-{maxCodepoint:X}]");
            }

            byte minByte = minBytes[bytePos];
            byte maxByte = maxBytes[bytePos];

            // For large ranges, we need to be more careful about intermediate values
            // Sample some points in between to ensure we capture the full range
            if (maxCodepoint - minCodepoint > 100)
            {
                int sampleCount = Math.Min(50, maxCodepoint - minCodepoint + 1);
                int step = Math.Max(1, (maxCodepoint - minCodepoint + 1) / sampleCount);

                for (int cp = minCodepoint; cp <= maxCodepoint; cp += step)
                {
                    // SKIP SURROGATE CODEPOINTS - they are not valid Unicode scalar values
                    if (cp >= 0xD800 && cp <= 0xDFFF)
                    {
                        continue;
                    }

                    var bytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(cp));
                    if (bytes.Length == utf8Length && bytePos < bytes.Length)
                    {
                        minByte = Math.Min(minByte, bytes[bytePos]);
                        maxByte = Math.Max(maxByte, bytes[bytePos]);
                    }
                }
            }

            return (minByte, maxByte);
        }
        private static int GetUtf8Length(int codepoint)
        {
            if (codepoint < 0x80) return 1;
            if (codepoint < 0x800) return 2;
            if (codepoint < 0x10000) return 3;
            return 4;
        }

        private static int GetMaxCodepointForUtf8Length(int length)
        {
            return length switch
            {
                1 => 0x7F,
                2 => 0x7FF,
                3 => 0xFFFF,
                4 => 0x10FFFF,
                _ => throw new ArgumentException($"Invalid UTF-8 length: {length}")
            };
        }

        /// <summary>
        /// Represents a UTF-8 byte pattern for DFA transformation
        /// </summary>
        class Utf8BytePattern
        {
            public List<Utf8ByteRange> ByteRanges { get; set; } = new List<Utf8ByteRange>();

            public override string ToString()
            {
                return $"[{string.Join(" ", ByteRanges)}]";
            }
        }

        /// <summary>
        /// Represents a range of bytes at a specific position in a UTF-8 sequence
        /// </summary>
        class Utf8ByteRange
        {
            public byte Min { get; }
            public byte Max { get; }

            public Utf8ByteRange(byte min, byte max)
            {
                Min = min;
                Max = max;
            }

            public override string ToString()
            {
                if (Min == Max)
                    return $"0x{Min:X2}";
                return $"0x{Min:X2}-0x{Max:X2}";
            }
        }
    }
}