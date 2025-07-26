using System;
using System.Collections.Generic;
using System.Linq;

namespace TestFA
{
    // Corrected DirectDfaBuilder with proper followpos computation
    class DirectDfaBuilder
    {
        
        public static Dfa BuildDfa(RegexExpression regexAst)
        {
            RegexExpression _endMarker = new RegexTerminatorExpression();
            var _positions = new Dictionary<int, RegexExpression>();
            var _followPos = new Dictionary<RegexExpression, HashSet<RegexExpression>>();

            // Clear any previous state
            regexAst.ResetDfaInfo();
            _positions.Clear();
            var _positionCounter = 1;
            
            
            // Step 1: Augment with end marker
            var augmentedAst = new RegexConcatExpression(regexAst, _endMarker);

            // Step 2: Assign positions to leaf nodes
            AssignPositions(_positionCounter, _positions, augmentedAst);

            // Step 3: Compute nullable, firstpos, lastpos
            ComputeNodeProperties(augmentedAst);

            // Step 4: Compute followpos
            ComputeFollowPos(_positions,augmentedAst);

            // Step 5: Build DFA
            return ConstructDfa(_endMarker, augmentedAst);
        }


        private static void AssignPositions(int _positionCounter, Dictionary<int, RegexExpression> _positions, RegexExpression? node)
        {
            if (node == null) return;

            // Assign positions to leaf nodes (literals and character classes)
            if (node.IsLeaf)
            {
                if (node.DfaIndex == -1)
                {
                    node.SetDfaIndex(_positionCounter++);
                    _positions[node.DfaIndex] = node;
                }
            } else
            {
                if (node is RegexUnaryExpression unary)
                {
                    AssignPositions(_positionCounter, _positions, unary.Expression);
                }
                else if (node is RegexBinaryExpression binary)
                {
                    AssignPositions(_positionCounter, _positions, binary.Left);
                    AssignPositions(_positionCounter, _positions, binary.Right);
                }
            }
        }


        private static void ComputeNodeProperties(RegexExpression? node)
        {
            if (node == null) return;

            // Post-order traversal - compute children first
            switch (node)
            {
                case RegexConcatExpression concat:
                    ComputeNodeProperties(concat.Left);
                    ComputeNodeProperties(concat.Right);

                    // Concatenation rules
                    var children = new[] { concat.Left, concat.Right }.Where(c => c != null).ToArray();

                    // Nullable: all children must be nullable
                    node.SetNullable(children.All(c => c.IsNullable));

                    // FirstPos: union of firstpos of children until we hit a non-nullable
                    foreach (var child in children)
                    {
                        node.FirstPos.UnionWith(child!.FirstPos);
                        if (!child!.IsNullable)
                            break;
                    }

                    // LastPos: union of lastpos of children from right until we hit a non-nullable
                    for (int i = children.Length - 1; i >= 0; i--)
                    {
                        node?.LastPos.UnionWith(children[i]!.LastPos);
                        if (!children[i]!.IsNullable)
                            break;
                    }
                    break;

                case RegexOrExpression or:
                    ComputeNodeProperties(or.Left);
                    ComputeNodeProperties(or.Right);

                    var orChildren = new[] { or.Left, or.Right }.Where(c => c != null).ToArray();

                    // Nullable: any child can be nullable
                    node.SetNullable(orChildren.Any(c => c!.IsNullable));

                    // FirstPos and LastPos: union of all children
                    foreach (var child in orChildren)
                    {
                        node.FirstPos.UnionWith(child.FirstPos);
                        node.LastPos.UnionWith(child.LastPos);
                    }
                    break;

                case RegexRepeatExpression repeat:
                    ComputeNodeProperties(repeat.Expression);

                    if (repeat.Expression == null) break;

                    // Handle different repeat types
                    if (repeat.MinOccurs <= 0) // *, {0,n}, or ?
                    {
                        node.SetNullable(true);
                    }
                    else // +, {1,n}, {n}
                    {
                        node.SetNullable(repeat.Expression.IsNullable);
                    }

                    node.FirstPos.UnionWith(repeat.Expression.FirstPos);
                    node.LastPos.UnionWith(repeat.Expression.LastPos);
                    break;
                case RegexTerminatorExpression terminator:
                    // End marker is not nullable but gets special treatment
                    node.SetNullable(false);
                    node.FirstPos.Add(node);
                    node?.LastPos.Add(node);
                   
                    break;
                case RegexLiteralExpression literal:
                case RegexCharsetExpression charset:
                    // Leaf nodes are never nullable (except empty literals)
                    if (node is RegexLiteralExpression lit && (lit.Codepoint == -1))
                    {

                        node.SetNullable(true);
                    }
                    else
                    {
                        node.SetNullable(false);
                        node.FirstPos.Add(node);
                        node?.LastPos.Add(node);
                    }
                    break;
            }
        }

        private static void ComputeFollowPos(Dictionary<int, RegexExpression> _positions, RegexExpression node)
        {
            // Initialize followpos sets for all positions
            foreach (var pos in _positions.Values)
            {
                pos.FollowPos.Clear();
            }

            ComputeFollowPosRecursive(node);
        }

        private static void ComputeFollowPosRecursive(RegexExpression node)
        {
            if (node == null) return;

            switch (node)
            {
                case RegexConcatExpression concat:
                    // Rule 1: For concatenation, if i is in lastpos(c1), 
                    // then all positions in firstpos(c2) are in followpos(i)
                    if (concat.Left != null && concat.Right != null)
                    {
                        foreach (var pos in concat.Left.LastPos)
                        {
                            pos.FollowPos.UnionWith(concat.Right.FirstPos);
                            //if (_followPos.ContainsKey(pos))
                            //{
                            //    _followPos[pos].UnionWith(concat.Right.GetFirstPos());
                            //}
                        }
                    }

                    ComputeFollowPosRecursive(concat.Left);
                    ComputeFollowPosRecursive(concat.Right);
                    break;

                case RegexOrExpression or:
                    ComputeFollowPosRecursive(or.Left);
                    ComputeFollowPosRecursive(or.Right);
                    break;

                case RegexRepeatExpression repeat:
                    // Rule 2: For repeat expressions
                    // The key fix: For + and * operators, if i is in lastpos(n), 
                    // then all positions in firstpos(n) are in followpos(i)
                    if (repeat.Expression != null)
                    {
                        // This applies to *, +, and {n,m} where max > 1 or max == -1 (unlimited)
                        bool canRepeat = (repeat.MinOccurs == -1 || repeat.MinOccurs == 0) || // * case
                                        (repeat.MinOccurs == 1 && (repeat.MaxOccurs == -1 || repeat.MaxOccurs == 0)) || // + case  
                                        (repeat.MaxOccurs > 1 || repeat.MaxOccurs == -1); // {n,m} where m > 1

                        if (canRepeat)
                        {
                            foreach (var lastPos in repeat.Expression.LastPos)
                            {
                                lastPos.FollowPos.UnionWith(repeat.Expression.FirstPos);
                                //if (_followPos.ContainsKey(lastPos))
                                //{
                                //    _followPos[lastPos].UnionWith(repeat.Expression.GetFirstPos());
                                //}
                            }
                        }
                    }

                    ComputeFollowPosRecursive(repeat.Expression);
                    break;
            }
        }

        private static Dfa ConstructDfa(RegexExpression _endMarker, RegexExpression root)
        {
            var startState = CreateStateFromPositions(_endMarker, root.FirstPos);
            var unmarkedStates = new Queue<Dfa>();
            var allStates = new Dictionary<string, Dfa>();

            string startKey = GetStateKey(root.FirstPos);
            allStates[startKey] = startState;
            unmarkedStates.Enqueue(startState);

            while (unmarkedStates.Count > 0)
            {
                var currentState = unmarkedStates.Dequeue();
                var currentPositions = GetPositionsFromState(currentState);

                // Group positions by their input symbols
                var transitionMap = new Dictionary<int, HashSet<RegexExpression>>();

                foreach (var pos in currentPositions)
                {
                    if (pos == _endMarker) continue; // Skip end marker

                    var ranges = pos.GetRanges();
                    foreach (var range in ranges)
                    {
                        for (var cp = range.Min; cp <= range.Max; cp++)
                        {
                            HashSet<RegexExpression> hset;
                            if (!transitionMap.TryGetValue(cp, out hset))
                            {
                                hset = new HashSet<RegexExpression>();
                                transitionMap.Add(cp, hset);
                            }
                            hset.Add(pos);
                        }
                    }
                }

                foreach (var transition in transitionMap)
                {
                    var symbol = transition.Key;
                    var positions = transition.Value;

                    // Compute next state as union of followpos for all positions
                    var nextPositions = new HashSet<RegexExpression>();
                    foreach (var pos in positions)
                    {
                        nextPositions.UnionWith(pos.FollowPos);
                    }

                    if (nextPositions.Count == 0) continue;

                    string nextKey = GetStateKey(nextPositions);
                    Dfa nextState;

                    if (!allStates.ContainsKey(nextKey))
                    {
                        nextState = CreateStateFromPositions(_endMarker, nextPositions);
                        allStates[nextKey] = nextState;
                        unmarkedStates.Enqueue(nextState);
                    }
                    else
                    {
                        nextState = allStates[nextKey];
                    }

                    // Add transition
                    currentState.AddTransition(new FATransition(nextState, symbol, symbol));
                }
            }

            return startState;
        }

        private static Dfa CreateStateFromPositions(RegexExpression _endMarker, HashSet<RegexExpression> positions)
        {
            var state = new Dfa();

            // Mark as accept state if it contains the end marker
            if (positions.Contains(_endMarker))
            {
                state.Attributes["IsAccept"] = true;
            }

            // Store positions for reference
            state.Attributes["Positions"] = positions.ToList();

            return state;
        }

        private static HashSet<RegexExpression> GetPositionsFromState(Dfa state)
        {
            var positions = (List<RegexExpression>)state.Attributes["Positions"];
            return new HashSet<RegexExpression>(positions);
        }

        private static string GetStateKey(HashSet<RegexExpression> positions)
        {
            var sorted = positions.Where(p => p.DfaIndex != -1)
                                .OrderBy(p => p.DfaIndex)
                                .Select(p => p.DfaIndex.ToString());
            return string.Join(",", sorted);
        }
    }
}