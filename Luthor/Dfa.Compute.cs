using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Luthor
{
    partial class Dfa
    {
        #region _FList
        private sealed class _FListNode
        {
            public _FListNode(Dfa q, _FList sl)
            {
                State = q;
                StateList = sl;
                if (sl.Count++ == 0)
                {
                    sl.First = sl.Last = this;
                }
                else
                {
                    System.Diagnostics.Debug.Assert(sl.Last != null);
                    sl.Last.Next = this;
                    Prev = sl.Last;
                    sl.Last = this;

                }
            }

            public _FListNode Next { get; private set; }

            private _FListNode Prev { get; set; }

            public _FList StateList { get; private set; }

            public Dfa State { get; private set; }

            public void Remove()
            {
                System.Diagnostics.Debug.Assert(StateList != null);
                StateList.Count--;
                if (StateList.First == this)
                {
                    StateList.First = Next;
                }
                else
                {
                    System.Diagnostics.Debug.Assert(Prev != null);
                    Prev.Next = Next;
                }

                if (StateList.Last == this)
                {
                    StateList.Last = Prev;
                }
                else
                {
                    System.Diagnostics.Debug.Assert(Next != null);
                    Next.Prev = Prev;
                }
            }
        }
        private sealed class _FList
        {
            public int Count { get; set; }

            public _FListNode First { get; set; }

            public _FListNode Last { get; set; }

            public _FListNode Add(Dfa q)
            {
                return new _FListNode(q, this);
            }
        }
        #endregion // _FList

        static int _TransitionComparison(DfaTransition x, DfaTransition y)
        {
            var c = x.Max.CompareTo(y.Max); if (0 != c) return c; return x.Min.CompareTo(y.Min);
        }

        #region Totalize()
        /// <summary>
        /// For this machine, fills and sorts transitions such that any missing range now points to an empty non-accepting state
        /// </summary>
        public void Totalize()
        {
            Totalize(FillClosure());
        }
        /// <summary>
        /// For this closure, fills and sorts transitions such that any missing range now points to an empty non-accepting state
        /// </summary>
        /// <param name="closure">The closure to totalize</param>
        public static void Totalize(IList<Dfa> closure)
        {
            var s = new Dfa();
            s._transitions.Add(new DfaTransition(s, 0, 0x10ffff));
            foreach (Dfa p in closure)
            {
                int maxi = 0;
                var sortedTrans = new List<DfaTransition>(p._transitions);
                sortedTrans.Sort(_TransitionComparison);
                foreach (var t in sortedTrans)
                {
                    if (t.IsEpsilon)
                    {
                        continue;
                    }
                    if (t.Min > maxi)
                    {
                        p._transitions.Add(new DfaTransition(s, maxi, (t.Min - 1)));
                    }

                    if (t.Max + 1 > maxi)
                    {
                        maxi = t.Max + 1;
                    }
                }

                if (maxi <= 0x10ffff)
                {
                    p._transitions.Add(new DfaTransition(s, maxi, 0x10ffff));
                }
            }
        }

        #endregion //Totalize()

        public Dfa ToMinimized()
        {
            return _Minimize(this,null);
        }
       
        #region _Minimize()
        static void _Init<T>(IList<T?> list, int count)
        {
            for (int i = 0; i < count; ++i)
            {
                list.Add(default(T));
            }
        }
        static Dfa _Minimize(Dfa a, IProgress<int>? progress)
        {
            
            int prog = 0;
            progress?.Report(prog);

            var tr = a._transitions;
            if (1 == tr.Count)
            {
                DfaTransition t = tr[0];
                if (t.To == a && t.Min == 0 && t.Max == 0x10ffff)
                {
                    return a;
                }
            }

            a.Totalize();
            ++prog;
            progress?.Report(prog);
            // Make arrays for numbered states and effective alphabet.
            var cl = a.FillClosure();
            var states = new Dfa[cl.Count];
            int number = 0;
            var _minTags = new Dictionary<Dfa, int>();
            foreach (var q in cl)
            {
                states[number] = q;
                _minTags[q] = number;
                ++number;
            }

            var pp = new List<int>();
            for (int ic = cl.Count, i = 0; i < ic; ++i)
            {
                var ffa = cl[i];
                pp.Add(0);
                foreach (var t in ffa._transitions)
                {
                    // Skip anchor codepoints when building alphabet
                    if (t.Min < 0) continue;

                    pp.Add(t.Min);
                    if (t.Max < 0x10ffff)
                    {
                        pp.Add((t.Max + 1));
                    }
                }
                
            }

            var sigma = new int[pp.Count];
            pp.CopyTo(sigma, 0);
            Array.Sort(sigma);

            // Initialize data structures.
            var reverse = new List<List<Queue<Dfa>>>();
            foreach (var s in states)
            {
                var v = new List<Queue<Dfa>>();
                _Init(v, sigma.Length);
                reverse.Add(v);
            }
            prog = 2;
            if (progress != null) { progress.Report(prog); }
            var reverseNonempty = new bool[states.Length, sigma.Length];

            var partition = new List<LinkedList<Dfa>>();
            _Init(partition, states.Length);
            ++prog;
            if (progress != null) { progress.Report(prog); }
            var block = new int[states.Length];
            var active = new _FList[states.Length, sigma.Length];
            var active2 = new _FListNode[states.Length, sigma.Length];
            var pending = new Queue<KeyValuePair<int, int>>();
            var pending2 = new bool[sigma.Length, states.Length];
            var split = new List<Dfa>();
            var split2 = new bool[states.Length];
            var refine = new List<int>();
            var refine2 = new bool[states.Length];

            var splitblock = new List<List<Dfa>>();
            _Init(splitblock, states.Length);
            ++prog;
            progress?.Report(prog);
            for (int q = 0; q < states.Length; q++)
            {
                splitblock[q] = new List<Dfa>();
                partition[q] = new LinkedList<Dfa>();
                for (int x = 0; x < sigma.Length; x++)
                {
                    reverse[q][x] = new Queue<Dfa>();
                    active[q, x] = new _FList();
                }
            }

            // Find initial partition and reverse edges.
            foreach (var qq in states)
            {
                int j = qq.IsAccept ? 0 : 1;

                partition[j]?.AddLast(qq);
                block[_minTags[qq]] = j;
                for (int x = 0; x < sigma.Length; x++)
                {
                    var y = sigma[x];
                    var p = qq._Step(y);
                    System.Diagnostics.Debug.Assert(p != null);
                    var pn = _minTags[p];
                    reverse[pn]?[x]?.Enqueue(qq);
                    reverseNonempty[pn, x] = true;
                }
                ++prog;
                progress?.Report(prog);
            }

            // Initialize active sets.
            for (int j = 0; j <= 1; j++)
            {
                for (int x = 0; x < sigma.Length; x++)
                {
                    var part = partition[j];
                    System.Diagnostics.Debug.Assert(part != null);
                    foreach (var qq in part)
                    {
                        System.Diagnostics.Debug.Assert(qq != null);
                        if (reverseNonempty[_minTags[qq], x])
                        {
                            active2[_minTags[qq], x] = active[j, x].Add(qq);
                        }
                    }
                }
                ++prog;
                progress?.Report(prog);
            }

            // Initialize pending.
            for (int x = 0; x < sigma.Length; x++)
            {
                int a0 = active[0, x].Count;
                int a1 = active[1, x].Count;
                int j = a0 <= a1 ? 0 : 1;
                pending.Enqueue(new KeyValuePair<int, int>(j, x));
                pending2[x, j] = true;
            }

            // Process pending until fixed point.
            int k = 2;
            while (pending.Count > 0)
            {
                KeyValuePair<int, int> ip = pending.Dequeue();
                int p = ip.Key;
                int x = ip.Value;
                pending2[x, p] = false;

                // Find states that need to be split off their blocks.
                for (var m = active[p, x].First; m != null; m = m.Next)
                {
                    System.Diagnostics.Debug.Assert(m.State != null);
                    foreach (var s in reverse[_minTags[m.State]][x])
                    {
                        if (!split2[_minTags[s]])
                        {
                            split2[_minTags[s]] = true;
                            split.Add(s);
                            int j = block[_minTags[s]];
                            splitblock[j]?.Add(s);
                            if (!refine2[j])
                            {
                                refine2[j] = true;
                                refine.Add(j);
                            }
                        }
                    }
                }
                ++prog;
                if (progress != null) { progress.Report(prog); }
                // Refine blocks.
                foreach (int j in refine)
                {
                    if (splitblock[j]?.Count < partition[j]?.Count)
                    {
                        LinkedList<Dfa> b1 = partition[j];
                        System.Diagnostics.Debug.Assert(b1 != null);
                        LinkedList<Dfa> b2 = partition[k];
                        System.Diagnostics.Debug.Assert(b2 != null);
                        var e = splitblock[j];
                        System.Diagnostics.Debug.Assert(e != null);
                        foreach (var s in e)
                        {
                            b1.Remove(s);
                            b2.AddLast(s);
                            block[_minTags[s]] = k;
                            for (int c = 0; c < sigma.Length; c++)
                            {
                                _FListNode sn = active2[_minTags[s], c];
                                if (sn != null && sn.StateList == active[j, c])
                                {
                                    sn.Remove();
                                    active2[_minTags[s], c] = active[k, c].Add(s);
                                }
                            }
                        }

                        // Update pending.
                        for (int c = 0; c < sigma.Length; c++)
                        {
                            int aj = active[j, c].Count;
                            int ak = active[k, c].Count;
                            if (!pending2[c, j] && 0 < aj && aj <= ak)
                            {
                                pending2[c, j] = true;
                                pending.Enqueue(new KeyValuePair<int, int>(j, c));
                            }
                            else
                            {
                                pending2[c, k] = true;
                                pending.Enqueue(new KeyValuePair<int, int>(k, c));
                            }
                        }

                        k++;
                    }
                    var sbj = splitblock[j];
                    System.Diagnostics.Debug.Assert(sbj != null);
                    foreach (var s in sbj)
                    {
                        split2[_minTags[s]] = false;
                    }

                    refine2[j] = false;
                    //splitblock[j].Clear();
                    sbj.Clear();
                    ++prog;
                    if (progress != null) { progress.Report(prog); }
                }

                split.Clear();
                refine.Clear();
            }
            ++prog;
            if (progress != null) { progress.Report(prog); }
            // Make a new state for each equivalence class, set initial state.
            var newstates = new Dfa[k];
            for (int n = 0; n < newstates.Length; n++)
            {
                var s = new Dfa();
                newstates[n] = s;
                var pn = partition[n];
                System.Diagnostics.Debug.Assert(pn != null);
                foreach (var q in pn)
                {
                    if (q == a)
                    {
                        a = s;
                    }
                    s.Attributes["AcceptSymbol"] = q.AcceptSymbol;
                    _minTags[s] = _minTags[q]; // Select representative.				
                    _minTags[q] = n;
                }
                ++prog;
                progress?.Report(prog);
            }

            // Build transitions and set acceptance.
            foreach (var s in newstates)
            {
                var st = states[_minTags[s]];
                s.Attributes["AcceptSymbol"] = st.AcceptSymbol;
                foreach (var t in st._transitions)
                {
                    s._transitions.Add(new DfaTransition(newstates[_minTags[t.To]], t.Min, t.Max));
                }
                ++prog;
                progress?.Report(prog);
            }
            // remove dead transitions
            foreach (var ffa in a.FillClosure())
            {
                var itrns = new List<DfaTransition>(ffa._transitions);
                foreach (var trns in itrns)
                {
                    var found = false;
                    foreach(var afa in trns.To.FillClosure())
                    {
                        if(afa.IsAccept)
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        ffa._transitions.Remove(trns);
                    }
                }
            }
            return a;
        }
        Dfa _Step(int input)
        {
            for (int ic = _transitions.Count, i = 0; i < ic; ++i)
            {
                var t = _transitions[i];
                if (t.Min <= input && input <= t.Max)
                    return t.To;

            }
            return null;
        }
        #endregion // _Minimize()

    }
}
