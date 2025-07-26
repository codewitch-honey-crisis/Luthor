using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Luthor
{
    partial class Dfa
    {

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
        
    }
}
