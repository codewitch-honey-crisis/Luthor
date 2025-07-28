using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Luthor
{
    /// <summary>
    /// Transforms a Unicode codepoint-based DFA into a UTF-16 code unit-based DFA
    /// </summary>
    static class DfaUtf16Transformer
    {
        /// <summary>
        /// Transform a Unicode DFA to UTF-16 DFA
        /// </summary>
        public static Dfa TransformToUtf16(Dfa unicodeDfa)
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
            var utf16State = GetMappedState(originalState, stateMap);

            // Group transitions by destination state for processing
            var transitionGroups = originalState.FillInputTransitionRangesGroupedByState();

            foreach (var group in transitionGroups)
            {
                var destState = group.Key;
                var ranges = group.Value;

                // Convert each range to UTF-16 transitions
                foreach (var range in ranges)
                {
                    CreateUtf16Transitions(utf16State, destState, range.Min, range.Max, stateMap, intermediateStates);
                }
            }
        }

        private static void CreateUtf16Transitions(Dfa fromState, Dfa originalDestState, int minCodepoint, int maxCodepoint, Dictionary<Dfa, Dfa> stateMap, Dictionary<string, Dfa> intermediateStates)
        {
            // Handle special anchor codepoints (negative values) - pass through unchanged
            if (minCodepoint < 0 || maxCodepoint < 0)
            {
                var destState = GetMappedState(originalDestState, stateMap);
                fromState.AddTransition(new DfaTransition(destState, minCodepoint, maxCodepoint));
                return;
            }

            // Convert the codepoint range to UTF-16 patterns
            var utf16Patterns = ConvertCodepointRangeToUtf16Patterns(minCodepoint, maxCodepoint);

            // For each pattern, create the necessary state chain
            foreach (var pattern in utf16Patterns)
            {
                CreateUtf16StateChain(fromState, originalDestState, pattern, stateMap, intermediateStates);
            }
        }

        private static void CreateUtf16StateChain(Dfa fromState, Dfa originalDestState, Utf16CodeUnitPattern pattern, Dictionary<Dfa, Dfa> stateMap, Dictionary<string, Dfa> intermediateStates)
        {
            var currentState = fromState;

            // Create intermediate states for all but the last code unit
            for (int i = 0; i < pattern.CodeUnits.Count - 1; i++)
            {
                var codeUnitRange = pattern.CodeUnits[i];

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

                // Add transition for this code unit range
                currentState.AddTransition(new DfaTransition(nextState, codeUnitRange.Min, codeUnitRange.Max));
                currentState = nextState;
            }

            // Final transition to the mapped destination state
            if (pattern.CodeUnits.Count > 0)
            {
                var finalCodeUnitRange = pattern.CodeUnits.Last();
                var finalDestState = GetMappedState(originalDestState, stateMap);
                currentState.AddTransition(new DfaTransition(finalDestState, finalCodeUnitRange.Min, finalCodeUnitRange.Max));
            }
        }

        private static string CreateIntermediateStateKey(Dfa fromState, Dfa originalDestState, Utf16CodeUnitPattern pattern, int codeUnitIndex)
        {
            // Create a unique key that identifies this specific intermediate state
            var patternStr = string.Join(",", pattern.CodeUnits.Take(codeUnitIndex + 1).Select(cu => $"{cu.Min:X4}-{cu.Max:X4}"));
            return $"{fromState.GetHashCode()}->{originalDestState.GetHashCode()}:unit{codeUnitIndex}:{patternStr}";
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

        /// <summary>
        /// Convert a Unicode codepoint range to UTF-16 code unit patterns
        /// </summary>
        private static List<Utf16CodeUnitPattern> ConvertCodepointRangeToUtf16Patterns(int minCodepoint, int maxCodepoint)
        {
            var patterns = new List<Utf16CodeUnitPattern>();

            // Validate input ranges - skip surrogates in BMP
            if (minCodepoint >= 0xD800 && minCodepoint <= 0xDFFF)
            {
                minCodepoint = 0xE000; // Skip surrogate range
            }
            if (maxCodepoint >= 0xD800 && maxCodepoint <= 0xDFFF)
            {
                maxCodepoint = 0xD7FF; // End before surrogate range
                if (maxCodepoint < minCodepoint) return patterns; // Empty range
            }

            const int BMP_MAX = 0xFFFF;
            const int SUPPLEMENTARY_MIN = 0x10000;

            if (maxCodepoint <= BMP_MAX)
            {
                // Handle BMP range, potentially split around surrogates
                foreach (var (min, max) in SplitRangeAroundSurrogates(minCodepoint, maxCodepoint))
                {
                    var pattern = new Utf16CodeUnitPattern();
                    pattern.CodeUnits.Add(new CodeUnitRange(min, max));
                    patterns.Add(pattern);
                }
            }
            else if (minCodepoint >= SUPPLEMENTARY_MIN)
            {
                // Entire range is supplementary - use new method
                patterns.AddRange(GenerateSurrogatePairPatterns(minCodepoint, maxCodepoint));
            }
            else
            {
                // Range crosses BMP boundary - split it

                // BMP part (split around surrogates)
                foreach (var (min, max) in SplitRangeAroundSurrogates(minCodepoint, Math.Min(maxCodepoint, BMP_MAX)))
                {
                    var bmpPattern = new Utf16CodeUnitPattern();
                    bmpPattern.CodeUnits.Add(new CodeUnitRange(min, max));
                    patterns.Add(bmpPattern);
                }

                // Supplementary part
                if (maxCodepoint >= SUPPLEMENTARY_MIN)
                {
                    patterns.AddRange(GenerateSurrogatePairPatterns(SUPPLEMENTARY_MIN, maxCodepoint));
                }
            }

            return patterns;
        }
        private static IEnumerable<(int min, int max)> SplitRangeAroundSurrogates(int minCodepoint, int maxCodepoint)
        {
            // Range entirely before surrogates
            if (maxCodepoint < 0xD800)
            {
                yield return (minCodepoint, maxCodepoint);
                yield break;
            }

            // Range entirely after surrogates  
            if (minCodepoint > 0xDFFF)
            {
                yield return (minCodepoint, maxCodepoint);
                yield break;
            }

            // Range spans surrogates - split it
            if (minCodepoint < 0xD800)
            {
                yield return (minCodepoint, 0xD7FF); // Before surrogates
            }

            if (maxCodepoint > 0xDFFF)
            {
                yield return (0xE000, maxCodepoint); // After surrogates
            }
        }

        private static List<Utf16CodeUnitPattern> GenerateSurrogatePairPatterns(int minCodepoint, int maxCodepoint)
        {
            var patterns = new List<Utf16CodeUnitPattern>();

            if (minCodepoint < 0x10000 || maxCodepoint < 0x10000)
            {
                throw new ArgumentException("Codepoints must be in supplementary range (>= 0x10000)");
            }

            var (minHigh, minLow) = CodepointToSurrogates(minCodepoint);
            var (maxHigh, maxLow) = CodepointToSurrogates(maxCodepoint);

            if (minHigh == maxHigh)
            {
                // Range is within a single high surrogate - simple case
                var pattern = new Utf16CodeUnitPattern();
                pattern.CodeUnits.Add(new CodeUnitRange(minHigh, minHigh));
                pattern.CodeUnits.Add(new CodeUnitRange(minLow, maxLow));
                patterns.Add(pattern);
            }
            else
            {
                // Range spans multiple high surrogates - need multiple patterns

                // First high surrogate: minHigh with minLow to 0xDFFF
                if (minLow <= 0xDFFF)
                {
                    var firstPattern = new Utf16CodeUnitPattern();
                    firstPattern.CodeUnits.Add(new CodeUnitRange(minHigh, minHigh));
                    firstPattern.CodeUnits.Add(new CodeUnitRange(minLow, 0xDFFF));
                    patterns.Add(firstPattern);
                }

                // Middle high surrogates: (minHigh+1) to (maxHigh-1) with full low range
                if (maxHigh > minHigh + 1)
                {
                    var middlePattern = new Utf16CodeUnitPattern();
                    middlePattern.CodeUnits.Add(new CodeUnitRange(minHigh + 1, maxHigh - 1));
                    middlePattern.CodeUnits.Add(new CodeUnitRange(0xDC00, 0xDFFF));
                    patterns.Add(middlePattern);
                }

                // Last high surrogate: maxHigh with 0xDC00 to maxLow
                if (maxLow >= 0xDC00)
                {
                    var lastPattern = new Utf16CodeUnitPattern();
                    lastPattern.CodeUnits.Add(new CodeUnitRange(maxHigh, maxHigh));
                    lastPattern.CodeUnits.Add(new CodeUnitRange(0xDC00, maxLow));
                    patterns.Add(lastPattern);
                }
            }

            return patterns;
        }

        private static (int high, int low) CodepointToSurrogates(int codepoint)
        {
            if (codepoint < 0x10000)
            {
                throw new ArgumentException($"Codepoint 0x{codepoint:X} is in BMP, not supplementary");
            }

            int adjusted = codepoint - 0x10000;
            int high = 0xD800 + (adjusted >> 10);
            int low = 0xDC00 + (adjusted & 0x3FF);

            return (high, low);
        }

        /// <summary>
        /// Represents a UTF-16 code unit pattern for DFA transformation
        /// </summary>
        class Utf16CodeUnitPattern
        {
            public List<CodeUnitRange> CodeUnits { get; set; } = new List<CodeUnitRange>();

            public override string ToString()
            {
                return $"[{string.Join(" ", CodeUnits)}]";
            }
        }

        /// <summary>
        /// Represents a range of UTF-16 code units
        /// </summary>
        class CodeUnitRange
        {
            public int Min { get; }
            public int Max { get; }

            public CodeUnitRange(int min, int max)
            {
                Min = min;
                Max = max;
            }

            public override string ToString()
            {
                if (Min == Max)
                    return $"0x{Min:X4}";
                return $"0x{Min:X4}-0x{Max:X4}";
            }
        }
    }
}

