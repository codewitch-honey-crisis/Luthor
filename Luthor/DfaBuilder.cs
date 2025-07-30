using System;
using System.Collections.Generic;
using System.Linq;

namespace Luthor
{
    /// <summary>
    /// Complete implementation of Dr. Robert van Engelen's lazy DFA construction algorithm
    /// </summary>
    class DfaBuilder
    {
        // Core AST and configuration
        private RegexExpression augmented;
        private readonly RegexExpression root;
        private readonly bool isLexer;
        private readonly int logLevel;

        // Position and state mappings
        private readonly Dictionary<int, RegexExpression> positions = new();
        private readonly Dictionary<RegexExpression, int> positionMap = new();
        private readonly Dictionary<RegexExpression, bool> nullableMap = new();
        private readonly Dictionary<RegexExpression, HashSet<RegexExpression>> firstPosMap = new();
        private readonly Dictionary<RegexExpression, HashSet<RegexExpression>> lastPosMap = new();
        private readonly Dictionary<RegexExpression, HashSet<RegexExpression>> followPos = new();

        // Lazy attribution
        private readonly Dictionary<RegexExpression, bool> lazyPositions = new();

        // Lexer support
        private readonly Dictionary<RegexExpression, int> positionToAcceptSymbol = new();
        private readonly Dictionary<RegexTerminatorExpression, int> endMarkerToAcceptSymbol = new();

        // DFA construction state
        private readonly Dictionary<DfaAttributes, Dfa> allStates = new();
        private readonly Queue<Dfa> unmarkedStates = new();
        private readonly int[] characterPoints;

        private int positionCounter = 1;

        private DfaBuilder(RegexExpression ast, int logLevel = 0)
        {
            this.root = ast.ExpandRepeatingQuantifiers();
            augmented = null;
            this.isLexer = ast is RegexLexerExpression;
            this.logLevel = logLevel;

            // Calculate character point ranges
            var pointSet = new HashSet<int>();
            root.Visit((parent, expression, childIndex, level) =>
            {
                foreach (var range in expression.GetRanges())
                {
                    if (range.Min == -1 || range.Max == -1) continue;
                    pointSet.Add(range.Min);
                    if (range.Max < 0x10ffff)
                        pointSet.Add(range.Max + 1);
                }
                return true;
            });
            pointSet.Add(0);
            characterPoints = pointSet.OrderBy(x => x).ToArray();
        }

        private void Log(int level, string message)
        {
            if (logLevel >= level)
                Console.WriteLine($"[VanEngelen:{level}] {message}");
        }

        private Dfa BuildImpl()
        {
            Log(1, "Starting van Engelen lazy DFA construction");

            // Phase 1: Augment AST with end markers
            var augmentedAst = AugmentWithEndMarkers();
            augmented = augmentedAst;
            // Phase 2: Assign positions to leaf nodes
            AssignPositions(augmentedAst);

            // Phase 3: Mark lazy positions using AST queries
            MarkLazyPositions();

            // Phase 4: Compute node properties (nullable, firstpos, lastpos)
            ComputeNodeProperties(augmentedAst);

            // Phase 5: Compute followpos relationships
            ComputeFollowPos(augmentedAst);

            // Phase 5.5: Modify followpos for lazy completion semantics
            //ModifyLazyFollowPos();

            // Phase 6: Construct DFA with proper lazy semantics
            var startDfa = ConstructLazyAwareDfa();

            // Phase 7: Apply van Engelen lazy edge trimming
            ApplyLazyEdgeTrimming(startDfa);

            Log(1, $"DFA construction complete: {allStates.Count} states");
            return startDfa;
        }

        private RegexExpression AugmentWithEndMarkers()
        {
            Log(2, "Augmenting AST with end markers");

            if (isLexer)
            {
                var lexer = (RegexLexerExpression)root;
                var augmentedRules = new List<RegexExpression>();
                int acceptSymbol = 0;
                Log(2, $"Processing {lexer.Rules.Count} lexer rules");
                for (int i = 0; i < lexer.Rules.Count; i++) { 
                    var rule = lexer.Rules[i];
                    Log(3, $"Rule {i}: {lexer.Rules[i]}");
                    var endMarker = new RegexTerminatorExpression();
                    endMarkerToAcceptSymbol[endMarker] = acceptSymbol;
                    var augmentedRule = new RegexConcatExpression(rule, endMarker);

                    // Mark positions for this rule
                    MarkPositionsWithAcceptSymbol(augmentedRule, acceptSymbol);
                    augmentedRules.Add(augmentedRule);
                    acceptSymbol++;
                }
                var result = new RegexLexerExpression(augmentedRules);
                result.SetSynthesizedPositions();
                return result;
            }
            else
            {
                var endMarker = new RegexTerminatorExpression();
                endMarkerToAcceptSymbol[endMarker] = 0;
                var result = new RegexConcatExpression(root, endMarker);
                result.SetSynthesizedPositions();
                return result;
            }
        }

        private void MarkPositionsWithAcceptSymbol(RegexExpression expr, int acceptSymbol)
        {
            expr.Visit((parent, node, childIndex, level) =>
            {
                if (node.IsLeaf)
                    positionToAcceptSymbol[node] = acceptSymbol;
                return true;
            });
        }

        private void AssignPositions(RegexExpression node)
        {
            if (node == null) return;

            if (node.IsLeaf)
            {
                if (node.GetDfaPosition(positionMap) == -1)
                {
                    node.SetDfaPosition(positionCounter++, positionMap);
                    positions[node.GetDfaPosition(positionMap)] = node;
                    //Log(3, $"Assigned position {positionCounter - 1} to {node}");
                }
                return;
            }

            switch (node)
            {
                case RegexLexerExpression lexer:
                    foreach (var rule in lexer.Rules)
                        AssignPositions(rule);
                    break;
                case RegexBinaryExpression binary:
                    AssignPositions(binary.Left);
                    AssignPositions(binary.Right);
                    break;
                case RegexUnaryExpression unary:
                    AssignPositions(unary.Expression);
                    break;
            }
        }

        private void MarkLazyPositions()
        {
            Log(2, "Marking lazy positions using AST structure");

            foreach (var pos in positions.Values)
            {
                if (pos.IsLeaf)
                {
                    // Use AST query to determine if this position is in a lazy context
                    var lazyAncestor = RegexExpression.GetAncestorLazyRepeat(augmented, pos);
                    lazyPositions[pos] = lazyAncestor != null;

                    //if (lazyPositions[pos])
                    //    Log(3, $"Position {pos} marked as lazy (ancestor: {lazyAncestor})");
                }
            }
        }

        private void ComputeNodeProperties(RegexExpression node)
        {
            if (node == null) return;

            //Log(2, "Computing node properties (nullable, firstpos, lastpos)");

            switch (node)
            {
                case RegexLexerExpression lexer:
                    Log(3, $"Computing properties for lexer with {lexer.Rules.Count} rules");

                    // Compute properties for all rules first
                    foreach (var rule in lexer.Rules)
                        ComputeNodeProperties(rule);

                    // Lexer is nullable if any rule is nullable
                    node.SetNullable(lexer.Rules.Any(r => r.GetNullable(nullableMap)), nullableMap);

                    // Lexer firstpos is union of all rules' firstpos
                    foreach (var rule in lexer.Rules)
                    {
                        Log(3, $"Rule firstpos count: {rule.GetFirstPos(firstPosMap).Count}");
                        node.GetFirstPos(firstPosMap).UnionWith(rule.GetFirstPos(firstPosMap)); // Keep only this one
                    }
                    Log(3, $"Total lexer firstpos count: {node.GetFirstPos(firstPosMap).Count}");

                    break;

                case RegexConcatExpression concat:
                    ComputeNodeProperties(concat.Left);
                    ComputeNodeProperties(concat.Right);

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
                    ComputeNodeProperties(or.Left);
                    ComputeNodeProperties(or.Right);

                    var orChildren = new[] { or.Left, or.Right }.Where(c => c != null).ToArray();
                    node.SetNullable(orChildren.Any(c => c.GetNullable(nullableMap)), nullableMap);

                    foreach (var child in orChildren)
                    {
                        node.GetFirstPos(firstPosMap).UnionWith(child.GetFirstPos(firstPosMap));
                        node.GetLastPos(lastPosMap).UnionWith(child.GetLastPos(lastPosMap));
                    }
                    break;

                case RegexRepeatExpression repeat:
                    ComputeNodeProperties(repeat.Expression);

                    if (repeat.Expression == null) break;

                    if (repeat.MinOccurs <= 0)
                        node.SetNullable(true, nullableMap);
                    else
                        node.SetNullable(repeat.Expression.GetNullable(nullableMap), nullableMap);

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
                        node.SetNullable(true, nullableMap);
                    else
                    {
                        node.SetNullable(false, nullableMap);
                        node.GetFirstPos(firstPosMap).Add(node);
                        node.GetLastPos(lastPosMap).Add(node);
                    }
                    break;
            }
        }

        private void ComputeFollowPos(RegexExpression node)
        {
            Log(2, "Computing followpos relationships");

            // Initialize followpos for all positions
            foreach (var pos in positions.Values)
                followPos[pos] = new HashSet<RegexExpression>();

            ComputeFollowPosRecursive(node);
        }

        private void ComputeFollowPosRecursive(RegexExpression node)
        {
            if (node == null) return;

            switch (node)
            {
                case RegexLexerExpression lexerExpr:
                    foreach (var rule in lexerExpr.Rules)
                        ComputeFollowPosRecursive(rule);
                    break;

                case RegexConcatExpression concat:
                    if (concat.Left != null && concat.Right != null)
                    {
                        var leftLastPos = concat.Left.GetLastPos(lastPosMap);
                        var rightFirstPos = concat.Right.GetFirstPos(firstPosMap);

                        foreach (var pos in leftLastPos)
                        {
                            if (followPos.ContainsKey(pos))
                                followPos[pos].UnionWith(rightFirstPos);
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
                    if (repeat.Expression != null && !repeat.Expression.IsEmptyElement)
                    {
                        bool canRepeat = (repeat.MaxOccurs == -1); // FIXED: was < -1

                        if (canRepeat)
                        {
                            var repeatLastPos = repeat.Expression.GetLastPos(lastPosMap);
                            var repeatFirstPos = repeat.Expression.GetFirstPos(firstPosMap);

                            foreach (var lastPos in repeatLastPos)
                            {
                                if (followPos.ContainsKey(lastPos))
                                    followPos[lastPos].UnionWith(repeatFirstPos);
                            }
                        }
                    }

                    ComputeFollowPosRecursive(repeat.Expression);
                    break;
            }
        }

        private Dfa ConstructLazyAwareDfa()
        {
            Log(2, "Constructing lazy-aware DFA with proper state semantics");

            var startPositions = augmented.GetFirstPos(firstPosMap);
            
            Log(2, $"Start state has {startPositions.Count} positions");
            // CRITICAL FIX: If root is nullable, start state should include end marker
            if (augmented.GetNullable(nullableMap))
            {
                foreach (var endMarker in endMarkerToAcceptSymbol.Keys)
                {
                    startPositions.Add(endMarker);
                }
            }

            var startLazyPositions = GetLazyPositions(startPositions);

            var startState = CreateSemanticState(startPositions, startLazyPositions, "start");
            allStates[startState.Attributes] = startState;
            unmarkedStates.Enqueue(startState);

            while (unmarkedStates.Count > 0)
            {
                var currentState = unmarkedStates.Dequeue();
                ProcessStateTransitions(currentState);
            }

            return startState;
        }

        private void ProcessStateTransitions(Dfa currentState)
        {
            var currentPositions = GetPositionsFromState(currentState);
            var currentLazyPositions = GetLazyPositionsFromState(currentState);
            var currentContext = GetSemanticContext(currentState);

             var transitionRanges = BuildTransitionRanges(currentPositions);

            foreach (var (range, positions) in transitionRanges)
            {
                CreateSemanticTransitions(currentState, range, positions, currentLazyPositions, currentContext);
            }
        }

        private Dictionary<DfaRange, HashSet<RegexExpression>> BuildTransitionRanges(HashSet<RegexExpression> positions)
        {
            var transitionMap = new Dictionary<DfaRange, HashSet<RegexExpression>>();

            foreach (var pos in positions)
            {
                if (pos is RegexTerminatorExpression || pos is RegexAnchorExpression)
                    continue;

                foreach (var range in GetRangesForPosition(pos))
                {
                    if (!transitionMap.TryGetValue(range, out var posSet))
                    {
                        posSet = new HashSet<RegexExpression>();
                        transitionMap[range] = posSet;
                    }
                    posSet.Add(pos);
                }
            }

            return transitionMap;
        }

        private IEnumerable<DfaRange> GetRangesForPosition(RegexExpression pos)
        {
            for (int i = 0; i < characterPoints.Length; ++i)
            {
                var first = characterPoints[i];
                var last = (i < characterPoints.Length - 1) ? characterPoints[i + 1] - 1 : 0x10ffff;
                var range = new DfaRange(first, last);

                if (PositionMatchesRange(pos, range))
                    yield return range;
            }
        }
     

        private bool PositionMatchesRange(RegexExpression pos, DfaRange range)
        {
            return pos.GetRanges().Any(r => r.Intersects(range));
        }

        private void CreateSemanticTransitions(Dfa fromState, DfaRange range,
    HashSet<RegexExpression> triggerPositions, HashSet<RegexExpression> currentLazyPositions, string currentContext)
        {
            // Separate triggers by lazy context
            var lazyTriggers = triggerPositions.Where(p => IsPositionLazy(p)).ToHashSet();
            var nonLazyTriggers = triggerPositions.Where(p => !IsPositionLazy(p)).ToHashSet();

            // Remove the hasConflict logic entirely and just do:
            var allTriggers = lazyTriggers.Union(nonLazyTriggers).ToHashSet();
            var semanticContext = lazyTriggers.Any() ? "lazy_continue" : (nonLazyTriggers.Any() ? "advance" : "normal");
            CreateSingleSemanticTransition(fromState, range, allTriggers, currentLazyPositions, semanticContext);

            // CONFLICT DETECTION: Both lazy and non-lazy triggers for same range
            bool hasConflict = lazyTriggers.Any() && nonLazyTriggers.Any();

        }

        private void CreateSingleSemanticTransition(Dfa fromState, DfaRange range,
            HashSet<RegexExpression> triggers, HashSet<RegexExpression> currentLazyPositions, string semanticContext)
        {
            // Calculate next positions
            var nextPositions = new HashSet<RegexExpression>();
            foreach (var trigger in triggers)
            {
                if (followPos.TryGetValue(trigger, out var followSet))
                    nextPositions.UnionWith(followSet);
            }
            foreach (var trigger in triggers)
            {
                if (followPos.TryGetValue(trigger, out var followSet))
                {
                    nextPositions.UnionWith(followSet);
                }
            }
            if (nextPositions.Count == 0) return;

            // Handle anchors
            var anchorsToProcess = nextPositions.Where(p => p is RegexAnchorExpression).ToList();
            foreach (var anchor in anchorsToProcess)
            {
                if (followPos.ContainsKey(anchor))
                    nextPositions.UnionWith(followPos[anchor]);
            }


            // Propagate lazy contagion
            var nextLazyPositions = PropagatelazyContagion(triggers, nextPositions, currentLazyPositions);

            // Create target state with semantic context
            var targetState = CreateSemanticState(nextPositions, nextLazyPositions, semanticContext);

            // Check for existing state with same semantics
            var existingState = FindOrCreateState(targetState);

            // Create transition
            var transition = new DfaTransition(existingState, range.Min, range.Max);
            transition.Attributes = new DfaAttributes();
            transition.Attributes["SemanticContext"] = semanticContext;
            transition.Attributes["IsLazy"] = semanticContext.Contains("lazy");

            fromState.AddTransition(transition);

        }

        private Dfa CreateSemanticState(HashSet<RegexExpression> positions,
            HashSet<RegexExpression> lazyPositions, string semanticContext)
        {
            var state = new Dfa();

            // Handle anchor mask
            int anchorMask = 0;
            const int START_ANCHOR = 1;
            const int END_ANCHOR = 2;

            bool hasAnchors = false;
            foreach (var pos in positions)
            {
                if (pos is RegexAnchorExpression anchor)
                {
                    hasAnchors = true;
                    switch (anchor.Type)
                    {
                        case RegexAnchorType.LineStart:
                            anchorMask |= START_ANCHOR;
                            break;
                        case RegexAnchorType.LineEnd:
                            anchorMask |= END_ANCHOR;
                            break;
                    }
                }
            }

            if (anchorMask != 0)
                state.Attributes["AnchorMask"] = anchorMask;

            // Check for acceptance
            int? acceptSymbol = null;
            foreach (var pos in positions)
            {
                if (pos is RegexTerminatorExpression endMarker && endMarkerToAcceptSymbol.TryGetValue(endMarker, out var symbol))
                {
                    if (!acceptSymbol.HasValue || symbol < acceptSymbol.Value)
                        acceptSymbol = symbol;
                }
            }

            // Check for anchor-based acceptance
            if (!acceptSymbol.HasValue && hasAnchors)
            {
                foreach (var pos in positions)
                {
                    if (pos is RegexAnchorExpression anchor &&
                        anchor.Type == RegexAnchorType.LineEnd &&
                        positionToAcceptSymbol.ContainsKey(pos))
                    {
                        acceptSymbol = positionToAcceptSymbol[pos];
                    }
                }
            }

            if (acceptSymbol.HasValue)
                state.Attributes["AcceptSymbol"] = acceptSymbol.Value;

            // Core state identity
            state.Attributes["Positions"] = positions.OrderBy(p => p.GetDfaPosition(positionMap)).ToList();
            state.Attributes["LazyPositions"] = lazyPositions.OrderBy(p => p.GetDfaPosition(positionMap)).ToList();

            // Semantic identity - this prevents unwanted state collapse
            state.Attributes["SemanticContext"] = semanticContext;

            return state;
        }

        private Dfa FindOrCreateState(Dfa candidateState)
        {
            var existingState = allStates.Values.FirstOrDefault(s => s.Attributes.Equals(candidateState.Attributes));

            if (existingState == null)
            {
                allStates[candidateState.Attributes] = candidateState;
                unmarkedStates.Enqueue(candidateState);
                return candidateState;
            }
            else
            {
                return existingState;
            }
        }

        private HashSet<RegexExpression> PropagatelazyContagion(HashSet<RegexExpression> sourcePositions,
            HashSet<RegexExpression> targetPositions, HashSet<RegexExpression> currentLazyPositions)
        {
            var newLazyPositions = new HashSet<RegexExpression>();

            // Include inherently lazy positions
            foreach (var pos in targetPositions)
            {
                if (IsPositionLazy(pos))
                    newLazyPositions.Add(pos);
            }

            // Propagate contagion within same lazy quantifier scope
            foreach (var sourcePos in sourcePositions)
            {
                if (currentLazyPositions.Contains(sourcePos) || IsPositionLazy(sourcePos))
                {
                    var sourceLazyAncestor = RegexExpression.GetAncestorLazyRepeat(augmented, sourcePos);

                    foreach (var targetPos in targetPositions)
                    {
                        var targetLazyAncestor = RegexExpression.GetAncestorLazyRepeat(augmented, targetPos);

                        // Propagate within same lazy scope
                        if (sourceLazyAncestor != null && sourceLazyAncestor == targetLazyAncestor)
                            newLazyPositions.Add(targetPos);
                    }
                }
            }

            return newLazyPositions;
        }
        private void ModifyLazyFollowPos()
        {
            Log(2, "Modifying followpos for lazy completion semantics");

            var completionPositions = FindCompletionPositions();

            // Make completion positions dead-ends (only go to end marker)
            foreach (var completionPos in completionPositions)
            {
                if (followPos.ContainsKey(completionPos))
                {
                    var originalFollowPos = new HashSet<RegexExpression>(followPos[completionPos]);
                    followPos[completionPos].Clear();

                    // Only allow transitions to end markers (completion of match)
                    foreach (var pos in originalFollowPos)
                    {
                        if (pos is RegexTerminatorExpression)
                        {
                            followPos[completionPos].Add(pos);
                        }
                    }

                    Log(3, $"Made completion position {completionPos} into dead-end (was: {string.Join(", ", originalFollowPos)})");
                }
            }

            // Remove inner lazy loops that cause greedy behavior
            RemoveInnerLazyLoops();
        }

        private HashSet<RegexExpression> FindCompletionPositions()
        {
            var completionPositions = new HashSet<RegexExpression>();

            Log(3, "Searching for completion positions after lazy quantifiers");

            augmented.Visit((parent, node, childIndex, level) =>
            {
                if (node is RegexConcatExpression concat &&
                    concat.Left is RegexRepeatExpression repeat && repeat.IsLazy)
                {
                    // The first position(s) of what comes after the lazy quantifier
                    // represent "minimal match completed, should stop here"
                    var rightFirstPos = concat.Right?.GetFirstPos(firstPosMap) ?? new HashSet<RegexExpression>();

                    foreach (var pos in rightFirstPos)
                    {
                        completionPositions.Add(pos);
                        Log(3, $"Found completion position {pos} after lazy quantifier {repeat}");
                    }
                }
                return true;
            });

            return completionPositions;
        }

        private void RemoveInnerLazyLoops()
        {
            Log(3, "Removing inner lazy loops that cause greedy behavior");

            var positionsToModify = new List<(RegexExpression from, RegexExpression to)>();

            // Find all followpos relationships that are inner loops within lazy quantifiers
            foreach (var kvp in followPos)
            {
                var fromPos = kvp.Key;
                var followSet = kvp.Value;

                foreach (var toPos in followSet.ToList()) // ToList to avoid modification during iteration
                {
                    if (IsInnerLazyLoop(fromPos, toPos))
                    {
                        positionsToModify.Add((fromPos, toPos));
                        Log(3, $"Marking inner lazy loop for removal: {fromPos} -> {toPos}");
                    }
                }
            }

            // Remove the problematic inner loops
            foreach (var (fromPos, toPos) in positionsToModify)
            {
                followPos[fromPos].Remove(toPos);
                Log(3, $"Removed inner lazy loop: {fromPos} -> {toPos}");
            }
        }

        private bool IsInnerLazyLoop(RegexExpression fromPos, RegexExpression toPos)
        {
            var fromLazyAncestor = RegexExpression.GetAncestorLazyRepeat(augmented, fromPos);
            var toLazyAncestor = RegexExpression.GetAncestorLazyRepeat(augmented, toPos);

            // If both positions are inside the same lazy quantifier, this is an inner loop
            // that could cause greedy behavior - we want to eliminate these
            if (fromLazyAncestor != null && fromLazyAncestor == toLazyAncestor)
            {
                // Additional check: is this a loop-back within the quantifier?
                var ancestorLastPos = fromLazyAncestor.Expression?.GetLastPos(lastPosMap) ?? new HashSet<RegexExpression>();
                var ancestorFirstPos = fromLazyAncestor.Expression?.GetFirstPos(firstPosMap) ?? new HashSet<RegexExpression>();

                // This is a problematic inner loop if it goes from end back to beginning of same lazy quantifier
                return ancestorLastPos.Contains(fromPos) && ancestorFirstPos.Contains(toPos);
            }

            return false;
        }
        private void ApplyLazyEdgeTrimming(Dfa startState)
        {
            Log(2, "Applying van Engelen lazy edge trimming");

            var closure = startState.FillClosure();
            int trimmedCount = 0;

            foreach (var state in closure)
            {
                if (!state.IsAccept) continue;

                var transitionsToRemove = new List<DfaTransition>();
                var statePositions = GetPositionsFromState(state);

                foreach (var transition in state.Transitions)
                {
                    bool shouldTrim = false;

                    // Van Engelen: Only trim true backward edges (lastpos -> firstpos of SAME lazy quantifier)
                    // Don't trim forward bypasses or necessary paths!

                    var fromPositions = GetPositionsFromState(state);
                    var targetPositions = GetPositionsFromState(transition.To);

                    // MUCH more restrictive: only trim if this is clearly a loop-back within same quantifier
                    foreach (var fromPos in fromPositions)
                    {
                        var lazyAncestor = RegexExpression.GetAncestorLazyRepeat(augmented, fromPos);
                        if (lazyAncestor == null) continue;

                        var ancestorLastPos = lazyAncestor.Expression?.GetLastPos(lastPosMap) ?? new HashSet<RegexExpression>();
                        var ancestorFirstPos = lazyAncestor.Expression?.GetFirstPos(firstPosMap) ?? new HashSet<RegexExpression>();

                        // Only trim if this is REALLY a backward move AND transition target contains loop-back
                        if (ancestorLastPos.Contains(fromPos) &&
                            targetPositions.Intersect(ancestorFirstPos).Any() &&
                            targetPositions.Count == ancestorFirstPos.Count) // Much stricter!
                        {
                            shouldTrim = true;
                            Log(3, $"TRUE BACKWARD EDGE: {transition}");
                            break;
                        }
                    }

                    if (shouldTrim)
                        transitionsToRemove.Add(transition);
                }

                foreach (var transition in transitionsToRemove)
                    state.RemoveTransition(transition);
            }

            Log(2, $"Trimmed {trimmedCount} lazy backward edges from accepting states");
        }
        
        private bool IsLazyBackwardEdge(HashSet<RegexExpression> fromPositions, DfaTransition transition)
        {
            var targetPositions = GetPositionsFromState(transition.To);
            
            foreach (var fromPos in fromPositions)
            {
                var lazyAncestor = RegexExpression.GetAncestorLazyRepeat(augmented, fromPos);
                if (lazyAncestor == null) continue;

                var ancestorFirstPos = lazyAncestor.Expression?.GetFirstPos(firstPosMap) ?? new HashSet<RegexExpression>();
                var ancestorLastPos = lazyAncestor.Expression?.GetLastPos(lastPosMap) ?? new HashSet<RegexExpression>();

                // This is a backward edge if: lastpos -> firstpos of same lazy quantifier
                if (ancestorLastPos.Contains(fromPos) && targetPositions.Intersect(ancestorFirstPos).Any())
                    return true;
            }

            return false;
        }

        // Helper methods
        private bool IsPositionLazy(RegexExpression pos) => lazyPositions.TryGetValue(pos, out var lazy) && lazy;

        private HashSet<RegexExpression> GetLazyPositions(HashSet<RegexExpression> positions) =>
            positions.Where(IsPositionLazy).ToHashSet();

        private HashSet<RegexExpression> GetPositionsFromState(Dfa state) =>
            ((List<RegexExpression>)state.Attributes["Positions"]).ToHashSet();

        private HashSet<RegexExpression> GetLazyPositionsFromState(Dfa state) =>
            state.Attributes.TryGetValue("LazyPositions", out var lazy)
                ? ((List<RegexExpression>)lazy).ToHashSet()
                : new HashSet<RegexExpression>();

        private string GetSemanticContext(Dfa state) =>
            state.Attributes.TryGetValue("SemanticContext", out var context) ? (string)context : "unknown";
        public static Dfa BuildDfa(RegexExpression ast)
        {
            var impl = new DfaBuilder(ast,0);
            var result = impl.BuildImpl();
            return result;
        }
    }
}