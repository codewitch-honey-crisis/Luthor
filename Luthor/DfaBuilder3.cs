using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace TestFA
{
    // Van Engelen's actual lazy DFA construction based on his email explanations
    class DirectDfaBuilder
    {
        private int _positionCounter = 1;
        private RegexExpression _endMarker;
        private int[] _points;
        private Dictionary<int, RegexExpression> _positions = new Dictionary<int, RegexExpression>();
        private Dictionary<RegexExpression, HashSet<RegexExpression>> _followPos =
            new Dictionary<RegexExpression, HashSet<RegexExpression>>();

        // Van Engelen's key insight: positions need lazy attribution
        private Dictionary<RegexExpression, bool> _lazyPositions = new Dictionary<RegexExpression, bool>();

        // Track which positions are inside lazy quantifiers for contagion
        private Dictionary<RegexExpression, RegexRepeatExpression> _positionToLazyParent =
            new Dictionary<RegexExpression, RegexRepeatExpression>();

        // Track lazy context for proper attribution
        private HashSet<RegexExpression> _currentLazyContext = new HashSet<RegexExpression>();

        public Dfa BuildDfa(RegexExpression regexAst)
        {
            var p = new HashSet<int>();
            regexAst.Visit((parent, expression, childIndex, level) => {
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
            _points = new int[p.Count];
            p.CopyTo(_points, 0);
            Array.Sort(_points);

            // Clear any previous state
            RegexExpressionExtensions.ClearDfaProperties();
            _positions.Clear();
            _followPos.Clear();
            _lazyPositions.Clear();
            _positionToLazyParent.Clear();
            _currentLazyContext.Clear();
            _positionCounter = 1;

            // Step 1: Identify lazy quantifiers and mark positions with context
            MarkLazyPositionsWithContext(regexAst, false);

            // Step 2: Augment with end marker
            var augmentedAst = AugmentWithEndMarker(regexAst);

            // Step 3: Assign positions to leaf nodes
            AssignPositions(augmentedAst);

            // Step 4: Compute nullable, firstpos, lastpos
            ComputeNodeProperties(augmentedAst);

            // Step 5: Compute followpos with proper disjunction handling
            ComputeFollowPos(augmentedAst);

            // Step 6: Build DFA with lazy attribution and contagion
            var dfa = ConstructLazyDfa(augmentedAst);

            // Step 7: Apply lazy edge trimming to accepting states
            ApplyLazyEdgeTrimming(dfa);

            return dfa;
        }

        // Van Engelen: "mark downstream regex positions in the DFA states as lazy when a parent position is lazy"
        private void MarkLazyPositionsWithContext(RegexExpression ast, bool inLazyContext)
        {
            if (ast == null) return;

            bool currentLazyContext = inLazyContext;

            // Check if this node introduces lazy context
            if (ast is RegexRepeatExpression repeat && repeat.IsLazy)
            {
                currentLazyContext = true;
                // Track the lazy parent for all positions inside
                repeat.Expression?.Visit((p, e, ci, l) => {
                    if (IsLeafNode(e))
                    {
                        _positionToLazyParent[e] = repeat;
                    }
                    return true;
                });
            }

            // Mark leaf nodes if they're in lazy context
            if (IsLeafNode(ast) && currentLazyContext)
            {
                _lazyPositions[ast] = true;
            }

            // Recursively process children with updated context
            switch (ast)
            {
                case RegexBinaryExpression binary:
                    MarkLazyPositionsWithContext(binary.Left, currentLazyContext);
                    MarkLazyPositionsWithContext(binary.Right, currentLazyContext);
                    break;

                case RegexUnaryExpression unary:
                    MarkLazyPositionsWithContext(unary.Expression, currentLazyContext);
                    break;
            }
        }

        private RegexConcatExpression AugmentWithEndMarker(RegexExpression root)
        {
            _endMarker = new RegexTerminatorExpression();
            return new RegexConcatExpression(root, _endMarker);
        }

        private void AssignPositions(RegexExpression node)
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
                    AssignPositions(binary.Left);
                    AssignPositions(binary.Right);
                    break;

                case RegexUnaryExpression unary:
                    AssignPositions(unary.Expression);
                    break;
            }
        }

        private bool IsLeafNode(RegexExpression node)
        {
            return node is RegexLiteralExpression || node is RegexCharsetExpression || node is RegexTerminatorExpression;
        }

        private void ComputeNodeProperties(RegexExpression node)
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

        private void ComputeFollowPos(RegexExpression node)
        {
            foreach (var pos in _positions.Values)
            {
                _followPos[pos] = new HashSet<RegexExpression>();
            }

            ComputeFollowPosRecursive(node);
        }

        private void ComputeFollowPosRecursive(RegexExpression node)
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

                    ComputeFollowPosRecursive(concat.Left);
                    ComputeFollowPosRecursive(concat.Right);
                    break;

                case RegexOrExpression or:
                    // CRITICAL FIX: Handle disjunction properly
                    // For alternation, we don't add followpos rules here
                    // The disjunction is handled in the DFA construction phase
                    // by including both branches in firstpos/lastpos
                    ComputeFollowPosRecursive(or.Left);
                    ComputeFollowPosRecursive(or.Right);
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

                    ComputeFollowPosRecursive(repeat.Expression);
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
        private Dfa ConstructLazyDfa(RegexExpression root)
        {
            var startPositions = root.GetFirstPos();
            var startLazyPositions = GetLazyPositions(startPositions);
            var startState = CreateStateFromPositions(startPositions, startLazyPositions);

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
                    if (pos == _endMarker) continue;

                    for (int i = 0; i < _points.Length; ++i)
                    {
                        var first = _points[i];
                        var last = (i < _points.Length - 1) ? _points[i + 1] - 1 : 0x10ffff;
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
                        if (_followPos.ContainsKey(pos))
                        {
                            nextPositions.UnionWith(_followPos[pos]);
                        }
                    }

                    if (nextPositions.Count == 0) continue;

                    // Van Engelen: "Laziness is contagious" - propagate lazy attribution
                    var nextLazyPositions = PropagatelazyContagion(positions, nextPositions, currentLazyPositions);

                    // CRITICAL: Ensure disjunctive states are properly distinguished
                    var nextKey = new LazyStateKey(nextPositions, nextLazyPositions);
                    Dfa nextState;

                    if (!allStates.ContainsKey(nextKey))
                    {
                        nextState = CreateStateFromPositions(nextPositions, nextLazyPositions);
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
        private HashSet<RegexExpression> GetLazyPositions(HashSet<RegexExpression> positions)
        {
            var lazyPositions = new HashSet<RegexExpression>();
            foreach (var pos in positions)
            {
                if (_lazyPositions.ContainsKey(pos) && _lazyPositions[pos])
                {
                    lazyPositions.Add(pos);
                }
            }
            return lazyPositions;
        }

        // CRITICAL FIX: Van Engelen: "Laziness is contagious" - improved propagation
        private HashSet<RegexExpression> PropagatelazyContagion(
            HashSet<RegexExpression> sourcePositions,
            HashSet<RegexExpression> targetPositions,
            HashSet<RegexExpression> currentLazyPositions)
        {
            var newLazyPositions = new HashSet<RegexExpression>();

            // Start with positions that are inherently lazy
            newLazyPositions.UnionWith(GetLazyPositions(targetPositions));

            // Van Engelen: "propagating laziness along a path"
            foreach (var sourcePos in sourcePositions)
            {
                // If source position is lazy (either inherently or through contagion)
                if (_lazyPositions.ContainsKey(sourcePos) && _lazyPositions[sourcePos] ||
                    currentLazyPositions.Contains(sourcePos))
                {
                    // Propagate laziness to all reachable target positions
                    if (_followPos.ContainsKey(sourcePos))
                    {
                        foreach (var targetPos in _followPos[sourcePos])
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
        private void ApplyLazyEdgeTrimming(Dfa startState)
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
                        TrimLazyEdges(state, lazyPositions);
                    }
                }
            }
        }

        // Van Engelen: Cut "lazy edges" by analyzing forward/backward moves
        private void TrimLazyEdges(Dfa acceptingState, HashSet<RegexExpression> lazyPositions)
        {
            var transitionsToRemove = new List<FATransition>();

            foreach (var transition in acceptingState.Transitions)
            {
                // Check if this transition represents a "lazy edge" that should be trimmed
                // Van Engelen: "we know when DFA edges point forward or backward in the regex string"
                bool isLazyEdge = IsLazyEdgeToTrim(acceptingState, transition.To, lazyPositions);

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
        private bool IsLazyEdgeToTrim(Dfa fromState, Dfa toState, HashSet<RegexExpression> lazyPositions)
        {
            var fromPositions = GetPositionsFromState(fromState);
            var toPositions = GetPositionsFromState(toState);

            // Van Engelen: "taking forward/backward regex moves to regex positions into account"
            // Check if this represents a "backward" move in a lazy context

            foreach (var lazyPos in lazyPositions)
            {
                if (fromPositions.Contains(lazyPos) && _positionToLazyParent.ContainsKey(lazyPos))
                {
                    var lazyParent = _positionToLazyParent[lazyPos];
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

        private Dfa CreateStateFromPositions(HashSet<RegexExpression> positions, HashSet<RegexExpression> lazyPositions)
        {
            var state = new Dfa();

            if (positions.Contains(_endMarker))
            {
                state.Attributes["AcceptSymbol"] = 0;
            }

            state.Attributes["Positions"] = positions.ToList();
            state.Attributes["LazyPositions"] = lazyPositions.ToList();
            return state;
        }

        private HashSet<RegexExpression> GetPositionsFromState(Dfa state)
        {
            var positions = (List<RegexExpression>)state.Attributes["Positions"];
            return new HashSet<RegexExpression>(positions);
        }

        private HashSet<RegexExpression> GetLazyPositionsFromState(Dfa state)
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