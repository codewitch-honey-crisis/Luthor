using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace TestFA
{
    
    // Corrected DirectDfaBuilder with proper followpos computation
    class DirectDfaBuilder
    {
        private int _positionCounter = 1;
        private RegexExpression _endMarker;
        private int[] _points;
        private Dictionary<int, RegexExpression> _positions = new Dictionary<int, RegexExpression>();
        private Dictionary<RegexExpression, HashSet<RegexExpression>> _followPos =
            new Dictionary<RegexExpression, HashSet<RegexExpression>>();
        // Add these to your class fields
        private Dictionary<int, List<FACaptureEvent>> _captureEvents = new Dictionary<int, List<FACaptureEvent>>();
        private int _groupCounter = 1; // Start at 1 since group 0 is the entire match


        public Dfa BuildDfa(RegexExpression regexAst)
        {
            var p = new HashSet<int>();
            _groupCounter = 1;
            _captureEvents.Clear();
            regexAst.Visit((parent, expression, childIndex, level) => {
                foreach(var range in expression.GetRanges())
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
            _positionCounter = 1;

            // Step 1: Augment with end marker
            var augmentedAst = AugmentWithEndMarker(regexAst);

            // Step 2: Assign positions to leaf nodes
            AssignPositions(augmentedAst);

            // Step 3: Compute nullable, firstpos, lastpos
            ComputeNodeProperties(augmentedAst);

            // Step 4: Compute followpos
            ComputeFollowPos(augmentedAst);

            // Step 5: Assign capture events 
            AssignCaptureEvents(augmentedAst);

            // Step 6: Build DFA
            return ConstructDfa(augmentedAst);
        }

        private RegexConcatExpression AugmentWithEndMarker(RegexExpression root)
        {
            // Create end marker - using RegexLiteralExpression with special marker
            _endMarker = new RegexTerminatorExpression();
            return new RegexConcatExpression(root, _endMarker);
        }
        private void AddCaptureEvent(int position, int groupNumber, string groupName, bool isStart)
        {
            if (position == -1) return;

            if (!_captureEvents.ContainsKey(position))
                _captureEvents[position] = new List<FACaptureEvent>();

            // Check for duplicates
            var existing = _captureEvents[position].FirstOrDefault(e =>
                e.IsStart == isStart && e.Index == groupNumber);
            if (existing == null)
            {
                _captureEvents[position].Add(new FACaptureEvent
                {
                    Name = groupName,
                    IsStart = isStart,
                    Index = groupNumber
                });
            }
        }
        // Separate method for capture event assignment
        private void AssignCaptureEvents(RegexExpression node)
        {
            if (node == null) return;

            switch (node)
            {
                case RegexCaptureExpression capture:
                    int groupNumber = _groupCounter++;

                    
                    // START events: assign to firstpos (this is correct)
                    foreach (var startPos in capture.GetFirstPos())
                    {
                        AddCaptureEvent(startPos.GetDfaPosition(), groupNumber, capture.NamedGroup, true);
                    }

                    // END events: assign to lastpos positions themselves
                    // The key insight: END should trigger when we COMPLETE consuming a lastpos position
                    foreach (var lastPos in capture.GetLastPos())
                    {
                        AddCaptureEvent(lastPos.GetDfaPosition(), groupNumber, capture.NamedGroup, false);
                    }

                    AssignCaptureEvents(capture.Expression);
                    break;
                                    
                case RegexBinaryExpression binary:
                    AssignCaptureEvents(binary.Left);
                    AssignCaptureEvents(binary.Right);
                    break;

                case RegexUnaryExpression unary:
                    AssignCaptureEvents(unary.Expression);
                    break;
            }
        }
        private void AssignPositions(RegexExpression node)
        {
            if (node == null) return;

            // Assign positions to leaf nodes (literals and character classes)
            if (IsLeafNode(node))
            {
                if (node.GetDfaPosition() == -1)
                {
                    node.SetDfaPosition(_positionCounter++);
                    _positions[node.GetDfaPosition()] = node;
                }
                // DEBUG: Show what each position represents
                if (node is RegexLiteralExpression lit)
                {
                    //Console.WriteLine($"Position {node.GetDfaPosition()}: Literal '{(char)lit.Codepoint}'");
                }
                else if (node is RegexTerminatorExpression)
                {
                    //Console.WriteLine($"Position {node.GetDfaPosition()}: END MARKER");
                }
                else
                {
                   // Console.WriteLine($"Position {node.GetDfaPosition()}: {node.GetType().Name}");
                }
                return;
            }


            // Recursively process child nodes
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

            // Post-order traversal - compute children first
            switch (node)
            {
                case RegexConcatExpression concat:
                    ComputeNodeProperties(concat.Left);
                    ComputeNodeProperties(concat.Right);

                    // Concatenation rules
                    var children = new[] { concat.Left, concat.Right }.Where(c => c != null).ToArray();

                    // Nullable: all children must be nullable
                    node.SetNullable(children.All(c => c.GetNullable()));

                    // FirstPos: union of firstpos of children until we hit a non-nullable
                    foreach (var child in children)
                    {
                        node.GetFirstPos().UnionWith(child.GetFirstPos());
                        if (!child.GetNullable())
                            break;
                    }

                    // LastPos: union of lastpos of children from right until we hit a non-nullable
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

                    // Nullable: any child can be nullable
                    node.SetNullable(orChildren.Any(c => c.GetNullable()));

                    // FirstPos and LastPos: union of all children
                    foreach (var child in orChildren)
                    {
                        node.GetFirstPos().UnionWith(child.GetFirstPos());
                        node.GetLastPos().UnionWith(child.GetLastPos());
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
                        node.SetNullable(repeat.Expression.GetNullable());
                    }

                    node.GetFirstPos().UnionWith(repeat.Expression.GetFirstPos());
                    node.GetLastPos().UnionWith(repeat.Expression.GetLastPos());
                    break;
                case RegexCaptureExpression capture:
                    ComputeNodeProperties(capture.Expression);

                    if (capture.Expression == null || capture.Expression.IsEmptyElement) break;

                    
                    node.SetNullable(capture.Expression.GetNullable());
                    

                    node.GetFirstPos().UnionWith(capture.Expression.GetFirstPos());
                    node.GetLastPos().UnionWith(capture.Expression.GetLastPos());
                    break;
                case RegexTerminatorExpression terminator:

                case RegexLiteralExpression literal:
                    // End marker is not nullable but gets special treatment
                    node.SetNullable(false);
                    node.GetFirstPos().Add(node);
                    node.GetLastPos().Add(node);
                    break;
                case RegexCharsetExpression charset:
                    // Leaf nodes are never nullable (except empty literals)
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
            // Initialize followpos sets for all positions
            foreach (var pos in _positions.Values)
            {
                _followPos[pos] = new HashSet<RegexExpression>();
                // And also add this in the followpos computation debug:
                Console.WriteLine($"    Position {pos.GetDfaPosition()} followpos:");
                foreach (var followPos in _followPos[pos])
                {
                    Console.WriteLine($"      → {followPos.GetDfaPosition()} ({followPos.GetType().Name})");
                    if (followPos is RegexLiteralExpression flit)
                    {
                        Console.WriteLine($"          '{(char)flit.Codepoint}' (HashCode: {followPos.GetHashCode()})");
                    }
                }
            }

            ComputeFollowPosRecursive(node);


        }

        private void ComputeFollowPosRecursive(RegexExpression node)
        {
            if (node == null) return;

            switch (node)
            {
                case RegexConcatExpression concat:
                    // Rule 1: For concatenation, if i is in lastpos(c1), 
                    // then all positions in firstpos(c2) are in followpos(i)
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
                    ComputeFollowPosRecursive(or.Left);
                    ComputeFollowPosRecursive(or.Right);
                    break;

                case RegexRepeatExpression repeat:
                    // Rule 2: For repeat expressions
                    // The key fix: For + and * operators, if i is in lastpos(n), 
                    // then all positions in firstpos(n) are in followpos(i)
                    if (repeat.Expression != null && !repeat.IsEmptyElement)
                    {
                        // This applies to *, +, and {n,m} where max > 1 or max == -1 (unlimited)
                        bool canRepeat = (repeat.MinOccurs == -1 || repeat.MinOccurs == 0) || // * case
                                        (repeat.MinOccurs == 1 && (repeat.MaxOccurs == -1 || repeat.MaxOccurs == 0)) || // + case  
                                        (repeat.MaxOccurs > 1 || repeat.MaxOccurs == -1); // {n,m} where m > 1

                        if (canRepeat)
                        {
                            foreach (var lastPos in repeat.Expression.GetLastPos())
                            {
                                if (_followPos.ContainsKey(lastPos))
                                {
                                    _followPos[lastPos].UnionWith(repeat.Expression.GetFirstPos());
                                }
                            }
                        }
                    }

                    ComputeFollowPosRecursive(repeat.Expression);
                    break;
                case RegexCaptureExpression capture:
                    
                    ComputeFollowPosRecursive(capture.Expression);
                    break;
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
        private Dfa ConstructDfa(RegexExpression root)
        {
            var startState = CreateStateFromPositions(root.GetFirstPos());
            var unmarkedStates = new Queue<Dfa>();
            var allStates = new Dictionary<string, Dfa>();

            var startKey = GetStateKey(root.GetFirstPos());
            allStates[startKey] = startState;
            unmarkedStates.Enqueue(startState);

            //Console.WriteLine($"DEBUG: Start state key: {startKey}");

            while (unmarkedStates.Count > 0)
            {
                var currentState = unmarkedStates.Dequeue();
                var currentPositions = GetPositionsFromState(currentState);
                var currentKey = GetStateKey(currentPositions);

               // Console.WriteLine($"\nDEBUG: Processing state [{currentKey}]");

                // Group positions by their input symbols
                var transitionMap = new Dictionary<FARange, HashSet<RegexExpression>>();

                foreach (var pos in currentPositions)
                {
                    if (pos == _endMarker) continue; // Skip end marker

                    for (int i = 0; i < _points.Length; ++i)
                    {
                        var first = _points[i];
                        var last = (i < _points.Length - 1) ? _points[i + 1] - 1 : 0x10ffff;
                        var range = new FARange(first, last);

                        // Only add this position if it matches this range
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
                foreach (var pos in currentPositions)
                {
                    if (pos == _endMarker) continue;

                    //Console.WriteLine($"    Position {pos.GetDfaPosition()}: {pos.GetType().Name}");
                    //if (pos is RegexLiteralExpression lit)
                    //{
                    //    Console.WriteLine($"      Character: '{(char)lit.Codepoint}'");
                    //    Console.WriteLine($"      HashCode: {pos.GetHashCode()}");
                    //    Console.WriteLine($"      Object Identity: {pos.GetHashCode()}");
                    //}
                }
                foreach (var transition in transitionMap)
                {
                    var range = transition.Key;
                    var positions = transition.Value;

                    //Console.WriteLine($"  On range [{range.Min}-{range.Max}] from positions [{string.Join(",", positions.Select(p => p.GetDfaPosition()))}]:");

                    // Compute next state as union of followpos for all positions
                    var nextPositions = new HashSet<RegexExpression>();
                    foreach (var pos in positions)
                    {
                        if (_followPos.ContainsKey(pos))
                        {
                            //Console.WriteLine($"    Position {pos.GetDfaPosition()} followpos: [{string.Join(",", _followPos[pos].Select(p => p.GetDfaPosition()))}]");
                            nextPositions.UnionWith(_followPos[pos]);
                        }
                    }

                    if (nextPositions.Count == 0) continue;

                    string nextKey = GetStateKey(nextPositions);
                    //Console.WriteLine($"    → Next state: [{nextKey}]");

                    Dfa nextState;

                    if (!allStates.ContainsKey(nextKey))
                    {
                        nextState = CreateStateFromPositions(nextPositions);
                        allStates[nextKey] = nextState;
                        unmarkedStates.Enqueue(nextState);
                    }
                    else
                    {
                        nextState = allStates[nextKey];
                    }

                    // Add transition
                    currentState.AddTransition(new FATransition(nextState, range.Min, range.Max));
                }
            }

            // remove dead transitions (destinations with no accept)
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
                            found = true; break;
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
        private Dfa ConstructDfa2(RegexExpression root)
        {
            var startState = CreateStateFromPositions(root.GetFirstPos());
            var unmarkedStates = new Queue<Dfa>();
            var allStates = new Dictionary<string, Dfa>();
            
            var startKey = GetStateKey(root.GetFirstPos());
            allStates[startKey] = startState;
            unmarkedStates.Enqueue(startState);

            while (unmarkedStates.Count > 0)
            {
                var currentState = unmarkedStates.Dequeue();
                var currentPositions = GetPositionsFromState(currentState);

                // Group positions by their input symbols
                var transitionMap = new Dictionary<FARange, HashSet<RegexExpression>>();

                foreach (var pos in currentPositions)
                {
                    if (pos == _endMarker) continue; // Skip end marker

                    for (int i = 0; i < _points.Length; ++i)
                    {
                        var first = _points[i];
                        var last = (i < _points.Length - 1) ? _points[i + 1] - 1 : 0x10ffff;
                        var range = new FARange(first, last);

                        // Only add this position if it matches this range
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

                foreach (var transition in transitionMap)
                {
                    var range = transition.Key;
                    var positions = transition.Value;

                    // Compute next state as union of followpos for all positions
                    var nextPositions = new HashSet<RegexExpression>();
                    foreach (var pos in positions)
                    {
                        if (_followPos.ContainsKey(pos))
                        {
                            nextPositions.UnionWith(_followPos[pos]);
                        }
                    }

                    if (nextPositions.Count == 0) continue;

                    string nextKey = GetStateKey(nextPositions);
                    Dfa nextState;

                    if (!allStates.ContainsKey(nextKey))
                    {
                        nextState = CreateStateFromPositions(nextPositions);
                        allStates[nextKey] = nextState;
                        unmarkedStates.Enqueue(nextState);
                    }
                    else
                    {
                        nextState = allStates[nextKey];
                    }

                    // Add transition
                    currentState.AddTransition(new FATransition(nextState, range.Min, range.Max));
                }
            }
            // remove dead transitions (destinations with no accept)
            foreach (var ffa in startState.FillClosure())
            {
                var itrns = new List<FATransition>(ffa.Transitions);
                foreach (var trns in itrns)
                {
                    var found = false;
                    foreach(var tto in trns.To.FillClosure())
                    {
                        if(tto.IsAccept)
                        {
                            found = true; break;
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

        private Dfa CreateStateFromPositions(HashSet<RegexExpression> positions)
        {
            var state = new Dfa();

            // Mark as accept state if it contains the end marker
            if (positions.Contains(_endMarker))
            {
                state.Attributes["IsAccept"] = true;
            }

            // Store positions for reference
            state.Attributes["Positions"] = positions.ToList();

            // Collect all capture events for positions in this state
            var captureStartsSet = new HashSet<(int groupNumber, string groupName)>();
            var captureEndsSet = new HashSet<(int groupNumber, string groupName)>();

            foreach (var pos in positions)
            {
                if (pos.GetDfaPosition() != -1 && _captureEvents.ContainsKey(pos.GetDfaPosition()))
                {
                    foreach (var captureEvent in _captureEvents[pos.GetDfaPosition()])
                    {
                        if (captureEvent.IsStart)
                        {
                            captureStartsSet.Add((captureEvent.Index, captureEvent.Name));
                        }
                        else
                        {
                            captureEndsSet.Add((captureEvent.Index, captureEvent.Name));
                        }
                    }
                }
            }

            // Convert sets back to lists for storage
            var captureStarts = captureStartsSet.Select(x => new FACaptureEvent
            {
                Index = x.groupNumber,
                Name = x.groupName,
                IsStart = true
            }).ToList();

            var captureEnds = captureEndsSet.Select(x => new FACaptureEvent
            {
                Index = x.groupNumber,
                Name = x.groupName,
                IsStart = false
            }).ToList();
            foreach(var captureEvent in captureStarts)
            {
                state.CaptureStarts.Add(captureEvent);
            }
            foreach (var captureEvent in captureEnds)
            {
                state.CaptureEnds.Add(captureEvent);
            }
            if (captureStarts.Any() || captureEnds.Any())
            {
                Console.WriteLine($"State with positions [{string.Join(",", positions.Select(p => p.GetDfaPosition()))}]:");
                foreach (var start in captureStarts)
                    Console.WriteLine($"  START: Group {start.Index} '{start.Name}'");
                foreach (var end in captureEnds)
                    Console.WriteLine($"  END: Group {end.Index} '{end.Name}'");
            }
            return state;


        }

        private HashSet<RegexExpression> GetPositionsFromState(Dfa state)
        {
            var positions = (List<RegexExpression>)state.Attributes["Positions"];
            return new HashSet<RegexExpression>(positions);
        }

        private string GetStateKey(HashSet<RegexExpression> positions)
        {
            var sorted = positions.Where(p => p.GetDfaPosition() != -1)
                                .OrderBy(p => p.GetDfaPosition())
                                .Select(p => p.GetDfaPosition().ToString());
            return string.Join(",", sorted);
        }
    }
}