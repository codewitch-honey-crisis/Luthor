using System.Text;

namespace Luthor
{
    internal class Tests
    {
        public static void TestRangeConverter()
        {
            // Test cases of increasing complexity
            TestUnicodeRange(0x61, 0x7A);        // [a-z] - simple ASCII
            TestUnicodeRange(0x7E, 0x82);        // Your boundary-crossing example
            TestUnicodeRange(0x41, 0x1F642);     // [A-🙂] - spans 1,2,3,4 byte lengths
            TestUnicodeRange(0x800, 0x801);      // Simple 3-byte range

            // Test specific codepoints for validation
            TestSpecificCodepoints();
        }

        static void TestUnicodeRange(int minCodepoint, int maxCodepoint)
        {
            Console.WriteLine($"\n=== Testing Unicode range [U+{minCodepoint:X4}-U+{maxCodepoint:X4}] ===");

            // Show what the Unicode range contains
            for (int cp = minCodepoint; cp <= Math.Min(maxCodepoint, minCodepoint + 10); cp++)
            {
                string str = char.ConvertFromUtf32(cp);
                byte[] utf8 = Encoding.UTF8.GetBytes(str);
                Console.WriteLine($"U+{cp:X4} → {string.Join(" ", utf8.Select(b => $"0x{b:X2}"))}");
            }
            if (maxCodepoint > minCodepoint + 10)
            {
                Console.WriteLine($"... (showing first 10 of {maxCodepoint - minCodepoint + 1} codepoints)");
            }

            // Convert to UTF-8 byte patterns
            var patterns = ConvertToUtf8Patterns(minCodepoint, maxCodepoint);

            Console.WriteLine("UTF-8 Byte Patterns:");
            foreach (var pattern in patterns)
            {
                Console.WriteLine($"  {pattern}");
            }

            // Validate: ensure every original codepoint is covered
            ValidateCoverage(minCodepoint, maxCodepoint, patterns);
        }

        /// <summary>
        /// Calculate byte range for a specific position, avoiding surrogate codepoints
        /// </summary>
        static (byte min, byte max) CalculateByteRangeCarefully(int minCodepoint, int maxCodepoint, int byteIndex, int utf8Length)
        {
            byte minByte = 0xFF;
            byte maxByte = 0x00;

            // Sample the range more carefully, avoiding surrogates
            int sampleCount = Math.Min(200, maxCodepoint - minCodepoint + 1);
            int step = Math.Max(1, (maxCodepoint - minCodepoint + 1) / sampleCount);

            for (int cp = minCodepoint; cp <= maxCodepoint; cp += step)
            {
                // Skip surrogate codepoints
                if (cp >= 0xD800 && cp <= 0xDFFF)
                {
                    continue;
                }

                try
                {
                    var bytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(cp));
                    if (bytes.Length == utf8Length && byteIndex < bytes.Length)
                    {
                        minByte = Math.Min(minByte, bytes[byteIndex]);
                        maxByte = Math.Max(maxByte, bytes[byteIndex]);
                    }
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Skip invalid codepoints
                    continue;
                }
            }

            // Always check the actual endpoints if they're valid
            if (minCodepoint < 0xD800 || minCodepoint > 0xDFFF)
            {
                try
                {
                    var minBytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(minCodepoint));
                    if (minBytes.Length == utf8Length)
                    {
                        minByte = Math.Min(minByte, minBytes[byteIndex]);
                        maxByte = Math.Max(maxByte, minBytes[byteIndex]);
                    }
                }
                catch (ArgumentOutOfRangeException) { }
            }

            if (maxCodepoint < 0xD800 || maxCodepoint > 0xDFFF)
            {
                try
                {
                    var maxBytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(maxCodepoint));
                    if (maxBytes.Length == utf8Length)
                    {
                        minByte = Math.Min(minByte, maxBytes[byteIndex]);
                        maxByte = Math.Max(maxByte, maxBytes[byteIndex]);
                    }
                }
                catch (ArgumentOutOfRangeException) { }
            }

            // Fallback if we couldn't determine the range
            if (minByte == 0xFF && maxByte == 0x00)
            {
                // Use the endpoint bytes as fallback
                var fallbackMin = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(Math.Max(minCodepoint, 0xE000)));
                var fallbackMax = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(Math.Min(maxCodepoint, 0xD7FF)));

                if (fallbackMin.Length == utf8Length) minByte = fallbackMin[byteIndex];
                if (fallbackMax.Length == utf8Length) maxByte = fallbackMax[byteIndex];
            }

            return (minByte, maxByte);
        }


        /// <summary>
        /// Check if UTF-8 bytes match a pattern
        /// </summary>
        static bool MatchesPattern(byte[] utf8Bytes, Utf8BytePattern pattern)
        {
            if (utf8Bytes.Length != pattern.ByteRanges.Count)
                return false;

            for (int i = 0; i < utf8Bytes.Length; i++)
            {
                var byteRange = pattern.ByteRanges[i];
                if (utf8Bytes[i] < byteRange.Min || utf8Bytes[i] > byteRange.Max)
                    return false;
            }

            return true;
        }
        /// <summary>
        /// Validate that the generated patterns cover all codepoints in the original range
        /// IMPROVED VERSION: Fix counting bugs and add detailed diagnostics
        /// </summary>
        static void ValidateCoverage(int minCodepoint, int maxCodepoint, List<Utf8BytePattern> patterns)
        {
            Console.WriteLine("Validating coverage...");

            // Get valid ranges (excluding surrogates)
            var validRanges = SplitAroundSurrogates(minCodepoint, maxCodepoint);
            var totalValidCodepoints = validRanges.Sum(r => r.max - r.min + 1);

            Console.WriteLine($"Split into {validRanges.Count} valid ranges:");
            foreach (var (rangeMin, rangeMax) in validRanges)
            {
                Console.WriteLine($"  [U+{rangeMin:X4}-U+{rangeMax:X4}]: {rangeMax - rangeMin + 1} codepoints");
            }

            var coveredCodepoints = new HashSet<int>();
            var failedCodepoints = new List<int>();
            var actualTestedCount = 0;

            // For each valid range, test a sample of codepoints
            foreach (var (rangeMin, rangeMax) in validRanges)
            {
                int rangeSize = rangeMax - rangeMin + 1;
                int targetSampleSize = Math.Min(500, rangeSize);
                int step = Math.Max(1, rangeSize / targetSampleSize);

                Console.WriteLine($"  Range [U+{rangeMin:X4}-U+{rangeMax:X4}]: targeting {targetSampleSize} samples with step {step}");

                int rangeActualTested = 0;
                int rangeCovered = 0;

                for (int cp = rangeMin; cp <= rangeMax; cp += step)
                {
                    // Skip surrogate codepoints (shouldn't happen in validRanges, but be safe)
                    if (cp >= 0xD800 && cp <= 0xDFFF) continue;

                    actualTestedCount++;
                    rangeActualTested++;

                    try
                    {
                        var utf8Bytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(cp));

                        bool matchesAnyPattern = patterns.Any(pattern =>
                            MatchesPattern(utf8Bytes, pattern));

                        if (matchesAnyPattern)
                        {
                            coveredCodepoints.Add(cp);
                            rangeCovered++;
                        }
                        else
                        {
                            failedCodepoints.Add(cp);
                            if (failedCodepoints.Count <= 10) // Limit output
                            {
                                var bytesStr = string.Join(" ", utf8Bytes.Select(b => $"0x{b:X2}"));
                                Console.WriteLine($"    ✗ FAIL: U+{cp:X4} -> {bytesStr} doesn't match any pattern");
                            }
                        }
                    }
                    catch (ArgumentOutOfRangeException ex)
                    {
                        Console.WriteLine($"    ✗ ERROR: U+{cp:X4} - {ex.Message}");
                        failedCodepoints.Add(cp);
                    }
                }

                Console.WriteLine($"    Range result: {rangeCovered}/{rangeActualTested} covered");
            }

            // Show detailed results
            Console.WriteLine($"=== VALIDATION RESULTS ===");
            Console.WriteLine($"Actually tested: {actualTestedCount} codepoints");
            Console.WriteLine($"Successfully covered: {coveredCodepoints.Count} codepoints");
            Console.WriteLine($"Failed to cover: {failedCodepoints.Count} codepoints");
            Console.WriteLine($"Total valid codepoints in range: {totalValidCodepoints} (excluding surrogates)");

            if (failedCodepoints.Count > 10)
            {
                Console.WriteLine($"(First 10 failures shown above, {failedCodepoints.Count - 10} more failures not displayed)");
            }

            // Show success/failure
            if (coveredCodepoints.Count == actualTestedCount)
            {
                Console.WriteLine("✓ All sampled codepoints are covered by the patterns");
            }
            else
            {
                Console.WriteLine($"⚠ {failedCodepoints.Count} codepoints not covered - VALIDATION FAILED");

                // Show some specific failures for debugging
                Console.WriteLine("Failed codepoints for debugging:");
                foreach (var cp in failedCodepoints.Take(5))
                {
                    try
                    {
                        var utf8Bytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(cp));
                        var bytesStr = string.Join(" ", utf8Bytes.Select(b => $"0x{b:X2}"));
                        Console.WriteLine($"  U+{cp:X4} -> {bytesStr}");

                        // Show which patterns exist
                        Console.WriteLine($"  Available patterns:");
                        foreach (var pattern in patterns)
                        {
                            Console.WriteLine($"    {pattern}");
                        }
                        break; // Just show one detailed failure
                    }
                    catch { }
                }
            }
        }
        /// <summary>
        /// Get maximum codepoint that can be encoded with given UTF-8 length
        /// </summary>
        static int GetMaxCodepointForUtf8Length(int length)
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
        /// Generate UTF-8 byte pattern for a codepoint range with the same encoding length
        /// </summary>
        static Utf8BytePattern GenerateUtf8Pattern(int minCodepoint, int maxCodepoint, int utf8Length)
        {
            var pattern = new Utf8BytePattern();

            // Get UTF-8 bytes for min and max codepoints
            var minBytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(minCodepoint));
            var maxBytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(maxCodepoint));

            if (minBytes.Length != maxBytes.Length || minBytes.Length != utf8Length)
            {
                throw new InvalidOperationException($"Inconsistent UTF-8 lengths for range [{minCodepoint:X}-{maxCodepoint:X}]");
            }

            // Calculate byte ranges for each position
            for (int i = 0; i < utf8Length; i++)
            {
                byte minByte = minBytes[i];
                byte maxByte = maxBytes[i];

                // For large ranges, we might need to expand the byte range
                if (maxCodepoint - minCodepoint > 100 && i > 0)
                {
                    var (refinedMin, refinedMax) = CalculateByteRangeCarefully(minCodepoint, maxCodepoint, i, utf8Length);
                    pattern.ByteRanges.Add(new ByteRange(refinedMin, refinedMax));
                }
                else
                {
                    pattern.ByteRanges.Add(new ByteRange(minByte, maxByte));
                }
            }

            return pattern;
        }

        /// <summary>
        /// Split a codepoint range around surrogate codepoints (0xD800-0xDFFF)
        /// </summary>
        static List<(int min, int max)> SplitAroundSurrogates(int minCodepoint, int maxCodepoint)
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
                // Add range before surrogates
                ranges.Add((minCodepoint, Math.Min(maxCodepoint, 0xD7FF)));
            }

            if (maxCodepoint > 0xDFFF)
            {
                // Add range after surrogates
                ranges.Add((Math.Max(minCodepoint, 0xE000), maxCodepoint));
            }

            return ranges;
        }

        /// <summary>
        /// Convert a Unicode codepoint range to UTF-8 byte patterns
        /// </summary>
        static List<Utf8BytePattern> ConvertToUtf8Patterns(int minCodepoint, int maxCodepoint)
        {
            var patterns = new List<Utf8BytePattern>();

            // Split ranges that contain surrogates
            var validRanges = SplitAroundSurrogates(minCodepoint, maxCodepoint);

            foreach (var (rangeMin, rangeMax) in validRanges)
            {
                // UTF-8 encoding boundaries
                var currentMin = rangeMin;

                while (currentMin <= rangeMax)
                {
                    // Find the UTF-8 length for currentMin
                    int utf8Length = GetUtf8Length(currentMin);

                    // Find the maximum codepoint we can encode with the same UTF-8 length
                    int maxForLength = GetMaxCodepointForUtf8Length(utf8Length);
                    int currentMax = Math.Min(rangeMax, maxForLength);

                    // Generate pattern for this length range
                    var pattern = GenerateUtf8Pattern(currentMin, currentMax, utf8Length);
                    if (pattern != null && pattern.ByteRanges.Count > 0)
                    {
                        patterns.Add(pattern);
                    }

                    currentMin = currentMax + 1;
                }
            }

            return patterns;
        }

        /// <summary>
        /// Get UTF-8 byte length for a codepoint
        /// </summary>
        static int GetUtf8Length(int codepoint)
        {
            if (codepoint < 0x80) return 1;
            if (codepoint < 0x800) return 2;
            if (codepoint < 0x10000) return 3;
            return 4;
        }

        /// <summary>
        /// Test a specific codepoint against the pattern generation
        /// </summary>
        static void TestSpecificCodepoint(int codepoint)
        {
            Console.WriteLine($"\n--- Testing Codepoint U+{codepoint:X4} ---");

            try
            {
                // Convert codepoint to UTF-8 bytes
                var actualBytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(codepoint));
                var actualBytesStr = string.Join(" ", actualBytes.Select(b => $"0x{b:X2}"));
                Console.WriteLine($"Actual UTF-8 bytes: {actualBytesStr}");

                // Generate patterns for this single codepoint
                var patterns = ConvertToUtf8Patterns(codepoint, codepoint);
                Console.WriteLine($"Generated {patterns.Count} pattern(s):");

                foreach (var pattern in patterns)
                {
                    Console.WriteLine($"  Pattern: {pattern}");
                }

                // Test if actual bytes match any pattern
                bool matchesAny = false;
                foreach (var pattern in patterns)
                {
                    bool matches = MatchesPattern(actualBytes, pattern);
                    Console.WriteLine($"  Matches pattern {pattern}: {(matches ? "✓ YES" : "✗ NO")}");
                    if (matches) matchesAny = true;
                }

                Console.WriteLine($"Overall result: {(matchesAny ? "✓ PASS" : "✗ FAIL")}");

                // Also test within a range context
                Console.WriteLine($"\nTesting within range context [U+{codepoint:X4}-U+{codepoint:X4}]:");
                TestUnicodeRange(codepoint, codepoint);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ ERROR: {ex.Message}");
            }
        }

        /// <summary>
        /// Test multiple specific codepoints
        /// </summary>
        static void TestSpecificCodepoints()
        {
            Console.WriteLine("\n=== Testing Specific Codepoints ===");

            var testCases = new[]
            {
            0x41,     // 'A' - simple ASCII
            0x7F,     // DEL - last 1-byte
            0x80,     // First 2-byte  
            0x7FF,    // Last 2-byte
            0x800,    // First 3-byte
            0xD7FF,   // Last before surrogates
            0xE000,   // First after surrogates
            0xFFFF,   // Last 3-byte
            0x10000,  // First 4-byte
            0x1F642,  // 🙂 emoji
            0x10FFFF  // Last valid Unicode
        };

            foreach (var codepoint in testCases)
            {
                TestSpecificCodepoint(codepoint);
            }
        }
        /// <summary>
        /// Represents a range of bytes at a specific position in a UTF-8 sequence
        /// </summary>
        class ByteRange
        {
            public byte Min { get; set; }
            public byte Max { get; set; }

            public ByteRange(byte min, byte max)
            {
                Min = min;
                Max = max;
            }

            public override string ToString()
            {
                if (Min == Max)
                    return $"0x{Min:X2}";
                return $"[0x{Min:X2}-0x{Max:X2}]";
            }
        }

        /// <summary>
        /// Represents a UTF-8 byte pattern that can match multiple byte sequences
        /// </summary>
        class Utf8BytePattern
        {
            public List<ByteRange> ByteRanges { get; set; } = new List<ByteRange>();

            public override string ToString()
            {
                return string.Join(" ", ByteRanges.Select(br => br.ToString()));
            }
        }

        public static void TestUtf8DfaTransformation()
        {
            Console.WriteLine("=== Testing DFA UTF-8 Transformation ===");

            // Test 1: Simple ASCII (should be unchanged)
            Console.WriteLine("\n1. ASCII [a-z] test:");
            var asciiDfa = CreateTestUtf8Dfa();
            Console.WriteLine("Original DFA (Unicode codepoints):");
            PrintUtf8DfaInfo(asciiDfa);
            var asciiUtf8Dfa = DfaUtf8Transformer.TransformToUtf8(asciiDfa);
            Console.WriteLine("Transformed DFA (UTF-8 bytes):");
            PrintUtf8DfaInfo(asciiUtf8Dfa);

            // Test 2: Multi-byte UTF-8 range (should create intermediate states)
            Console.WriteLine("\n2. Multi-byte UTF-8 [À-ÿ] test:");
            var multibyteDfa = CreateMultiByteUtf8Dfa();
            Console.WriteLine("Original DFA (Unicode codepoints):");
            PrintUtf8DfaInfo(multibyteDfa);
            var multibyteUtf8Dfa = DfaUtf8Transformer.TransformToUtf8(multibyteDfa);
            Console.WriteLine("Transformed DFA (UTF-8 bytes):");
            PrintUtf8DfaInfo(multibyteUtf8Dfa);

            // Test 3: Cross-boundary range (should create multiple patterns)
            Console.WriteLine("\n3. Cross-boundary [~-‚] test:");
            var crossBoundaryDfa = CreateCrossBoundaryUtf8Dfa();
            Console.WriteLine("Original DFA (Unicode codepoints):");
            PrintUtf8DfaInfo(crossBoundaryDfa);
            var crossBoundaryUtf8Dfa = DfaUtf8Transformer.TransformToUtf8(crossBoundaryDfa);
            Console.WriteLine("Transformed DFA (UTF-8 bytes):");
            PrintUtf8DfaInfo(crossBoundaryUtf8Dfa);
        }

        private static Dfa CreateMultiByteUtf8Dfa()
        {
            var startState = new Dfa();
            var acceptState = new Dfa();
            acceptState.Attributes["AcceptSymbol"] = 1;

            // Add transition for [À-ÿ] (0xC0-0xFF) - all 2-byte UTF-8
            startState.AddTransition(new DfaTransition(acceptState, 0xC0, 0xFF));

            return startState;
        }

        private static Dfa CreateCrossBoundaryUtf8Dfa()
        {
            var startState = new Dfa();
            var acceptState = new Dfa();
            acceptState.Attributes["AcceptSymbol"] = 1;

            // Add transition for [~-‚] (0x7E-0x82) - crosses 1-byte to 2-byte boundary
            startState.AddTransition(new DfaTransition(acceptState, 0x7E, 0x82));

            return startState;
        }

        private static Dfa CreateTestUtf8Dfa()
        {
            var startState = new Dfa();
            var acceptState = new Dfa();
            acceptState.Attributes["AcceptSymbol"] = 1;

            // Add transition for [a-z] (0x61-0x7A)
            startState.AddTransition(new DfaTransition(acceptState, 0x61, 0x7A));

            return startState;
        }

        private static void PrintUtf8DfaInfo(Dfa dfa)
        {
            var states = dfa.FillClosure();
            Console.WriteLine($"States: {states.Count}");

            foreach (var state in states)
            {
                var isAccept = state.IsAccept ? " (ACCEPT)" : "";
                var isIntermediate = state.Attributes.ContainsKey("IsIntermediate") ? " (INTERMEDIATE)" : "";
                Console.WriteLine($"State {state.GetHashCode()}{isAccept}{isIntermediate}:");

                foreach (var transition in state.Transitions)
                {
                    var range = transition.Min == transition.Max
                        ? $"0x{transition.Min:X2}"
                        : $"0x{transition.Min:X2}-0x{transition.Max:X2}";
                    Console.WriteLine($"  {range} -> State {transition.To.GetHashCode()}");
                }
            }
        }
        public static void TestUtf16DfaTransformation()
        {
            Console.WriteLine("=== Testing DFA UTF-16 Transformation ===");

            // Test 1: Simple ASCII (should be unchanged)
            Console.WriteLine("\n1. ASCII [a-z] test:");
            var asciiDfa = CreateTestUtf16Dfa();
            Console.WriteLine("Original DFA (Unicode codepoints):");
            PrintUtf16DfaInfo(asciiDfa);
            var asciiUtf16Dfa = DfaUtf16Transformer.TransformToUtf16(asciiDfa);
            Console.WriteLine("Transformed DFA (UTF-16 code units):");
            PrintUtf16DfaInfo(asciiUtf16Dfa);

            // Test 2: BMP characters (should be direct encoding)
            Console.WriteLine("\n2. BMP characters [À-ÿ] test:");
            var bmpDfa = CreateBmpUtf16Dfa();
            Console.WriteLine("Original DFA (Unicode codepoints):");
            PrintUtf16DfaInfo(bmpDfa);
            var bmpUtf16Dfa = DfaUtf16Transformer.TransformToUtf16(bmpDfa);
            Console.WriteLine("Transformed DFA (UTF-16 code units):");
            PrintUtf16DfaInfo(bmpUtf16Dfa);

            // Test 3: Supplementary characters (should create surrogate pairs)
            Console.WriteLine("\n3. Supplementary characters [😀-😏] test:");
            var supplementaryDfa = CreateSupplementaryUtf16Dfa();
            Console.WriteLine("Original DFA (Unicode codepoints):");
            PrintUtf16DfaInfo(supplementaryDfa);
            var supplementaryUtf16Dfa = DfaUtf16Transformer.TransformToUtf16(supplementaryDfa);
            Console.WriteLine("Transformed DFA (UTF-16 code units):");
            PrintUtf16DfaInfo(supplementaryUtf16Dfa);

            // Test 4: Cross-boundary range (should split)
            Console.WriteLine("\n4. Cross-boundary [A-😂] test:");
            var crossBoundaryDfa = CreateCrossBoundaryUtf16Dfa();
            Console.WriteLine("Original DFA (Unicode codepoints):");
            PrintUtf16DfaInfo(crossBoundaryDfa);
            var crossBoundaryUtf16Dfa = DfaUtf16Transformer.TransformToUtf16(crossBoundaryDfa);
            Console.WriteLine("Transformed DFA (UTF-16 code units):");
            PrintUtf16DfaInfo(crossBoundaryUtf16Dfa);
        }

        private static Dfa CreateTestUtf16Dfa()
        {
            var startState = new Dfa();
            var acceptState = new Dfa();
            acceptState.Attributes["AcceptSymbol"] = 1;

            // Add transition for [a-z] (0x61-0x7A)
            startState.AddTransition(new DfaTransition(acceptState, 0x61, 0x7A));

            return startState;
        }

        private static Dfa CreateBmpUtf16Dfa()
        {
            var startState = new Dfa();
            var acceptState = new Dfa();
            acceptState.Attributes["AcceptSymbol"] = 1;

            // Add transition for [À-ÿ] (0xC0-0xFF) - BMP characters
            startState.AddTransition(new DfaTransition(acceptState, 0xC0, 0xFF));

            return startState;
        }

        private static Dfa CreateSupplementaryUtf16Dfa()
        {
            var startState = new Dfa();
            var acceptState = new Dfa();
            acceptState.Attributes["AcceptSymbol"] = 1;

            // Add transition for [😀-😏] (0x1F600-0x1F60F) - supplementary characters
            startState.AddTransition(new DfaTransition(acceptState, 0x1F600, 0x1F60F));

            return startState;
        }

        private static Dfa CreateCrossBoundaryUtf16Dfa()
        {
            var startState = new Dfa();
            var acceptState = new Dfa();
            acceptState.Attributes["AcceptSymbol"] = 1;

            // Add transition for [A-😂] (0x41-0x1F602) - crosses BMP boundary
            startState.AddTransition(new DfaTransition(acceptState, 0x41, 0x1F602));

            return startState;
        }

        private static void PrintUtf16DfaInfo(Dfa dfa)
        {
            var states = dfa.FillClosure();
            Console.WriteLine($"States: {states.Count}");

            foreach (var state in states)
            {
                var isAccept = state.IsAccept ? " (ACCEPT)" : "";
                var isIntermediate = state.Attributes.ContainsKey("IsIntermediate") ? " (INTERMEDIATE)" : "";
                Console.WriteLine($"State {state.GetHashCode()}{isAccept}{isIntermediate}:");

                foreach (var transition in state.Transitions)
                {
                    var range = transition.Min == transition.Max
                        ? (transition.Min >= 0 ? $"0x{transition.Min:X}" : transition.Min.ToString())
                        : (transition.Min >= 0 ? $"0x{transition.Min:X}-0x{transition.Max:X}" : $"{transition.Min}-{transition.Max}");
                    Console.WriteLine($"  {range} -> State {transition.To.GetHashCode()}");
                }
            }
        }


        public static void MainTests()
        {
            string[] inputs =
            {
                "aaabababb",
                "/* foo */",
                "fubar",
                "foobaz",
                "/* broke *",
                "hello world!",
                "hello world!\n"
            };
            try
            {

                var ccommentLazy = "# C comment\n" + @"/\*(.|\n)*?\*/";
                var test1 = "(?<baz>foo|fubar)+";
                var test2 = "(a|b)*?(b{2})";
                var test3 = "^hello world!$";
                var lexer = $"# Test Lexer\n{test1}\n{ccommentLazy}\n{test2}\n{test3}";
                var ast = RegexExpression.Parse(lexer)!;
                
                Console.WriteLine("Expression/Lexer:");
                Console.WriteLine(lexer);

                var dfa = ast.ToDfa();

                dfa.RenderToFile(@"..\..\..\dfa.dot");
                dfa.RenderToFile(@"..\..\..\dfa.jpg");
                Console.WriteLine("DFA construction successful!");
                Console.WriteLine($"Start state created with {dfa.Transitions.Count} transitions. State machine has {dfa.FillClosure().Count} states.");
                // Test the DFA with some strings
                foreach (var input in inputs) TestUtf32Dfa(dfa, input);
                
                Console.WriteLine();

                var utf16dfa = DfaUtf16Transformer.TransformToUtf16(dfa);
                Console.WriteLine("UTF16 transformation successful!");

                utf16dfa.RenderToFile(@"..\..\..\utf16dfa.dot");
                utf16dfa.RenderToFile(@"..\..\..\utf16dfa.jpg");
                Console.WriteLine("DFA construction successful!");
                Console.WriteLine($"Start state created with {utf16dfa.Transitions.Count} transitions. State machine has {utf16dfa.FillClosure().Count} states.");

                // Test the DFA with some strings
                foreach (var input in inputs) TestUtf16Dfa(utf16dfa, input);

                Console.WriteLine();

                var utf8dfa = DfaUtf8Transformer.TransformToUtf8(dfa);
                Console.WriteLine("UTF8 transformation successful!");
                utf8dfa.RenderToFile(@"..\..\..\utf8dfa.dot");
                utf8dfa.RenderToFile(@"..\..\..\utf8dfa.jpg");

                var array = utf8dfa.ToArray();
                if (Dfa.IsRangeArray(array))
                {
                    Console.WriteLine("Using range array");
                }
                else
                {
                    Console.WriteLine("Using non range array");
                }

                Console.WriteLine($"Start state created with {utf8dfa.Transitions.Count} transitions. State machine has {utf8dfa.FillClosure().Count} states. Array length is {array.Length}");

                // Test the DFA with some strings
                foreach (var input in inputs) TestUtf8Dfa(utf8dfa, input);

                Console.WriteLine();
                Console.WriteLine("Testing over array");
                foreach (var input in inputs) TestUtf8DfaArray(array,input);

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
        static void TestUtf32Dfa(Dfa startState, string input)
        {
            var currentState = startState;
            int position = 0;
            bool atLineStart = true;

            Console.WriteLine($"\n=== Testing '{input}' ===");

            var codepoints = RegexExpression.ToUtf32(input).ToArray();
            bool atLineEnd = codepoints.Length == 0 || (codepoints.Length == 1 && codepoints[0] == '\n');

            while (position <= codepoints.Length)
            {
                bool found = false;

                foreach (var transition in currentState.Transitions)
                {
                    // Check anchor transitions first
                    if (transition.Min == -2 && transition.Max == -2)  // START_ANCHOR ^
                    {
                        if (atLineStart)
                        {
                            currentState = transition.To;
                            atLineStart = false;
                            found = true;
                            break;
                        }
                    }
                    else if (transition.Min == -3 && transition.Max == -3)  // END_ANCHOR $
                    {
                        if (atLineEnd)
                        {
                            currentState = transition.To;
                            found = true;
                            break;
                        }
                    }
                    // Check character transitions only if not an anchor
                    else if (transition.Min >= 0 && position < codepoints.Length)
                    {
                        int codepoint = codepoints[position];
                        if (codepoint >= transition.Min && codepoint <= transition.Max)
                        {
                            currentState = transition.To;
                            position++;
                            atLineEnd = (position == codepoints.Length) || (position < codepoints.Length && codepoints[position] == '\n');
                            atLineStart = (codepoint == '\n');
                            found = true;
                            break;
                        }
                    }
                }

                if (!found)
                {
                    if (position < codepoints.Length)
                    {
                        var cp = codepoints[position];
                        var cpStr = cp <= 0xFFFF && !char.IsSurrogate((char)cp)
                            ? $"'{char.ConvertFromUtf32(cp)}'"
                            : $"U+{cp:X4}";
                        Console.WriteLine($"REJECTED: No transition for {cpStr} at codepoint position {position}");
                    }
                    else
                        Console.WriteLine($"REJECTED: No valid end transition");
                    return;
                }

                // Check for acceptance - KEY FIX: allow 1 remaining character (trailing newline)
                if (currentState.IsAccept)
                {
                    if (position < codepoints.Length - 1)
                    {
                        Console.WriteLine($"Rejected: Input remaining at codepoint position {position}");
                    }
                    else
                    {
                        Console.WriteLine($"ACCEPTED: {currentState.AcceptSymbol}");
                    }
                    return;
                }
            }

            Console.WriteLine($"REJECTED: Not in accept state");
        }
        static void TestUtf16Dfa(Dfa startState, string input)
        {

            var currentState = startState;
            int position = 0;
            bool atLineStart = true;
            bool atLineEnd = input.Length == 0 || input.Length == 1 && input[0] == '\n';
            Console.WriteLine($"\n=== Testing '{input}' ===");

            while (position <= input.Length)
            {
                bool found = false;

                foreach (var transition in currentState.Transitions)
                {
                    // Check anchor transitions first
                    if (transition.Min == -2 && transition.Max == -2)  // START_ANCHOR ^
                    {
                        if (atLineStart)
                        {
                            currentState = transition.To;
                            atLineStart = false;
                            found = true;
                            break;  // Exit foreach, don't check other transitions
                        }
                    }
                    else if (transition.Min == -3 && transition.Max == -3)  // END_ANCHOR $
                    {
                        if (atLineEnd)
                        {
                            currentState = transition.To;
                            found = true;
                            break;  // Exit foreach, don't check other transitions
                        }
                    }
                    // Check character transitions only if not an anchor
                    else if (transition.Min >= 0 && position < input.Length)
                    {
                        char c = input[position];
                        if (c >= transition.Min && c <= transition.Max)
                        {
                            currentState = transition.To;
                            position++;
                            atLineEnd = (position == input.Length) || (position < input.Length && input[position] == '\n');
                            atLineStart = (c == '\n');
                            found = true;
                            break;  // Exit foreach, don't check other transitions
                        }
                    }
                }

                if (!found)
                {
                    if (position < input.Length)
                        Console.WriteLine($"REJECTED: No transition for '{input[position]}' at position {position}");
                    else
                        Console.WriteLine($"REJECTED: No valid end transition");
                    return;
                }

                // Check for acceptance
                if (currentState.IsAccept)
                {
                    if (position < input.Length - 1)
                    {
                        Console.WriteLine($"Rejected: Input remaining");
                    }
                    else
                    {
                        Console.WriteLine($"ACCEPTED: {currentState.AcceptSymbol}");
                    }
                    return;
                }
            }

            Console.WriteLine($"REJECTED: Not in accept state");
        }
        static void TestUtf8DfaArray(int[] dfa, string input)
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            int currentStateIndex = 0;
            int position = 0;
            bool atLineStart = true;
            bool atLineEnd = bytes.Length == 0 || (bytes.Length == 1 && bytes[0] == '\n');
            bool isRangeArray = Dfa.IsRangeArray(dfa);
            Console.WriteLine($"\n=== Testing '{input}' ===");

            while (position <= bytes.Length)
            {
                bool found = false;

                // Parse current state's transitions from array
                int stateIndex = currentStateIndex;
                int acceptId = dfa[stateIndex++];
                int transitionCount = dfa[stateIndex++];

                // Check each transition (like foreach transition in working version)
                for (int t = 0; t < transitionCount; t++)
                {
                    int destStateIndex = dfa[stateIndex++];
                    int rangeCount = dfa[stateIndex++];

                    // Check if any range in this transition matches
                    bool transitionMatches = false;
                    for (int r = 0; r < rangeCount; r++)
                    {
                        int min, max;
                        if (isRangeArray)
                        {
                            min = dfa[stateIndex++];
                            max = dfa[stateIndex++];
                        }
                        else
                        {
                            min = max = dfa[stateIndex++];
                        }

                        // Check anchor transitions first (exact same logic as working version)
                        if (min == -2 && max == -2)  // START_ANCHOR ^
                        {
                            if (atLineStart)
                            {
                                currentStateIndex = destStateIndex;
                                atLineStart = false;
                                found = true;
                                transitionMatches = true;
                                break;
                            }
                        }
                        else if (min == -3 && max == -3)  // END_ANCHOR $
                        {
                            if (atLineEnd)
                            {
                                currentStateIndex = destStateIndex;
                                found = true;
                                transitionMatches = true;
                                break;
                            }
                        }
                        // Check character transitions (exact same logic as working version)
                        else if (min >= 0 && position < bytes.Length)
                        {
                            byte c = bytes[position];
                            if (c >= min && c <= max)
                            {
                                currentStateIndex = destStateIndex;
                                position++;
                                atLineEnd = (position == bytes.Length) || (position < bytes.Length && bytes[position] == '\n');
                                atLineStart = (c == '\n');
                                found = true;
                                transitionMatches = true;
                                break;
                            }
                        }
                    }

                    if (transitionMatches) break; // Exit transition loop, just like working version
                }

                if (!found)
                {
                    if (position < bytes.Length)
                        Console.WriteLine($"REJECTED: No transition for byte 0x{bytes[position]:X2} at position {position}");
                    else
                        Console.WriteLine($"REJECTED: No valid end transition");
                    return;
                }

                // Check for acceptance (exact same logic as working version)
                int currentAcceptId = dfa[currentStateIndex];
                if (currentAcceptId != -1)
                {
                    if (position < bytes.Length - 1)
                    {
                        Console.WriteLine($"Rejected: Input remaining");
                    }
                    else
                    {
                        Console.WriteLine($"ACCEPTED: {currentAcceptId}");
                    }
                    return;
                }
            }

            Console.WriteLine($"REJECTED: Not in accept state");
        }
        static void TestUtf8Dfa(Dfa startState, string input)
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            var currentState = startState;
            int position = 0;
            bool atLineStart = true;
            bool atLineEnd = bytes.Length == 0 || bytes.Length == 1 && bytes[0] == '\n';
            Console.WriteLine($"\n=== Testing '{input}' ===");

            while (position <= bytes.Length)
            {
                bool found = false;

                foreach (var transition in currentState.Transitions)
                {
                    // Check anchor transitions first
                    if (transition.Min == -2 && transition.Max == -2)  // START_ANCHOR ^
                    {
                        if (atLineStart)
                        {
                            currentState = transition.To;
                            atLineStart = false;
                            found = true;
                            break;  // Exit foreach, don't check other transitions
                        }
                    }
                    else if (transition.Min == -3 && transition.Max == -3)  // END_ANCHOR $
                    {
                        if (atLineEnd)
                        {
                            currentState = transition.To;
                            found = true;
                            break;  // Exit foreach, don't check other transitions
                        }
                    }
                    // Check character transitions only if not an anchor
                    else if (transition.Min >= 0 && position < input.Length)
                    {
                        byte c = bytes[position];
                        if (c >= transition.Min && c <= transition.Max)
                        {
                            currentState = transition.To;
                            position++;
                            atLineEnd = (position == input.Length) || (position < input.Length && input[position] == '\n');
                            atLineStart = (c == '\n');
                            found = true;
                            break;  // Exit foreach, don't check other transitions
                        }
                    }
                }

                if (!found)
                {
                    if (position < input.Length)
                        Console.WriteLine($"REJECTED: No transition for '{input[position]}' at position {position}");
                    else
                        Console.WriteLine($"REJECTED: No valid end transition");
                    return;
                }

                // Check for acceptance
                if (currentState.IsAccept)
                {
                    if (position < input.Length - 1)
                    {
                        Console.WriteLine($"Rejected: Input remaining");
                    }
                    else
                    {
                        Console.WriteLine($"ACCEPTED: {currentState.AcceptSymbol}");
                    }
                    return;
                }
            }

            Console.WriteLine($"REJECTED: Not in accept state");
        }
    }
}
