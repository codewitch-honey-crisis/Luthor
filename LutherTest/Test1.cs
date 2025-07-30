using Luthor;

using System;
using System.Text;
using System.Xml.Linq;

using static System.Net.Mime.MediaTypeNames;
namespace LutherTest
{
    [TestClass]
    public sealed class Test1
    {
        delegate int Runner(Dfa startState, string input);
        (Runner Matcher, string Name)[] _runners = new (Runner runner, string Name)[] {
            (TestUtf32Dfa,"UTF-32 Matcher"),
            (TestUtf16Dfa,"UTF-16 Matcher"),
            (TestUtf8Dfa,"UTF-8 Matcher")
        };
        (Dfa UnminDfa, Dfa Utf32Dfa, Dfa Utf16Dfa, Dfa Utf8Dfa) CreateDfas(RegexExpression expr)
        {
            if (expr == null)
            {
                return (null, null, null, null);
            }
            Console.WriteLine($"Testing expression AST: {expr}");
            var unminDfa = expr.ToDfa();
            unminDfa.RenderToFile(@"C:\Users\gazto\Pictures\test.jpg");
            var utf32Dfa = unminDfa.ToMinimized();
            var utf16Dfa = DfaUtf16Transformer.TransformToUtf16(utf32Dfa);
            var utf8Dfa = DfaUtf8Transformer.TransformToUtf8(utf32Dfa);
            return (unminDfa, utf32Dfa, utf16Dfa, utf8Dfa);
        }
        void TestMatches(Runner runner, Dfa dfa, IEnumerable<(string Text, int ExpectingSymbol)> inputs)
        {
            foreach (var input in inputs)
            {
                var exp = input.ExpectingSymbol == -1 ? "<no match>" : input.ExpectingSymbol.ToString();
                var acc = runner(dfa, input.Text);
                var exp2 = acc == -1 ? "<no match>" : acc.ToString();
                var pass = acc == input.ExpectingSymbol ? "pass" : "fail";
                Console.WriteLine($"Matching {input.Text}\t: Expected: {exp}, Result: {exp2}, Test: {pass}");
                if(input.ExpectingSymbol!=acc)
                {
                    Console.WriteLine($"== TEST FAILURE == on {runner.Method.Name} for text: {input.Text}");
                    //Console.WriteLine(Environment.CurrentDirectory);
                    Console.WriteLine("Failure dfa rendered to fail.jpg");
                    dfa.RenderToFile(@"..\..\..\fail.jpg",true);
                    dfa.ToMinimized().RenderToFile(@"..\..\..\fail_min.jpg",true);
                }
                Assert.AreEqual(input.ExpectingSymbol, acc);
            }
        }
        void TestMatches(Encoding encoding, Dfa dfa, IEnumerable<(string Text, int ExpectingSymbol)> inputs)
        {
            foreach (var input in inputs)
            {
                var exp = input.ExpectingSymbol == -1 ? "<no match>" : input.ExpectingSymbol.ToString();
                var acc = TestDfa(dfa, input.Text,encoding);
                var exp2 = acc == -1 ? "<no match>" : acc.ToString();
                var pass = acc == input.ExpectingSymbol ? "pass" : "fail";
                Console.WriteLine($"Matching {input.Text}\t: Expected: {exp}, Result: {exp2}, Test: {pass}");
                if (input.ExpectingSymbol != acc)
                {
                    Console.WriteLine($"== TEST FAILURE == on {encoding.EncodingName} matcher for text: {input.Text}");
                    //Console.WriteLine(Environment.CurrentDirectory);
                    Console.WriteLine("Failure dfa rendered to fail.jpg");
                    dfa.RenderToFile(@"..\..\..\fail.jpg", true);
                    dfa.ToMinimized().RenderToFile(@"..\..\..\fail_min.jpg", true);
                }
                Assert.AreEqual(input.ExpectingSymbol, acc);
            }
        }
        void TestExpression(string expr, IEnumerable<(string Text, int ExpectingSymbol)> inputs, Dfa dfa, Encoding encoding)
        {
            Console.WriteLine($"Using the regex: {expr}");
            var ast = RegexExpression.Parse(expr);
            try
            {
                Console.WriteLine($"  Running {encoding.EncodingName} transformed DFA...");
                TestMatches(encoding, dfa, inputs);
                Console.WriteLine();
            }
            catch
            {

                throw;
            }
        }
        void TestExpression(string expr, IEnumerable<(string Text, int ExpectingSymbol)> inputs)
        {

            Console.WriteLine($"Using the regex: {expr}");
            var ast = RegexExpression.Parse(expr);
            var dfas = CreateDfas(ast);

            try
            {
                Console.WriteLine($"  Running ${_runners[0].Name} Native DFA...");
                TestMatches(_runners[0].Matcher, dfas.UnminDfa, inputs);
                Console.WriteLine();
                Console.WriteLine($"  Running ${_runners[1].Name} UTF-16 DFA...");
                TestMatches(_runners[1].Matcher, dfas.Utf16Dfa, inputs);
                Console.WriteLine();
                Console.WriteLine($"  Running ${_runners[1].Name} UTF-8 DFA...");
                TestMatches(_runners[1].Matcher, dfas.Utf8Dfa, inputs);
                Console.WriteLine();

                Console.WriteLine();
            }
            catch 
            {
                
                throw;
            }
        }
        [TestMethod]
        public void BuildADfa()
        {
            var q0 = new Dfa();
            q0.Attributes["AcceptSymbol"] = 0;
            var q1 = new Dfa();
            q1.Attributes["AcceptSymbol"] = 0;
            q0.AddTransition(new DfaTransition(q1, 'b', 'b'));
            var q2 = new Dfa();
            q2.Attributes["AcceptSymbol"] = 0;
            q1.AddTransition(new DfaTransition(q2, 'b', 'b'));
            var q3 = new Dfa();
            q0.AddTransition(new DfaTransition(q3, 'a', 'a'));
            q3.AddTransition(new DfaTransition(q2, 'b', 'b'));

            q0.RenderToFile(@"..\..\..\abb.jpg", true);

            Console.WriteLine(q0.ToString());

        }
        [TestMethod]
        public void ForIsolatedFailureTesting()
        {
            // ((a|b)??b)* == (a?b)*
            TestExpression(@"((a|b)??b)*",
                new (string, int)[] {
            ("", 0),           // empty string
            ("b", 0),          // just b
            ("ab", 0),         // a then b
            ("bb", 0),         // b then b
            ("bab", 0),        // b, a, b
            ("abab", 0),       // ab twice
            ("babab", 0),      // b, ab, ab
            ("ababab", 0),     // ab three times
            ("a", -1),         // trailing a
            ("aba", -1),       // trailing a
            ("c", -1),         // invalid char
                });
        }
        [TestMethod]
        public void TestLexer()
        {
            var ccommentLazy = "# C comment\n" + @"/\*(.|\n)*?\*/";
            var test1 = "(?<baz>foo|fubar)+";
            var test2 = "(a|b)*?(b{2})";
            var test3 = "^hello world!$";
            var lexer = $"# Test Lexer\n{test1}\n{ccommentLazy}\n{test2}\n{test3}";
            TestExpression(lexer, new (string, int)[] {
                ("aaabababb",2),
                ("/* foo */",1),
                ("fubar",0),
                ("foobaz",-1),
                ("/* broke *",-1),
                ("hello world!",3),
                ("hello world!\n",3)
            });

        }
        [TestMethod]
        public void TestLazy()
        {
            // Original working tests
            TestExpression(@"(a|b)*?bb",
                new (string, int)[] {
            ("aaabb", 0),
            ("caaabb", -1),
            ("aaabbc", -1),
                });

            TestExpression(@"(/\*(.|\n)*?\*/)|(//.*$)",
                new (string, int)[] {
            ("// foo", 0),
            ("// foo\n", 0),
            ("/*** bar */", 0),
            ("/**/", 0),
            ("//", 0),
            ("//\n", 0),
            ("///", 0),
            ("/", -1),
            ("/ *****", -1),
                });

            // Test lazy nested in greedy

            // ((a|b)??b)* == (a?b)*
            TestExpression(@"((a|b)??b)*",
                new (string, int)[] {
            ("", 0),           // empty string
            ("b", 0),          // just b
            ("ab", 0),         // a then b
            ("bb", 0),         // b then b
            ("bab", 0),        // b, a, b
            ("abab", 0),       // ab twice
            ("babab", 0),      // b, ab, ab
            ("ababab", 0),     // ab three times
            ("a", -1),         // trailing a
            ("aba", -1),       // trailing a
            ("c", -1),         // invalid char
                });

            // ((a|b)*?b)* == (a*b)*
            TestExpression(@"((a|b)*?b)*",
                new (string, int)[] {
            ("", 0),           // empty string
            ("b", 0),          // just b
            ("ab", 0),         // a then b
            ("aab", 0),        // aa then b
            ("aaab", 0),       // aaa then b
            ("bab", 0),        // b, a, b
            ("baab", 0),       // b, aa, b
            ("bb", 0),         // b, b
            ("abaaab", 0),     // ab, aaa, b
            ("a", -1),         // trailing a
            ("aba", -1),       // trailing a
            ("c", -1),         // invalid char
                });

            // ((a|b)+?b)* == (a+b)*
            TestExpression(@"((a|b)+?b)*",
                new (string, int)[] {
            ("", 0),           // empty string
            ("ab", 0),         // a then b
            ("bb", 0),         // b then b
            ("aab", 0),        // aa then b
            ("abab", 0),       // ab twice
            ("abbb", 0),       // ab, bb
            ("aabbb", 0),      // aab, bb
            ("b", -1),         // just b (needs at least one a|b before b)
            ("a", -1),         // trailing a
            ("c", -1),         // invalid char
                });

            // ((a|b)??b)? == (a?b)?
            TestExpression(@"((a|b)??b)?",
                new (string, int)[] {
            ("", 0),           // empty (optional)
            ("b", 0),          // just b
            ("ab", 0),         // a then b
            ("bb", 0),        // too much (passes anyway due to funadamental DFA restrictions)
            ("a", -1),         // incomplete
            ("c", -1),         // invalid
                });

            // ((a|b)*?b)? == (a*b)?
            TestExpression(@"((a|b)*?b)?",
                new (string, int)[] {
            ("", 0),           // empty (optional)
            ("b", 0),          // just b
            ("ab", 0),         // a then b
            ("aab", 0),        // aa then b
            ("aaab", 0),       // aaa then b
            ("bb", 0),        // extra b // DFA limitation - correct match but not lazy preference order.
            ("a", -1),         // incomplete
            ("c", -1),         // invalid
                });

            // ((a|b)+?b)? == ((a|b)(a*b))?
            TestExpression(@"((a|b)+?b)?",
                new (string, int)[] {
            ("", 0),           // empty (optional)
            ("ab", 0),         // a then b
            ("bb", 0),         // b then b
            ("aab", 0),        // aa then b
            ("b", -1),         // just b (needs +)
            ("a", -1),         // incomplete
            ("c", -1),         // invalid
                });

            // ((a|b)??b)+ == (a?b)+
            TestExpression(@"((a|b)??b)+",
                new (string, int)[] {
            ("b", 0),          // just b
            ("ab", 0),         // a then b
            ("bb", 0),         // b, b
            ("bab", 0),        // b, a, b
            ("abab", 0),       // ab, ab
            ("", -1),          // empty (needs +)
            ("a", -1),         // incomplete
            ("c", -1),         // invalid
                });

            // ((a|b)*?b)+ == (a*b)+
            TestExpression(@"((a|b)*?b)+",
                new (string, int)[] {
            ("b", 0),          // just b
            ("ab", 0),         // a then b
            ("aab", 0),        // aa then b
            ("bb", 0),         // b, b
            ("bab", 0),        // b, a, b
            ("abaab", 0),      // ab, aab
            ("", -1),          // empty (needs +)
            ("a", -1),         // incomplete
            ("c", -1),         // invalid
                });

            // ((a|b)+?b)+ == (a+b)+
            TestExpression(@"((a|b)+?b)+",
                new (string, int)[] {
            ("ab", 0),         // a then b
            ("bb", 0),         // b then b
            ("aab", 0),        // aa then b
            ("abab", 0),       // ab, ab
            ("abbb", 0),       // ab, bb
            ("", -1),          // empty (needs +)
            ("b", -1),         // just b (needs + before b)
            ("a", -1),         // incomplete
            ("c", -1),         // invalid
                });

            // ((a|b)??)* == empty
            TestExpression(@"((a|b)??)*",
                new (string, int)[] {
            ("", 0),           // empty only
            ("a", 0),         
            ("b", 0),
            ("ab", 0),
                });

            // ((a|b)*?)* == empty
            TestExpression(@"((a|b)*?)*",
                new (string, int)[] {
            ("", 0), 
            ("a", 0),
            ("b", 0),
            ("ab", 0),
                });

            // ((a|b)+?)* == empty
            TestExpression(@"((a|b)+?)*",
                new (string, int)[] {
            ("", 0),          
            ("a", 0),         
            ("b", 0),
            ("ab", 0),
                });

            // ((a|b)??)? == empty
            TestExpression(@"((a|b)??)?",
                new (string, int)[] {
            ("", 0),           
            ("a", 0),         
            ("b", 0),
                });

            // ((a|b)*?)? == empty
            TestExpression(@"((a|b)*?)?",
                new (string, int)[] {
            ("", 0),          
            ("a", 0),         
            ("b", 0),
                });

            // ((a|b)+?)? == (a|b)?
            TestExpression(@"((a|b)+?)?",
                new (string, int)[] {
            ("", 0),           // empty
            ("a", 0),          // single a
            ("b", 0),          // single b
            ("ab", 0),        // not too much!
            ("c", -1),         // invalid
                });

            // ((a|b)??)+ == empty
            TestExpression(@"((a|b)??)+",
                new (string, int)[] {
            ("", 0),         
            ("a", 0),        
            ("b", 0),
                });

            // ((a|b)*?)+ == empty
            TestExpression(@"((a|b)*?)+",
                new (string, int)[] {
            ("", 0),          
            ("a", 0),        
            ("b", 0),
                });

            // ((a|b)+?)+ == (a|b)+
            TestExpression(@"((a|b)+?)+",
                new (string, int)[] {
            ("a", 0),          // single a
            ("b", 0),          // single b
            ("ab", 0),         // a and b
            ("ba", 0),         // b and a
            ("aab", 0),        // multiple
            ("", -1),          // empty (needs +)
            ("c", -1),         // invalid
                });

            // (b(a|b)*?)* == b*
            TestExpression(@"(b(a|b)*?)*",
                new (string, int)[] {
            ("", 0),           // empty
            ("b", 0),          // single b
            ("bb", 0),         // multiple b
            ("bbb", 0),        // many b
            ("a", -1),         // must start with b if not empty
            ("ba", 0),        
            ("ab", -1),        // can't start with a
                });

            // Test lazy followed by greedy - Complex alternations

            // (a|b)*?a*b*|(c|d)*?c*d+|(e|f)*?e+f*|(g|h)*?g+h+|(i|j)+?i*j*|(k|l)+?k*l+|(m|n)+?m+n*|(o|p)+?o+p+
            TestExpression(@"(a|b)*?a*b*|(c|d)*?c*d+|(e|f)*?e+f*|(g|h)*?g+h+|(i|j)+?i*j*|(k|l)+?k*l+|(m|n)+?m+n*|(o|p)+?o+p+",
                new (string, int)[] {
            // First alternative: (a|b)*?a*b*
            ("", 0),           // empty
            ("a", 0),          // just a
            ("b", 0),          // just b
            ("ab", 0),         // a then b
            ("aab", 0),        // aa then b
            ("abb", 0),        // a then bb
            ("aabb", 0),       // aa then bb
            
            // Second alternative: (c|d)*?c*d+
            ("d", 0),          // just d
            ("cd", 0),         // c then d
            ("ccd", 0),        // cc then d
            ("cdd", 0),        // c then dd
            ("dd", 0),         // just dd
            ("c", -1),         // c without d
            
            // Third alternative: (e|f)*?e+f*
            ("e", 0),          // just e
            ("ee", 0),         // multiple e
            ("ef", 0),         // e then f
            ("eef", 0),        // ee then f
            ("eeff", 0),       // ee then ff
            ("f", -1),         // f without e
            
            // Fourth alternative: (g|h)*?g+h+
            ("gh", 0),         // g then h
            ("ggh", 0),        // gg then h
            ("ghh", 0),        // g then hh
            ("gghh", 0),       // gg then hh
            ("g", -1),         // g without h
            ("h", -1),         // h without g
            
            // Fifth alternative: (i|j)+?i*j*
            ("i", 0),          // just i
            ("j", 0),          // just j
            ("ij", 0),         // i then j
            ("ji", 0),         // j then i
            ("iij", 0),        // ii then j
            ("ijj", 0),        // i then jj
            
            // Sixth alternative: (k|l)+?k*l+
            ("kl", 0),         // k then l
            ("ll", 0),         // l then l
            ("kkl", 0),        // kk then l
            ("kll", 0),        // k then ll
            ("k", -1),         // k without l
            
            // Seventh alternative: (m|n)+?m+n*
            ("m", -1),          // just m
            ("mm", 0),         // multiple m
            ("mn", -1),         // m then n
            ("mmn", 0),        // mm then n
            ("mmnn", 0),       // mm then nn
            ("n", -1),         // n without m
            
            // Eighth alternative: (o|p)+?o+p+
            ("op", -1),         // o then p
            ("oop", 0),        // oo then p
            ("opp", -1),        // o then pp
            ("oopp", 0),       // oo then pp
            ("o", -1),         // o without p
            ("p", -1),         // p without o
            
            // Invalid cases
            ("x", -1),         // invalid char
            ("ac", -1),        // mixed alternations
                });

            // Test lazy followed by lazy

            // (a|b)??b(a|b)??b|(c|d)*?d(c|d)*?d|(e|f)+?f(e|f)+?f|((g|h)??h){2}|((i|j)*?j){2}|((k|l)+?l){2}
            TestExpression(@"(a|b)??b(a|b)??b|(c|d)*?d(c|d)*?d|(e|f)+?f(e|f)+?f|((g|h)??h){2}|((i|j)*?j){2}|((k|l)+?l){2}",
                new (string, int)[] {
            // First alternative: (a|b)??b(a|b)??b
            ("bb", 0),         // b then b
            ("abb", 0),        // a, b, b
            ("bab", 0),        // b, a, b
            ("abab", 0),       // a, b, a, b
            ("b", -1),         // incomplete
            
            // Second alternative: (c|d)*?d(c|d)*?d
            ("dd", 0),         // d then d
            ("cdd", 0),        // c, d, d
            ("dcd", 0),        // d, c, d
            ("cdcd", 0),       // c, d, c, d
            ("d", -1),         // incomplete
            
            // Third alternative: (e|f)+?f(e|f)+?f
            ("ff", -1),         // f then f
            ("eff", -1),        // e, f, f
            ("fef", -1),        // f, e, f
            ("efef", 0),       // e, f, e, f
            ("f", -1),         // incomplete
            
            // Fourth alternative: ((g|h)??h){2}
            ("hh", 0),         // h, h
            ("ghh", 0),        // g, h, h
            ("hgh", 0),        // h, g, h
            ("ghgh", 0),       // g, h, g, h
            ("h", -1),         // incomplete
            
            // Fifth alternative: ((i|j)*?j){2}
            ("jj", 0),         // j, j
            ("ijj", 0),        // i, j, j
            ("jij", 0),        // j, i, j
            ("ijij", 0),       // i, j, i, j
            ("j", -1),         // incomplete
            
            // Sixth alternative: ((k|l)+?l){2}
            ("ll", 0),         // l, l
            ("kll", 0),        // k, l, l
            ("lkl", 0),        // l, k, l
            ("klkl", 0),       // k, l, k, l
            ("l", -1),         // incomplete
            
            // Invalid cases
            ("x", -1),         // invalid char
            ("ab", -1),        // wrong pattern
                });
        }
        [TestMethod]
        public void TestLazyAnchoring()
        {
            TestExpression(@"//$", [("//", 0)]);
            TestExpression(@"//.*$", [("//", 0)]);
            TestExpression(@"//.*$", [("//foo", 0)]);
            TestExpression(@"//.*?", [("//foo", 0)]); // between the choice to make this greedy or cut it off // we went with greedy
            TestExpression(@"//.*?", [("//", 0)]);

        }
        [TestMethod]
        public void TestUtf8SurrogateRangeHandling()
        {
            Console.WriteLine("=== Testing UTF-8 Surrogate Range Handling ===");

            // Test pattern that would include surrogate range if not handled properly
            var pattern = @"[\uD000-\uE001]"; // Range that crosses surrogate boundary
            var expr = RegexExpression.Parse(pattern);
            expr.SetSynthesizedPositions();

            var originalDfa = expr.ToDfa();
            Assert.IsNotNull(originalDfa);

            // This should not crash - the transform should skip surrogate codepoints
            var utf8Dfa = DfaUtf8Transformer.TransformToUtf8(originalDfa);
            Assert.IsNotNull(utf8Dfa);

            Console.WriteLine($"Original DFA: {originalDfa.FillClosure().Count} states");
            Console.WriteLine($"UTF-8 DFA: {utf8Dfa.FillClosure().Count} states");

            // Test valid characters that should match (before and after surrogate range)
            var testInputs = new[]
            {
        "\uD7FF", // Last character before surrogates
        "\uE000", // First character after surrogates  
        "\uE001"  // Character after surrogates
    };

            foreach (var input in testInputs)
            {
                var result = TestUtf8Dfa(utf8Dfa, input);
                Console.WriteLine($"Input '{input}' (U+{(int)input[0]:X4}): {(result != -1 ? "MATCH" : "NO MATCH")}");
                // These should match since they're in the valid ranges
                Assert.AreNotEqual(-1, result, $"Expected match for {input}");
            }
        }

        [TestMethod]
        public void TestUtf8CrossBoundaryRanges()
        {
            var chars = new[] { "ÿ", "ခ" }; // U+00FF and U+1001
            foreach (var testChar in chars)
            {
                var utf8Bytes = Encoding.UTF8.GetBytes(testChar);
                Console.WriteLine($"UTF-8 bytes for '{testChar}' (U+{(int)testChar[0]:X4}): [{string.Join(", ", utf8Bytes.Select(b => $"0x{b:X2}"))}]");
            }

            // Test ranges that cross UTF-8 encoding length boundaries
            var testCases = new[]
            {
        (@"[~-‚]", "Test 1-2 byte boundary (0x7E-0x82)", new[] { "~", "\u007F", "‚" }),
        (@"[ÿ-ခ]", "Test 2-3 byte boundary (0xFF-0x1000)", new[] { "ÿ", "Ā", "ခ" }),
        (@"[A-🙂]", "Test large range 1-4 bytes (0x41-0x1F642)", new[] { "A", "á", "漢", "🙂" })
    };

            foreach (var (pattern, description, inputs) in testCases)
            {
                Console.WriteLine($"\n{description}: {pattern}");

                var expr = RegexExpression.Parse(pattern);
                expr.SetSynthesizedPositions();

                var originalDfa = expr.ToDfa();
                var utf8Dfa = DfaUtf8Transformer.TransformToUtf8(originalDfa);

                Console.WriteLine($"States: {originalDfa.FillClosure().Count} -> {utf8Dfa.FillClosure().Count}");

                foreach (var input in inputs)
                {
                    var originalResult = TestUtf32Dfa(originalDfa, input);
                    var utf8Result = TestUtf8Dfa(utf8Dfa, input);

                    Console.WriteLine($"  '{input}': Original={originalResult}, UTF8={utf8Result}");
                    Assert.AreEqual(originalResult, utf8Result, $"Results should match for input '{input}'");
                }
            }
        }

        [TestMethod]
        public void TestUtf16SurrogateRangeHandling()
        {
            Console.WriteLine("=== Testing UTF-16 Surrogate Range Handling ===");

            // Test pattern that would include surrogate range
            var pattern = @"[\uD000-\uE000]"; // Range that crosses surrogate boundary
            var expr = RegexExpression.Parse(pattern);
            expr.SetSynthesizedPositions();

            var originalDfa = expr.ToDfa();
            Assert.IsNotNull(originalDfa);

            // This should not crash and should properly handle surrogates
            var utf16Dfa = DfaUtf16Transformer.TransformToUtf16(originalDfa);
            Assert.IsNotNull(utf16Dfa);

            Console.WriteLine($"Original DFA: {originalDfa.FillClosure().Count} states");
            Console.WriteLine($"UTF-16 DFA: {utf16Dfa.FillClosure().Count} states");

            // Test valid characters (should skip surrogate range)
            var testInputs = new[]
            {
        "\uD7FF", // Last character before surrogates
        "\uE000", // First character after surrogates
        "\uE001"  // Character after surrogates
    };

            foreach (var input in testInputs)
            {
                var originalResult = TestUtf32Dfa(originalDfa, input);
                var utf16Result = TestUtf16Dfa(utf16Dfa, input);

                Console.WriteLine($"Input '{input}' (U+{(int)input[0]:X4}): Original={originalResult}, UTF16={utf16Result}");
                Assert.AreEqual(originalResult, utf16Result, $"Results should match for input '{input}'");
            }
        }

        [TestMethod]
        public void TestUtf16CrossBmpBoundary()
        {
            Console.WriteLine("=== Testing UTF-16 Cross-BMP Boundary ===");

            // Test range that crosses BMP boundary (requires surrogate pairs)
            var pattern = @"[A-😂]"; // 0x41 to 0x1F602 - crosses 0xFFFF boundary
            var expr = RegexExpression.Parse(pattern);
            expr.SetSynthesizedPositions();

            var originalDfa = expr.ToDfa();
            var utf16Dfa = DfaUtf16Transformer.TransformToUtf16(originalDfa);

            Console.WriteLine($"Pattern: {pattern}");
            Console.WriteLine($"States: {originalDfa.FillClosure().Count} -> {utf16Dfa.FillClosure().Count}");

            // Test inputs from both BMP and supplementary planes
            var testInputs = new[]
            {
        "A",        // Basic ASCII
        "ÿ",        // End of Latin-1
        "\uFFFF",   // Last BMP character
        "😀",       // First emoji in range (0x1F600)
        "😂"        // Target emoji (0x1F602)
    };

            foreach (var input in testInputs)
            {
                var originalResult = TestUtf32Dfa(originalDfa, input);
                var utf16Result = TestUtf16Dfa(utf16Dfa, input);

                Console.WriteLine($"Input '{input}': Original={originalResult}, UTF16={utf16Result}");
                Assert.AreEqual(originalResult, utf16Result, $"Results should match for input '{input}'");
            }
        }

        [TestMethod]
        public void TestUtf16SupplementaryCharacterRanges()
        {
            Console.WriteLine("=== Testing UTF-16 Supplementary Character Ranges ===");

            // Test range entirely in supplementary plane that spans multiple high surrogates
            //var pattern = @"[\U0001F600-\U0001F600]"; // 0x1F600 to 0x1F914 - spans multiple high surrogates
            var pattern = @"[\U0001F600-\U0001F914]";
            //var pattern = @"[\U0001F600-\U0001F600]";
            var expr = RegexExpression.Parse(pattern);
            Console.WriteLine("Displaying AST");
            expr.Visit((parent, expr, childIndex, level) =>
            {
                Console.WriteLine($"{new string(' ', level * 2)}{expr.GetType().Name}\t{expr.ToString()}");
                return true;
            });
            var charset = (RegexCharsetExpression)expr;
            var ranges = charset.GetRanges();
            Console.WriteLine("Raw charset ranges:");
            foreach (var range in ranges)
            {
                Console.WriteLine($"  U+{range.Min:X4}-U+{range.Max:X4}");
                if (range.Min >= 0xD800 && range.Max <= 0xDFFF)
                    Console.WriteLine($"    ^^^ SURROGATE RANGE!");
            }

            var originalDfa = expr.ToDfa();
            var utf16Dfa = DfaUtf16Transformer.TransformToUtf16(originalDfa);

            Console.WriteLine($"Pattern: {pattern}");
            Console.WriteLine($"States: {originalDfa.FillClosure().Count} -> {utf16Dfa.FillClosure().Count}");

            // Test various emoji in the range
            var testInputs = new[]
            {
        "😀",  // 0x1F600 - start of range
        "😏",  // 0x1F60F - within range
        "🙂",  // 0x1F642 - within range  
        "🤔",  // 0x1F914 - end of range
        "🤕"   // 0x1F915 - just outside range
    };

            foreach (var input in testInputs)
            {
                var codepoints = RegexExpression.ToUtf32(input).ToArray();
                var originalResult = TestUtf32Dfa(originalDfa, input);
                var utf16Result = TestUtf16Dfa(utf16Dfa, input);

                var shouldMatch = input != "🤕"; // All except the last should match
                Console.WriteLine($"Input '{input}': Original={originalResult}, UTF16={utf16Result}, Expected={shouldMatch}");

                Assert.AreEqual(originalResult, utf16Result, $"Results should match for input '{input}'");

                if (shouldMatch)
                {
                    Assert.AreNotEqual(-1, utf16Result, $"Expected match for '{input}'");
                }
                else
                {
                    Assert.AreEqual(-1, utf16Result, $"Expected no match for '{input}'");
                }
            }
        }

        [TestMethod]
        public void TestUtf8LargeRangeSampling()
        {
            Console.WriteLine("=== Testing UTF-8 Large Range Sampling ===");

            // Test a very large range that requires careful sampling to avoid surrogates
            var pattern = @"[\u0100-\uFFFE]"; // Large range that includes surrogate area
            var expr = RegexExpression.Parse(pattern);
            expr.SetSynthesizedPositions();

            var originalDfa = expr.ToDfa();

            // This should not crash despite the large range including surrogates
            var utf8Dfa = DfaUtf8Transformer.TransformToUtf8(originalDfa);
            Assert.IsNotNull(utf8Dfa);

            Console.WriteLine($"Large range pattern: {pattern}");
            Console.WriteLine($"States: {originalDfa.FillClosure().Count} -> {utf8Dfa.FillClosure().Count}");

            // Test characters from different parts of the range
            var testInputs = new[]
            {
        "\u0100", // Start of range (Latin Extended-A)
        "\u0800", // 3-byte UTF-8 boundary
        "\uD7FF", // Just before surrogates (should match)
        "\uE000", // Just after surrogates (should match)
        "\uFFFE"  // End of range
    };

            foreach (var input in testInputs)
            {
                var originalResult = TestUtf32Dfa(originalDfa, input);
                var utf8Result = TestUtf8Dfa(utf8Dfa, input);

                Console.WriteLine($"Input '{input}' (U+{(int)input[0]:X4}): Original={originalResult}, UTF8={utf8Result}");
                Assert.AreEqual(originalResult, utf8Result, $"Results should match for input '{input}'");
                Assert.AreNotEqual(-1, utf8Result, $"Expected match for '{input}' in large range");
            }
        }

        [TestMethod]
        public void TestEncodingTransformationEdgeCases()
        {
            Console.WriteLine("=== Testing Encoding Transformation Edge Cases ===");

            var edgeCases = new[]
            {
        // UTF-8 boundary cases
        (@"[\u007F-\u0080]", "ASCII-to-2byte boundary"),
        (@"[\u07FF-\u0800]", "2byte-to-3byte boundary"),
        (@"[\uFFFF-\U00010000]", "3byte-to-4byte boundary"),
        
        // UTF-16 boundary cases  
        (@"[\uFFFE-\U00010001]", "BMP-to-supplementary boundary"),
        (@"[\U0001F600-\U0001F6FF]", "Emoji block (supplementary)")
    };

            foreach (var (pattern, description) in edgeCases)
            {
                Console.WriteLine($"\n{description}: {pattern}");

                try
                {
                    var expr = RegexExpression.Parse(pattern);
                    expr.SetSynthesizedPositions();

                    var originalDfa = expr.ToDfa();

                    // Both transformations should succeed without exceptions
                    var utf8Dfa = DfaUtf8Transformer.TransformToUtf8(originalDfa);
                    var utf16Dfa = DfaUtf16Transformer.TransformToUtf16(originalDfa);

                    Assert.IsNotNull(utf8Dfa, "UTF-8 transformation should succeed");
                    Assert.IsNotNull(utf16Dfa, "UTF-16 transformation should succeed");

                    Console.WriteLine($"  ✓ Transformations successful");
                    Console.WriteLine($"  States: {originalDfa.FillClosure().Count} -> UTF8:{utf8Dfa.FillClosure().Count}, UTF16:{utf16Dfa.FillClosure().Count}");
                }
                catch (Exception ex)
                {
                    Assert.Fail($"Transformation failed for {description}: {ex.Message}");
                }
            }
        }

        [TestMethod]
        public void TestSurrogateHandlingConsistency()
        {
            Console.WriteLine("=== Testing Surrogate Handling Consistency ===");

            var test = "퀀";
            var utf8Bytes = Encoding.UTF8.GetBytes(test);
            Console.WriteLine($"UTF-8 bytes for '{test}': [{string.Join(", ", utf8Bytes.Select(b => $"0x{b:X2}"))}]");

            // Test that both transformations handle surrogate ranges consistently
            var pattern = @"[\uD000-\uE000]"; // Range spanning surrogates
            var expr = RegexExpression.Parse(pattern);
            expr.SetSynthesizedPositions();

            var originalDfa = expr.ToDfa();
            var utf8Dfa = DfaUtf8Transformer.TransformToUtf8(originalDfa);
            var utf16Dfa = DfaUtf16Transformer.TransformToUtf16(originalDfa);

            Console.WriteLine("UTF-8 DFA state machine:");
            var utf8States = utf8Dfa.FillClosure();
            for (int i = 0; i < utf8States.Count; i++)
            {
                var state = utf8States[i];
                var isAccept = state.IsAccept ? " (ACCEPT)" : "";
                var isIntermediate = state.Attributes.ContainsKey("IsIntermediate") ? " (INTERMEDIATE)" : "";
                Console.WriteLine($"State {i}{isAccept}{isIntermediate}:");

                foreach (var transition in state.Transitions)
                {
                    var range = transition.Min == transition.Max
                        ? $"0x{transition.Min:X2}"
                        : $"0x{transition.Min:X2}-0x{transition.Max:X2}";
                    Console.WriteLine($"  {range} -> State {utf8States.IndexOf(transition.To)}");
                }
            }
            // Test the same inputs against all three DFAs
            var testInputs = new[]
            {
        "\uD000", // Start of problematic range
        "\uD7FF", // Last before surrogates
        "\uE000", // First after surrogates
        "\uE001"  // After surrogates
    };

            Console.WriteLine("Testing consistency across transformations:");
            foreach (var input in testInputs)
            {
                var originalResult = TestUtf32Dfa(originalDfa, input);
                var utf8Result = TestUtf8Dfa(utf8Dfa, input);
                var utf16Result = TestUtf16Dfa(utf16Dfa, input);

                Console.WriteLine($"Input '{input}' (U+{(int)input[0]:X4}):");
                Console.WriteLine($"  Original: {originalResult}, UTF8: {utf8Result}, UTF16: {utf16Result}");

                // All transformations should give the same result
                Assert.AreEqual(originalResult, utf8Result, $"UTF-8 result should match original for '{input}'");
                Assert.AreEqual(originalResult, utf16Result, $"UTF-16 result should match original for '{input}'");
                Assert.AreEqual(utf8Result, utf16Result, $"UTF-8 and UTF-16 results should match for '{input}'");
            }
        }
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
        public void TestAst()
        {
            var ccommentLazy = @"/\*(.|\n)*?\*/";
            var test1 = "(?<baz>foo|fubar)+";
            var test2 = "(a|b)*?(b{2})";
            var test3 = "^hello world!$";
            var lexer = $"# Test Lexer\n{test1}\n# C block comment\n{ccommentLazy}\n{test2}\n{test3}";
            var ast = RegexExpression.Parse(lexer)!;
            // normalize it
            ast = RegexExpression.Parse(lexer.ToString());
            var ast2 = ast.Clone();
            Assert.AreEqual(ast,ast2);
        }
        

        public void MainTests()
        {
            (string Text, int Expected)[] inputs =
            {
                ("aaabababb",2),
                ("/* foo */",1),
                ("fubar",0),
                ("foobaz",-1),
                ("/* broke *",-1),
                ("hello world!",3),
                ("hello world!\n",3)
            };


            Dfa dfa = null;
            Assert.IsNotNull(dfa);



            Console.WriteLine($"Start state created with {dfa.Transitions.Count} transitions. State machine has {dfa.FillClosure().Count} states.");
            // Test the DFA with some strings
            foreach (var input in inputs) Assert.AreEqual(input.Expected, TestUtf32Dfa(dfa, input.Text), null, input.Text);

            Console.WriteLine();

            var utf16dfa = DfaUtf16Transformer.TransformToUtf16(dfa);
            Console.WriteLine("UTF16 transformation successful!");


            Console.WriteLine("DFA construction successful!");
            Console.WriteLine($"Start state created with {utf16dfa.Transitions.Count} transitions. State machine has {utf16dfa.FillClosure().Count} states.");

            // Test the DFA with some strings
            foreach (var input in inputs) Assert.AreEqual(input.Expected, TestUtf16Dfa(utf16dfa, input.Text), null, input.Text);

            Console.WriteLine();

            var utf8dfa = DfaUtf8Transformer.TransformToUtf8(dfa);
            Console.WriteLine("UTF8 transformation successful!");

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
            foreach (var input in inputs) Assert.AreEqual(input.Expected, TestUtf8Dfa(utf8dfa, input.Text), null, input.Text);

            Console.WriteLine();
            Console.WriteLine("Testing over array");
            foreach (var input in inputs) Assert.AreEqual(input.Expected, TestUtf8DfaArray(array, input.Text), null, input.Text);

            Console.WriteLine();
            Console.WriteLine("==== Testing minimized dfas =====");

            // DEBUG THE MINIMIZED START STATE
            Console.WriteLine("=== Minimized Start State Analysis ===");
            Console.WriteLine($"Uninimized start state has {dfa.Transitions.Count} transitions:");
            foreach (var transition in dfa.Transitions)
            {
                if (transition.Min >= 32 && transition.Max <= 126)
                {
                    var minChar = transition.Min == transition.Max ?
                        $"'{(char)transition.Min}'" :
                        $"'{(char)transition.Min}'-'{(char)transition.Max}'";
                    Console.WriteLine($"  {minChar} -> AcceptSymbol={transition.To.AcceptSymbol}");
                }
                else
                {
                    Console.WriteLine($"  [U+{transition.Min:X4}-U+{transition.Max:X4}] -> AcceptSymbol={transition.To.AcceptSymbol}");
                }
            }


            dfa = dfa.ToMinimized();
            foreach (var input in inputs) Assert.AreEqual(input.Expected, TestUtf32Dfa(dfa, input.Text), null, input.Text);

            foreach (var input in inputs) Assert.AreEqual(input.Expected, TestUtf32Dfa(dfa, input.Text), null, input.Text);

            utf16dfa = utf16dfa.ToMinimized();
            foreach (var input in inputs) Assert.AreEqual(input.Expected, TestUtf16Dfa(utf16dfa, input.Text), null, input.Text);

            utf8dfa = utf8dfa.ToMinimized();
            foreach (var input in inputs) Assert.AreEqual(input.Expected, TestUtf8Dfa(utf8dfa, input.Text), null, input.Text);

            array = utf8dfa.ToArray();
            foreach (var input in inputs) Assert.AreEqual(input.Expected, TestUtf8DfaArray(array, input.Text), null, input.Text);

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
            var text1 = "café";
            var text2 = "é";
            var text3 = "grande é";
            var text4 = "cafe"; // No accented e
            var inputs = new (string, int)[]
            {
                (text1, 0),
                (text2, 0),
                (text3, 0),
                (text4, -1)
            };
            TestExpression(pattern, inputs, transformedDfa, encoding);
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



        static bool CheckAnchorConditions(Dfa state, bool atLineStart, bool atLineEnd)
        {
            if (!state.Attributes.TryGetValue("AnchorMask", out var anchorObj) || !(anchorObj is int anchorMask))
                return true; // No anchors = always valid

            const int START_ANCHOR = 1;  // ^
            const int END_ANCHOR = 2;    // $

           // Console.WriteLine($"ANCHOR DEBUG: mask={anchorMask}, atLineStart={atLineStart}, atLineEnd={atLineEnd}");

            // Check start anchor condition
            if ((anchorMask & START_ANCHOR) != 0 && !atLineStart)
            {
               // Console.WriteLine($"ANCHOR DEBUG: START_ANCHOR failed");
                return false;
            }

            // Check end anchor condition  
            if ((anchorMask & END_ANCHOR) != 0 && !atLineEnd)
            {
              // Console.WriteLine($"ANCHOR DEBUG: END_ANCHOR failed");
                return false;
            }

           // Console.WriteLine($"ANCHOR DEBUG: All conditions passed");
            return true;
        }
        static int TestUtf32Dfa(Dfa startState, string input)
        {
            var currentState = startState;
            int position = 0;
            bool atLineStart = true;
            var codepoints = RegexExpression.ToUtf32(input).ToArray();
            bool atLineEnd = codepoints.Length == 0 || (codepoints.Length == 1 && codepoints[0] == '\n');
            var closure = startState.FillClosure();

            while (position <= codepoints.Length)
            {
                bool found = false;
                var currentStateIndex = closure.IndexOf(currentState);

                // SPECIAL CASE: Check for $ anchor before newline
                if (currentState.IsAccept &&
                    currentState.Attributes.ContainsKey("AnchorMask") &&
                    position < codepoints.Length &&
                    codepoints[position] == '\n')
                {
                    var anchorMask = (int)currentState.Attributes["AnchorMask"];
                    const int END_ANCHOR = 2;  // $

                    if ((anchorMask & END_ANCHOR) != 0)
                    {
                        Console.WriteLine($"ACCEPTED: {currentState.AcceptSymbol}");
                        return currentState.AcceptSymbol;
                    }
                }

                // Try to consume the next character if available
                if (position < codepoints.Length)
                {
                    foreach (var transition in currentState.Transitions)
                    {
                        var dstStateIndex = closure.IndexOf(transition.To);
                        var range = new DfaRange(transition.Min,transition.Max);
                        //Console.WriteLine($"DEBUG: has q{currentStateIndex}->q{dstStateIndex} on {range}");
                        //currentStateIndex
                        int codepoint = codepoints[position];
                        if (codepoint >= transition.Min && codepoint <= transition.Max)
                        {
                            var targetIndex = closure.IndexOf(transition.To);
                
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
                else
                {
                    // At end of input - no more characters to consume
                    found = false;
                }

                // If we couldn't find a transition (either no more input or no matching transition)
                if (!found)
                {
                    // Check if current state is accepting
                    if (currentState.IsAccept)
                    {

                        // Check anchor conditions
                        if (currentState.Attributes.ContainsKey("AnchorMask"))
                        {
                            var anchorMask = (int)currentState.Attributes["AnchorMask"];

                            if (CheckAnchorConditions(currentState, atLineStart, atLineEnd))
                            {
                                if (position == codepoints.Length)
                                {
                                    Console.WriteLine($"ACCEPTED: {currentState.AcceptSymbol}");
                                    return currentState.AcceptSymbol;
                                }
                                else
                                {
                                    Console.WriteLine("REJECTED: state is accepting but with input remaining");
                                    return -1;
                                }
                            }
                            else
                            {
                                Console.WriteLine($"REJECTED: Anchor condition not met");
                                return -1;
                            }
                        }
                        else
                        {
                            if (position == codepoints.Length) { 
                                Console.WriteLine($"ACCEPTED: {currentState.AcceptSymbol}");
                                return currentState.AcceptSymbol;
                            }
                            else
                            {
                                Console.WriteLine("REJECTED: state is accepting but with input remaining");
                                return -1;
                            }
                        }
                    }
                    else
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
                        {
                            Console.WriteLine($"REJECTED: Not in accept state at end of input. Final state: q{closure.IndexOf(currentState)} (IsAccept: {currentState.IsAccept})");
                        }
                        return -1;
                    }
                }

                // Continue the loop if we found a transition
            }

            // Should never reach here
            Console.WriteLine($"REJECTED: Unexpected end of loop");
            return -1;
        }
        static int TestUtf16Dfa(Dfa startState, string input)
        {
            
            var currentState = startState;
            int position = 0;
            bool atLineStart = true;
            bool atLineEnd = input.Length == 0 || (input.Length == 1 && input[0] == '\n');
            Console.WriteLine($"\n=== Testing '{input}' ===");

            while (position <= input.Length)
            {
                bool found = false;

                // SPECIAL CASE: Check for $ anchor before newline
                if (currentState.IsAccept &&
                    currentState.Attributes.ContainsKey("AnchorMask") &&
                    position < input.Length &&
                    input[position] == '\n')
                {
                    var anchorMask = (int)currentState.Attributes["AnchorMask"];
                    const int END_ANCHOR = 2;  // $

                    if ((anchorMask & END_ANCHOR) != 0)
                    {
                        Console.WriteLine($"ACCEPTED: {currentState.AcceptSymbol}");
                        return currentState.AcceptSymbol;
                    }
                }

                // Try to consume the next character if available
                if (position < input.Length)
                {
                    foreach (var transition in currentState.Transitions)
                    {
                        char c = input[position];
                        
                        if (c >= transition.Min && c <= transition.Max)
                        {
                            
                            currentState = transition.To;
                            position++;
                            atLineEnd = (position == input.Length) ||
                                      (position < input.Length && input[position] == '\n');
                            atLineStart = (c == '\n');
                            found = true;
                            break;
                        }
                    }
                }
                else
                {
                    // At end of input - no more characters to consume
                    found = false;
                }

                // If we couldn't find a transition (either no more input or no matching transition)
                if (!found)
                {
                    // Check if current state is accepting
                    if (currentState.IsAccept)
                    {
                        
                        // Check anchor conditions
                        if (currentState.Attributes.ContainsKey("AnchorMask"))
                        {
                            var anchorMask = (int)currentState.Attributes["AnchorMask"];


                            if (CheckAnchorConditions(currentState, atLineStart, atLineEnd))
                            {
                                if (position == input.Length)
                                {
                                    Console.WriteLine($"ACCEPTED: {currentState.AcceptSymbol}");
                                    return currentState.AcceptSymbol;
                                }
                                else
                                {
                                    Console.WriteLine("REJECTED: state is accepting but with input remaining");
                                    return -1;
                                }
                            }
                            else
                            {
                                Console.WriteLine($"REJECTED: Anchor condition not met");
                                return -1;
                            }
                        }
                        else
                        {
                            if (position == input.Length)
                            {
                                Console.WriteLine($"ACCEPTED: {currentState.AcceptSymbol}");
                                return currentState.AcceptSymbol;
                            }
                            else
                            {
                                Console.WriteLine("REJECTED: state is accepting but with input remaining");
                                return -1;
                            }
                        }
                    }
                    else
                    {
                        if (position < input.Length)
                        {
                            Console.WriteLine($"REJECTED: No transition for '{input[position]}' at position {position}");
                        }
                        else
                        {
                            Console.WriteLine($"REJECTED: Not in accept state at end of input");
                        }
                        return -1;
                    }
                }

                // Continue the loop if we found a transition
            }

            // Should never reach here
            Console.WriteLine($"REJECTED: Unexpected end of loop");
            return -1;
        }
        static int TestUtf8Dfa(Dfa startState, string input)
        {
            
            var bytes = Encoding.UTF8.GetBytes(input);
            var currentState = startState;
            int position = 0;
            bool atLineStart = true;
            bool atLineEnd = bytes.Length == 0 || (bytes.Length == 1 && bytes[0] == '\n');
            Console.WriteLine($"\n=== Testing '{input}' ===");

            while (position <= bytes.Length)
            {
                bool found = false;

                // SPECIAL CASE: Check for $ anchor before newline
                if (currentState.IsAccept &&
                    currentState.Attributes.ContainsKey("AnchorMask") &&
                    position < bytes.Length &&
                    bytes[position] == '\n')
                {
                    var anchorMask = (int)currentState.Attributes["AnchorMask"];
                    const int END_ANCHOR = 2;  // $

                    if ((anchorMask & END_ANCHOR) != 0)
                    {
                        Console.WriteLine($"ACCEPTED: {currentState.AcceptSymbol}");
                        return currentState.AcceptSymbol;
                    }
                }

                // Try to consume the next byte if available
                if (position < bytes.Length)
                {
                    foreach (var transition in currentState.Transitions)
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
                else
                {
                    // At end of input - no more bytes to consume
                    found = false;
                }

                // If we couldn't find a transition (either no more input or no matching transition)
                if (!found)
                {
                    // Check if current state is accepting
                    if (currentState.IsAccept)
                    {
                        // Check anchor conditions
                        if (currentState.Attributes.ContainsKey("AnchorMask"))
                        {
                            var anchorMask = (int)currentState.Attributes["AnchorMask"];
                        
                            if (CheckAnchorConditions(currentState, atLineStart, atLineEnd))
                            {
                                if (position == bytes.Length)
                                {
                                    Console.WriteLine($"ACCEPTED: {currentState.AcceptSymbol}");
                                    return currentState.AcceptSymbol;
                                }
                                else
                                {
                                    Console.WriteLine("REJECTED: state is accepting but with input remaining");
                                    return -1;
                                }
                                
                            }
                            else
                            {
                                Console.WriteLine($"REJECTED: Anchor condition not met");
                                return -1;
                            }
                        }
                        else
                        {
                            if (position == bytes.Length)
                            {
                                Console.WriteLine($"ACCEPTED: {currentState.AcceptSymbol}");
                                return currentState.AcceptSymbol;
                            }
                            else
                            {
                                Console.WriteLine("REJECTED: state is accepting but with input remaining");
                                return -1;
                            }
                            
                        }
                    }
                    else
                    {
                        if (position < bytes.Length)
                        {
                            Console.WriteLine($"REJECTED: No transition for byte 0x{bytes[position]:X2} at position {position}");
                        }
                        else
                        {
                            Console.WriteLine($"REJECTED: Not in accept state at end of input");
                        }
                        return -1;
                    }
                }

                // Continue the loop if we found a transition
            }

            // Should never reach here
            Console.WriteLine($"REJECTED: Unexpected end of loop");
            return -1;
        }
        static int TestUtf8DfaArray(int[] dfa, string input)
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
                int machineIndex = currentStateIndex;
                int acceptId = dfa[machineIndex++];
                int anchorMask = dfa[machineIndex++];  // Read anchor mask
                int transitionCount = dfa[machineIndex++];

                // SPECIAL CASE: Check for $ anchor before newline
                if (acceptId != -1 &&
                    position < bytes.Length &&
                    bytes[position] == '\n')
                {
                    const int END_ANCHOR = 2;  // $

                    if ((anchorMask & END_ANCHOR) != 0)
                    {
                        Console.WriteLine($"ACCEPTED: {acceptId}");
                        return acceptId;
                    }
                }

                // Try to consume the next byte if available
                if (position < bytes.Length)
                {
                    // Check each transition (only character transitions)
                    for (int t = 0; t < transitionCount; t++)
                    {
                        int destStateIndex = dfa[machineIndex++];
                        int rangeCount = dfa[machineIndex++];

                        // Check if any range in this transition matches
                        bool transitionMatches = false;
                        for (int r = 0; r < rangeCount; r++)
                        {
                            int min, max;
                            if (isRangeArray)
                            {
                                min = dfa[machineIndex++];
                                max = dfa[machineIndex++];
                            }
                            else
                            {
                                min = max = dfa[machineIndex++];
                            }

                            // Skip any anchor transitions (shouldn't exist after refactor)
                            if (min < 0) continue;

                            // Check character transitions only
                            byte c = bytes[position];
                            
                            if (c >= min && c <= max)
                            {
                                
                                currentStateIndex = destStateIndex;
                                position++;
                                atLineEnd = (position == bytes.Length) ||
                                          (position < bytes.Length && bytes[position] == '\n');
                                atLineStart = (c == '\n');
                                found = true;
                                transitionMatches = true;
                                break;
                            }
                        }
                        if (transitionMatches) break; // Exit transition loop
                    }
                }
                else
                {
                    // At end of input - skip through all transitions to get past them in the array
                    for (int t = 0; t < transitionCount; t++)
                    {
                        int destStateIndex = dfa[machineIndex++];
                        int rangeCount = dfa[machineIndex++];

                        for (int r = 0; r < rangeCount; r++)
                        {
                            if (isRangeArray)
                            {
                                machineIndex += 2; // Skip min, max
                            }
                            else
                            {
                                machineIndex++; // Skip single value
                            }
                        }
                    }
                    found = false;
                }

                // If we couldn't find a transition (either no more input or no matching transition)
                if (!found)
                {
                    // Check if current state is accepting
                    int currentAcceptId = dfa[currentStateIndex];
                    int currentAnchorMask = dfa[currentStateIndex + 1];

                    if (currentAcceptId != -1)
                    {
                        
                        // Validate anchor conditions if present
                        if (CheckAnchorConditions(currentAnchorMask, atLineStart, atLineEnd))
                        {
                            Console.WriteLine($"ACCEPTED: {currentAcceptId}");
                            return currentAcceptId;
                        }
                        else
                        {
                            Console.WriteLine($"REJECTED: Anchor condition not met");
                            return -1;
                        }
                    }
                    else
                    {
                        if (position < bytes.Length)
                        {
                            Console.WriteLine($"REJECTED: No transition for byte 0x{bytes[position]:X2} at position {position}");
                        }
                        else
                        {
                            Console.WriteLine($"REJECTED: Not in accept state at end of input");
                        }
                        return -1;
                    }
                }

                // Continue the loop if we found a transition
            }

            // Should never reach here
            Console.WriteLine($"REJECTED: Unexpected end of loop");
            return -1;
        }
        static int TestDfa(Dfa startState, string input, Encoding enc)
        {
            if (enc == Encoding.UTF8)
            {
                return TestUtf8Dfa(startState, input);
            }
            else if (enc == Encoding.Unicode)
            {
                return TestUtf16Dfa(startState, input);
            }
            else if (enc == Encoding.UTF32)
            {
                return TestUtf32Dfa(startState, input);
            } else if(enc.IsSingleByte==false)
            {
                throw new NotSupportedException("This encoding cannot be used with Luthor");
            }
            var bytes = enc.GetBytes(input);
            var currentState = startState;
            int position = 0;
            bool atLineStart = true;
            bool atLineEnd = bytes.Length == 0 || (bytes.Length == 1 && bytes[0] == '\n');
            Console.WriteLine($"\n=== Testing '{input}' ===");

            while (position <= bytes.Length)
            {
                bool found = false;

                // SPECIAL CASE: Check for $ anchor before newline
                if (currentState.IsAccept &&
                    currentState.Attributes.ContainsKey("AnchorMask") &&
                    position < bytes.Length &&
                    bytes[position] == '\n')
                {
                    var anchorMask = (int)currentState.Attributes["AnchorMask"];
                    const int END_ANCHOR = 2;  // $

                    if ((anchorMask & END_ANCHOR) != 0)
                    {
                        Console.WriteLine($"ACCEPTED: {currentState.AcceptSymbol}");
                        return currentState.AcceptSymbol;
                    }
                }

                // Try to consume the next byte if available
                if (position < bytes.Length)
                {
                    foreach (var transition in currentState.Transitions)
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
                else
                {
                    // At end of input - no more bytes to consume
                    found = false;
                }

                // If we couldn't find a transition (either no more input or no matching transition)
                if (!found)
                {
                    // Check if current state is accepting
                    if (currentState.IsAccept)
                    {
                        // Check anchor conditions
                        if (currentState.Attributes.ContainsKey("AnchorMask"))
                        {
                            var anchorMask = (int)currentState.Attributes["AnchorMask"];

                            if (CheckAnchorConditions(currentState, atLineStart, atLineEnd))
                            {
                                if (position == bytes.Length)
                                {
                                    Console.WriteLine($"ACCEPTED: {currentState.AcceptSymbol}");
                                    return currentState.AcceptSymbol;
                                }
                                else
                                {
                                    Console.WriteLine("REJECTED: state is accepting but with input remaining");
                                    return -1;
                                }

                            }
                            else
                            {
                                Console.WriteLine($"REJECTED: Anchor condition not met");
                                return -1;
                            }
                        }
                        else
                        {
                            if (position == bytes.Length)
                            {
                                Console.WriteLine($"ACCEPTED: {currentState.AcceptSymbol}");
                                return currentState.AcceptSymbol;
                            }
                            else
                            {
                                Console.WriteLine("REJECTED: state is accepting but with input remaining");
                                return -1;
                            }

                        }
                    }
                    else
                    {
                        if (position < bytes.Length)
                        {
                            Console.WriteLine($"REJECTED: No transition for byte 0x{bytes[position]:X2} at position {position}");
                        }
                        else
                        {
                            Console.WriteLine($"REJECTED: Not in accept state at end of input");
                        }
                        return -1;
                    }
                }

                // Continue the loop if we found a transition
            }

            // Should never reach here
            Console.WriteLine($"REJECTED: Unexpected end of loop");
            return -1;
        }
        static bool CheckAnchorConditions(int anchorMask, bool atLineStart, bool atLineEnd)
        {
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
