// An implementation of Dr. Robert van Engelen's lazy DFA matching state construction algorithm used in his RE/FLEX project
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Luthor
{
    // Dr. Robert van Engelen's lazy DFA construction based on his email correspondence
    static class DfaBuilder
    {
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
            var dfa = ConstructLazyDfa(augmentedAst, points, lazyPositions, endMarkerToAcceptSymbol, followPos, isLexer, positionToAcceptSymbol, firstPosMap, positionMap);

            // Step 7: Apply lazy edge trimming to accepting states
            ApplyLazyEdgeTrimming(dfa, positionToLazyParent, firstPosMap);

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

        private static void ComputeFollowPosRecursive(RegexExpression node, Dictionary<RegexExpression, HashSet<RegexExpression>> _followPos, Dictionary<RegexExpression, HashSet<RegexExpression>> lastPosMap, Dictionary<RegexExpression, HashSet<RegexExpression>> firstPosMap, Dictionary<RegexExpression, int> positionMap)
        {
            if (node == null) return;

            switch (node)
            {
                case RegexLexerExpression lexerExpr:  // ADD THIS CASE
                    foreach (var rule in lexerExpr.Rules)
                    {
                        ComputeFollowPosRecursive(rule, _followPos, lastPosMap, firstPosMap, positionMap);
                    }
                    break;

                case RegexConcatExpression concat:
                    if (concat.Left != null && concat.Right != null)
                    {
                      
                        foreach (var pos in concat.Left.GetLastPos(lastPosMap))
                        {
                            if (_followPos.ContainsKey(pos))
                            {
                                _followPos[pos].UnionWith(concat.Right.GetFirstPos(firstPosMap));
                            }
                        }
                    }
                    ComputeFollowPosRecursive(concat.Left, _followPos, lastPosMap, firstPosMap, positionMap);
                    ComputeFollowPosRecursive(concat.Right, _followPos, lastPosMap, firstPosMap, positionMap);
                    break;

                case RegexOrExpression or:
                    // CRITICAL FIX: Handle disjunction properly
                    // For alternation, we don't add followpos rules here
                    // The disjunction is handled in the DFA construction phase
                    // by including both branches in firstpos/lastpos
                    ComputeFollowPosRecursive(or.Left, _followPos, lastPosMap, firstPosMap, positionMap);
                    ComputeFollowPosRecursive(or.Right, _followPos, lastPosMap, firstPosMap, positionMap);

                    break;

                case RegexRepeatExpression repeat:
                    if (repeat.Expression != null && !repeat.Expression.IsEmptyElement)
                    {

                        bool canRepeat = (repeat.MinOccurs == -1 || repeat.MinOccurs == 0) ||
                                        (repeat.MinOccurs == 1 && (repeat.MaxOccurs == -1 || repeat.MaxOccurs == 0)) ||
                                        (repeat.MaxOccurs > 1 || repeat.MaxOccurs == -1);

                        if (canRepeat)
                        {
                            // Van Engelen: track forward/backward moves in regex string
                            foreach (var lastPos in repeat.Expression.GetLastPos(lastPosMap))
                            {
                                if (_followPos.ContainsKey(lastPos))
                                {
                                    // This is a "backward" move in the regex string (loop back)
                                    _followPos[lastPos].UnionWith(repeat.Expression.GetFirstPos(firstPosMap));
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
            Dictionary<RegexExpression, int> positionMap)
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
                // Group positions by character ranges for transition construction
                var transitionMap = new Dictionary<DfaRange, HashSet<RegexExpression>>();
                foreach (var pos in currentPositions)
                {
                    // Skip all end markers
                    if (pos is RegexTerminatorExpression) continue;

                    // Handle anchor positions elsewhere
                    if (pos is RegexAnchorExpression anchor)
                    {
                        continue;

                    }

                    // Handle character ranges (existing code for non-anchors)
                    for (int i = 0; i < points.Length; ++i)
                    {
                        var first = points[i];
                        var last = (i < points.Length - 1) ? points[i + 1] - 1 : 0x10ffff;
                        var range = new DfaRange(first, last);

                        if (PositionMatchesRange(pos, range))
                        {
                            if (!transitionMap.TryGetValue(range, out var hset))
                            {
                                hset = new HashSet<RegexExpression>();
                                transitionMap.Add(range, hset);
                            }
                            hset.Add(pos);
                        }
                    }
                }

                // Create transitions for each character range
                foreach (var transition in transitionMap)
                {

                    var range = transition.Key;
                    var positions = transition.Value;

                    var nextPositions = new HashSet<RegexExpression>();
                    foreach (var pos in positions)
                    {
                        if (followPos.ContainsKey(pos))
                        {
                            nextPositions.UnionWith(followPos[pos]);
                        }
                    }

                    // NEW: Handle anchors as immediate epsilon moves
                    var anchorsToProcess = nextPositions.Where(p => p is RegexAnchorExpression).ToList();
                    foreach (var anchor in anchorsToProcess)
                    {
                        if (followPos.ContainsKey(anchor))
                        {
                            nextPositions.UnionWith(followPos[anchor]); // Include positions after anchor
                        }
                    }

                    if (nextPositions.Count == 0) continue;

                    // Van Engelen: "Laziness is contagious" - propagate lazy attribution
                    var nextLazyPositions = PropagatelazyContagion(positions, nextPositions, currentLazyPositions, lazyPositions, followPos);

                    // CRITICAL: Ensure disjunctive states are properly distinguished by full attributes
                    var candidateState = CreateStateFromPositions(nextPositions, nextLazyPositions, endMarkerToAcceptSymbol, positionToAcceptSymbol);

                    Dfa nextState;

                    // Find existing state with same attributes, or use the new one
                    var existingState = allStates.Values.FirstOrDefault(s => {
                        bool areEqual = s.Attributes.Equals(candidateState.Attributes);

                        return areEqual;
                    });

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

                    // Add transition to next state
                    currentState.AddTransition(new DfaTransition(nextState, range.Min, range.Max));
                }
            }

            // Remove dead transitions
            foreach (var ffa in startState.FillClosure())
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

            // Van Engelen: "propagating laziness along a path"
            foreach (var sourcePos in sourcePositions)
            {
                // If source position is lazy (either inherently or through contagion)
                if (lazyPositions.ContainsKey(sourcePos) && lazyPositions[sourcePos] ||
                    currentLazyPositions.Contains(sourcePos))
                {
                    // Propagate laziness to all reachable target positions
                    if (followPos.ContainsKey(sourcePos))
                    {
                        foreach (var targetPos in followPos[sourcePos])
                        {
                            if (targetPositions.Contains(targetPos))
                            {
                                newLazyPositions.Add(targetPos);
                            }
                        }
                    }
                }
            }

            return newLazyPositions;
        }

        // Van Engelen: "lazy edge trimming" - cut lazy edges from accepting states
        private static void ApplyLazyEdgeTrimming(Dfa startState, Dictionary<RegexExpression, RegexRepeatExpression> positionToLazyParent, Dictionary<RegexExpression, HashSet<RegexExpression>> firstPosMap)
        {
            var allStates = startState.FillClosure();

            foreach (var state in allStates)
            {
                if (state.IsAccept)
                {
                    // This is an accepting state - apply lazy edge trimming
                    var lazyPositions = GetLazyPositionsFromState(state);

                    if (lazyPositions.Count > 0)
                    {
                        TrimLazyEdges(state, lazyPositions, positionToLazyParent, firstPosMap);
                    }
                }
            }
        }

        // Van Engelen: Cut "lazy edges" by analyzing forward/backward moves
        private static void TrimLazyEdges(Dfa acceptingState, HashSet<RegexExpression> lazyPositions, Dictionary<RegexExpression, RegexRepeatExpression> positionToLazyParent, Dictionary<RegexExpression, HashSet<RegexExpression>> firstPosMap)
        {
            var transitionsToRemove = new List<DfaTransition>();

            foreach (var transition in acceptingState.Transitions)
            {
                // Check if this transition represents a "lazy edge" that should be trimmed
                // Van Engelen: "we know when DFA edges point forward or backward in the regex string"
                bool isLazyEdge = IsLazyEdgeToTrim(acceptingState, transition.To, lazyPositions, positionToLazyParent, firstPosMap);

                if (isLazyEdge)
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

        // Determine if an edge should be trimmed based on lazy attribution
        private static bool IsLazyEdgeToTrim(Dfa fromState, Dfa toState, HashSet<RegexExpression> lazyPositions, Dictionary<RegexExpression, RegexRepeatExpression> positionToLazyParent, Dictionary<RegexExpression, HashSet<RegexExpression>> firstPosMap)
        {
            var fromPositions = GetPositionsFromState(fromState);
            var toPositions = GetPositionsFromState(toState);

            // Van Engelen: "taking forward/backward regex moves to regex positions into account"
            // Check if this represents a "backward" move in a lazy context

            foreach (var lazyPos in lazyPositions)
            {
                if (fromPositions.Contains(lazyPos) && positionToLazyParent.ContainsKey(lazyPos))
                {
                    var lazyParent = positionToLazyParent[lazyPos];
                    var parentFirstPos = lazyParent.Expression?.GetFirstPos(firstPosMap) ?? new HashSet<RegexExpression>();

                    // If the transition goes to positions that include the start of the lazy construct,
                    // this represents a "backward" move that should be trimmed in lazy mode
                    if (toPositions.Intersect(parentFirstPos).Any())
                    {
                        return true; // This is a lazy edge to trim
                    }
                }
            }

            return false;
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