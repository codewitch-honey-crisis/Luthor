// An implementation of Dr. Robert van Engelen's lazy DFA matching state construction algorithm used in his RE/FLEX project
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Luthor
{
    // Dr. Robert van Engelen's lazy DFA construction based on his email correspondence
    static class DfaBuilder
    {

        // Helper methods using static RegexExpression methods
        private static bool IsPositionLazy(RegexExpression position, RegexExpression root, Dictionary<RegexExpression, bool> lazyPositions)
        {
            // Use existing lazyPositions dictionary first
            if (lazyPositions.ContainsKey(position))
            {
                return lazyPositions[position];
            }

            // Fallback to AST query if needed
            var containingRepeat = RegexExpression.TryGetAncestorRepeat(root, position);
            return containingRepeat?.IsLazy == true;
        }

        private static RegexRepeatExpression GetLazyParent(RegexExpression position, RegexExpression root)
        {
            var containingRepeat = RegexExpression.TryGetAncestorRepeat(root, position);
            return containingRepeat?.IsLazy == true ? containingRepeat : null;
        }

        // REPLACE: PropagatelazyContagion method
        private static HashSet<RegexExpression> PropagatelazyContagion(
            HashSet<RegexExpression> sourcePositions,
            HashSet<RegexExpression> targetPositions,
            HashSet<RegexExpression> currentLazyPositions,
            Dictionary<RegexExpression, bool> lazyPositions,
            Dictionary<RegexExpression, HashSet<RegexExpression>> followPos,
            RegexExpression root) // Add root parameter for AST queries
        {
            var newLazyPositions = new HashSet<RegexExpression>();

            // Start with positions that are inherently lazy
            foreach (var pos in targetPositions)
            {
                if (IsPositionLazy(pos, root, lazyPositions))
                {
                    newLazyPositions.Add(pos);
                }
            }

            // Van Engelen: "propagating laziness along a path" - simplified with AST queries
            foreach (var sourcePos in sourcePositions)
            {
                bool sourcePosIsLazy = (lazyPositions.ContainsKey(sourcePos) && lazyPositions[sourcePos]) ||
                                       currentLazyPositions.Contains(sourcePos);

                if (sourcePosIsLazy && followPos.ContainsKey(sourcePos))
                {
                    var sourceLazyParent = GetLazyParent(sourcePos, root);

                    foreach (var targetPos in followPos[sourcePos])
                    {
                        if (targetPositions.Contains(targetPos))
                        {
                            var targetLazyParent = GetLazyParent(targetPos, root);

                            // Propagate within same lazy scope or to inherently lazy positions
                            if (sourceLazyParent != null && sourceLazyParent == targetLazyParent)
                            {
                                newLazyPositions.Add(targetPos);
                            }
                            else if (IsPositionLazy(targetPos, root, lazyPositions))
                            {
                                newLazyPositions.Add(targetPos);
                            }
                        }
                    }
                }
            }

            return newLazyPositions;
        }
        static void Debug(string message)
        {
            Console.WriteLine(message);
        }
        // REPLACE: CreateLazyAwareTransitions method  
        // KEY FIX: Create ALL valid transitions, don't filter them during construction
        private static void CreateLazyAwareTransitions(
            RegexExpression root,
            Dictionary<RegexExpression, HashSet<RegexExpression>> firstPosMap,
            Dictionary<RegexExpression, HashSet<RegexExpression>> lastPosMap,
            Dfa currentState,
            DfaRange range,
            HashSet<RegexExpression> triggeringPositions,
            HashSet<RegexExpression> currentLazyPositions,
            Dictionary<RegexExpression, bool> lazyPositions,
            Dictionary<RegexExpression, HashSet<RegexExpression>> followPos,
            Dictionary<RegexTerminatorExpression, int> endMarkerToAcceptSymbol,
            Dictionary<RegexExpression, int> positionToAcceptSymbol,
            Dictionary<DfaAttributes, Dfa> allStates,
            Dictionary<RegexExpression, RegexRepeatExpression> positionToLazyParent,
            Queue<Dfa> unmarkedStates)
        {
            // Separate lazy vs non-lazy triggering positions using existing data
            var lazyTriggers = triggeringPositions.Where(p => currentLazyPositions.Contains(p) ||
                                                              (lazyPositions.ContainsKey(p) && lazyPositions[p])).ToHashSet();
            var nonLazyTriggers = triggeringPositions.Where(p => !lazyTriggers.Contains(p)).ToHashSet();

            var candidateTransitions = new List<(HashSet<RegexExpression> triggers, bool isLazy)>();

            // Create candidate transitions for each group
            if (lazyTriggers.Any())
            {
                candidateTransitions.Add((lazyTriggers, true));
            }

            if (nonLazyTriggers.Any())
            {
                candidateTransitions.Add((nonLazyTriggers, false));
            }

            //if (candidateTransitions.Count > 1)
            //{
            //    var hasLiteralPosition = nonLazyTriggers.Any(p => IsLiteralPosition(p, root));
            //    var hasLazyQuantifierPosition = lazyTriggers.Any(p => IsInsideLazyQuantifier(p, root));

            //    if (hasLiteralPosition && hasLazyQuantifierPosition)
            //    {
            //        // Van Engelen: Literal characters take precedence over lazy quantifiers
            //        // This implements "lazy quantifiers prefer minimal matching"
            //        candidateTransitions = candidateTransitions.Where(c => !c.isLazy).ToList();
            //    }
            //}
            // VAN ENGELEN KEY INSIGHT: Create ALL valid transitions during construction
            // Don't filter them here - lazy preference is handled by edge trimming at accepting states
            // This fixes the /*** issue where you need both lazy and non-lazy paths

            foreach (var (triggers, isLazy) in candidateTransitions)
            {

                // Calculate next positions for this specific set of triggers
                var specificNextPositions = new HashSet<RegexExpression>();

                foreach (var pos in triggers)
                {
                    if (followPos.ContainsKey(pos))
                    {
                        specificNextPositions.UnionWith(followPos[pos]);
                    }

                }

                foreach (var pos in triggers)
                {
                    if (followPos.ContainsKey(pos))
                    {
                        specificNextPositions.UnionWith(followPos[pos]);
                    }
                }

                if (isLazy)
                {
                    // For lazy transitions, ensure ALL lazy quantifier siblings are included
                    var allRelatedLazyPositions = new HashSet<RegexExpression>();
                    foreach (var trigger in triggers)
                    {
                        var lazyAncestor = RegexExpression.GetAncestorLazyRepeat(root, trigger);
                        if (lazyAncestor != null)
                        {
                            // Add all positions from the same lazy quantifier
                            var ancestorPositions = lazyAncestor.Expression?.GetFirstPos(firstPosMap) ?? new HashSet<RegexExpression>();
                            allRelatedLazyPositions.UnionWith(ancestorPositions);
                        }
                    }

                    // Include the related lazy positions in the target state calculation
                    specificNextPositions.UnionWith(allRelatedLazyPositions);
                }

                if (specificNextPositions.Count == 0) continue;

                // Handle anchors
                var anchorsToProcess = specificNextPositions.Where(p => p is RegexAnchorExpression).ToList();
                foreach (var anchor in anchorsToProcess)
                {
                    if (followPos.ContainsKey(anchor))
                    {
                        specificNextPositions.UnionWith(followPos[anchor]);
                    }
                }

                // Propagate lazy contagion for this specific transition
                var nextLazyPositions = PropagatelazyContagion(triggers, specificNextPositions, currentLazyPositions, lazyPositions, followPos, root);

                // Create target state with semantic attributes
                var candidateState = CreateStateFromPositions(specificNextPositions, nextLazyPositions, endMarkerToAcceptSymbol, positionToAcceptSymbol);

                // Add semantic attributes to distinguish lazy behavior
                candidateState.Attributes["SemanticRole"] = isLazy ? "quantifier_body" : "quantifier_exit";


                // In CreateLazyAwareTransitions, before the existingState check:

                Dfa nextState;
                var existingState = allStates.Values.FirstOrDefault(s => s.Attributes.Equals(candidateState.Attributes));

                if (existingState == null)
                {
                    nextState = candidateState;
                    allStates[candidateState.Attributes] = nextState;
                    unmarkedStates.Enqueue(nextState);
                }
                else
                {
                    nextState = existingState;
                }




                var candidatePositions = (List<RegexExpression>)candidateState.Attributes["Positions"];
                var candidateLazyPositions = (List<RegexExpression>)candidateState.Attributes["LazyPositions"];

                // Create transition with attributes
                var dfaTransition = new DfaTransition(nextState, range.Min, range.Max);
                dfaTransition.Attributes = new DfaAttributes();
                dfaTransition.Attributes["IsLazy"] = isLazy;
                dfaTransition.Attributes["TriggeringPositions"] = triggers.ToList();

                currentState.AddTransition(dfaTransition);

            }

        }


        private static bool IsLiteralPosition(RegexExpression position, RegexExpression root)
        {
            // A literal position is one that's NOT inside any lazy quantifier
            return RegexExpression.GetAncestorLazyRepeat(root, position) == null;
        }

        private static bool IsInsideLazyQuantifier(RegexExpression position, RegexExpression root)
        {
            // Check if this position is inside a lazy quantifier
            return RegexExpression.GetAncestorLazyRepeat(root, position) != null;
        }



        // Helper: Analyze what a state represents semantically during construction
        private static string AnalyzeStateSemantics(
            Dfa state,
            RegexExpression root,
            Dictionary<RegexExpression, RegexRepeatExpression> positionToLazyParent,
            Dictionary<RegexExpression, HashSet<RegexExpression>> firstPosMap,
            Dictionary<RegexExpression, HashSet<RegexExpression>> lastPosMap)
        {
            var positions = GetPositionsFromState(state);
            var lazyPositions = GetLazyPositionsFromState(state);
            var rootFirstPos = root.GetFirstPos(firstPosMap);

            var analysis = new List<string>();

            // Check if this is the start state
            if (positions.IsSupersetOf(rootFirstPos))
            {
                analysis.Add("START_STATE");
            }

            // Check if this represents completion of lazy quantifiers
            foreach (var pos in positions)
            {
                if (positionToLazyParent.ContainsKey(pos))
                {
                    var lazyParent = positionToLazyParent[pos];
                    var parentLastPos = lazyParent.Expression?.GetLastPos(lastPosMap) ?? new HashSet<RegexExpression>();

                    if (parentLastPos.Contains(pos))
                    {
                        analysis.Add($"COMPLETED_{lazyParent}");
                    }
                }
            }

            // Check for end markers
            var hasEndMarker = positions.Any(p => p is RegexTerminatorExpression);
            if (hasEndMarker)
            {
                analysis.Add("HAS_END_MARKER");
            }

            // Accepting state?
            if (state.IsAccept)
            {
                analysis.Add("ACCEPTING");
            }

            return string.Join("|", analysis);
        }

        public static Dfa BuildDfa(RegexExpression regexAst)
        {
            // we have to expand the tree
            //regexAst = RegexExpression.Parse(regexAst.ToString("x"));
            regexAst = regexAst.ExpandRepeatingQuantifiers();
            int positionCounter = 1;
            RegexTerminatorExpression endMarker = null;
            int[] points;

            // Create thread-local dictionaries instead of static fields
            var positionMap = new Dictionary<RegexExpression, int>();
            var nullableMap = new Dictionary<RegexExpression, bool>();
            var firstPosMap = new Dictionary<RegexExpression, HashSet<RegexExpression>>();
            var lastPosMap = new Dictionary<RegexExpression, HashSet<RegexExpression>>();

            bool isLexer = regexAst is RegexLexerExpression;
            Dictionary<int, RegexExpression> positions = new Dictionary<int, RegexExpression>();
            Dictionary<RegexExpression, HashSet<RegexExpression>> followPos =
                new Dictionary<RegexExpression, HashSet<RegexExpression>>();

            // positions need lazy attribution
            Dictionary<RegexExpression, bool> lazyPositions = new Dictionary<RegexExpression, bool>();

            // Track which positions are inside lazy quantifiers for contagion
            Dictionary<RegexExpression, RegexRepeatExpression> positionToLazyParent =
                new Dictionary<RegexExpression, RegexRepeatExpression>();

            // Lexer support: Track which positions belong to which disjunction
            Dictionary<RegexExpression, int> positionToAcceptSymbol = new Dictionary<RegexExpression, int>();
            Dictionary<RegexTerminatorExpression, int> endMarkerToAcceptSymbol = new Dictionary<RegexTerminatorExpression, int>();

            // Track lazy context for proper attribution
            HashSet<RegexExpression> _currentLazyContext = new HashSet<RegexExpression>();
            var p = new HashSet<int>();
            regexAst.Visit((parent, expression, childIndex, level) =>
            {
                foreach (var range in expression.GetRanges())
                {
                    p.Add(0);
                    if (range.Min == -1 || range.Max == -1) continue; // shouldn't happen.
                    p.Add(range.Min);
                    if (range.Max < 0x10ffff)
                    {
                        p.Add((range.Max + 1));
                    }
                }

                return true;
            });
            points = new int[p.Count];
            p.CopyTo(points, 0);
            Array.Sort(points);

            positionCounter = 1;

            // Step 1: Identify lazy quantifiers and mark positions with context
            MarkLazyPositionsWithContext(regexAst, false, positionToLazyParent, lazyPositions);

            // Step 2: Augment with end marker(s) - different for lexer vs single regex
            var augmentedAst = isLexer
                ? AugmentWithEndMarkersForLexer(regexAst, positions, positionToAcceptSymbol, endMarkerToAcceptSymbol)
                : AugmentWithEndMarker(regexAst, out endMarker, positions);
            // For non-lexer case, we still need the endMarker reference
            if (!isLexer)
            {
                endMarkerToAcceptSymbol[endMarker!] = 0;
            }

            // Step 3: Assign positions to leaf nodes
            AssignPositions(augmentedAst, positions, ref positionCounter, positionMap);

            // Step 4: Compute nullable, firstpos, lastpos
            ComputeNodeProperties(augmentedAst, nullableMap, firstPosMap, lastPosMap, positionMap);

            // Step 5: Compute followpos with proper disjunction handling
            ComputeFollowPos(augmentedAst, positions, followPos, lastPosMap, firstPosMap, positionMap);

            // Step 6: Build DFA with lazy attribution and contagion
            var dfa = ConstructLazyDfa(augmentedAst, points, lazyPositions, endMarkerToAcceptSymbol, followPos, isLexer, positionToAcceptSymbol, firstPosMap, positionMap, positionToLazyParent, lastPosMap);


            // Step 7: Apply lazy edge trimming to accepting states
            ApplyLazyEdgeTrimming(dfa, positionToLazyParent, firstPosMap, lastPosMap);
            //DebugLazyAttribution(augmentedAst, lazyPositions, positionToLazyParent);
            return dfa;
        }

        // New method to handle lexer augmentation with multiple end markers
        private static RegexExpression AugmentWithEndMarkersForLexer(
    RegexExpression root,
    Dictionary<int, RegexExpression> positions,
    Dictionary<RegexExpression, int> positionToAcceptSymbol,
    Dictionary<RegexTerminatorExpression, int> endMarkerToAcceptSymbol)
        {
            var rootLexer = root as RegexLexerExpression;
            if (rootLexer == null)
            {
                throw new ArgumentException("Expected RegexLexerExpression for lexer mode");
            }

            // Create new lexer with augmented rules
            var augmentedRules = new List<RegexExpression>();
            int acceptSymbol = 0;

            foreach (var rule in rootLexer.Rules)
            {
                var endMarker = new RegexTerminatorExpression();
                endMarkerToAcceptSymbol[endMarker] = acceptSymbol;

                var augmentedRule = new RegexConcatExpression(rule, endMarker);

                // MOVE THIS LINE: Mark positions on the augmented rule, not the original
                MarkPositionsWithAcceptSymbol(augmentedRule, acceptSymbol, positionToAcceptSymbol);

                augmentedRules.Add(augmentedRule);
                acceptSymbol++;
            }

            return new RegexLexerExpression(augmentedRules);
        }
        // Mark all positions in a subtree with the given accept symbol
        private static void MarkPositionsWithAcceptSymbol(RegexExpression expr, int acceptSymbol, Dictionary<RegexExpression, int> positionToAcceptSymbol)
        {
            expr.Visit((parent, node, childIndex, level) =>
            {
                if (node.IsLeaf)  // This includes both character positions AND anchor positions
                {
                    positionToAcceptSymbol[node] = acceptSymbol;
                }
                return true;
            });
        }



        // Van Engelen: "mark downstream regex positions in the DFA states as lazy when a parent position is lazy"
        private static void MarkLazyPositionsWithContext(RegexExpression ast, bool inLazyContext, Dictionary<RegexExpression, RegexRepeatExpression> positionToLazyParent, Dictionary<RegexExpression, bool> lazyPositions)
        {
            if (ast == null) return;

            bool currentLazyContext = inLazyContext;

            // Check if this node introduces lazy context
            if (ast is RegexRepeatExpression repeat && repeat.IsLazy)
            {
                currentLazyContext = true;
                // Track the lazy parent for all positions inside
                repeat.Expression?.Visit((p, e, ci, l) =>
                {
                    if (e.IsLeaf)
                    {
                        positionToLazyParent[e] = repeat;
                    }
                    return true;
                });
            }

            // Mark leaf nodes if they're in lazy context
            if (ast.IsLeaf && currentLazyContext)
            {
                lazyPositions[ast] = true;
            }

            // Recursively process children with updated context
            switch (ast)
            {
                case RegexBinaryExpression binary:
                    MarkLazyPositionsWithContext(binary.Left, currentLazyContext, positionToLazyParent, lazyPositions);
                    MarkLazyPositionsWithContext(binary.Right, currentLazyContext, positionToLazyParent, lazyPositions);
                    break;

                case RegexUnaryExpression unary:
                    MarkLazyPositionsWithContext(unary.Expression, currentLazyContext, positionToLazyParent, lazyPositions);
                    break;
            }
        }

        private static RegexConcatExpression AugmentWithEndMarker(RegexExpression root, out RegexTerminatorExpression _endMarker, Dictionary<int, RegexExpression> _positions)
        {
            _endMarker = new RegexTerminatorExpression();
            return new RegexConcatExpression(root, _endMarker);
        }

        private static void AssignPositions(RegexExpression node, Dictionary<int, RegexExpression> _positions, ref int _positionCounter, Dictionary<RegexExpression, int> positionMap)
        {
            if (node == null) return;

            if (node.IsLeaf)
            {
                if (node.GetDfaPosition(positionMap) == -1)
                {
                    node.SetDfaPosition(_positionCounter++, positionMap);
                    _positions[node.GetDfaPosition(positionMap)] = node;
                }
                return;
            }

            switch (node)
            {
                case RegexLexerExpression lexer:  // ADD THIS CASE
                    foreach (var rule in lexer.Rules)
                    {
                        AssignPositions(rule, _positions, ref _positionCounter, positionMap);
                    }
                    break;
                case RegexBinaryExpression binary:
                    AssignPositions(binary.Left, _positions, ref _positionCounter, positionMap);
                    AssignPositions(binary.Right, _positions, ref _positionCounter, positionMap);
                    break;

                case RegexUnaryExpression unary:
                    AssignPositions(unary.Expression, _positions, ref _positionCounter, positionMap);
                    break;
            }
        }


        private static void ComputeNodeProperties(RegexExpression node, Dictionary<RegexExpression, bool> nullableMap, Dictionary<RegexExpression, HashSet<RegexExpression>> firstPosMap, Dictionary<RegexExpression, HashSet<RegexExpression>> lastPosMap, Dictionary<RegexExpression, int> positionMap)
        {
            if (node == null) return;

            switch (node)
            {
                case RegexLexerExpression lexer:
                    // Compute properties for all rules first
                    foreach (var rule in lexer.Rules)
                    {
                        ComputeNodeProperties(rule, nullableMap, firstPosMap, lastPosMap, positionMap);
                    }

                    // Lexer is nullable if any rule is nullable
                    node.SetNullable(lexer.Rules.Any(r => r.GetNullable(nullableMap)), nullableMap);

                    // Lexer firstpos is union of all rules' firstpos
                    foreach (var rule in lexer.Rules)
                    {
                        node.GetFirstPos(firstPosMap).UnionWith(rule.GetFirstPos(firstPosMap));
                    }

                    // Lexer lastpos is union of all rules' lastpos  
                    foreach (var rule in lexer.Rules)
                    {
                        node.GetLastPos(lastPosMap).UnionWith(rule.GetLastPos(lastPosMap));
                    }
                    break;
                case RegexConcatExpression concat:
                    ComputeNodeProperties(concat.Left, nullableMap, firstPosMap, lastPosMap, positionMap);
                    ComputeNodeProperties(concat.Right, nullableMap, firstPosMap, lastPosMap, positionMap);

                    var children = new[] { concat.Left, concat.Right }.Where(c => c != null).ToArray();

                    node.SetNullable(children.All(c => c.GetNullable(nullableMap)), nullableMap);

                    foreach (var child in children)
                    {
                        node.GetFirstPos(firstPosMap).UnionWith(child.GetFirstPos(firstPosMap));
                        if (!child.GetNullable(nullableMap))
                            break;
                    }

                    for (int i = children.Length - 1; i >= 0; i--)
                    {
                        node.GetLastPos(lastPosMap).UnionWith(children[i].GetLastPos(lastPosMap));
                        if (!children[i].GetNullable(nullableMap))
                            break;
                    }
                    break;

                case RegexOrExpression or:
                    ComputeNodeProperties(or.Left, nullableMap, firstPosMap, lastPosMap, positionMap);
                    ComputeNodeProperties(or.Right, nullableMap, firstPosMap, lastPosMap, positionMap);

                    var orChildren = new[] { or.Left, or.Right }.Where(c => c != null).ToArray();
                    node.SetNullable(orChildren.Any(c => c.GetNullable(nullableMap)), nullableMap);

                    foreach (var child in orChildren)
                    {
                        node.GetFirstPos(firstPosMap).UnionWith(child.GetFirstPos(firstPosMap));
                        node.GetLastPos(lastPosMap).UnionWith(child.GetLastPos(lastPosMap));
                    }


                    break;
                case RegexRepeatExpression repeat:
                    ComputeNodeProperties(repeat.Expression, nullableMap, firstPosMap, lastPosMap, positionMap);

                    if (repeat.Expression == null) break;

                    if (repeat.MinOccurs <= 0)
                    {
                        node.SetNullable(true, nullableMap);
                    }
                    else
                    {
                        node.SetNullable(repeat.Expression.GetNullable(nullableMap), nullableMap);
                    }

                    node.GetFirstPos(firstPosMap).UnionWith(repeat.Expression.GetFirstPos(firstPosMap));
                    node.GetLastPos(lastPosMap).UnionWith(repeat.Expression.GetLastPos(lastPosMap));
                    break;
                case RegexTerminatorExpression:
                case RegexLiteralExpression:
                    node.SetNullable(false, nullableMap);
                    node.GetFirstPos(firstPosMap).Add(node);
                    node.GetLastPos(lastPosMap).Add(node);
                    break;

                case RegexAnchorExpression:
                    node.SetNullable(true, nullableMap);   //  Anchors are nullable/transparent
                    node.GetFirstPos(firstPosMap).Add(node);
                    node.GetLastPos(lastPosMap).Add(node);
                    break;
                case RegexCharsetExpression charset:
                    if (node is RegexLiteralExpression lit && (lit.Codepoint == -1))
                    {
                        node.SetNullable(true, nullableMap);
                    }
                    else
                    {
                        node.SetNullable(false, nullableMap);
                        node.GetFirstPos(firstPosMap).Add(node);
                        node.GetLastPos(lastPosMap).Add(node);
                    }
                    break;
            }
        }

        private static void ComputeFollowPos(RegexExpression node, Dictionary<int, RegexExpression> positions, Dictionary<RegexExpression, HashSet<RegexExpression>> followPos, Dictionary<RegexExpression, HashSet<RegexExpression>> lastPosMap, Dictionary<RegexExpression, HashSet<RegexExpression>> firstPosMap, Dictionary<RegexExpression, int> positionMap)
        {
            foreach (var pos in positions.Values)
            {
                followPos[pos] = new HashSet<RegexExpression>();
            }

            ComputeFollowPosRecursive(node, followPos, lastPosMap, firstPosMap, positionMap);
        }

        // Add this debug version of ComputeFollowPosRecursive to see what's happening

        private static void ComputeFollowPosRecursive(RegexExpression node, Dictionary<RegexExpression, HashSet<RegexExpression>> _followPos, Dictionary<RegexExpression, HashSet<RegexExpression>> lastPosMap, Dictionary<RegexExpression, HashSet<RegexExpression>> firstPosMap, Dictionary<RegexExpression, int> positionMap)
        {
            if (node == null) return;


            switch (node)
            {
                case RegexLexerExpression lexerExpr:
                    foreach (var rule in lexerExpr.Rules)
                    {
                        ComputeFollowPosRecursive(rule, _followPos, lastPosMap, firstPosMap, positionMap);
                    }
                    break;

                case RegexConcatExpression concat:
                    if (concat.Left != null && concat.Right != null)
                    {
                        var leftLastPos = concat.Left.GetLastPos(lastPosMap);
                        var rightFirstPos = concat.Right.GetFirstPos(firstPosMap);

                        foreach (var pos in leftLastPos)
                        {
                            if (_followPos.ContainsKey(pos))
                            {
                                _followPos[pos].UnionWith(rightFirstPos);
                            }
                        }
                    }

                    ComputeFollowPosRecursive(concat.Left, _followPos, lastPosMap, firstPosMap, positionMap);
                    ComputeFollowPosRecursive(concat.Right, _followPos, lastPosMap, firstPosMap, positionMap);
                    break;

                case RegexOrExpression or:
                    ComputeFollowPosRecursive(or.Left, _followPos, lastPosMap, firstPosMap, positionMap);
                    ComputeFollowPosRecursive(or.Right, _followPos, lastPosMap, firstPosMap, positionMap);
                    break;

                case RegexRepeatExpression repeat:

                    if (repeat.Expression != null && !repeat.Expression.IsEmptyElement)
                    {
                        bool canRepeat = (repeat.MaxOccurs <= 0);

                        if (canRepeat)
                        {
                            var repeatLastPos = repeat.Expression.GetLastPos(lastPosMap);
                            var repeatFirstPos = repeat.Expression.GetFirstPos(firstPosMap);

                            foreach (var lastPos in repeatLastPos)
                            {
                                if (_followPos.ContainsKey(lastPos))
                                {
                                    _followPos[lastPos].UnionWith(repeatFirstPos);
                                }
                            }
                        }
                    }

                    ComputeFollowPosRecursive(repeat.Expression, _followPos, lastPosMap, firstPosMap, positionMap);
                    break;
            }


        }
        private static bool PositionMatchesRange(RegexExpression pos, DfaRange range)
        {
            foreach (var range2 in pos.GetRanges())
            {
                if (range2.Intersects(range)) return true;
            }
            return false;
        }
        // Helper method to detect backward moves during construction
        private static bool CheckIfBackwardMove(
    HashSet<RegexExpression> triggeringPositions,
    HashSet<RegexExpression> targetPositions,
    Dfa currentState,
    RegexExpression rootExpression,
    Dictionary<RegexExpression, RegexRepeatExpression> positionToLazyParent,
    Dictionary<RegexExpression, HashSet<RegexExpression>> firstPosMap,
    Dictionary<RegexExpression, HashSet<RegexExpression>> lastPosMap)
        {
            // CRITICAL FIX: Use DFA structure context instead of trying to infer runtime context

            // If this is the start state (contains root firstpos), no backward moves possible
            var currentPositions = GetPositionsFromState(currentState);
            var rootFirstPos = rootExpression.GetFirstPos(firstPosMap);

            if (currentPositions.IsSupersetOf(rootFirstPos))
            {
                return false; // Start state - all moves are forward (can't go backward if you haven't gone forward)
            }

            // For non-start states, check for true lastpos -> firstpos loop-backs
            foreach (var pos in triggeringPositions)
            {
                if (positionToLazyParent.ContainsKey(pos))
                {
                    var lazyParent = positionToLazyParent[pos];
                    var parentLastPos = lazyParent.Expression?.GetLastPos(lastPosMap) ?? new HashSet<RegexExpression>();
                    var parentFirstPos = lazyParent.Expression?.GetFirstPos(firstPosMap) ?? new HashSet<RegexExpression>();

                    // Check if this is a lastpos -> firstpos transition (backward move)
                    // AND we're not in a start-like state
                    if (parentLastPos.Contains(pos) && targetPositions.Intersect(parentFirstPos).Any())
                    {
                        // Additional check: Make sure this isn't the first iteration
                        // by verifying we have positions that indicate completion
                        bool hasCompletedIteration = currentPositions.Intersect(parentLastPos).Any();

                        if (hasCompletedIteration)
                        {
                            return true; // This is a true backward move
                        }
                    }
                }
            }
            return false;
        }

        // Updated TrimLazyEdges - much simpler!
        private static void TrimLazyEdges(
            Dfa acceptingState,
            HashSet<RegexExpression> lazyPositions,
            Dictionary<RegexExpression, RegexRepeatExpression> positionToLazyParent,
            Dictionary<RegexExpression, HashSet<RegexExpression>> firstPosMap,
            Dictionary<RegexExpression, HashSet<RegexExpression>> lastPosMap)
        {
            var transitionsToRemove = new List<DfaTransition>();

            foreach (var transition in acceptingState.Transitions)
            {
                // Read the lazy information directly from transition attributes
                bool isLazyTriggered = transition.Attributes?.ContainsKey("IsLazyTriggered") == true &&
                                      (bool)transition.Attributes["IsLazyTriggered"];
                bool isBackwardMove = transition.Attributes?.ContainsKey("IsBackwardMove") == true &&
                                     (bool)transition.Attributes["IsBackwardMove"];

                // Van Engelen: Trim lazy backward moves from accepting states
                if (isBackwardMove && isLazyTriggered)
                {
                    transitionsToRemove.Add(transition);
                }
            }

            // Remove the lazy edges
            foreach (var transition in transitionsToRemove)
            {
                acceptingState.RemoveTransition(transition);
            }
        }

        // Van Engelen: Build DFA with lazy contagion during construction
        private static Dfa ConstructLazyDfa(
    RegexExpression root,
    int[] points,
    Dictionary<RegexExpression, bool> lazyPositions,
    Dictionary<RegexTerminatorExpression, int> endMarkerToAcceptSymbol,
    Dictionary<RegexExpression, HashSet<RegexExpression>> followPos,
    bool isLexer,
    Dictionary<RegexExpression, int> positionToAcceptSymbol,
    Dictionary<RegexExpression, HashSet<RegexExpression>> firstPosMap,
    Dictionary<RegexExpression, int> positionMap,
    // ADD THESE PARAMETERS:
    Dictionary<RegexExpression, RegexRepeatExpression> positionToLazyParent,
    Dictionary<RegexExpression, HashSet<RegexExpression>> lastPosMap)
        {
            var startPositions = root.GetFirstPos(firstPosMap);
            var startLazyPositions = GetLazyPositions(startPositions, lazyPositions);

            var unmarkedStates = new Queue<Dfa>();
            var allStates = new Dictionary<DfaAttributes, Dfa>();

            var startState = CreateStateFromPositions(startPositions, startLazyPositions, endMarkerToAcceptSymbol, positionToAcceptSymbol);
            allStates[startState.Attributes] = startState;

            unmarkedStates.Enqueue(startState);

            while (unmarkedStates.Count > 0)
            {
                var currentState = unmarkedStates.Dequeue();
                var currentPositions = GetPositionsFromState(currentState);
                var currentLazyPositions = GetLazyPositionsFromState(currentState);

                // Console.WriteLine($"=== PROCESSING STATE ===");
                // Console.WriteLine($"Current positions: {string.Join(", ", currentPositions.Select(p => p.ToString()))}");
                // Console.WriteLine($"Current lazy positions: {string.Join(", ", currentLazyPositions.Select(p => p.ToString()))}");

                // Group positions by character ranges for transition construction
                var transitionMap = new Dictionary<DfaRange, HashSet<RegexExpression>>();

                foreach (var pos in currentPositions)
                {
                    // Console.WriteLine($"Processing position: {pos} (Type: {pos.GetType().Name})");

                    // Skip all end markers
                    if (pos is RegexTerminatorExpression)
                    {
                        // Console.WriteLine($"  Skipping terminator: {pos}");
                        continue;
                    }

                    // Handle anchor positions elsewhere
                    if (pos is RegexAnchorExpression anchor)
                    {
                        // Console.WriteLine($"  Skipping anchor: {pos}");
                        continue;
                    }

                    // Handle character ranges (existing code for non-anchors)
                    for (int i = 0; i < points.Length; ++i)
                    {
                        var first = points[i];
                        var last = (i < points.Length - 1) ? points[i + 1] - 1 : 0x10ffff;
                        var range = new DfaRange(first, last);

                        bool matches = PositionMatchesRange(pos, range);

                        if (matches)
                        {
                            // Console.WriteLine($"  Position {pos} MATCHES range [{first}-{last}] (char: '{(char)first}'-'{(char)Math.Min(last, 127)}')");

                            if (!transitionMap.TryGetValue(range, out var hset))
                            {
                                hset = new HashSet<RegexExpression>();
                                transitionMap.Add(range, hset);
                            }
                            hset.Add(pos);
                        }
                        else
                        {
                            // Only log misses for a few key ranges to avoid spam
                            if (first == 32 || first == 42 || first == 47) // space, *, /
                            {
                                // Console.WriteLine($"  Position {pos} does NOT match range [{first}-{last}] (char: '{(char)first}')");
                            }
                        }
                    }
                }

                // Console.WriteLine($"TransitionMap created with {transitionMap.Count} ranges:");
                foreach (var kvp in transitionMap)
                {
                    // Console.WriteLine($"  Range [{kvp.Key.Min}-{kvp.Key.Max}] ('{(char)Math.Min(kvp.Key.Min, 127)}'): {kvp.Value.Count} positions");
                }

                // Create transitions for each character range
                foreach (var transition in transitionMap)
                {
                    var range = transition.Key;
                    var positions = transition.Value;

                    // Console.WriteLine($"Creating transitions for range [{range.Min}-{range.Max}] with {positions.Count} positions");

                    // Use lazy-aware transition creation
                    CreateLazyAwareTransitions(
                        root,
                        firstPosMap,
                        lastPosMap,
                        currentState,
                        range,
                        positions,
                        currentLazyPositions,
                        lazyPositions,
                        followPos,
                        endMarkerToAcceptSymbol,
                        positionToAcceptSymbol,
                        allStates,
                        positionToLazyParent,
                        unmarkedStates);
                }

                // Console.WriteLine($"=== END STATE PROCESSING ===\n");
            }
            var closure = startState.FillClosure();
            // Remove dead transitions
            foreach (var ffa in closure)
            {
                var itrns = new List<DfaTransition>(ffa.Transitions);
                foreach (var trns in itrns)
                {
                    var found = false;
                    foreach (var tto in trns.To.FillClosure())
                    {
                        if (tto.IsAccept)
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        //ffa.RemoveTransition(trns);
                    }
                }
            }


            return startState;
        }

        // Van Engelen: Get positions marked as lazy
        private static HashSet<RegexExpression> GetLazyPositions(HashSet<RegexExpression> positions, Dictionary<RegexExpression, bool> lazyPositions)
        {
            var result = new HashSet<RegexExpression>();
            foreach (var pos in positions)
            {
                if (lazyPositions.ContainsKey(pos) && lazyPositions[pos])
                {
                    result.Add(pos);
                }
            }
            return result;
        }

        // CRITICAL FIX: Van Engelen: "Laziness is contagious" - improved propagation
        // FIXED: Van Engelen's "Laziness is contagious" - but only within proper scope
        private static HashSet<RegexExpression> PropagatelazyContagion(
            HashSet<RegexExpression> sourcePositions,
            HashSet<RegexExpression> targetPositions,
            HashSet<RegexExpression> currentLazyPositions,
            Dictionary<RegexExpression, bool> lazyPositions,
            Dictionary<RegexExpression, HashSet<RegexExpression>> followPos)
        {
            var newLazyPositions = new HashSet<RegexExpression>();

            // Start with positions that are inherently lazy
            newLazyPositions.UnionWith(GetLazyPositions(targetPositions, lazyPositions));

            // Van Engelen: "propagating laziness along a path" - but SCOPE-AWARE
            foreach (var sourcePos in sourcePositions)
            {
                // If source position is lazy (either inherently or through contagion)
                bool sourcePosIsLazy = (lazyPositions.ContainsKey(sourcePos) && lazyPositions[sourcePos]) ||
                                       currentLazyPositions.Contains(sourcePos);

                if (sourcePosIsLazy && followPos.ContainsKey(sourcePos))
                {
                    foreach (var targetPos in followPos[sourcePos])
                    {
                        if (targetPositions.Contains(targetPos))
                        {
                            // CRITICAL FIX: Only propagate laziness if the target position
                            // should actually inherit it based on context
                            if (ShouldInheritLaziness(sourcePos, targetPos, lazyPositions))
                            {
                                newLazyPositions.Add(targetPos);
                            }
                        }
                    }
                }
            }

            return newLazyPositions;
        }

        // Helper: Determine if a target position should inherit laziness from a source position
        private static bool ShouldInheritLaziness(
            RegexExpression sourcePos,
            RegexExpression targetPos,
            Dictionary<RegexExpression, bool> lazyPositions)
        {
            // Rule 1: If target is already inherently lazy, it should remain lazy
            if (lazyPositions.ContainsKey(targetPos) && lazyPositions[targetPos])
            {
                return true;
            }

            // Rule 2: If target is inherently non-lazy (like literal positions outside lazy quantifiers),
            // it should NOT inherit laziness from other positions
            if (lazyPositions.ContainsKey(targetPos) && !lazyPositions[targetPos])
            {
                return false; // Don't propagate to explicitly non-lazy positions
            }

            // Rule 3: For positions not in the lazy attribution map (like end markers),
            // be conservative and don't propagate laziness
            if (!lazyPositions.ContainsKey(targetPos))
            {
                return false; // Don't propagate to unknown positions
            }

            // Rule 4: If we get here, use the inherent lazy value
            return lazyPositions.ContainsKey(targetPos) && lazyPositions[targetPos];
        }



        // Van Engelen: "lazy edge trimming" - cut lazy edges from accepting states
        private static void ApplyLazyEdgeTrimming(Dfa startState, Dictionary<RegexExpression, RegexRepeatExpression> positionToLazyParent, Dictionary<RegexExpression, HashSet<RegexExpression>> firstPosMap, Dictionary<RegexExpression, HashSet<RegexExpression>> lastPosMap)
        {
            var allStates = startState.FillClosure();

            foreach (var state in allStates)
            {
                if (state.IsAccept)
                {
                    // This is an accepting state - apply lazy edge trimming
                    var lazyPositions = GetLazyPositionsFromState(state);

                    var lazyCount = lazyPositions.Count;


                    if (lazyCount > 0)
                    {
                        // DO NOT remove transitions from lazy accepting states
                        // The lazy behavior should be handled during matching, not by removing transitions

                        // Instead, mark the state as having lazy behavior for the matcher to handle
                        if (!state.Attributes.ContainsKey("HasLazyBehavior"))
                        {
                            state.Attributes["HasLazyBehavior"] = true;
                        }
                    }
                    if (lazyPositions.Count > 0)
                    {
                        TrimLazyEdges(state, lazyPositions, positionToLazyParent, firstPosMap, lastPosMap);
                    }
                }
            }
        }
        // Debug helper: Print lazy attribution for a pattern
        private static void DebugLazyAttribution(RegexExpression ast, Dictionary<RegexExpression, bool> lazyPositions, Dictionary<RegexExpression, RegexRepeatExpression> positionToLazyParent)
        {
            Debug("=== LAZY ATTRIBUTION DEBUG ===");

            ast.Visit((parent, node, childIndex, level) =>
            {
                string indent = new string(' ', level * 2);

                if (node.IsLeaf)
                {
                    bool isLazy = lazyPositions.ContainsKey(node) && lazyPositions[node];
                    bool hasLazyParent = positionToLazyParent.ContainsKey(node);
                    string lazyParentInfo = hasLazyParent ? $" (parent: {positionToLazyParent[node]})" : "";

                    Debug($"{indent}LEAF: {node} - Lazy: {isLazy}{lazyParentInfo}");
                }
                else
                {
                    string typeInfo = "";
                    if (node is RegexRepeatExpression repeat)
                    {
                        typeInfo = $" - {repeat.MinOccurs},{repeat.MaxOccurs} Lazy:{repeat.IsLazy}";
                    }
                    Debug($"{indent}NODE: {node.GetType().Name}{typeInfo}");
                }

                return true;
            });

            Debug("=== END LAZY ATTRIBUTION DEBUG ===\n");
        }

        // Debug helper: Print state information during DFA construction
        private static void DebugState(Dfa state, string context)
        {
            Debug($"=== STATE DEBUG: {context} ===");

            var positions = GetPositionsFromState(state);
            var lazyPositions = GetLazyPositionsFromState(state);

            Debug($"Accepting: {state.IsAccept}");
            Debug($"Total positions: {positions.Count}");
            Debug($"Lazy positions: {lazyPositions.Count}");

            Debug("Positions:");
            foreach (var pos in positions)
            {
                bool isLazy = lazyPositions.Contains(pos);
                Debug($"  {pos} (lazy: {isLazy})");
            }

            Debug($"Transitions: {state.Transitions.Count()}");
            foreach (var transition in state.Transitions)
            {
                Debug($"  {transition}");
            }

            Debug("=== END STATE DEBUG ===\n");
        }


        // Van Engelen: Cut "lazy edges" by analyzing forward/backward moves
        //private static void TrimLazyEdges(Dfa acceptingState, HashSet<RegexExpression> lazyPositions, Dictionary<RegexExpression, RegexRepeatExpression> positionToLazyParent, Dictionary<RegexExpression, HashSet<RegexExpression>> firstPosMap, Dictionary<RegexExpression, HashSet<RegexExpression>> lastPosMap)
        //{
        //    var transitionsToRemove = new List<DfaTransition>();

        //    foreach (var transition in acceptingState.Transitions)
        //    {
        //        // Check if this transition represents a "lazy edge" that should be trimmed
        //        // Van Engelen: "we know when DFA edges point forward or backward in the regex string"
        //        bool isLazyEdge = IsLazyEdgeToTrim(acceptingState, transition.To, lazyPositions, positionToLazyParent, firstPosMap,lastPosMap);

        //        if (isLazyEdge)
        //        {
        //            transitionsToRemove.Add(transition);
        //        }
        //    }

        //    // Remove the lazy edges
        //    foreach (var transition in transitionsToRemove)
        //    {
        //        acceptingState.RemoveTransition(transition);
        //    }
        //}

        // Determine if an edge should be trimmed based on lazy attribution
        // Van Engelen's lazy edge trimming: Only trim from states that represent COMPLETED patterns
        // Key insight: Don't trim from entry points, only from completion points

        private static bool IsLazyEdgeToTrim(Dfa fromState, Dfa toState, HashSet<RegexExpression> lazyPositions, Dictionary<RegexExpression, RegexRepeatExpression> positionToLazyParent, Dictionary<RegexExpression, HashSet<RegexExpression>> firstPosMap, Dictionary<RegexExpression, HashSet<RegexExpression>> lastPosMap)
        {
            // Only trim from accepting states (van Engelen: "cut lazy edges from accepting states")
            if (!fromState.IsAccept)
            {
                return false;
            }

            // Don't trim from states with anchors
            bool hasAnchors = fromState.Attributes.ContainsKey("AnchorMask") &&
                             (int)fromState.Attributes["AnchorMask"] != 0;
            if (hasAnchors)
            {
                return false;
            }

            var fromPositions = GetPositionsFromState(fromState);
            var toPositions = GetPositionsFromState(toState);

            // Van Engelen: "we know when DFA edges point forward or backward in the regex string"
            // A backward move is specifically: lastpos -> firstpos of the same lazy quantifier
            // This represents completing one iteration and starting the next (loop-back)

            foreach (var fromPos in fromPositions)
            {
                // Only consider lazy positions that have a parent quantifier
                if (!lazyPositions.Contains(fromPos) || !positionToLazyParent.ContainsKey(fromPos))
                {
                    continue;
                }

                var lazyParent = positionToLazyParent[fromPos];
                var parentFirstPos = lazyParent.Expression?.GetFirstPos(firstPosMap) ?? new HashSet<RegexExpression>();
                var parentLastPos = lazyParent.Expression?.GetLastPos(lastPosMap) ?? new HashSet<RegexExpression>();

                // CRITICAL FIX: Only trim if we're transitioning FROM a lastpos position
                // This ensures we've completed the pattern before considering loop-back
                if (parentLastPos.Contains(fromPos))
                {
                    // AND we're transitioning TO a firstpos of the same pattern
                    bool transitionsToSamePatternStart = toPositions.Intersect(parentFirstPos).Any();

                    if (transitionsToSamePatternStart)
                    {
                        // This is a backward move: end-of-pattern -> start-of-pattern
                        // Van Engelen: These represent lazy loop-backs that can be trimmed
                        return true;
                    }
                }

                // If fromPos is NOT in lastpos, this is a forward move through the pattern
                // Forward moves should NEVER be trimmed, even if they go to firstpos
            }

            return false; // Not a backward move - don't trim
        }


        private static Dfa CreateStateFromPositions(
     HashSet<RegexExpression> positions,
     HashSet<RegexExpression> lazyPositions,
     Dictionary<RegexTerminatorExpression, int> endMarkerToAcceptSymbol,
     Dictionary<RegexExpression, int> positionToAcceptSymbol)
        {
            var state = new Dfa();
            // Compute anchor mask from positions
            int anchorMask = 0;
            const int START_ANCHOR = 1;  // ^
            const int END_ANCHOR = 2;    // $

            bool hasAnchors = false;
            foreach (var pos in positions)
            {
                if (pos is RegexAnchorExpression anchor)
                {
                    hasAnchors = true;
                    // Use anchor type instead of virtual codepoint
                    switch (anchor.Type) // or whatever property identifies the anchor type
                    {
                        case RegexAnchorType.LineStart: // or similar enum value
                            anchorMask |= START_ANCHOR;
                            break;
                        case RegexAnchorType.LineEnd:
                            anchorMask |= END_ANCHOR;
                            break;
                    }
                }
            }

            if (anchorMask != 0)
            {
                state.Attributes["AnchorMask"] = anchorMask;
            }

            // Check for acceptance - both end markers AND anchor positions can make a state accepting
            int? acceptSymbol = null;

            // First check for end markers (traditional acceptance)
            foreach (var pos in positions)
            {
                if (pos is RegexTerminatorExpression endMarker && endMarkerToAcceptSymbol.ContainsKey(endMarker))
                {
                    int currentAcceptSymbol = endMarkerToAcceptSymbol[endMarker];
                    if (!acceptSymbol.HasValue || currentAcceptSymbol < acceptSymbol.Value)
                    {
                        acceptSymbol = currentAcceptSymbol;
                    }
                }
            }

            // Only END anchors at pattern boundaries should make states accepting
            if (!acceptSymbol.HasValue && hasAnchors)
            {
                foreach (var pos in positions)
                {
                    if (pos is RegexAnchorExpression anchor &&
                        anchor.Type == RegexAnchorType.LineEnd &&  // Only $ anchors
                        positionToAcceptSymbol.ContainsKey(pos))
                    {
                        // Additional check: ensure this $ is actually at the end of its pattern
                        // (you may need followpos analysis to verify this)
                        acceptSymbol = positionToAcceptSymbol[pos];
                    }
                }
            }


            // Set the accept symbol if this is an accepting state
            if (acceptSymbol.HasValue)
            {
                state.Attributes["AcceptSymbol"] = acceptSymbol.Value;
            }

            state.Attributes["Positions"] = positions.ToList();
            state.Attributes["LazyPositions"] = lazyPositions.ToList();
            return state;
        }

        private static HashSet<RegexExpression> GetPositionsFromState(Dfa state)
        {
            var positions = (List<RegexExpression>)state.Attributes["Positions"];
            return new HashSet<RegexExpression>(positions);
        }

        private static HashSet<RegexExpression> GetLazyPositionsFromState(Dfa state)
        {
            if (state.Attributes.ContainsKey("LazyPositions"))
            {
                var lazyPositions = (List<RegexExpression>)state.Attributes["LazyPositions"];
                return new HashSet<RegexExpression>(lazyPositions);
            }
            return new HashSet<RegexExpression>();
        }
    }
}