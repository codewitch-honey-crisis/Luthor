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

            // Convert the codepoint range to UTF-8 patterns efficiently
            var utf8Patterns = ConvertCodepointRangeToUtf8PatternsOptimized(minCodepoint, maxCodepoint, Utf8ThresholdOptimizer.FindOptimalThreshold(fromState));

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

                // Create a shared key for intermediate states based on remaining pattern
                var stateKey = CreateSharedIntermediateStateKey(pattern, i);

                Dfa nextState;
                if (!intermediateStates.TryGetValue(stateKey, out nextState))
                {
                    // Create new intermediate state
                    nextState = new Dfa();
                    // Mark as intermediate (not accepting)
                    nextState.Attributes["AcceptSymbol"] = -1;
                    nextState.Attributes["IsIntermediate"] = true;

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

        private static string CreateSharedIntermediateStateKey(Utf8BytePattern pattern, int byteIndex)
        {
            // Create a key based on the remaining pattern structure only
            // This allows sharing of intermediate states across different source states
            var remainingPattern = string.Join(",", pattern.ByteRanges.Skip(byteIndex + 1).Select(br => $"{br.Min:X2}-{br.Max:X2}"));
            return $"intermediate:byte{byteIndex}:remaining:{remainingPattern}";
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

        private static List<Utf8BytePattern> ConvertCodepointRangeToUtf8PatternsOptimized(int minCodepoint, int maxCodepoint, double threshold)
        {
            var patterns = new List<Utf8BytePattern>();
            var validRanges = SplitAroundSurrogates(minCodepoint, maxCodepoint);

            foreach (var (rangeMin, rangeMax) in validRanges)
            {
                // Group by UTF-8 length and process each length efficiently
                var currentMin = rangeMin;
                while (currentMin <= rangeMax)
                {
                    int utf8Length = GetUtf8Length(currentMin);
                    int maxForLength = GetMaxCodepointForUtf8Length(utf8Length);
                    int currentMax = Math.Min(rangeMax, maxForLength);

                    // Generate patterns for this UTF-8 length range
                    var lengthPatterns = GenerateUtf8PatternsForLength(currentMin, currentMax, utf8Length, threshold);
                    patterns.AddRange(lengthPatterns);

                    currentMin = currentMax + 1;
                }
            }

            return patterns;
        }

        private static List<(int min, int max)> SplitAroundSurrogates(int minCodepoint, int maxCodepoint)
        {
            var ranges = new List<(int, int)>();

            // If range doesn't touch surrogates, return as-is
            if (maxCodepoint < 0xD800 || minCodepoint > 0xDFFF)
            {
                ranges.Add((minCodepoint, maxCodepoint));
                return ranges;
            }

            // Split around surrogates
            if (minCodepoint < 0xD800)
            {
                ranges.Add((minCodepoint, Math.Min(maxCodepoint, 0xD7FF)));
            }

            if (maxCodepoint > 0xDFFF)
            {
                ranges.Add((Math.Max(minCodepoint, 0xE000), maxCodepoint));
            }

            return ranges;
        }

        private static List<Utf8BytePattern> GenerateUtf8PatternsForLength(int minCodepoint, int maxCodepoint, int utf8Length, double threshold)
        {
            if ((minCodepoint >= 0xD800 && minCodepoint <= 0xDFFF) ||
                (maxCodepoint >= 0xD800 && maxCodepoint <= 0xDFFF))
            {
                throw new InvalidOperationException($"Surrogate codepoints should have been filtered out: [{minCodepoint:X}-{maxCodepoint:X}]");
            }

            var patterns = new List<Utf8BytePattern>();
            // NEW: Check if this range should use broad UTF-8 patterns
            if (ShouldUseBroadPatterns(minCodepoint, maxCodepoint, utf8Length, threshold))
            {
                return CreateBroadUtf8Patterns(minCodepoint, maxCodepoint, utf8Length);
            }

            // Original precise logic for smaller ranges
            if (utf8Length == 1)
            {
                // Single byte case
                var pattern = new Utf8BytePattern();
                pattern.ByteRanges.Add(new Utf8ByteRange((byte)minCodepoint, (byte)maxCodepoint));
                patterns.Add(pattern);
            }
            else if (utf8Length == 2)
            {
                // 2-byte UTF-8: optimize by grouping by first byte
                var minBytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(minCodepoint));
                var maxBytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(maxCodepoint));

                // Group by first byte to minimize patterns
                for (byte firstByte = minBytes[0]; firstByte <= maxBytes[0]; firstByte++)
                {
                    var pattern = new Utf8BytePattern();
                    pattern.ByteRanges.Add(new Utf8ByteRange(firstByte, firstByte));

                    // Calculate valid second byte range for this first byte
                    byte minSecond = (byte)(firstByte == minBytes[0] ? minBytes[1] : 0x80);
                    byte maxSecond = (byte)(firstByte == maxBytes[0] ? maxBytes[1] : 0xBF);

                    pattern.ByteRanges.Add(new Utf8ByteRange(minSecond, maxSecond));
                    patterns.Add(pattern);
                }
            }
            else if (utf8Length == 3)
            {
                // 3-byte UTF-8: Handle encoding constraints properly
                var minBytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(minCodepoint));
                var maxBytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(maxCodepoint));

                // Group by first byte due to UTF-8 encoding rules
                for (byte firstByte = minBytes[0]; firstByte <= maxBytes[0]; firstByte++)
                {
                    var pattern = new Utf8BytePattern();
                    pattern.ByteRanges.Add(new Utf8ByteRange(firstByte, firstByte));

                    // Calculate valid second byte range for this first byte
                    byte minSecond, maxSecond;
                    if (firstByte == 0xE0)
                    {
                        // For 0xE0, second byte must be 0xA0-0xBF (no overlong encodings)
                        minSecond = (byte)Math.Max(0xA0, firstByte == minBytes[0] ? minBytes[1] : 0x80);
                        maxSecond = (byte)(firstByte == maxBytes[0] ? maxBytes[1] : 0xBF);
                    }
                    else
                    {
                        // For 0xE1-0xEF, second byte can be 0x80-0xBF
                        minSecond = (byte)(firstByte == minBytes[0] ? minBytes[1] : 0x80);
                        maxSecond = (byte)(firstByte == maxBytes[0] ? maxBytes[1] : 0xBF);
                    }

                    pattern.ByteRanges.Add(new Utf8ByteRange(minSecond, maxSecond));

                    // Third byte range
                    byte minThird = (byte)(firstByte == minBytes[0] && minSecond == minBytes[1] ? minBytes[2] : 0x80);
                    byte maxThird = (byte)(firstByte == maxBytes[0] && maxSecond == maxBytes[1] ? maxBytes[2] : 0xBF);

                    pattern.ByteRanges.Add(new Utf8ByteRange(minThird, maxThird));
                    patterns.Add(pattern);
                }
            }
            else if (utf8Length == 4)
            {
                // 4-byte UTF-8: Similar to 3-byte but simpler constraints
                var minBytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(minCodepoint));
                var maxBytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(maxCodepoint));

                // For now, use a single pattern approach for 4-byte sequences
                var pattern = new Utf8BytePattern();
                for (int i = 0; i < 4; i++)
                {
                    pattern.ByteRanges.Add(new Utf8ByteRange(minBytes[i], maxBytes[i]));
                }
                patterns.Add(pattern);
            }

            return patterns;
        }

       private static bool ShouldUseBroadPatterns(int minCodepoint, int maxCodepoint, int utf8Length, double threshold)
        {
            var (lengthMin, lengthMax) = GetUtf8LengthBounds(utf8Length);

            var effectiveMin = Math.Max(minCodepoint, lengthMin);
            var effectiveMax = Math.Min(maxCodepoint, lengthMax);

            if (effectiveMax < effectiveMin)
                return false;

            var coveredCodepoints = effectiveMax - effectiveMin + 1;
            var totalCodepointsInLength = lengthMax - lengthMin + 1;
            var coverage = (double)coveredCodepoints / totalCodepointsInLength;

            return coverage > threshold;
        }

        private static (int min, int max) GetUtf8LengthBounds(int utf8Length)
        {
            return utf8Length switch
            {
                1 => (0x00, 0x7F),
                2 => (0x80, 0x7FF),
                3 => (0x800, 0xFFFF),
                4 => (0x10000, 0x10FFFF),
                _ => throw new ArgumentException($"Invalid UTF-8 length: {utf8Length}")
            };
        }

        private static List<Utf8BytePattern> CreateBroadUtf8Patterns(int minCodepoint, int maxCodepoint, int utf8Length)
        {
            var patterns = new List<Utf8BytePattern>();

            if (utf8Length == 1)
            {
                // ASCII: just use the range as-is
                var pattern = new Utf8BytePattern();
                pattern.ByteRanges.Add(new Utf8ByteRange((byte)Math.Max(0, minCodepoint), (byte)Math.Min(0x7F, maxCodepoint)));
                patterns.Add(pattern);
            }
            else if (utf8Length == 2)
            {
                // 2-byte: [C2-DF][80-BF] instead of 30 separate patterns
                var pattern = new Utf8BytePattern();

                // Calculate actual first byte range based on codepoints
                var minFirstByte = Math.Max(0xC2, 0xC0 + (Math.Max(0x80, minCodepoint) >> 6));
                var maxFirstByte = Math.Min(0xDF, 0xC0 + (Math.Min(0x7FF, maxCodepoint) >> 6));

                pattern.ByteRanges.Add(new Utf8ByteRange((byte)minFirstByte, (byte)maxFirstByte));
                pattern.ByteRanges.Add(new Utf8ByteRange(0x80, 0xBF));
                patterns.Add(pattern);
            }
            else if (utf8Length == 3)
            {
                // 3-byte: [E0-EF][80-BF/A0-BF][80-BF] 
                // Need to handle E0 special case

                var minFirstByte = Math.Max(0xE0, 0xE0 + ((Math.Max(0x800, minCodepoint) >> 12) & 0x0F));
                var maxFirstByte = Math.Min(0xEF, 0xE0 + ((Math.Min(0xFFFF, maxCodepoint) >> 12) & 0x0F));

                if (minFirstByte == 0xE0 && maxFirstByte > 0xE0)
                {
                    // Split into E0 (special) and E1-EF (normal)

                    // E0 pattern: E0 [A0-BF] [80-BF]
                    var e0Pattern = new Utf8BytePattern();
                    e0Pattern.ByteRanges.Add(new Utf8ByteRange(0xE0, 0xE0));
                    e0Pattern.ByteRanges.Add(new Utf8ByteRange(0xA0, 0xBF));
                    e0Pattern.ByteRanges.Add(new Utf8ByteRange(0x80, 0xBF));
                    patterns.Add(e0Pattern);

                    // E1-EF pattern: [E1-EF] [80-BF] [80-BF]
                    if (maxFirstByte > 0xE0)
                    {
                        var e1Pattern = new Utf8BytePattern();
                        e1Pattern.ByteRanges.Add(new Utf8ByteRange(0xE1, (byte)maxFirstByte));
                        e1Pattern.ByteRanges.Add(new Utf8ByteRange(0x80, 0xBF));
                        e1Pattern.ByteRanges.Add(new Utf8ByteRange(0x80, 0xBF));
                        patterns.Add(e1Pattern);
                    }
                }
                else
                {
                    // Single pattern covering the range
                    var pattern = new Utf8BytePattern();
                    pattern.ByteRanges.Add(new Utf8ByteRange((byte)minFirstByte, (byte)maxFirstByte));

                    if (minFirstByte == 0xE0)
                        pattern.ByteRanges.Add(new Utf8ByteRange(0xA0, 0xBF)); // E0 constraint
                    else
                        pattern.ByteRanges.Add(new Utf8ByteRange(0x80, 0xBF)); // Normal

                    pattern.ByteRanges.Add(new Utf8ByteRange(0x80, 0xBF));
                    patterns.Add(pattern);
                }
            }
            else if (utf8Length == 4)
            {
                // 4-byte: [F0-F4][80-BF][80-BF][80-BF]
                var pattern = new Utf8BytePattern();

                var minFirstByte = Math.Max(0xF0, 0xF0 + ((Math.Max(0x10000, minCodepoint) >> 18) & 0x07));
                var maxFirstByte = Math.Min(0xF4, 0xF0 + ((Math.Min(0x10FFFF, maxCodepoint) >> 18) & 0x07));

                pattern.ByteRanges.Add(new Utf8ByteRange((byte)minFirstByte, (byte)maxFirstByte));
                pattern.ByteRanges.Add(new Utf8ByteRange(0x80, 0xBF));
                pattern.ByteRanges.Add(new Utf8ByteRange(0x80, 0xBF));
                pattern.ByteRanges.Add(new Utf8ByteRange(0x80, 0xBF));
                patterns.Add(pattern);
            }

            return patterns;
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

        static class Utf8ThresholdOptimizer
        {
            public static double FindOptimalThreshold(Dfa state)
            {
                // Extract all ranges from this state using the existing method
                var transitionGroups = state.FillInputTransitionRangesGroupedByState();
                var allRanges = ExtractRangesFromTransitionGroups(transitionGroups);

                if (allRanges.Count == 0)
                    return 0.75; // Default for states with no Unicode transitions

                // Test different thresholds and estimate costs
                var candidates = new[] { 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 0.95 };
                var bestThreshold = 0.75;
                var bestCost = int.MaxValue;

                foreach (var threshold in candidates)
                {
                    var estimatedCost = EstimateUtf8ConversionCost(allRanges, threshold);

                    if (estimatedCost < bestCost)
                    {
                        bestCost = estimatedCost;
                        bestThreshold = threshold;
                    }
                }

                return bestThreshold;
            }

            private static List<(int min, int max)> ExtractRangesFromTransitionGroups(IDictionary<Dfa, IList<DfaRange>> transitionGroups)
            {
                var ranges = new List<(int, int)>();

                foreach (var group in transitionGroups.Values)
                {
                    foreach (var range in group)
                    {
                        // Skip anchor/epsilon transitions
                        if (range.Min < 0 || range.Max < 0)
                            continue;

                        // Skip ASCII-only ranges (they don't expand much)
                        if (range.Max <= 0x7F)
                            continue;

                        ranges.Add((range.Min, range.Max));
                    }
                }

                return ranges;
            }

            private static int EstimateUtf8ConversionCost(List<(int min, int max)> ranges, double threshold)
            {
                int totalPatterns = 0;

                foreach (var (min, max) in ranges)
                {
                    // Estimate patterns for each UTF-8 length this range spans
                    var patternsForRange = EstimatePatternsForRange(min, max, threshold);
                    totalPatterns += patternsForRange;
                }

                return totalPatterns;
            }

            private static int EstimatePatternsForRange(int minCodepoint, int maxCodepoint, double threshold)
            {
                int totalPatterns = 0;

                // Split range by UTF-8 lengths and estimate each portion
                var currentMin = minCodepoint;
                while (currentMin <= maxCodepoint)
                {
                    int utf8Length = GetUtf8Length(currentMin);
                    int maxForLength = GetMaxCodepointForUtf8Length(utf8Length);
                    int currentMax = Math.Min(maxCodepoint, maxForLength);

                    if (currentMax >= currentMin)
                    {
                        totalPatterns += EstimatePatternsForUtf8Length(currentMin, currentMax, utf8Length, threshold);
                    }

                    currentMin = currentMax + 1;
                }

                return totalPatterns;
            }

            private static int EstimatePatternsForUtf8Length(int minCodepoint, int maxCodepoint, int utf8Length, double threshold)
            {
                var (lengthMin, lengthMax) = GetUtf8LengthBounds(utf8Length);

                var effectiveMin = Math.Max(minCodepoint, lengthMin);
                var effectiveMax = Math.Min(maxCodepoint, lengthMax);

                if (effectiveMax < effectiveMin)
                    return 0;

                var coveredCodepoints = effectiveMax - effectiveMin + 1;
                var totalCodepointsInLength = lengthMax - lengthMin + 1;
                var coverage = (double)coveredCodepoints / totalCodepointsInLength;

                if (coverage > threshold)
                {
                    // Would use broad patterns
                    return EstimateBroadPatternCount(utf8Length);
                }
                else
                {
                    // Would use precise patterns (current method)
                    return EstimatePrecisePatternCount(effectiveMin, effectiveMax, utf8Length);
                }
            }

            private static int EstimateBroadPatternCount(int utf8Length)
            {
                return utf8Length switch
                {
                    1 => 1,  // ASCII range
                    2 => 1,  // [C2-DF][80-BF]
                    3 => 2,  // [E0][A0-BF][80-BF] + [E1-EF][80-BF][80-BF]
                    4 => 1,  // [F0-F4][80-BF][80-BF][80-BF]
                    _ => 1
                };
            }

            private static int EstimatePrecisePatternCount(int minCodepoint, int maxCodepoint, int utf8Length)
            {
                if (utf8Length == 1)
                {
                    return 1; // Single ASCII range
                }
                else if (utf8Length == 2)
                {
                    // Estimate by first byte count
                    var minBytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(minCodepoint));
                    var maxBytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(maxCodepoint));
                    return maxBytes[0] - minBytes[0] + 1;
                }
                else if (utf8Length == 3)
                {
                    // Estimate by first byte count
                    var minBytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(minCodepoint));
                    var maxBytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(maxCodepoint));
                    return maxBytes[0] - minBytes[0] + 1;
                }
                else if (utf8Length == 4)
                {
                    // Current method uses single pattern for 4-byte
                    return 1;
                }

                return 1;
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

            private static (int min, int max) GetUtf8LengthBounds(int utf8Length)
            {
                return utf8Length switch
                {
                    1 => (0x00, 0x7F),
                    2 => (0x80, 0x7FF),
                    3 => (0x800, 0xFFFF),
                    4 => (0x10000, 0x10FFFF),
                    _ => throw new ArgumentException($"Invalid UTF-8 length: {utf8Length}")
                };
            }
        }

    }

}
