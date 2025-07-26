using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Luthor
{
    static class RegexExpressionExtensions
    {
        private static Dictionary<RegexExpression, int> _positions = new Dictionary<RegexExpression, int>();
        private static Dictionary<RegexExpression, bool> _nullable = new Dictionary<RegexExpression, bool>();
        private static Dictionary<RegexExpression, HashSet<RegexExpression>> _firstPos = new Dictionary<RegexExpression, HashSet<RegexExpression>>();
        private static Dictionary<RegexExpression, HashSet<RegexExpression>> _lastPos = new Dictionary<RegexExpression, HashSet<RegexExpression>>();

        public static int GetDfaPosition(this RegexExpression expr)
        {
            return _positions.TryGetValue(expr, out int pos) ? pos : -1;
        }

        public static void SetDfaPosition(this RegexExpression expr, int position)
        {
            _positions[expr] = position;
        }

        public static bool GetNullable(this RegexExpression expr)
        {
            return _nullable.TryGetValue(expr, out bool nullable) && nullable;
        }

        public static void SetNullable(this RegexExpression expr, bool nullable)
        {
            _nullable[expr] = nullable;
        }

        public static HashSet<RegexExpression> GetFirstPos(this RegexExpression expr)
        {
            if (!_firstPos.TryGetValue(expr, out HashSet<RegexExpression> set))
            {
                set = new HashSet<RegexExpression>();
                _firstPos[expr] = set;
            }
            return set;
        }

        public static HashSet<RegexExpression> GetLastPos(this RegexExpression expr)
        {
            if (!_lastPos.TryGetValue(expr, out HashSet<RegexExpression> set))
            {
                set = new HashSet<RegexExpression>();
                _lastPos[expr] = set;
            }
            return set;
        }

        public static void ClearDfaProperties()
        {
            _positions.Clear();
            _nullable.Clear();
            _firstPos.Clear();
            _lastPos.Clear();
        }
    }

}
