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
            regexAst = RegexExpression.Parse(regexAst.ToString("x"));
            int positionCounter = 1;
            RegexTerminatorExpression endMarker=null;
            int[] points;
            int[] anchorPoints;
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
            var ap = new HashSet<int>();
            regexAst.Visit((parent, expression, childIndex, level) =>
            {
                if (expression is RegexAnchorExpression anchor)
                {
                    ap.Add(anchor.GetVirtualCodepoint());
                }
                else
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
                }
                return true;
            });
            points = new int[p.Count];
            p.CopyTo(points, 0);
            Array.Sort(points);

            // Clear any previous state
            RegexExpressionExtensions.ClearDfaProperties();

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
            AssignPositions(augmentedAst, positions, ref positionCounter);
            
            // Step 4: Compute nullable, firstpos, lastpos
            ComputeNodeProperties(augmentedAst);
            // DEBUG AFTER ComputeNodeProperties
            // Step 5: Compute followpos with proper disjunction handling
            ComputeFollowPos(augmentedAst, positions, followPos);

            // Step 6: Build DFA with lazy attribution and contagion
            var dfa = ConstructLazyDfa(augmentedAst, points, lazyPositions, endMarkerToAcceptSymbol, followPos, isLexer, positionToAcceptSymbol);

            // Step 7: Apply lazy edge trimming to accepting states
            ApplyLazyEdgeTrimming(dfa, positionToLazyParent);
           
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

                MarkPositionsWithAcceptSymbol(rule, acceptSymbol, positionToAcceptSymbol);

                var augmentedRule = new RegexConcatExpression(rule, endMarker);
                augmentedRules.Add(augmentedRule);

                acceptSymbol++;
            }

            // Return new lexer with augmented rules
            var augmentedLexer = new RegexLexerExpression(augmentedRules);
            // Copy any other properties if needed
            return augmentedLexer;
        }


        // Mark all positions in a subtree with the given accept symbol
        private static void MarkPositionsWithAcceptSymbol(RegexExpression expr, int acceptSymbol, Dictionary<RegexExpression, int> positionToAcceptSymbol)
        {
            expr.Visit((parent, node, childIndex, level) =>
            {
                if (node.IsLeaf)
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

        private static void AssignPositions(RegexExpression node, Dictionary<int, RegexExpression> _positions, ref int _positionCounter)
        {
            if (node == null) return;

            if (node.IsLeaf)
            {
                if (node.GetDfaPosition() == -1)
                {
                    node.SetDfaPosition(_positionCounter++);
                    _positions[node.GetDfaPosition()] = node;
                }
                return;
            }

            switch (node)
            {
                case RegexLexerExpression lexer:  // ADD THIS CASE
                    foreach (var rule in lexer.Rules)
                    {
                        AssignPositions(rule, _positions, ref _positionCounter);
                    }
                    break;
                case RegexBinaryExpression binary:
                    AssignPositions(binary.Left, _positions, ref _positionCounter);
                    AssignPositions(binary.Right, _positions, ref _positionCounter);
                    break;

                case RegexUnaryExpression unary:
                    AssignPositions(unary.Expression, _positions, ref _positionCounter);
                    break;
            }
        }

        
        private static void ComputeNodeProperties(RegexExpression node)
        {
            if (node == null) return;

            switch (node)
            {
                case RegexLexerExpression lexer:
                    // Compute properties for all rules first
                    foreach (var rule in lexer.Rules)
                    {
                        ComputeNodeProperties(rule);
                    }

                    // Lexer is nullable if any rule is nullable
                    node.SetNullable(lexer.Rules.Any(r => r.GetNullable()));

                    // Lexer firstpos is union of all rules' firstpos
                    foreach (var rule in lexer.Rules)
                    {
                        node.GetFirstPos().UnionWith(rule.GetFirstPos());
                    }

                    // Lexer lastpos is union of all rules' lastpos  
                    foreach (var rule in lexer.Rules)
                    {
                        node.GetLastPos().UnionWith(rule.GetLastPos());
                    }
                    break;
                case RegexConcatExpression concat:
                    ComputeNodeProperties(concat.Left);
                    ComputeNodeProperties(concat.Right);

                    var children = new[] { concat.Left, concat.Right }.Where(c => c != null).ToArray();

                    node.SetNullable(children.All(c => c.GetNullable()));

                    foreach (var child in children)
                    {
                        node.GetFirstPos().UnionWith(child.GetFirstPos());
                        if (!child.GetNullable())
                            break;
                    }

                    for (int i = children.Length - 1; i >= 0; i--)
                    {
                        node.GetLastPos().UnionWith(children[i].GetLastPos());
                        if (!children[i].GetNullable())
                            break;
                    }
                    break;

                case RegexOrExpression or:
                    ComputeNodeProperties(or.Left);
                    ComputeNodeProperties(or.Right);

                    var orChildren = new[] { or.Left, or.Right }.Where(c => c != null).ToArray();
                    node.SetNullable(orChildren.Any(c => c.GetNullable()));

                    foreach (var child in orChildren)
                    {
                        node.GetFirstPos().UnionWith(child.GetFirstPos());
                        node.GetLastPos().UnionWith(child.GetLastPos());
                    }

                   
                    break;
                case RegexRepeatExpression repeat:
                    ComputeNodeProperties(repeat.Expression);

                    if (repeat.Expression == null) break;

                    if (repeat.MinOccurs <= 0)
                    {
                        node.SetNullable(true);
                    }
                    else
                    {
                        node.SetNullable(repeat.Expression.GetNullable());
                    }

                    node.GetFirstPos().UnionWith(repeat.Expression.GetFirstPos());
                    node.GetLastPos().UnionWith(repeat.Expression.GetLastPos());
                    break;
                case RegexTerminatorExpression:
                case RegexLiteralExpression:
                case RegexAnchorExpression:
                    node.SetNullable(false);
                    node.GetFirstPos().Add(node);
                    node.GetLastPos().Add(node);
                    break;

                case RegexCharsetExpression charset:
                    if (node is RegexLiteralExpression lit && (lit.Codepoint == -1))
                    {
                        node.SetNullable(true);
                    }
                    else
                    {
                        node.SetNullable(false);
                        node.GetFirstPos().Add(node);
                        node.GetLastPos().Add(node);
                    }
                    break;
            }
        }

        private static void ComputeFollowPos(RegexExpression node, Dictionary<int, RegexExpression> positions, Dictionary<RegexExpression, HashSet<RegexExpression>> followPos)
        {
            foreach (var pos in positions.Values)
            {
                followPos[pos] = new HashSet<RegexExpression>();
            }

            ComputeFollowPosRecursive(node, followPos);
        }

        private static void ComputeFollowPosRecursive(RegexExpression node, Dictionary<RegexExpression, HashSet<RegexExpression>> _followPos)
        {
            if (node == null) return;
            
            switch (node)
            {
                case RegexLexerExpression lexerExpr:  // ADD THIS CASE
                    foreach (var rule in lexerExpr.Rules)
                    {
                        ComputeFollowPosRecursive(rule, _followPos);
                    }
                    break;

                case RegexConcatExpression concat:
                    if (concat.Left != null && concat.Right != null)
                    {
                        // Debug output
                        //var leftLast = string.Join(",", concat.Left.GetLastPos().Select(p => p.GetDfaPosition()));
                        //var rightFirst = string.Join(",", concat.Right.GetFirstPos().Select(p => p.GetDfaPosition()));
                        
                        foreach (var pos in concat.Left.GetLastPos())
                        {
                            if (_followPos.ContainsKey(pos))
                            {
                                _followPos[pos].UnionWith(concat.Right.GetFirstPos());
                            }
                        }
                    }
                    ComputeFollowPosRecursive(concat.Left, _followPos);
                    ComputeFollowPosRecursive(concat.Right, _followPos);
                    break;

                case RegexOrExpression or:
                    // CRITICAL FIX: Handle disjunction properly
                    // For alternation, we don't add followpos rules here
                    // The disjunction is handled in the DFA construction phase
                    // by including both branches in firstpos/lastpos
                    ComputeFollowPosRecursive(or.Left, _followPos);
                    ComputeFollowPosRecursive(or.Right, _followPos);
                    
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
                            foreach (var lastPos in repeat.Expression.GetLastPos())
                            {
                                if (_followPos.ContainsKey(lastPos))
                                {
                                    // This is a "backward" move in the regex string (loop back)
                                    _followPos[lastPos].UnionWith(repeat.Expression.GetFirstPos());
                                }
                            }
                        }
                    }

                    ComputeFollowPosRecursive(repeat.Expression, _followPos);
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
            Dictionary<RegexExpression, int> positionToAcceptSymbol)
        {
            var startPositions = root.GetFirstPos();
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

                    // Handle anchor positions - add them as single-codepoint ranges
                    if (pos is RegexAnchorExpression anchor)
                    {
                        var virtualCodepoint = anchor.GetVirtualCodepoint();
                        var anchorRange = new DfaRange(virtualCodepoint, virtualCodepoint);
                        if (!transitionMap.TryGetValue(anchorRange, out var anchorSet))
                        {
                            anchorSet = new HashSet<RegexExpression>();
                            transitionMap.Add(anchorRange, anchorSet);
                        }
                        anchorSet.Add(pos);
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

                    var nextPosStr = string.Join(",", nextPositions.Select(p => p.GetDfaPosition()));
                    
                    if (nextPositions.Count == 0) continue;


                    // Van Engelen: "Laziness is contagious" - propagate lazy attribution
                    var nextLazyPositions = PropagatelazyContagion(positions, nextPositions, currentLazyPositions, lazyPositions, followPos);

                    // CRITICAL: Ensure disjunctive states are properly distinguished by full attributes
                    var candidateState = CreateStateFromPositions(nextPositions, nextLazyPositions, endMarkerToAcceptSymbol, positionToAcceptSymbol);

                    Dfa nextState;

                    // Debug: Show what attributes we're comparing
                    var posStr = string.Join(",", nextPositions.Select(p => p.GetDfaPosition()));
                    
                    // Find existing state with same attributes, or use the new one
                    var existingState = allStates.Values.FirstOrDefault(s => {
                        bool areEqual = s.Attributes.Equals(candidateState.Attributes);
                        return areEqual;
                    });

                    if (existingState == null)
                    {
                        // No existing state with these attributes - use the new one
                        nextState = candidateState;
                        allStates[candidateState.Attributes] = nextState;
                        unmarkedStates.Enqueue(nextState);
                    }
                    else
                    {
                        // Found existing state with same attributes - reuse it
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
        private static void ApplyLazyEdgeTrimming(Dfa startState, Dictionary<RegexExpression, RegexRepeatExpression> positionToLazyParent)
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
                        TrimLazyEdges(state, lazyPositions, positionToLazyParent);
                    }
                }
            }
        }

        // Van Engelen: Cut "lazy edges" by analyzing forward/backward moves
        private static void TrimLazyEdges(Dfa acceptingState, HashSet<RegexExpression> lazyPositions, Dictionary<RegexExpression, RegexRepeatExpression> positionToLazyParent)
        {
            var transitionsToRemove = new List<DfaTransition>();

            foreach (var transition in acceptingState.Transitions)
            {
                // Check if this transition represents a "lazy edge" that should be trimmed
                // Van Engelen: "we know when DFA edges point forward or backward in the regex string"
                bool isLazyEdge = IsLazyEdgeToTrim(acceptingState, transition.To, lazyPositions, positionToLazyParent);

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
        private static bool IsLazyEdgeToTrim(Dfa fromState, Dfa toState, HashSet<RegexExpression> lazyPositions, Dictionary<RegexExpression, RegexRepeatExpression> positionToLazyParent)
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
                    var parentFirstPos = lazyParent.Expression?.GetFirstPos() ?? new HashSet<RegexExpression>();

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

        // Updated to handle multiple end markers and accept symbols
        private static Dfa CreateStateFromPositions(
            HashSet<RegexExpression> positions,
            HashSet<RegexExpression> lazyPositions,
            Dictionary<RegexTerminatorExpression, int> endMarkerToAcceptSymbol,
            Dictionary<RegexExpression, int> positionToAcceptSymbol)
        {
            var state = new Dfa();

            // Check if this state contains any end markers (accepting state)
            int? acceptSymbol = null;
            var endMarkersFound = new List<(RegexTerminatorExpression, int)>();

            foreach (var pos in positions)
            {
                if (pos is RegexTerminatorExpression endMarker && endMarkerToAcceptSymbol.ContainsKey(endMarker))
                {
                    int currentAcceptSymbol = endMarkerToAcceptSymbol[endMarker];
                    endMarkersFound.Add((endMarker, currentAcceptSymbol));

                    // Use the lowest accept symbol (highest precedence) if multiple end markers are present
                    if (!acceptSymbol.HasValue || currentAcceptSymbol < acceptSymbol.Value)
                    {
                        acceptSymbol = currentAcceptSymbol;
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