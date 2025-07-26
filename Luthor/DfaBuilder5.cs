using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace TestFA
{
    // Dr. Robert van Engelen's lazy DFA construction based on his email correspondence
    class DfaBuilder
    {
        public static Dfa BuildDfa(RegexExpression regexAst)
        {
            int positionCounter = 1;
            RegexTerminatorExpression endMarker;
            int[] points;
            bool isLexer = regexAst is RegexOrExpression;
            Dictionary<int, RegexExpression> positions = new Dictionary<int, RegexExpression>();
            Dictionary<RegexExpression, HashSet<RegexExpression>> followPos =
                new Dictionary<RegexExpression, HashSet<RegexExpression>>();

            // positions need lazy attribution
            Dictionary<RegexExpression, bool> lazyPositions = new Dictionary<RegexExpression, bool>();

            // Track which positions are inside lazy quantifiers for contagion
            Dictionary<RegexExpression, RegexRepeatExpression> positionToLazyParent =
                new Dictionary<RegexExpression, RegexRepeatExpression>();

            // Track lazy context for proper attribution
            HashSet<RegexExpression> _currentLazyContext = new HashSet<RegexExpression>();
            var p = new HashSet<int>();
            regexAst.Visit((parent, expression, childIndex, level) =>
            {
                foreach (var range in expression.GetRanges())
                {
                    p.Add(0);
                    if (range.Min == -1 || range.Max == -1) continue;
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

            // Clear any previous state
            RegexExpressionExtensions.ClearDfaProperties();

            positionCounter = 1;

            // Step 1: Identify lazy quantifiers and mark positions with context
            MarkLazyPositionsWithContext(regexAst, false,positionToLazyParent,lazyPositions);

            // Step 2: Augment with end marker
            var augmentedAst = AugmentWithEndMarker(regexAst,out endMarker,positions);

            // Step 3: Assign positions to leaf nodes
            AssignPositions(augmentedAst,positions, ref positionCounter);

            // Step 4: Compute nullable, firstpos, lastpos
            ComputeNodeProperties(augmentedAst);

            // Step 5: Compute followpos with proper disjunction handling
            ComputeFollowPos(augmentedAst,positions,followPos);

            // Step 6: Build DFA with lazy attribution and contagion
            var dfa = ConstructLazyDfa(augmentedAst,points,lazyPositions,endMarker,followPos, isLexer);

            // Step 7: Apply lazy edge trimming to accepting states
            ApplyLazyEdgeTrimming(dfa,positionToLazyParent);

            return dfa;
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
                    if (IsLeafNode(e))
                    {
                        positionToLazyParent[e] = repeat;
                    }
                    return true;
                });
            }

            // Mark leaf nodes if they're in lazy context
            if (IsLeafNode(ast) && currentLazyContext)
            {
                lazyPositions[ast] = true;
            }

            // Recursively process children with updated context
            switch (ast)
            {
                case RegexBinaryExpression binary:
                    MarkLazyPositionsWithContext(binary.Left, currentLazyContext,positionToLazyParent,lazyPositions);
                    MarkLazyPositionsWithContext(binary.Right, currentLazyContext,positionToLazyParent,lazyPositions);
                    break;

                case RegexUnaryExpression unary:
                    MarkLazyPositionsWithContext(unary.Expression, currentLazyContext,positionToLazyParent,lazyPositions);
                    break;
            }
        }

        private static RegexConcatExpression AugmentWithEndMarker(RegexExpression root,out RegexTerminatorExpression _endMarker, Dictionary<int, RegexExpression> _positions)
        {
            _endMarker = new RegexTerminatorExpression();
            return new RegexConcatExpression(root, _endMarker);
        }

        private static void AssignPositions(RegexExpression node, Dictionary<int, RegexExpression> _positions, ref int _positionCounter)
        {
            if (node == null) return;

            if (IsLeafNode(node))
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
                case RegexBinaryExpression binary:
                    AssignPositions(binary.Left,_positions, ref _positionCounter);
                    AssignPositions(binary.Right,_positions, ref _positionCounter);
                    break;

                case RegexUnaryExpression unary:
                    AssignPositions(unary.Expression,_positions,ref _positionCounter);
                    break;
            }
        }

        private static bool IsLeafNode(RegexExpression node)
        {
            return node is RegexLiteralExpression || node is RegexCharsetExpression || node is RegexTerminatorExpression;
        }

        private static void ComputeNodeProperties(RegexExpression node)
        {
            if (node == null) return;

            switch (node)
            {
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
                case RegexConcatExpression concat:
                    if (concat.Left != null && concat.Right != null)
                    {
                        foreach (var pos in concat.Left.GetLastPos())
                        {
                            if (_followPos.ContainsKey(pos))
                            {
                                _followPos[pos].UnionWith(concat.Right.GetFirstPos());
                            }
                        }
                    }

                    ComputeFollowPosRecursive(concat.Left,_followPos);
                    ComputeFollowPosRecursive(concat.Right,_followPos);
                    break;

                case RegexOrExpression or:
                    // CRITICAL FIX: Handle disjunction properly
                    // For alternation, we don't add followpos rules here
                    // The disjunction is handled in the DFA construction phase
                    // by including both branches in firstpos/lastpos
                    ComputeFollowPosRecursive(or.Left,_followPos);
                    ComputeFollowPosRecursive(or.Right,_followPos);
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

                    ComputeFollowPosRecursive(repeat.Expression,_followPos);
                    break;
            }
        }

        // Van Engelen: "DFA states are unique sets of regex positions" but with lazy attribution
        private class LazyStateKey
        {
            public HashSet<RegexExpression> Positions { get; set; }
            public HashSet<RegexExpression> LazyPositions { get; set; }

            public LazyStateKey(HashSet<RegexExpression> positions, HashSet<RegexExpression> lazyPositions)
            {
                Positions = positions;
                LazyPositions = lazyPositions;
            }

            public override bool Equals(object obj)
            {
                if (obj is LazyStateKey other)
                {
                    return Positions.SetEquals(other.Positions) && LazyPositions.SetEquals(other.LazyPositions);
                }
                return false;
            }

            public override int GetHashCode()
            {
                int hash = 0;
                foreach (var pos in Positions.OrderBy(p => p.GetDfaPosition()))
                {
                    hash ^= pos.GetHashCode();
                }
                foreach (var lazyPos in LazyPositions.OrderBy(p => p.GetDfaPosition()))
                {
                    hash ^= lazyPos.GetHashCode() << 1;
                }
                return hash;
            }

            public override string ToString()
            {
                var posStr = string.Join(",", Positions.Where(p => p.GetDfaPosition() != -1)
                    .OrderBy(p => p.GetDfaPosition()).Select(p => p.GetDfaPosition()));
                var lazyStr = string.Join(",", LazyPositions.Where(p => p.GetDfaPosition() != -1)
                    .OrderBy(p => p.GetDfaPosition()).Select(p => p.GetDfaPosition()));
                return $"[{posStr}]L[{lazyStr}]";
            }
        }

        private static bool PositionMatchesRange(RegexExpression pos, FARange range)
        {
            foreach (var range2 in pos.GetRanges())
            {
                if (range2.Intersects(range)) return true;
            }
            return false;
        }

        // Van Engelen: Build DFA with lazy contagion during construction
        private static Dfa ConstructLazyDfa(RegexExpression root, int[] points, Dictionary<RegexExpression, bool> lazyPositions, RegexTerminatorExpression endMarker, Dictionary<RegexExpression, HashSet<RegexExpression>> followPos, bool isLexer)
        {
            var startPositions = root.GetFirstPos();
            var startLazyPositions = GetLazyPositions(startPositions,lazyPositions);
            var startState = CreateStateFromPositions(startPositions, startLazyPositions,endMarker);

            var unmarkedStates = new Queue<Dfa>();
            var allStates = new Dictionary<LazyStateKey, Dfa>();

            var startKey = new LazyStateKey(startPositions, startLazyPositions);
            allStates[startKey] = startState;
            unmarkedStates.Enqueue(startState);

            while (unmarkedStates.Count > 0)
            {
                var currentState = unmarkedStates.Dequeue();
                var currentPositions = GetPositionsFromState(currentState);
                var currentLazyPositions = GetLazyPositionsFromState(currentState);

                // Group positions by character ranges for transition construction
                var transitionMap = new Dictionary<FARange, HashSet<RegexExpression>>();

                foreach (var pos in currentPositions)
                {
                    if (pos == endMarker) continue;

                    for (int i = 0; i < points.Length; ++i)
                    {
                        var first = points[i];
                        var last = (i < points.Length - 1) ? points[i + 1] - 1 : 0x10ffff;
                        var range = new FARange(first, last);

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

                    if (nextPositions.Count == 0) continue;

                    // Van Engelen: "Laziness is contagious" - propagate lazy attribution
                    var nextLazyPositions = PropagatelazyContagion(positions, nextPositions, currentLazyPositions,lazyPositions, followPos);

                    // CRITICAL: Ensure disjunctive states are properly distinguished
                    var nextKey = new LazyStateKey(nextPositions, nextLazyPositions);
                    Dfa nextState;

                    if (!allStates.ContainsKey(nextKey))
                    {
                        nextState = CreateStateFromPositions(nextPositions, nextLazyPositions,endMarker);
                        allStates[nextKey] = nextState;
                        unmarkedStates.Enqueue(nextState);
                    }
                    else
                    {
                        nextState = allStates[nextKey];
                    }

                    // Add transition to next state
                    currentState.AddTransition(new FATransition(nextState, range.Min, range.Max));
                }
            }

            // Remove dead transitions
            foreach (var ffa in startState.FillClosure())
            {
                var itrns = new List<FATransition>(ffa.Transitions);
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
                        ffa.RemoveTransition(trns);
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
            newLazyPositions.UnionWith(GetLazyPositions(targetPositions,lazyPositions));

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
                        TrimLazyEdges(state, lazyPositions,positionToLazyParent);
                    }
                }
            }
        }

        // Van Engelen: Cut "lazy edges" by analyzing forward/backward moves
        private static void TrimLazyEdges(Dfa acceptingState, HashSet<RegexExpression> lazyPositions, Dictionary<RegexExpression, RegexRepeatExpression> positionToLazyParent)
        {
            var transitionsToRemove = new List<FATransition>();

            foreach (var transition in acceptingState.Transitions)
            {
                // Check if this transition represents a "lazy edge" that should be trimmed
                // Van Engelen: "we know when DFA edges point forward or backward in the regex string"
                bool isLazyEdge = IsLazyEdgeToTrim(acceptingState, transition.To, lazyPositions,positionToLazyParent);

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

        private static Dfa CreateStateFromPositions(HashSet<RegexExpression> positions, HashSet<RegexExpression> lazyPositions, RegexTerminatorExpression endMarker)
        {
            var state = new Dfa();

            if (positions.Contains(endMarker))
            {
                state.Attributes["AcceptSymbol"] = 0;
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