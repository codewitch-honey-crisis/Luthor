using Luthor;

using System.Text;
namespace LutherTest
{
    [TestClass]
    public sealed class Test1
    {
        public static void Contains(object value, System.Collections.IEnumerable collection)
        {
            var found = false;
            foreach (var item in collection)
            {
                if (object.Equals(item, value))
                {
                    found = true;
                    break;
                }
            }
            Assert.IsTrue(found, "The item was not found in the collection");
        }
        [TestMethod]
        public void MainTests()
        {
            Tests.MainTests();

        }
        [TestMethod]
        public void TestLatin1Transform()
        {
            // Arrange - pattern with lazy quantifier followed by accented e
            string pattern = @"[A-Za-z ]*?é"; // Lazy match ASCII letters/spaces, then accented é
            var encoding = Encoding.Latin1;

            // Act
            var expr = RegexExpression.Parse(pattern);
            var originalDfa = expr.ToDfa();
            var transformedDfa = DfaEncodingTransform.Transform(originalDfa, encoding);

            // Assert - check specific byte mappings exist
            var transitionBytes = GetAllTransitionBytes(transformedDfa);

            // Verify ASCII characters are mapped (A-Z = 65-90, a-z = 97-122, space = 32)
            Contains(65, transitionBytes);  // A
            Contains(97, transitionBytes);  // a
            Contains(32, transitionBytes);  // space
            Contains(233, transitionBytes); // é (U+00E9 → byte 233)

            Console.WriteLine($"Encoding: {encoding.EncodingName}");
            Console.WriteLine($"Is single byte: {encoding.IsSingleByte}");
            Console.WriteLine($"Transition bytes: [{string.Join(", ", transitionBytes.OrderBy(x => x))}]");

            // Verify transformation worked
            Assert.IsTrue(transitionBytes.Count > 0);
            Assert.IsTrue(transformedDfa.FillClosure().Count > 0);

            // Test with "café" - should match "caf" lazily, then "é"
            var text1 = "café";
            Assert.AreNotEqual(-1, TestDfaEnc(transformedDfa, text1, encoding));

            // Test with just "é" - empty lazy match, then "é"  
            var text2 = "é";
            Assert.AreNotEqual(-1, TestDfaEnc(transformedDfa, text2, encoding));

            // Test with "grande é" - should match "grande " lazily, then "é"
            var text3 = "grande é";
            Assert.AreNotEqual(-1, TestDfaEnc(transformedDfa, text3, encoding));

            // Test negative case - no "é" at end
            var text4 = "cafe"; // No accented e
            Assert.AreEqual(-1, TestDfaEnc(transformedDfa, text4, encoding));
        }

        private List<int> GetAllTransitionBytes(Dfa dfa)
        {
            var bytes = new List<int>();
            foreach (var state in dfa.FillClosure())
            {
                foreach (var transition in state.Transitions)
                {
                    for (int b = transition.Min; b <= transition.Max; b++)
                        bytes.Add(b);
                }
            }
            return bytes.Distinct().ToList();
        }
        [TestMethod]
        public void TestMininization()
        {
            var ast = RegexExpression.Parse(@"(/\*(.|\n)*?\*/)|(//.*$)");
            Assert.IsNotNull(ast);
            var dfa = ast.ToDfa();
            Assert.IsNotNull(dfa);
            var minDfa = dfa.ToMinimized();
            Assert.IsNotNull(minDfa);
            var exprs = new(string Input, bool Expected)[]
            {
                (@"/* foo *** */",true),
                (@"/* bar ***",false),
                (@"/**/",true),
                (@"/* / */",true),
                (@"/* */ */  ",false)
            };
            foreach(var expr in exprs)
            {
                int acc;
                Assert.AreEqual(acc=TestDfa(dfa, expr.Input), TestDfa(minDfa, expr.Input));
                Assert.AreEqual(acc != -1, expr.Expected);
            }
            
            

        }
        static int TestDfa(Dfa startState, string input)
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

                //  check character transitions 
                foreach (var transition in currentState.Transitions)
                {

                    if (position < codepoints.Length)
                    {
                        int codepoint = codepoints[position];
                        if (codepoint >= transition.Min && codepoint <= transition.Max)
                        {
                            currentState = transition.To;
                            position++;
                            atLineEnd = (position == codepoints.Length) ||
                                      (position < codepoints.Length && codepoints[position] == '\n');
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
                    return -1;
                }

                // Check for acceptance with anchor validation
                if (currentState.IsAccept)
                {
                    // Validate anchor conditions if present
                    if (!CheckAnchorConditions(currentState, atLineStart, atLineEnd))
                    {
                        Console.WriteLine($"REJECTED: Anchor condition not met");
                        return -1;
                    }

                    if (position < codepoints.Length - 1)
                    {
                        Console.WriteLine($"Rejected: Input remaining at codepoint position {position}");
                        return -1;
                    }
                    else
                    {
                        Console.WriteLine($"ACCEPTED: {currentState.AcceptSymbol}");
                        return currentState.AcceptSymbol;
                    }
                    
                }
            }
            Console.WriteLine($"REJECTED: Not in accept state");
            return -1;
        }
        static int TestDfaEnc(Dfa startState, string input, Encoding encoding)
        {
            var bytes = encoding.GetBytes(input);
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
                    if (position < input.Length)
                    {
                        byte c = bytes[position];
                        if (c >= transition.Min && c <= transition.Max)
                        {
                            currentState = transition.To;
                            position++;
                            atLineEnd = (position == bytes.Length) ||
                                        (position < bytes.Length && bytes[position] == '\n');
                            atLineStart = (c == '\n');
                            found = true;
                            break;
                        }
                    }
                }

                if (!found)
                {
                    if (position < input.Length)
                        Console.WriteLine($"REJECTED: No transition for '{input[position]}' at position {position}");
                    else
                        Console.WriteLine($"REJECTED: No valid end transition");
                    return -1;
                }

                // Check for acceptance with anchor validation
                if (currentState.IsAccept)
                {
                    // Validate anchor conditions if present
                    if (!CheckAnchorConditions(currentState, atLineStart, atLineEnd))
                    {
                        Console.WriteLine($"REJECTED: Anchor condition not met");
                        return -1;
                    }

                    if (position < input.Length - 1)
                    {
                        Console.WriteLine($"Rejected: Input remaining at codepoint position {position}");
                        return -1;
                    }
                    else
                    {
                        Console.WriteLine($"ACCEPTED: {currentState.AcceptSymbol}");
                        return currentState.AcceptSymbol;
                    }

                }
            }

            Console.WriteLine($"REJECTED: Not in accept state");
            return -1;
        }
        static bool CheckAnchorConditions(Dfa state, bool atLineStart, bool atLineEnd)
        {
            if (!state.Attributes.TryGetValue("AnchorMask", out var anchorObj) || !(anchorObj is int anchorMask))
                return true; // No anchors = always valid

            const int START_ANCHOR = 1;  // ^
            const int END_ANCHOR = 2;    // $

            // Check start anchor condition
            if ((anchorMask & START_ANCHOR) != 0 && !atLineStart)
                return false;

            // Check end anchor condition  
            if ((anchorMask & END_ANCHOR) != 0 && !atLineEnd)
                return false;

            return true;
        }
    }

}
