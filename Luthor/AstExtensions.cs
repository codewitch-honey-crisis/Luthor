using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Luthor
{
    static class RegexExpressionExtensions
    {
        public static int GetDfaPosition(this RegexExpression expr, Dictionary<RegexExpression, int> positions)
        {
            return positions.TryGetValue(expr, out int pos) ? pos : -1;
        }

        public static void SetDfaPosition(this RegexExpression expr, int position, Dictionary<RegexExpression, int> positions)
        {
            positions[expr] = position;
        }

        public static bool GetNullable(this RegexExpression expr, Dictionary<RegexExpression, bool> nullable)
        {
            return nullable.TryGetValue(expr, out bool result) && result;
        }

        public static void SetNullable(this RegexExpression expr, bool nullableValue, Dictionary<RegexExpression, bool> nullable)
        {
            nullable[expr] = nullableValue;
        }

        public static HashSet<RegexExpression> GetFirstPos(this RegexExpression expr, Dictionary<RegexExpression, HashSet<RegexExpression>> firstPos)
        {
            if (!firstPos.TryGetValue(expr, out HashSet<RegexExpression> set))
            {
                set = new HashSet<RegexExpression>();
                firstPos[expr] = set;
            }
            return set;
        }

        public static HashSet<RegexExpression> GetLastPos(this RegexExpression expr, Dictionary<RegexExpression, HashSet<RegexExpression>> lastPos)
        {
            if (!lastPos.TryGetValue(expr, out HashSet<RegexExpression> set))
            {
                set = new HashSet<RegexExpression>();
                lastPos[expr] = set;
            }
            return set;
        }
    }
}