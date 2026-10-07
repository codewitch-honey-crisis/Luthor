#!/usr/bin/env python3
"""Luthor: a lexer generator that compiles regular expressions into flat int arrays.

Single-file Python port of the C# tool (Program.cs, FileParser.cs, Builder.cs, Compiler.cs,
Graph.cs). It produces the same arrays and the same Graphviz output as the C# version.
Single-byte code pages use Python codec names (cp1252, cp037, iso8859-15, ...), so their byte
mappings follow Python's tables. Rendering a graph to an image needs Graphviz's dot on the PATH.

Usage: luthor.py <rules-file|pattern> [-e <encoding>] [-n] [-u] [-g <graph-file>] [-v] [-d <dpi>]
(run with --help for details)
"""

import codecs
import os
import re as _re
import sys

MAX_CP = 0x10FFFF

# ============================================================================
# Shared DFA state (codepoint DFA and code-unit DFA)
# ============================================================================


class DfaState:
    """Bol / Eol are zero-width edges taken at a line start / before newline or end of input."""
    __slots__ = ("accept", "bol", "eol", "moves")

    def __init__(self, accept=-1, bol=-1, eol=-1, moves=None):
        self.accept = accept
        self.bol = bol
        self.eol = eol
        self.moves = moves if moves is not None else []  # [(lo, hi, to)] sorted, non-overlapping


class CodepointDfa:
    def __init__(self, states, error_id):
        self.states = states
        self.error_id = error_id


class LuthorFormatError(Exception):
    pass


# ============================================================================
# Builder: lazy-aware Aho-Sethi-Ullman (followpos) DFA construction over codepoints.
# Lazy quantifiers follow RE/flex (Robert van Engelen): positions are tagged with a lazy
# index and DFA states are trimmed during subset construction.
#
# Syntax: literals, escapes (\n \r \t \f \v \0 \xHH \x{H..} \uHHHH \d \D \w \W \s \S),
# '.', [...] / [^...], POSIX classes inside brackets ([[:alpha:]], negated [[:^alpha:]]),
# \p{Name} / \P{Name} / \p{^Name} with the same class names (Alpha, XDigit, ...),
# ( ), (?: ), |, *, +, ?, {n}, {n,}, {n,m}, lazy forms of all quantifiers,
# and the anchors ^ (line start) and $ (line end).
# Classes are ASCII unless build(..., unicode=True), which uses RE/flex's Unicode-mode
# definitions from _UNICODE_CLASSES (at the end of this file).
# ============================================================================

CHAR, LINE_START, LINE_END = 0, 1, 2

# A position is a tuple (accept, lazy, id). accept is 0/1; accept positions use id = rule
# index. Tuple ordering (accept, then lazy, then id) matches Pos.CompareTo in the C# code.
# The same leaf with different lazy tags is a DIFFERENT position.


def _with_lazy(p, lazy):
    return (p[0], lazy, p[2])


def _add(s, p):
    if p not in s:
        s.append(p)


def _add_all(s, ps):
    for p in ps:
        if p not in s:
            s.append(p)


def _distinct(ps):
    return list(dict.fromkeys(ps))


def _normalize(rs):
    res = []
    for lo, hi in sorted(rs, key=lambda r: r[0]):  # stable, like OrderBy
        if res and lo <= res[-1][1] + 1:
            res[-1] = (res[-1][0], max(res[-1][1], hi))
        else:
            res.append((lo, hi))
    return res


def _complement(rs):
    res, nxt = [], 0
    for lo, hi in _normalize(rs):
        if lo > nxt:
            res.append((nxt, lo - 1))
        nxt = hi + 1
    if nxt <= MAX_CP:
        res.append((nxt, MAX_CP))
    return res


_DIGIT = [(ord("0"), ord("9"))]
_WORD = [(ord("0"), ord("9")), (ord("A"), ord("Z")), (ord("_"), ord("_")), (ord("a"), ord("z"))]
_SPACE = [(ord("\t"), ord("\r")), (ord(" "), ord(" "))]

# The 14 POSIX classes as RE/flex defines them (lib/posix.cpp): ASCII definitions.
# In Unicode mode, _UNICODE_CLASSES overrides all but Blank and XDigit.
_POSIX = {
    "ASCII": [(0, 0x7F)],
    "Alnum": [(ord("0"), ord("9")), (ord("A"), ord("Z")), (ord("a"), ord("z"))],
    "Alpha": [(ord("A"), ord("Z")), (ord("a"), ord("z"))],
    "Blank": [(ord("\t"), ord("\t")), (ord(" "), ord(" "))],
    "Cntrl": [(0, 0x1F), (0x7F, 0x7F)],
    "Digit": _DIGIT,
    "Graph": [(0x21, 0x7E)],
    "Lower": [(ord("a"), ord("z"))],
    "Print": [(0x20, 0x7E)],
    "Punct": [(0x21, 0x2F), (0x3A, 0x40), (0x5B, 0x60), (0x7B, 0x7E)],
    "Space": _SPACE,
    "Upper": [(ord("A"), ord("Z"))],
    "Word": _WORD,
    "XDigit": [(ord("0"), ord("9")), (ord("A"), ord("F")), (ord("a"), ord("f"))],
}

_HEX_RE = _re.compile(r"(?:0[xX])?[0-9a-fA-F]+")


def _parse_hex(s):
    # Mirrors Convert.ToInt32(s, 16): optional 0x prefix, 32-bit two's complement.
    if not _HEX_RE.fullmatch(s):
        raise LuthorFormatError(f"bad hex value '{s}'")
    v = int(s, 16)
    if v > 0xFFFFFFFF:
        raise LuthorFormatError("hex value too large")
    return v - (1 << 32) if v >= (1 << 31) else v


def _is_digit(c):
    return c != "" and c.isdecimal()  # char.IsDigit: Unicode category Nd


class _Leaf:
    __slots__ = ("kind", "set", "loc")

    def __init__(self, kind, rset, loc):
        self.kind = kind
        self.set = rset   # codepoint ranges (CHAR leaves only)
        self.loc = loc    # source location; {n,m} copies share it


class _Frag:
    __slots__ = ("first", "last", "nullable", "lazies")

    def __init__(self, first=None, last=None, nullable=False, lazies=None):
        self.first = first if first is not None else []
        self.last = last if last is not None else []
        self.nullable = nullable
        self.lazies = lazies if lazies is not None else []  # [(index, loc)] active lazies


class Builder:
    def __init__(self, unicode=False):
        # Unicode mode: POSIX classes, \p{..}, \d \w \s use Unicode definitions (as RE/flex's
        # %option unicode). Otherwise they are ASCII.
        self.unicode = unicode
        self.leaves = []
        self.follow = {}       # leaf id -> followpos list
        self.all_lazies = []   # [(index, loc)]
        self.lazy_idx = 0
        self.re = ""
        self.i = 0
        self.offset = 0        # makes source locations global across rules

    def _f(self, leaf_id):
        lst = self.follow.get(leaf_id)
        if lst is None:
            lst = self.follow[leaf_id] = []
        return lst

    def _follow_of(self, k):
        """followpos of k, with k's lazy tag propagated along the path."""
        lazy = k[1]
        if lazy:
            return [_with_lazy(p, lazy) for p in self._f(k[2])]
        return list(self._f(k[2]))

    def _class_ranges(self, name):
        """Ranges of a canonical class name ("Alpha", "XDigit", ...), or None if there is none."""
        if self.unicode:
            flat = _UNICODE_CLASSES.get(name)
            if flat is not None:
                return [(flat[k], flat[k + 1]) for k in range(0, len(flat), 2)]
        ascii_ = _POSIX.get(name)
        return list(ascii_) if ascii_ is not None else None

    # ---------------- parsing: alternation > concatenation > postfix > atom ----------------

    def _peek(self, off=0):
        j = self.i + off
        return self.re[j] if j < len(self.re) else ""

    def parse_rule(self, pattern):
        self.re, self.i = pattern, 0
        f = self._alt()
        if self.i != len(self.re):
            raise LuthorFormatError(f"unexpected '{self.re[self.i]}' at {self.i}")
        self.offset += len(pattern) + 1
        return f

    def _alt(self):
        f = self._concat()
        while self._peek() == "|":
            self.i += 1
            g = self._concat()
            _add_all(f.first, g.first)
            _add_all(f.last, g.last)
            f.nullable = f.nullable or g.nullable
            f.lazies.extend(g.lazies)
        return f

    def _concat(self):
        f = _Frag(nullable=True)
        first = True
        while self.i < len(self.re) and self.re[self.i] not in "|)":
            g = self._postfix()
            if first:
                f, first = g, False
                continue
            if f.nullable:
                _add_all(f.first, g.first)
            for p in f.last:
                _add_all(self._f(p[2]), g.first)
            if g.nullable:
                _add_all(f.last, g.last)
            else:
                f.last = g.last
                f.nullable = False
            f.lazies.extend(g.lazies)
        return f

    def _at_repeat(self):
        return self.i + 1 < len(self.re) and self.re[self.i] == "{" and _is_digit(self.re[self.i + 1])

    def _postfix(self, stop_at=sys.maxsize):
        """stop_at limits parsing when re-parsing an operand to make a {n,m} copy."""
        start = self.i
        lazy_idx0 = self.lazy_idx
        f = self._atom()
        re_ = self.re
        while (self.i < len(re_) and self.i < stop_at
               and (re_[self.i] in "*+?" or self._at_repeat())):
            if re_[self.i] == "{":
                f = self._repeat(f, start, lazy_idx0)
                continue
            c = re_[self.i]
            self.i += 1
            if c != "+":
                f.nullable = True
            if self._peek() == "?":
                # new lazy quantifier: tag the entry points (firstpos) with a fresh index
                q = self._new_lazy(self.offset + self.i)
                self.i += 1
                f.lazies.append(q)
                f.first = _distinct(_with_lazy(p, q[0]) for p in f.first)
            elif c != "?" and f.lazies:
                # greedy loop around something containing a lazy quantifier: entries become greedy
                f.first = _distinct(_with_lazy(p, 0) for p in f.first)
            if c != "?":
                for p in f.last:  # loop back
                    _add_all(self._f(p[2]), f.first)
        return f

    def _new_lazy(self, loc):
        if self.lazy_idx == 255:
            raise LuthorFormatError("too many lazy quantifiers (max 255)")
        self.lazy_idx += 1
        q = (self.lazy_idx, loc)
        if q not in self.all_lazies:
            self.all_lazies.append(q)
        return q

    def _repeat(self, f, start, lazy_idx0):
        """X{n}, X{n,}, X{n,m}, optionally lazy. f is the parsed first copy of X, whose source
        text is re[start .. i). Copies 2..m are made by re-parsing that text."""
        qpos = self.i
        self.i += 1  # '{'
        n = self._num()
        m = n
        unlimited = False
        if self._peek() == ",":
            self.i += 1
            if _is_digit(self._peek()):
                m = self._num()
            else:
                unlimited = True
        if self._peek() != "}":
            raise LuthorFormatError(f"bad repeat at {qpos}")
        self.i += 1
        if n > m and not unlimited:
            raise LuthorFormatError(f"bad repeat {n}>{m}")
        after = self.i
        lazy = self._peek() == "?"
        if lazy:
            self.i += 1

        if not unlimited and m == 0:
            return _Frag(nullable=True)   # X{0} matches empty
        if unlimited and n == 0:
            m = 1                         # X{0,} is X*

        # copies 1..m-1; re-parsing reuses the same lazy indexes for lazy quantifiers inside X,
        # like RE/flex's virtual copies do
        lazy_idx_after = self.lazy_idx
        copies = [f]
        for _ in range(1, m):
            self.i = start
            self.lazy_idx = lazy_idx0
            copies.append(self._postfix(stop_at=qpos))
        self.lazy_idx = lazy_idx_after
        self.i = after + (1 if lazy else 0)

        if lazy:
            q = self._new_lazy(self.offset + after)
            f.lazies.append(q)
            for c in copies:
                c.first = _distinct(_with_lazy(p, q[0]) for p in c.first)

        x_nullable = f.nullable
        r = _Frag(nullable=x_nullable or n == 0, lazies=f.lazies)  # shares f's lazy list
        for k in range(len(copies) - 1):  # copy k -> copy k+1
            for p in copies[k].last:
                _add_all(self._f(p[2]), copies[k + 1].first)
        if unlimited:  # last copy loops
            for p in copies[-1].last:
                _add_all(self._f(p[2]), copies[-1].first)
        _add_all(r.first, copies[0].first)
        if x_nullable:
            for k in range(1, len(copies)):
                _add_all(r.first, copies[k].first)
        for k in range(0 if r.nullable else n - 1, len(copies)):
            _add_all(r.last, copies[k].last)
        return r

    def _num(self):
        s = self.i
        while _is_digit(self._peek()):
            self.i += 1
        if s == self.i:
            raise LuthorFormatError(f"expected number at {s}")
        text = self.re[s:self.i]
        if not text.isascii():  # int.Parse rejects non-ASCII digits
            raise LuthorFormatError(f"bad number '{text}'")
        return int(text)

    def _new_leaf(self, kind, rset, loc):
        leaf_id = len(self.leaves)
        self.leaves.append(_Leaf(kind, rset, self.offset + loc))
        p = (0, 0, leaf_id)
        return _Frag(first=[p], last=[p], nullable=False)

    def _atom(self):
        loc = self.i
        c = self.re[self.i]
        if c == "(":
            self.i += 1
            if self._peek() == "?" and self._peek(1) == ":":
                self.i += 2  # (?: ) = ( )
            f = self._alt()
            if self._peek() != ")":
                raise LuthorFormatError("missing )")
            self.i += 1
            return f
        if c == "^":
            self.i += 1
            return self._new_leaf(LINE_START, [], loc)
        if c == "$":
            self.i += 1
            return self._new_leaf(LINE_END, [], loc)
        if c == ".":
            self.i += 1
            return self._new_leaf(CHAR, [(0, 9), (11, MAX_CP)], loc)
        if c == "[":
            return self._new_leaf(CHAR, self._parse_class(), loc)
        if c in "*+?)":
            raise LuthorFormatError(f"unexpected '{c}' at {self.i}")
        if c == "\\":
            return self._new_leaf(CHAR, self._parse_escape()[0], loc)
        cp = self._next_codepoint()
        return self._new_leaf(CHAR, [(cp, cp)], loc)

    def _next_codepoint(self):
        a = ord(self.re[self.i])
        if 0xD800 <= a <= 0xDBFF and self.i + 1 < len(self.re):
            b = ord(self.re[self.i + 1])
            if 0xDC00 <= b <= 0xDFFF:  # surrogate pair inside a Python str (rare)
                self.i += 2
                return 0x10000 + ((a - 0xD800) << 10) + (b - 0xDC00)
        self.i += 1
        return a

    def _parse_escape(self):
        """At '\\'. Returns (set, is_class); is_class is true for \\d \\w \\s, \\p{..} and negations."""
        self.i += 1
        if self.i >= len(self.re):
            raise LuthorFormatError("trailing backslash")
        c = self.re[self.i]
        self.i += 1
        simple = {"n": 10, "r": 13, "t": 9, "f": 12, "v": 11, "0": 0}
        if c in simple:
            v = simple[c]
            return [(v, v)], False
        named = {"d": "Digit", "w": "Word", "s": "Space"}
        if c in named:
            return self._class_ranges(named[c]), True
        if c in "DWS":
            return _complement(self._class_ranges(named[c.lower()])), True
        if c in "pP":
            # \p{Name}, \P{Name} = complement, \p{^Name} = complement (RE/flex syntax).
            # Name is matched exactly: Alpha, XDigit, ASCII, ...
            at = self.i - 2
            if self._peek() != "{":
                raise LuthorFormatError(f"expected {{ after \\{c} at {at}")
            e = self.re.find("}", self.i)
            if e < 0:
                raise LuthorFormatError(f"missing }} in \\{c}{{...}} at {at}")
            name = self.re[self.i + 1:e]
            self.i = e + 1
            neg = c == "P"
            if name.startswith("^"):
                neg = not neg
                name = name[1:]
            rset = self._class_ranges(name)
            if rset is None:
                raise LuthorFormatError(f"unknown class \\{c}{{{name}}} at {at}")
            return (_complement(rset) if neg else rset), True
        if c == "x":
            if self._peek() == "{":
                e = self.re.find("}", self.i)
                if e < 0:
                    raise LuthorFormatError("bad \\x{...}")
                v = _parse_hex(self.re[self.i + 1:e])
                self.i = e + 1
                return [(v, v)], False
            v = self._hex(2)
            return [(v, v)], False
        if c == "u":
            v = self._hex(4)
            return [(v, v)], False
        self.i -= 1
        v = self._next_codepoint()
        return [(v, v)], False

    def _hex(self, digits):
        if self.i + digits > len(self.re):
            raise LuthorFormatError("bad hex escape")
        v = _parse_hex(self.re[self.i:self.i + digits])
        self.i += digits
        return v

    def _parse_class(self):
        self.i += 1  # '['
        neg = self._peek() == "^"
        if neg:
            self.i += 1
        rset = []
        first = True
        re_ = self.re
        while True:
            if self.i >= len(re_):
                raise LuthorFormatError("missing ]")
            if re_[self.i] == "]" and not first:
                self.i += 1
                break
            first = False
            if re_[self.i] == "[" and self._peek(1) == ":":
                rset.extend(self._parse_posix_class())
                continue
            if re_[self.i] == "\\":
                e, is_class = self._parse_escape()
                if is_class:
                    rset.extend(e)
                    continue
                lo = e[0][0]
            else:
                lo = self._next_codepoint()
            hi = lo
            if self.i + 1 < len(re_) and re_[self.i] == "-" and re_[self.i + 1] != "]":
                self.i += 1
                hi = self._parse_escape()[0][0][0] if re_[self.i] == "\\" else self._next_codepoint()
                if hi < lo:
                    raise LuthorFormatError("bad range in class")
            rset.append((lo, hi))
        return _complement(rset) if neg else _normalize(rset)

    def _parse_posix_class(self):
        """At "[:" inside a bracket expression; parses [:name:] or [:^name:] and returns its ranges.
        Names follow RE/flex: the first letter may be either case ([:alpha:] = [:Alpha:]),
        and xdigit / ascii map to XDigit / ASCII."""
        start = self.i
        end = self.re.find(":]", self.i + 2)
        if end < 0:
            raise LuthorFormatError(f"unterminated POSIX class at {start}")
        self.i += 2
        neg = self.re[self.i] == "^"
        if neg:
            self.i += 1
        written = self.re[self.i:end]
        self.i = end + 2
        name = written[0].upper() + written[1:] if len(written) > 1 else written
        if name == "Xdigit":
            name = "XDigit"
        elif name == "Ascii":
            name = "ASCII"
        rset = self._class_ranges(name)
        if rset is None:
            raise LuthorFormatError(f"unknown POSIX class [:{written}:] at {start}")
        return _complement(rset) if neg else rset

    # ---------------- the heart of it: trim a DFA state ----------------

    def _trim_lazy(self, s):
        # 1. If some position tagged l is an accept, the lazy quantifier l has "succeeded":
        #    kill every other thread that carries tag l (cuts the lazy loop edges).
        k = 0
        while k < len(s):
            p = s[k]
            if p[1] != 0 and p[0]:
                lazy, pid = p[1], p[2]
                s[:] = [q for q in s if not (q[1] == lazy and not (q[0] and q[2] == pid))]
                s[s.index(p)] = _with_lazy(p, 0)
                k = 0  # restart scan; list changed
                continue
            k += 1
        s.sort()
        _dedup_sorted(s)
        # 2. If every remaining thread is lazy, positions past the relevant lazy quantifier(s)
        #    drop their tag (normalization; mirrors RE/flex trim_lazy's second half).
        if s and all(p[1] != 0 for p in s):
            mx = -1
            for index, loc in self.all_lazies:
                if loc > mx and any(p[1] == index for p in s):
                    mx = loc
            if mx >= 0:
                for k in range(len(s)):
                    if not s[k][0] and self.leaves[s[k][2]].loc > mx:
                        s[k] = _with_lazy(s[k], 0)
            s.sort()
            _dedup_sorted(s)

    # ---------------- subset construction over codepoints ----------------

    @staticmethod
    def build(rules, error_rule=False, unicode=False):
        """Rule 0 has the highest priority when several rules accept the same length.
        error_rule adds a catch-all rule with the lowest priority (accept id = len(rules))
        that matches any single character. unicode selects Unicode definitions for POSIX
        classes, \\p{..}, \\d \\w \\s (see _class_ranges)."""
        builder = Builder(unicode)
        rules = list(rules)
        if error_rule:
            rules.append(r"[\x{0}-\x{10FFFF}]")
        start = []
        for r, rule in enumerate(rules):
            f = builder.parse_rule(rule)
            _add_all(start, f.first)
            if f.nullable:
                _add(start, (1, 0, r))
            # accept positions carry the rule's lazy tags, so a path that SKIPS a lazy loop
            # still cuts the loop when it accepts
            if not f.lazies:
                accepts = [(1, 0, r)]
            else:
                accepts = [(1, q[0], r) for q in f.lazies]
            for p in f.last:
                _add_all(builder._f(p[2]), accepts)
        builder._trim_lazy(start)

        sets = [start]
        index = {tuple(sorted(start)): 0}

        def intern(s):
            key = tuple(sorted(s))
            t = index.get(key)
            if t is None:
                t = len(sets)
                sets.append(s)
                index[key] = t
            return t

        leaves = builder.leaves
        result = []
        si = 0
        while si < len(sets):
            S = sets[si]
            si += 1
            st = DfaState()
            acc = [p[2] for p in S if p[0]]
            st.accept = min(acc) if acc else -1

            # character moves: split all leaf ranges into disjoint elementary intervals
            items = []
            for k in S:
                if k[0] or leaves[k[2]].kind != CHAR:
                    continue
                follow = builder._follow_of(k)
                for lo, hi in leaves[k[2]].set:
                    items.append((lo, hi, follow))
            points = sorted({x for lo, hi, _ in items for x in (lo, hi + 1)})
            pos_of = {x: j for j, x in enumerate(points)}
            nb = max(0, len(points) - 1)
            buckets = [None] * nb
            for lo, hi, follow in items:
                j = pos_of[lo]
                while j < nb and points[j] <= hi:
                    if buckets[j] is None:
                        buckets[j] = []
                    _add_all(buckets[j], follow)
                    j += 1
            moves = st.moves
            for j in range(nb):
                target = buckets[j]
                if target is None:
                    continue
                builder._trim_lazy(target)
                if not target:
                    continue
                t = intern(target)
                lo, hi = points[j], points[j + 1] - 1
                if moves and moves[-1][2] == t and moves[-1][1] + 1 == lo:
                    moves[-1] = (moves[-1][0], hi, t)
                else:
                    moves.append((lo, hi, t))

            # zero-width anchor edges
            st.bol = builder._anchor_edge(S, LINE_START, intern)
            st.eol = builder._anchor_edge(S, LINE_END, intern)
            result.append(st)
        return CodepointDfa(result, len(rules) - 1 if error_rule else -1)

    def _anchor_edge(self, S, kind, intern):
        """Anchor threads advance past the anchor, all other threads stay where they are.
        The result goes through the same lazy trimming as any other move."""
        leaves = self.leaves

        def is_anchor(p):
            return not p[0] and leaves[p[2]].kind == kind

        if not any(is_anchor(p) for p in S):
            return -1
        work = list(S)
        done = set()
        while any(is_anchor(p) for p in work):
            nxt = []
            for p in work:
                if not is_anchor(p):
                    _add(nxt, p)
                elif p not in done:
                    done.add(p)
                    _add_all(nxt, self._follow_of(p))
            work = nxt
        self._trim_lazy(work)
        return intern(work)


def _dedup_sorted(s):
    for k in range(len(s) - 1, 0, -1):
        if s[k] == s[k - 1]:
            del s[k]


# ============================================================================
# Compiler: turns a codepoint DFA into a code-unit DFA for a given encoding, minimizes it,
# and flattens it into a single list of ints.
#
# Flat layout:
#   dfa[0]            newline code unit in this encoding (used by ^ and $), -1 if none
#   then per state, starting at offset 1 (the start state):
#     accept          rule index, or -1
#     bol             offset of the state reached by the ^ edge, or -1
#     eol             offset of the state reached by the $ edge, or -1
#     n               number of ranges
#     n x (min, max, target)   sorted by min, non-overlapping; target is an array offset
# ============================================================================


class Compiler:
    """Static-only, like the C# static class. Entry point: Compiler.compile."""

    @staticmethod
    def compile(cp_dfa, encoding="UTF-8", minimize=True):
        states, newline = Compiler._transform(cp_dfa.states, encoding, cp_dfa.error_id)
        if minimize:
            states = Compiler._minimize(states)
        return Compiler._flatten(states, newline)

    # ---------------- encoding transform ----------------

    @staticmethod
    def _transform(cp, encoding, error_id):
        # Start states: where a token begins (state 0, plus whatever its ^ and $ edges reach).
        # Only these need error handling, because the error rule only ever matches the first
        # character of a token.
        starts = set()
        if error_id >= 0:
            work = [0]
            while work:
                s = work.pop()
                if s < 0 or s in starts:
                    continue
                starts.add(s)
                work.append(cp[s].bol)
                work.append(cp[s].eol)

        # Start states always hold the catch-all rule's position, which no transition leads back to,
        # so they can't be reached in the middle of a token.
        if any(m[2] in starts for st in cp for m in st.moves):
            raise RuntimeError("a start state is reachable mid-token")

        e = encoding.upper().replace("-", "").replace("_", "")
        newline = 10
        if e in ("UTF32", "UTF32LE", "UTF32BE"):
            states = Compiler._copy(cp, keep_moves=True)
            max_unit = 0x7FFFFFFF
        elif e == "UTF8":
            states = Compiler._sequenced(cp, Compiler._utf8_sequences, starts, error_id)
            max_unit = 0xFF
        elif e in ("UTF16", "UTF16LE", "UTF16BE", "UNICODE"):
            states = Compiler._sequenced(cp, Compiler._utf16_sequences, starts, error_id)
            max_unit = 0xFFFF
        else:
            states, newline = Compiler._single_byte(cp, encoding)
            max_unit = 0xFF

        # Any code unit a start state has no move for (an invalid byte, a lone surrogate, a byte the
        # code page doesn't define, ...) is a one-unit error token.
        if error_id >= 0:
            error = len(states)
            states.append(DfaState(accept=error_id))
            for s in starts:
                states[s].moves = Compiler._fill_gaps(states[s].moves, max_unit, error)
        return states, newline

    @staticmethod
    def _fill_gaps(moves, max_unit, to):
        res = []
        nxt = 0
        for m in moves:
            if m[0] > nxt:
                res.append((nxt, m[0] - 1, to))
            res.append(m)
            nxt = m[1] + 1
        if nxt <= max_unit:
            res.append((nxt, max_unit, to))
        return res

    @staticmethod
    def _copy(cp, keep_moves):
        return [DfaState(s.accept, s.bol, s.eol, list(s.moves) if keep_moves else []) for s in cp]

    @staticmethod
    def _sequenced(cp, seqs, starts, error_id):
        """Multi-unit encodings: every codepoint range becomes one or more sequences of code-unit
        ranges; sequences leaving a state are merged into a trie of new states."""
        states = Compiler._copy(cp, keep_moves=False)
        memo = {}
        for s in range(len(cp)):
            items = []
            for lo, hi, to in cp[s].moves:
                for seq in seqs(lo, hi):
                    items.append((seq, to))
            if s not in starts:
                states[s].moves = Compiler._build_trie(items, 0, states, memo)
                continue
            # A start state gets its own, unshared trie whose partial-character states accept as the
            # error rule: a truncated or malformed sequence becomes one error token covering the
            # units read so far.
            first = len(states)
            states[s].moves = Compiler._build_trie(items, 0, states, {})
            for k in range(first, len(states)):
                states[k].accept = error_id
        return states

    @staticmethod
    def _build_trie(items, depth, states, memo):
        moves = []
        points = sorted({x for seq, _ in items for x in (seq[depth][0], seq[depth][1] + 1)})
        for j in range(len(points) - 1):
            lo, hi = points[j], points[j + 1] - 1
            group = [t for t in items if t[0][depth][0] <= lo and hi <= t[0][depth][1]]
            if not group:
                continue
            if all(len(t[0]) == depth + 1 for t in group):
                to = group[0][1]
                if any(t[1] != to for t in group):
                    raise RuntimeError("ambiguous encoding")
            else:
                if any(len(t[0]) == depth + 1 for t in group):
                    raise RuntimeError("mixed sequence lengths")
                # share identical suffix sub-tries
                key = tuple(sorted((tuple(t[0][depth + 1:]), t[1]) for t in group))
                to = memo.get(key)
                if to is None:
                    to = len(states)
                    states.append(DfaState())
                    memo[key] = to
                    states[to].moves = Compiler._build_trie(group, depth + 1, states, memo)
            if moves and moves[-1][2] == to and moves[-1][1] + 1 == lo:
                moves[-1] = (moves[-1][0], hi, to)
            else:
                moves.append((lo, hi, to))
        return moves

    @staticmethod
    def _no_surrogates(lo, hi):
        """Splits [lo,hi] around the surrogate block, which is not encodable in UTF-8/UTF-16."""
        if hi < 0xD800 or lo > 0xDFFF:
            return [(lo, hi)]
        res = []
        if lo < 0xD800:
            res.append((lo, 0xD7FF))
        if hi > 0xDFFF:
            res.append((0xE000, hi))
        return res

    @staticmethod
    def _utf8_sequences(lo, hi):
        res = []
        for a, b in Compiler._no_surrogates(lo, hi):
            s = a  # split where the encoded length changes
            for limit in (0x7F, 0x7FF, 0xFFFF, 0x10FFFF):
                if s > b:
                    break
                if s > limit:
                    continue
                Compiler._utf8_split(s, min(b, limit), res)
                s = limit + 1
        return res

    @staticmethod
    def _utf8_split(lo, hi, res):
        """Same encoded length assumed. Splits until every byte position is an independent range."""
        n = len(Compiler._utf8_encode(lo))
        for k in range(1, n):
            m = (1 << (6 * k)) - 1
            if (lo & ~m) != (hi & ~m):
                if (lo & m) != 0:
                    Compiler._utf8_split(lo, lo | m, res)
                    Compiler._utf8_split((lo | m) + 1, hi, res)
                    return
                if (hi & m) != m:
                    Compiler._utf8_split(lo, (hi & ~m) - 1, res)
                    Compiler._utf8_split(hi & ~m, hi, res)
                    return
        res.append(tuple(zip(Compiler._utf8_encode(lo), Compiler._utf8_encode(hi))))

    @staticmethod
    def _utf8_encode(cp):
        if cp < 0x80:
            return [cp]
        if cp < 0x800:
            return [0xC0 | cp >> 6, 0x80 | cp & 0x3F]
        if cp < 0x10000:
            return [0xE0 | cp >> 12, 0x80 | cp >> 6 & 0x3F, 0x80 | cp & 0x3F]
        return [0xF0 | cp >> 18, 0x80 | cp >> 12 & 0x3F, 0x80 | cp >> 6 & 0x3F, 0x80 | cp & 0x3F]

    @staticmethod
    def _utf16_sequences(lo, hi):
        res = []
        for a, b in Compiler._no_surrogates(lo, hi):
            if a <= 0xFFFF:
                res.append(((a, min(b, 0xFFFF)),))
            if b >= 0x10000:
                Compiler._utf16_split(max(a, 0x10000) - 0x10000, b - 0x10000, res)
        return res

    @staticmethod
    def _utf16_split(lo, hi, res):
        m = 0x3FF
        if (lo & ~m) != (hi & ~m):
            if (lo & m) != 0:
                Compiler._utf16_split(lo, lo | m, res)
                Compiler._utf16_split((lo | m) + 1, hi, res)
                return
            if (hi & m) != m:
                Compiler._utf16_split(lo, (hi & ~m) - 1, res)
                Compiler._utf16_split(hi & ~m, hi, res)
                return
        res.append(((0xD800 + (lo >> 10), 0xD800 + (hi >> 10)), (0xDC00 + (lo & m), 0xDC00 + (hi & m))))

    @staticmethod
    def _is_single_byte_codec(info):
        # Python has no IsSingleByte flag. Its single-byte codecs are the charmap codecs, whose
        # modules carry a decoding_table, plus the built-in ascii and latin-1.
        if info.name in ("ascii", "iso8859-1", "latin-1"):
            return True
        module = sys.modules.get(getattr(info.incrementaldecoder, "__module__", ""), None)
        return module is not None and hasattr(module, "decoding_table")

    @staticmethod
    def _single_byte(cp, name):
        """Any single-byte Python codec (ascii, latin-1, iso8859-x, cp125x, EBCDIC cp037/cp500, ...)."""
        try:
            info = codecs.lookup(name)
        except LookupError:
            raise ValueError(f"'{name}' is not a known encoding name") from None
        if not Compiler._is_single_byte_codec(info):
            raise ValueError(f"{name} is not UTF-8/16/32 or a single-byte encoding")
        cp_of = []
        for b in range(256):
            try:
                s = bytes([b]).decode(info.name)
                cp_of.append(ord(s) if len(s) == 1 else -1)
            except UnicodeDecodeError:
                cp_of.append(-1)
        newline = -1
        try:
            nl = "\n".encode(info.name)
            if len(nl) == 1:
                newline = nl[0]
        except UnicodeEncodeError:
            pass

        states = Compiler._copy(cp, keep_moves=False)
        for s in range(len(cp)):
            moves = states[s].moves
            for b in range(256):
                if cp_of[b] < 0:
                    continue
                to = Compiler._lookup(cp[s].moves, cp_of[b])
                if to < 0:
                    continue
                if moves and moves[-1][2] == to and moves[-1][1] + 1 == b:
                    moves[-1] = (moves[-1][0], b, to)
                else:
                    moves.append((b, b, to))
        return states, newline

    @staticmethod
    def _lookup(moves, c):
        lo, hi = 0, len(moves) - 1
        while lo <= hi:
            mid = (lo + hi) // 2
            if c < moves[mid][0]:
                hi = mid - 1
            elif c > moves[mid][1]:
                lo = mid + 1
            else:
                return moves[mid][2]
        return -1

    # ---------------- minimization (Moore partition refinement) ----------------

    @staticmethod
    def _minimize(states):
        n = len(states)
        cls = [0] * n
        count = 1
        while True:
            ids = {}
            nxt = [0] * n
            for s in range(n):
                st = states[s]
                key = (cls[s], st.accept,
                       -1 if st.bol < 0 else cls[st.bol],
                       -1 if st.eol < 0 else cls[st.eol],
                       tuple(Compiler._merge_by(st.moves, lambda t: cls[t])))
                k = ids.get(key)
                if k is None:
                    k = ids[key] = len(ids)
                nxt[s] = k
            cls = nxt
            if len(ids) == count:
                break
            count = len(ids)

        # renumber in BFS order from the start state (drops unreachable states)
        order = {cls[0]: 0}
        rep = [0]

        def num(s):
            if s < 0:
                return -1
            k = order.get(cls[s])
            if k is None:
                k = order[cls[s]] = len(rep)
                rep.append(s)
            return k

        result = []
        k = 0
        while k < len(rep):
            st = states[rep[k]]
            bol = num(st.bol)
            eol = num(st.eol)
            result.append(DfaState(st.accept, bol, eol, Compiler._merge_by(st.moves, num)))
            k += 1
        return result

    @staticmethod
    def _merge_by(moves, fn):
        res = []
        for lo, hi, to in moves:
            t = fn(to)
            if res and res[-1][2] == t and res[-1][1] + 1 == lo:
                res[-1] = (res[-1][0], hi, t)
            else:
                res.append((lo, hi, t))
        return res

    # ---------------- flatten ----------------

    HEADER = 1  # ints before the start state

    @staticmethod
    def _flatten(states, newline):
        off = []
        size = Compiler.HEADER
        for st in states:
            off.append(size)
            size += 4 + 3 * len(st.moves)
        dfa = [0] * size
        dfa[0] = newline
        for s, st in enumerate(states):
            k = off[s]
            dfa[k] = st.accept
            dfa[k + 1] = -1 if st.bol < 0 else off[st.bol]
            dfa[k + 2] = -1 if st.eol < 0 else off[st.eol]
            dfa[k + 3] = len(st.moves)
            k += 4
            for lo, hi, to in st.moves:
                dfa[k] = lo
                dfa[k + 1] = hi
                dfa[k + 2] = off[to]
                k += 3
        return dfa


compile_dfa = Compiler.compile  # backward-compatible alias


# ============================================================================
# Graph: renders a codepoint DFA (from Builder.build) as a Graphviz graph.
# ============================================================================


class GraphOptions:
    def __init__(self, dpi=300, state_prefix="q", hide_symbol_ids=False, symbol_names=None,
                 vertical=False):
        self.dpi = dpi                          # resolution, in dots-per-inch, to render at
        self.state_prefix = state_prefix        # prefix used for state labels
        self.hide_symbol_ids = hide_symbol_ids  # hide accept ids (names, when given, still show)
        self.symbol_names = symbol_names        # maps accept ids to names for display
        self.vertical = vertical                # top to bottom instead of left to right


class Graph:
    """Static-only, like the C# static class. Entry points: Graph.write_to, Graph.render_to_file."""

    @staticmethod
    def write_to(dfa, out, options=None):
        """Writes Graphviz dot source for the DFA to the text stream out."""
        options = options or GraphOptions()
        states = dfa.states
        w = out.write
        w("digraph FA {\n")
        w("\trankdir=TB;\n" if options.vertical else "\trankdir=LR;\n")
        w("\tnode [shape=circle];\n")

        # states
        for s, st in enumerate(states):
            label = f"{_html(options.state_prefix)}<SUB>{s}</SUB>"
            symbol = Graph._symbol_text(st.accept, options) if st.accept >= 0 else None
            if symbol is not None:
                label += "<BR/>" + _html(symbol)
            w(f"\ts{s} [label=<{label}>")
            if st.accept >= 0:
                w(", shape=doublecircle")
            w("];\n")

        # character moves: one edge per target, all ranges to that target merged into one label
        for s, st in enumerate(states):
            by_target = {}  # insertion-ordered: targets in order of first appearance
            for lo, hi, to in st.moves:
                by_target.setdefault(to, []).append((lo, hi))
            for to, ranges in by_target.items():
                w(f"\ts{s} -> s{to} [label=<{_html(Graph._range_label(ranges))}>];\n")

            # zero-width anchor edges
            if st.bol >= 0:
                w(f"\ts{s} -> s{st.bol} [label=<^>, style=dashed, color=gray, fontcolor=gray];\n")
            if st.eol >= 0:
                w(f"\ts{s} -> s{st.eol} [label=<$>, style=dashed, color=gray, fontcolor=gray];\n")
        w("}\n")

    @staticmethod
    def render_to_file(dfa, filename, options=None):
        """Renders the DFA to filename. The extension picks the format: .dot writes the dot source;
        anything else (.png, .jpg, .svg, .pdf, ...) is rendered by Graphviz's dot."""
        import io
        import subprocess

        options = options or GraphOptions()
        ext = os.path.splitext(filename)[1].lstrip(".").lower()
        if not ext:
            raise ValueError("The output filename needs an extension to indicate the format")

        buf = io.StringIO()
        Graph.write_to(dfa, buf, options)
        source = buf.getvalue()
        if ext == "dot":
            with open(filename, "w", encoding="utf-8", newline="\n") as fh:
                fh.write(source)
            return

        args = ["dot", "-T" + ext]
        if options.dpi > 0:
            args.append(f"-Gdpi={options.dpi}")
        args.append("-o" + filename)
        try:
            # labels can contain any Unicode character, and dot reads UTF-8
            proc = subprocess.run(args, input=source.encode("utf-8"), stdout=subprocess.DEVNULL,
                                  stderr=subprocess.PIPE)
        except FileNotFoundError:
            raise RuntimeError('Graphviz "dot" application is either not installed '
                               'or not in the system PATH') from None
        if proc.returncode != 0:
            msg = proc.stderr.decode("utf-8", errors="replace").strip()
            raise RuntimeError(f'Graphviz "dot" failed: {msg}')

    # ---------------- labels ----------------

    @staticmethod
    def _symbol_text(accept_id, options):
        """The name if one is provided, otherwise the id unless ids are hidden."""
        names = options.symbol_names
        if names is not None and accept_id < len(names) and names[accept_id]:
            return names[accept_id]
        return None if options.hide_symbol_ids else str(accept_id)

    @staticmethod
    def _range_label(ranges):
        """Regex-style label for a set of codepoint ranges: a single character, a class, or a
        negated class, whichever is shorter."""
        rset = _normalize(ranges)
        if len(rset) == 1 and rset[0][0] == rset[0][1]:
            return _escape_cp(rset[0][0], in_class=False)
        comp = _complement(rset)
        if not comp:
            return "any"
        pos = "[" + Graph._class_body(rset) + "]"
        neg = "[^" + Graph._class_body(comp) + "]"
        return neg if len(neg) < len(pos) else pos

    @staticmethod
    def _class_body(rset):
        out = []
        for lo, hi in rset:
            out.append(_escape_cp(lo, in_class=True))
            if hi == lo:
                continue
            if hi != lo + 1:  # two adjacent characters read better as "ab" than "a-b"
                out.append("-")
            out.append(_escape_cp(hi, in_class=True))
        return "".join(out)


_ESCAPES = {10: r"\n", 13: r"\r", 9: r"\t", 12: r"\f", 11: r"\v", 0: r"\0"}
_META = "\\.*+?()|[]{}^$"
_CLASS_META = "\\]^-["
# Categories that don't render as a visible glyph: control, format, surrogate, private use,
# unassigned, and the separators (which include ' ', invisible as a label).
_INVISIBLE = {"Cc", "Cf", "Cs", "Co", "Cn", "Zs", "Zl", "Zp"}


def _escape_cp(cp, in_class):
    """Escapes a codepoint using the same escape syntax the Builder accepts."""
    import unicodedata
    if cp in _ESCAPES:
        return _ESCAPES[cp]
    if cp < 0x80 and chr(cp) in (_CLASS_META if in_class else _META):
        return "\\" + chr(cp)
    if unicodedata.category(chr(cp)) not in _INVISIBLE:
        return chr(cp)
    if cp <= 0xFF:
        return f"\\x{cp:02X}"
    if cp <= 0xFFFF:
        return f"\\u{cp:04X}"
    return f"\\x{{{cp:X}}}"


def _html(s):
    """Escapes text for a Graphviz HTML-like label."""
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")


# ============================================================================
# FileParser: one rule per line; blank lines and lines starting with '#' are skipped.
# ============================================================================

# .NET String.Trim() whitespace (char.IsWhiteSpace), which differs slightly from str.strip().
_NET_WS = ("\t\n\v\f\r \x85\xa0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006"
           "\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000")
_LINE_SPLIT = _re.compile(r"\r\n|\r|\n")  # TextReader.ReadLine line breaks


def _read_text(path):
    """Like StreamReader(path, detectEncodingFromByteOrderMarks: true) with a UTF-8 default."""
    with open(path, "rb") as fh:
        data = fh.read()
    for bom, enc in ((codecs.BOM_UTF32_LE, "utf-32-le"), (codecs.BOM_UTF32_BE, "utf-32-be"),
                     (codecs.BOM_UTF8, "utf-8"), (codecs.BOM_UTF16_LE, "utf-16-le"),
                     (codecs.BOM_UTF16_BE, "utf-16-be")):
        if data.startswith(bom):
            return data[len(bom):].decode(enc, errors="replace")
    return data.decode("utf-8", errors="replace")


def read_rules(text):
    lines = _LINE_SPLIT.split(text)
    if lines and lines[-1] == "":
        lines.pop()  # a trailing newline doesn't start another line
    for line in lines:
        if line.startswith("#") or len(line.strip(_NET_WS)) == 0:
            continue
        yield line.strip(_NET_WS)


# ============================================================================
# Program
# ============================================================================

__version__ = "5.0.0.0"

_USAGE = """luthor v{version}

A DFA lexer generator tool

Usage: luthor.py <rules-file|pattern> [--encoding <encoding>] [--no-error] [--unicode] [--graph <graph-file>] [--vertical]
        [--dpi <dpi>]

    <rules-file|pattern>       text file containing regex rules, one per line, in the format 'name = pattern' or '#
                               comment' at the start of each line, or a single pattern to match
    -e, --encoding <encoding>  the encoding to use. Defaults to UTF-8.
    -n, --no-error             do not generate the error rule
    -u, --unicode              use Unicode character groups
    -g, --graph <graph-file>   generate a DFA graph (requires GraphViz in your PATH)
    -v, --vertical             use vertical DFA graphs
    -d, --dpi <dpi>            use the indicated DPI for graphs. Defaults to 300.
"""


def _print_usage():
    sys.stderr.write(_USAGE.format(version=__version__))


class _UsageError(ValueError):
    pass


class _Options:
    def __init__(self):
        self.input = None
        self.encoding = "UTF-8"
        self.no_error = False
        self.unicode = False
        self.graph = None
        self.vertical = False
        self.dpi = 300


def _parse_options(argv):
    """argv[0] is always the rules file or pattern (so a pattern may start with '-').
    The options follow in any order; a value can be given as "--opt value" or "--opt=value"."""
    opts = _Options()
    opts.input = argv[0]
    flags = {"-n": "no_error", "--no-error": "no_error", "-u": "unicode", "--unicode": "unicode",
             "-v": "vertical", "--vertical": "vertical"}
    valued = {"-e": "encoding", "--encoding": "encoding", "-g": "graph", "--graph": "graph",
              "-d": "dpi", "--dpi": "dpi"}
    seen = set()
    i = 1
    while i < len(argv):
        arg = argv[i]
        name, eq, inline = arg.partition("=") if arg.startswith("--") else (arg, "", "")
        if name in flags and not eq:
            setattr(opts, flags[name], True)
        elif name in valued:
            attr = valued[name]
            if attr in seen:
                raise _UsageError(f"{name} was specified more than once.")
            seen.add(attr)
            if eq:
                value = inline
            elif i + 1 < len(argv) and not argv[i + 1].startswith("-"):
                i += 1
                value = argv[i]
            else:
                value = ""
            if not value:
                raise _UsageError(f"{name} requires a value.")
            if attr == "dpi":
                if not value.isdigit() or int(value) <= 0:
                    raise _UsageError(f"--dpi must be a positive whole number, not '{value}'.")
                value = int(value)
            setattr(opts, attr, value)
        else:
            raise _UsageError(f"Unexpected argument '{arg}'.")
        i += 1
    if opts.graph is None:
        if opts.vertical:
            raise _UsageError("--vertical requires --graph.")
        if "dpi" in seen:
            raise _UsageError("--dpi requires --graph.")
    return opts

_METACHARS = set('\\/\"^$.|?*+()[]{}')
_WHITESPACE = {'\n': r'\n', '\r': r'\r', '\t': r'\t', '\f': r'\f', '\v': r'\v'}

def _needs_hex_escape(c: str) -> bool:
    cp = ord(c)
    return cp <= 0x1F or 0x7F <= cp <= 0x9F or 0xD800 <= cp <= 0xDFFF

def fsm_escape(literal: str) -> str:
    if not literal:  # an empty rule would match nothing useful
        raise ValueError("literal must be a non-empty string")
    out = []
    for c in literal:
        if c in _METACHARS:
            out.append('\\' + c)
        elif c in _WHITESPACE:
            out.append(_WHITESPACE[c])
        elif _needs_hex_escape(c):  # control char or lone surrogate
            out.append(f'\\x{{{ord(c):X}}}')
        else:
            out.append(c)
    return ''.join(out)

def main(argv):
    try:
        try:
            sys.stdout.reconfigure(encoding="utf-8")
        except (AttributeError, ValueError):
            pass
        if len(argv) < 1:
            raise _UsageError("The rules file or a pattern is required.")
        arg0 = argv[0]
        if arg0 in ("-?", "-h") or arg0.lower() == "--help":
            _print_usage()
            return
        opts = _parse_options(argv)
        is_pattern = "\0" in arg0 or not os.path.isfile(arg0)
        kind = "expression" if is_pattern else "lexer"
        print(f"Luthor {kind} compiler", file=sys.stderr)
        print(file=sys.stderr)
        if not is_pattern:
            patterns = list(read_rules(_read_text(arg0)))
            print(f"There are {len(patterns)} patterns.", file=sys.stderr)
        else:
            patterns = [arg0]

        if opts.no_error:
            print("The error rule was not generated.", file=sys.stderr)
        if opts.unicode:
            print("Unicode character groups are in use.", file=sys.stderr)

        dfa = Builder.build(patterns, not opts.no_error, opts.unicode)

        print(f"{len(dfa.states)} states were built.", file=sys.stderr)

        if opts.graph is not None:
            Graph.render_to_file(dfa, opts.graph, GraphOptions(dpi=opts.dpi, vertical=opts.vertical))
            print(f"The graph was written to {opts.graph}.", file=sys.stderr)

        array = Compiler.compile(dfa, opts.encoding)
        print(f"The array has {len(array)} elements.", file=sys.stderr)
        width = 8
        for n in array:
            if width == 8 and n > 127:
                width = 16
            if width == 16 and n > 32767:
                width = 32
        print(f"The array element width is {width} bits.", file=sys.stderr)
        out = []
        last = len(array) - 1
        for i, n in enumerate(array):
            if i % 16 == 0:
                out.append("\n")
            out.append(str(n))
            if i < last:
                out.append(", ")
        out.append("\n")
        sys.stdout.write("".join(out))
    except ValueError as ex:  # bad arguments (like C#'s ArgumentException): show the usage
        print(f"Error: {ex}", file=sys.stderr)
        print(file=sys.stderr)
        _print_usage()
    except Exception as ex:
        print(f"Error: {ex}", file=sys.stderr)


# ============================================================================
# Unicode character classes (used in Unicode mode)
# ============================================================================

# BEGIN UNICODE CLASSES (generated by tools/gen_unicode_classes.py --python; do not edit)
# Unicode-mode POSIX classes, defined as in RE/flex's Unicode mode.
# name -> sorted, non-overlapping (lo, hi) codepoint pairs, flattened
_UNICODE_CLASSES = {
    # ASCII: 128 code points in 1 ranges
    "ASCII": (
        0, 127,
    ),
    # Space: 22 code points in 8 ranges
    "Space": (
        9, 13, 32, 32, 160, 160, 5760, 5760, 8192, 8202, 8239, 8239, 8287, 8287, 12288, 12288,
    ),
    # Cntrl: 235 code points in 23 ranges
    "Cntrl": (
        0, 31, 127, 159, 173, 173, 1536, 1541, 1564, 1564, 1757, 1757, 1807, 1807, 2192, 2193,
        2274, 2274, 6158, 6158, 8203, 8207, 8234, 8238, 8288, 8292, 8294, 8303, 65279, 65279, 65529, 65531,
        69821, 69821, 69837, 69837, 78896, 78911, 113824, 113827, 119155, 119162, 917505, 917505, 917536, 917631,
    ),
    # Print: 1111829 code points in 24 ranges
    "Print": (
        32, 126, 160, 172, 174, 1535, 1542, 1563, 1565, 1756, 1758, 1806, 1808, 2191, 2194, 2273,
        2275, 6157, 6159, 8202, 8208, 8233, 8239, 8287, 8293, 8293, 8304, 55295, 57344, 65278, 65280, 65528,
        65532, 69820, 69822, 69836, 69838, 78895, 78912, 113823, 113828, 119154, 119163, 917504, 917506, 917535, 917632, 1114111,
    ),
    # Alnum: 4744 code points in 213 ranges
    "Alnum": (
        48, 57, 65, 90, 97, 122, 181, 181, 192, 214, 216, 246, 248, 442, 444, 447,
        452, 452, 454, 455, 457, 458, 460, 497, 499, 659, 661, 687, 880, 883, 886, 887,
        891, 893, 895, 895, 902, 902, 904, 906, 908, 908, 910, 929, 931, 1013, 1015, 1153,
        1162, 1327, 1329, 1366, 1376, 1416, 1632, 1641, 1776, 1785, 1984, 1993, 2406, 2415, 2534, 2543,
        2662, 2671, 2790, 2799, 2918, 2927, 3046, 3055, 3174, 3183, 3302, 3311, 3430, 3439, 3558, 3567,
        3664, 3673, 3792, 3801, 3872, 3881, 4160, 4169, 4240, 4249, 4256, 4293, 4295, 4295, 4301, 4301,
        4304, 4346, 4349, 4351, 5024, 5109, 5112, 5117, 6112, 6121, 6160, 6169, 6470, 6479, 6608, 6617,
        6784, 6793, 6800, 6809, 6992, 7001, 7088, 7097, 7232, 7241, 7248, 7257, 7296, 7304, 7312, 7354,
        7357, 7359, 7424, 7467, 7531, 7543, 7545, 7578, 7680, 7957, 7960, 7965, 7968, 8005, 8008, 8013,
        8016, 8023, 8025, 8025, 8027, 8027, 8029, 8029, 8031, 8061, 8064, 8071, 8080, 8087, 8096, 8103,
        8112, 8116, 8118, 8123, 8126, 8126, 8130, 8132, 8134, 8139, 8144, 8147, 8150, 8155, 8160, 8172,
        8178, 8180, 8182, 8187, 8450, 8450, 8455, 8455, 8458, 8467, 8469, 8469, 8473, 8477, 8484, 8484,
        8486, 8486, 8488, 8488, 8490, 8493, 8495, 8500, 8505, 8505, 8508, 8511, 8517, 8521, 8526, 8526,
        8579, 8580, 11264, 11387, 11390, 11492, 11499, 11502, 11506, 11507, 11520, 11557, 11559, 11559, 11565, 11565,
        42528, 42537, 42560, 42605, 42624, 42651, 42786, 42863, 42865, 42887, 42891, 42894, 42896, 42954, 42960, 42961,
        42963, 42963, 42965, 42969, 42997, 42998, 43002, 43002, 43216, 43225, 43264, 43273, 43472, 43481, 43504, 43513,
        43600, 43609, 43824, 43866, 43872, 43880, 43888, 43967, 44016, 44025, 64256, 64262, 64275, 64279, 65296, 65305,
        65313, 65338, 65345, 65370, 66560, 66639, 66720, 66729, 66736, 66771, 66776, 66811, 66928, 66938, 66940, 66954,
        66956, 66962, 66964, 66965, 66967, 66977, 66979, 66993, 66995, 67001, 67003, 67004, 68736, 68786, 68800, 68850,
        68912, 68921, 69734, 69743, 69872, 69881, 69942, 69951, 70096, 70105, 70384, 70393, 70736, 70745, 70864, 70873,
        71248, 71257, 71360, 71369, 71472, 71481, 71840, 71913, 72016, 72025, 72784, 72793, 73040, 73049, 73120, 73129,
        73552, 73561, 92768, 92777, 92864, 92873, 93008, 93017, 93760, 93823, 119808, 119892, 119894, 119964, 119966, 119967,
        119970, 119970, 119973, 119974, 119977, 119980, 119982, 119993, 119995, 119995, 119997, 120003, 120005, 120069, 120071, 120074,
        120077, 120084, 120086, 120092, 120094, 120121, 120123, 120126, 120128, 120132, 120134, 120134, 120138, 120144, 120146, 120485,
        120488, 120512, 120514, 120538, 120540, 120570, 120572, 120596, 120598, 120628, 120630, 120654, 120656, 120686, 120688, 120712,
        120714, 120744, 120746, 120770, 120772, 120779, 120782, 120831, 122624, 122633, 122635, 122654, 122661, 122666, 123200, 123209,
        123632, 123641, 124144, 124153, 125184, 125251, 125264, 125273, 130032, 130041,
    ),
    # Alpha: 4064 code points in 150 ranges
    "Alpha": (
        65, 90, 97, 122, 181, 181, 192, 214, 216, 246, 248, 442, 444, 447, 452, 452,
        454, 455, 457, 458, 460, 497, 499, 659, 661, 687, 880, 883, 886, 887, 891, 893,
        895, 895, 902, 902, 904, 906, 908, 908, 910, 929, 931, 1013, 1015, 1153, 1162, 1327,
        1329, 1366, 1376, 1416, 4256, 4293, 4295, 4295, 4301, 4301, 4304, 4346, 4349, 4351, 5024, 5109,
        5112, 5117, 7296, 7304, 7312, 7354, 7357, 7359, 7424, 7467, 7531, 7543, 7545, 7578, 7680, 7957,
        7960, 7965, 7968, 8005, 8008, 8013, 8016, 8023, 8025, 8025, 8027, 8027, 8029, 8029, 8031, 8061,
        8064, 8071, 8080, 8087, 8096, 8103, 8112, 8116, 8118, 8123, 8126, 8126, 8130, 8132, 8134, 8139,
        8144, 8147, 8150, 8155, 8160, 8172, 8178, 8180, 8182, 8187, 8450, 8450, 8455, 8455, 8458, 8467,
        8469, 8469, 8473, 8477, 8484, 8484, 8486, 8486, 8488, 8488, 8490, 8493, 8495, 8500, 8505, 8505,
        8508, 8511, 8517, 8521, 8526, 8526, 8579, 8580, 11264, 11387, 11390, 11492, 11499, 11502, 11506, 11507,
        11520, 11557, 11559, 11559, 11565, 11565, 42560, 42605, 42624, 42651, 42786, 42863, 42865, 42887, 42891, 42894,
        42896, 42954, 42960, 42961, 42963, 42963, 42965, 42969, 42997, 42998, 43002, 43002, 43824, 43866, 43872, 43880,
        43888, 43967, 64256, 64262, 64275, 64279, 65313, 65338, 65345, 65370, 66560, 66639, 66736, 66771, 66776, 66811,
        66928, 66938, 66940, 66954, 66956, 66962, 66964, 66965, 66967, 66977, 66979, 66993, 66995, 67001, 67003, 67004,
        68736, 68786, 68800, 68850, 71840, 71903, 93760, 93823, 119808, 119892, 119894, 119964, 119966, 119967, 119970, 119970,
        119973, 119974, 119977, 119980, 119982, 119993, 119995, 119995, 119997, 120003, 120005, 120069, 120071, 120074, 120077, 120084,
        120086, 120092, 120094, 120121, 120123, 120126, 120128, 120132, 120134, 120134, 120138, 120144, 120146, 120485, 120488, 120512,
        120514, 120538, 120540, 120570, 120572, 120596, 120598, 120628, 120630, 120654, 120656, 120686, 120688, 120712, 120714, 120744,
        120746, 120770, 120772, 120779, 122624, 122633, 122635, 122654, 122661, 122666, 125184, 125251,
    ),
    # Digit: 680 code points in 64 ranges
    "Digit": (
        48, 57, 1632, 1641, 1776, 1785, 1984, 1993, 2406, 2415, 2534, 2543, 2662, 2671, 2790, 2799,
        2918, 2927, 3046, 3055, 3174, 3183, 3302, 3311, 3430, 3439, 3558, 3567, 3664, 3673, 3792, 3801,
        3872, 3881, 4160, 4169, 4240, 4249, 6112, 6121, 6160, 6169, 6470, 6479, 6608, 6617, 6784, 6793,
        6800, 6809, 6992, 7001, 7088, 7097, 7232, 7241, 7248, 7257, 42528, 42537, 43216, 43225, 43264, 43273,
        43472, 43481, 43504, 43513, 43600, 43609, 44016, 44025, 65296, 65305, 66720, 66729, 68912, 68921, 69734, 69743,
        69872, 69881, 69942, 69951, 70096, 70105, 70384, 70393, 70736, 70745, 70864, 70873, 71248, 71257, 71360, 71369,
        71472, 71481, 71904, 71913, 72016, 72025, 72784, 72793, 73040, 73049, 73120, 73129, 73552, 73561, 92768, 92777,
        92864, 92873, 93008, 93017, 120782, 120831, 123200, 123209, 123632, 123641, 124144, 124153, 125264, 125273, 130032, 130041,
    ),
    # Graph: 1111810 code points in 26 ranges
    "Graph": (
        33, 126, 161, 172, 174, 1535, 1542, 1563, 1565, 1756, 1758, 1806, 1808, 2191, 2194, 2273,
        2275, 5759, 5761, 6157, 6159, 8191, 8208, 8231, 8240, 8286, 8293, 8293, 8304, 12287, 12289, 55295,
        57344, 65278, 65280, 65528, 65532, 69820, 69822, 69836, 69838, 78895, 78912, 113823, 113828, 119154, 119163, 917504,
        917506, 917535, 917632, 1114111,
    ),
    # Lower: 2233 code points in 658 ranges
    "Lower": (
        97, 122, 181, 181, 223, 246, 248, 255, 257, 257, 259, 259, 261, 261, 263, 263,
        265, 265, 267, 267, 269, 269, 271, 271, 273, 273, 275, 275, 277, 277, 279, 279,
        281, 281, 283, 283, 285, 285, 287, 287, 289, 289, 291, 291, 293, 293, 295, 295,
        297, 297, 299, 299, 301, 301, 303, 303, 305, 305, 307, 307, 309, 309, 311, 312,
        314, 314, 316, 316, 318, 318, 320, 320, 322, 322, 324, 324, 326, 326, 328, 329,
        331, 331, 333, 333, 335, 335, 337, 337, 339, 339, 341, 341, 343, 343, 345, 345,
        347, 347, 349, 349, 351, 351, 353, 353, 355, 355, 357, 357, 359, 359, 361, 361,
        363, 363, 365, 365, 367, 367, 369, 369, 371, 371, 373, 373, 375, 375, 378, 378,
        380, 380, 382, 384, 387, 387, 389, 389, 392, 392, 396, 397, 402, 402, 405, 405,
        409, 411, 414, 414, 417, 417, 419, 419, 421, 421, 424, 424, 426, 427, 429, 429,
        432, 432, 436, 436, 438, 438, 441, 442, 445, 447, 454, 454, 457, 457, 460, 460,
        462, 462, 464, 464, 466, 466, 468, 468, 470, 470, 472, 472, 474, 474, 476, 477,
        479, 479, 481, 481, 483, 483, 485, 485, 487, 487, 489, 489, 491, 491, 493, 493,
        495, 496, 499, 499, 501, 501, 505, 505, 507, 507, 509, 509, 511, 511, 513, 513,
        515, 515, 517, 517, 519, 519, 521, 521, 523, 523, 525, 525, 527, 527, 529, 529,
        531, 531, 533, 533, 535, 535, 537, 537, 539, 539, 541, 541, 543, 543, 545, 545,
        547, 547, 549, 549, 551, 551, 553, 553, 555, 555, 557, 557, 559, 559, 561, 561,
        563, 569, 572, 572, 575, 576, 578, 578, 583, 583, 585, 585, 587, 587, 589, 589,
        591, 659, 661, 687, 881, 881, 883, 883, 887, 887, 891, 893, 912, 912, 940, 974,
        976, 977, 981, 983, 985, 985, 987, 987, 989, 989, 991, 991, 993, 993, 995, 995,
        997, 997, 999, 999, 1001, 1001, 1003, 1003, 1005, 1005, 1007, 1011, 1013, 1013, 1016, 1016,
        1019, 1020, 1072, 1119, 1121, 1121, 1123, 1123, 1125, 1125, 1127, 1127, 1129, 1129, 1131, 1131,
        1133, 1133, 1135, 1135, 1137, 1137, 1139, 1139, 1141, 1141, 1143, 1143, 1145, 1145, 1147, 1147,
        1149, 1149, 1151, 1151, 1153, 1153, 1163, 1163, 1165, 1165, 1167, 1167, 1169, 1169, 1171, 1171,
        1173, 1173, 1175, 1175, 1177, 1177, 1179, 1179, 1181, 1181, 1183, 1183, 1185, 1185, 1187, 1187,
        1189, 1189, 1191, 1191, 1193, 1193, 1195, 1195, 1197, 1197, 1199, 1199, 1201, 1201, 1203, 1203,
        1205, 1205, 1207, 1207, 1209, 1209, 1211, 1211, 1213, 1213, 1215, 1215, 1218, 1218, 1220, 1220,
        1222, 1222, 1224, 1224, 1226, 1226, 1228, 1228, 1230, 1231, 1233, 1233, 1235, 1235, 1237, 1237,
        1239, 1239, 1241, 1241, 1243, 1243, 1245, 1245, 1247, 1247, 1249, 1249, 1251, 1251, 1253, 1253,
        1255, 1255, 1257, 1257, 1259, 1259, 1261, 1261, 1263, 1263, 1265, 1265, 1267, 1267, 1269, 1269,
        1271, 1271, 1273, 1273, 1275, 1275, 1277, 1277, 1279, 1279, 1281, 1281, 1283, 1283, 1285, 1285,
        1287, 1287, 1289, 1289, 1291, 1291, 1293, 1293, 1295, 1295, 1297, 1297, 1299, 1299, 1301, 1301,
        1303, 1303, 1305, 1305, 1307, 1307, 1309, 1309, 1311, 1311, 1313, 1313, 1315, 1315, 1317, 1317,
        1319, 1319, 1321, 1321, 1323, 1323, 1325, 1325, 1327, 1327, 1376, 1416, 4304, 4346, 4349, 4351,
        5112, 5117, 7296, 7304, 7424, 7467, 7531, 7543, 7545, 7578, 7681, 7681, 7683, 7683, 7685, 7685,
        7687, 7687, 7689, 7689, 7691, 7691, 7693, 7693, 7695, 7695, 7697, 7697, 7699, 7699, 7701, 7701,
        7703, 7703, 7705, 7705, 7707, 7707, 7709, 7709, 7711, 7711, 7713, 7713, 7715, 7715, 7717, 7717,
        7719, 7719, 7721, 7721, 7723, 7723, 7725, 7725, 7727, 7727, 7729, 7729, 7731, 7731, 7733, 7733,
        7735, 7735, 7737, 7737, 7739, 7739, 7741, 7741, 7743, 7743, 7745, 7745, 7747, 7747, 7749, 7749,
        7751, 7751, 7753, 7753, 7755, 7755, 7757, 7757, 7759, 7759, 7761, 7761, 7763, 7763, 7765, 7765,
        7767, 7767, 7769, 7769, 7771, 7771, 7773, 7773, 7775, 7775, 7777, 7777, 7779, 7779, 7781, 7781,
        7783, 7783, 7785, 7785, 7787, 7787, 7789, 7789, 7791, 7791, 7793, 7793, 7795, 7795, 7797, 7797,
        7799, 7799, 7801, 7801, 7803, 7803, 7805, 7805, 7807, 7807, 7809, 7809, 7811, 7811, 7813, 7813,
        7815, 7815, 7817, 7817, 7819, 7819, 7821, 7821, 7823, 7823, 7825, 7825, 7827, 7827, 7829, 7837,
        7839, 7839, 7841, 7841, 7843, 7843, 7845, 7845, 7847, 7847, 7849, 7849, 7851, 7851, 7853, 7853,
        7855, 7855, 7857, 7857, 7859, 7859, 7861, 7861, 7863, 7863, 7865, 7865, 7867, 7867, 7869, 7869,
        7871, 7871, 7873, 7873, 7875, 7875, 7877, 7877, 7879, 7879, 7881, 7881, 7883, 7883, 7885, 7885,
        7887, 7887, 7889, 7889, 7891, 7891, 7893, 7893, 7895, 7895, 7897, 7897, 7899, 7899, 7901, 7901,
        7903, 7903, 7905, 7905, 7907, 7907, 7909, 7909, 7911, 7911, 7913, 7913, 7915, 7915, 7917, 7917,
        7919, 7919, 7921, 7921, 7923, 7923, 7925, 7925, 7927, 7927, 7929, 7929, 7931, 7931, 7933, 7933,
        7935, 7943, 7952, 7957, 7968, 7975, 7984, 7991, 8000, 8005, 8016, 8023, 8032, 8039, 8048, 8061,
        8064, 8071, 8080, 8087, 8096, 8103, 8112, 8116, 8118, 8119, 8126, 8126, 8130, 8132, 8134, 8135,
        8144, 8147, 8150, 8151, 8160, 8167, 8178, 8180, 8182, 8183, 8458, 8458, 8462, 8463, 8467, 8467,
        8495, 8495, 8500, 8500, 8505, 8505, 8508, 8509, 8518, 8521, 8526, 8526, 8580, 8580, 11312, 11359,
        11361, 11361, 11365, 11366, 11368, 11368, 11370, 11370, 11372, 11372, 11377, 11377, 11379, 11380, 11382, 11387,
        11393, 11393, 11395, 11395, 11397, 11397, 11399, 11399, 11401, 11401, 11403, 11403, 11405, 11405, 11407, 11407,
        11409, 11409, 11411, 11411, 11413, 11413, 11415, 11415, 11417, 11417, 11419, 11419, 11421, 11421, 11423, 11423,
        11425, 11425, 11427, 11427, 11429, 11429, 11431, 11431, 11433, 11433, 11435, 11435, 11437, 11437, 11439, 11439,
        11441, 11441, 11443, 11443, 11445, 11445, 11447, 11447, 11449, 11449, 11451, 11451, 11453, 11453, 11455, 11455,
        11457, 11457, 11459, 11459, 11461, 11461, 11463, 11463, 11465, 11465, 11467, 11467, 11469, 11469, 11471, 11471,
        11473, 11473, 11475, 11475, 11477, 11477, 11479, 11479, 11481, 11481, 11483, 11483, 11485, 11485, 11487, 11487,
        11489, 11489, 11491, 11492, 11500, 11500, 11502, 11502, 11507, 11507, 11520, 11557, 11559, 11559, 11565, 11565,
        42561, 42561, 42563, 42563, 42565, 42565, 42567, 42567, 42569, 42569, 42571, 42571, 42573, 42573, 42575, 42575,
        42577, 42577, 42579, 42579, 42581, 42581, 42583, 42583, 42585, 42585, 42587, 42587, 42589, 42589, 42591, 42591,
        42593, 42593, 42595, 42595, 42597, 42597, 42599, 42599, 42601, 42601, 42603, 42603, 42605, 42605, 42625, 42625,
        42627, 42627, 42629, 42629, 42631, 42631, 42633, 42633, 42635, 42635, 42637, 42637, 42639, 42639, 42641, 42641,
        42643, 42643, 42645, 42645, 42647, 42647, 42649, 42649, 42651, 42651, 42787, 42787, 42789, 42789, 42791, 42791,
        42793, 42793, 42795, 42795, 42797, 42797, 42799, 42801, 42803, 42803, 42805, 42805, 42807, 42807, 42809, 42809,
        42811, 42811, 42813, 42813, 42815, 42815, 42817, 42817, 42819, 42819, 42821, 42821, 42823, 42823, 42825, 42825,
        42827, 42827, 42829, 42829, 42831, 42831, 42833, 42833, 42835, 42835, 42837, 42837, 42839, 42839, 42841, 42841,
        42843, 42843, 42845, 42845, 42847, 42847, 42849, 42849, 42851, 42851, 42853, 42853, 42855, 42855, 42857, 42857,
        42859, 42859, 42861, 42861, 42863, 42863, 42865, 42872, 42874, 42874, 42876, 42876, 42879, 42879, 42881, 42881,
        42883, 42883, 42885, 42885, 42887, 42887, 42892, 42892, 42894, 42894, 42897, 42897, 42899, 42901, 42903, 42903,
        42905, 42905, 42907, 42907, 42909, 42909, 42911, 42911, 42913, 42913, 42915, 42915, 42917, 42917, 42919, 42919,
        42921, 42921, 42927, 42927, 42933, 42933, 42935, 42935, 42937, 42937, 42939, 42939, 42941, 42941, 42943, 42943,
        42945, 42945, 42947, 42947, 42952, 42952, 42954, 42954, 42961, 42961, 42963, 42963, 42965, 42965, 42967, 42967,
        42969, 42969, 42998, 42998, 43002, 43002, 43824, 43866, 43872, 43880, 43888, 43967, 64256, 64262, 64275, 64279,
        65345, 65370, 66600, 66639, 66776, 66811, 66967, 66977, 66979, 66993, 66995, 67001, 67003, 67004, 68800, 68850,
        71872, 71903, 93792, 93823, 119834, 119859, 119886, 119892, 119894, 119911, 119938, 119963, 119990, 119993, 119995, 119995,
        119997, 120003, 120005, 120015, 120042, 120067, 120094, 120119, 120146, 120171, 120198, 120223, 120250, 120275, 120302, 120327,
        120354, 120379, 120406, 120431, 120458, 120485, 120514, 120538, 120540, 120545, 120572, 120596, 120598, 120603, 120630, 120654,
        120656, 120661, 120688, 120712, 120714, 120719, 120746, 120770, 120772, 120777, 120779, 120779, 122624, 122633, 122635, 122654,
        122661, 122666, 125218, 125251,
    ),
    # Punct: 842 code points in 191 ranges
    "Punct": (
        33, 35, 37, 42, 44, 47, 58, 59, 63, 64, 91, 93, 95, 95, 123, 123,
        125, 125, 161, 161, 167, 167, 171, 171, 182, 183, 187, 187, 191, 191, 894, 894,
        903, 903, 1370, 1375, 1417, 1418, 1470, 1470, 1472, 1472, 1475, 1475, 1478, 1478, 1523, 1524,
        1545, 1546, 1548, 1549, 1563, 1563, 1565, 1567, 1642, 1645, 1748, 1748, 1792, 1805, 2039, 2041,
        2096, 2110, 2142, 2142, 2404, 2405, 2416, 2416, 2557, 2557, 2678, 2678, 2800, 2800, 3191, 3191,
        3204, 3204, 3572, 3572, 3663, 3663, 3674, 3675, 3844, 3858, 3860, 3860, 3898, 3901, 3973, 3973,
        4048, 4052, 4057, 4058, 4170, 4175, 4347, 4347, 4960, 4968, 5120, 5120, 5742, 5742, 5787, 5788,
        5867, 5869, 5941, 5942, 6100, 6102, 6104, 6106, 6144, 6154, 6468, 6469, 6686, 6687, 6816, 6822,
        6824, 6829, 7002, 7008, 7037, 7038, 7164, 7167, 7227, 7231, 7294, 7295, 7360, 7367, 7379, 7379,
        8208, 8231, 8240, 8259, 8261, 8273, 8275, 8286, 8317, 8318, 8333, 8334, 8968, 8971, 9001, 9002,
        10088, 10101, 10181, 10182, 10214, 10223, 10627, 10648, 10712, 10715, 10748, 10749, 11513, 11516, 11518, 11519,
        11632, 11632, 11776, 11822, 11824, 11855, 11858, 11869, 12289, 12291, 12296, 12305, 12308, 12319, 12336, 12336,
        12349, 12349, 12448, 12448, 12539, 12539, 42238, 42239, 42509, 42511, 42611, 42611, 42622, 42622, 42738, 42743,
        43124, 43127, 43214, 43215, 43256, 43258, 43260, 43260, 43310, 43311, 43359, 43359, 43457, 43469, 43486, 43487,
        43612, 43615, 43742, 43743, 43760, 43761, 44011, 44011, 64830, 64831, 65040, 65049, 65072, 65106, 65108, 65121,
        65123, 65123, 65128, 65128, 65130, 65131, 65281, 65283, 65285, 65290, 65292, 65295, 65306, 65307, 65311, 65312,
        65339, 65341, 65343, 65343, 65371, 65371, 65373, 65373, 65375, 65381, 65792, 65794, 66463, 66463, 66512, 66512,
        66927, 66927, 67671, 67671, 67871, 67871, 67903, 67903, 68176, 68184, 68223, 68223, 68336, 68342, 68409, 68415,
        68505, 68508, 69293, 69293, 69461, 69465, 69510, 69513, 69703, 69709, 69819, 69820, 69822, 69825, 69952, 69955,
        70004, 70005, 70085, 70088, 70093, 70093, 70107, 70107, 70109, 70111, 70200, 70205, 70313, 70313, 70731, 70735,
        70746, 70747, 70749, 70749, 70854, 70854, 71105, 71127, 71233, 71235, 71264, 71276, 71353, 71353, 71484, 71486,
        71739, 71739, 72004, 72006, 72162, 72162, 72255, 72262, 72346, 72348, 72350, 72354, 72448, 72457, 72769, 72773,
        72816, 72817, 73463, 73464, 73539, 73551, 73727, 73727, 74864, 74868, 77809, 77810, 92782, 92783, 92917, 92917,
        92983, 92987, 92996, 92996, 93847, 93850, 94178, 94178, 113823, 113823, 121479, 121483, 125278, 125279,
    ),
    # Upper: 1831 code points in 646 ranges
    "Upper": (
        65, 90, 192, 214, 216, 222, 256, 256, 258, 258, 260, 260, 262, 262, 264, 264,
        266, 266, 268, 268, 270, 270, 272, 272, 274, 274, 276, 276, 278, 278, 280, 280,
        282, 282, 284, 284, 286, 286, 288, 288, 290, 290, 292, 292, 294, 294, 296, 296,
        298, 298, 300, 300, 302, 302, 304, 304, 306, 306, 308, 308, 310, 310, 313, 313,
        315, 315, 317, 317, 319, 319, 321, 321, 323, 323, 325, 325, 327, 327, 330, 330,
        332, 332, 334, 334, 336, 336, 338, 338, 340, 340, 342, 342, 344, 344, 346, 346,
        348, 348, 350, 350, 352, 352, 354, 354, 356, 356, 358, 358, 360, 360, 362, 362,
        364, 364, 366, 366, 368, 368, 370, 370, 372, 372, 374, 374, 376, 377, 379, 379,
        381, 381, 385, 386, 388, 388, 390, 391, 393, 395, 398, 401, 403, 404, 406, 408,
        412, 413, 415, 416, 418, 418, 420, 420, 422, 423, 425, 425, 428, 428, 430, 431,
        433, 435, 437, 437, 439, 440, 444, 444, 452, 452, 455, 455, 458, 458, 461, 461,
        463, 463, 465, 465, 467, 467, 469, 469, 471, 471, 473, 473, 475, 475, 478, 478,
        480, 480, 482, 482, 484, 484, 486, 486, 488, 488, 490, 490, 492, 492, 494, 494,
        497, 497, 500, 500, 502, 504, 506, 506, 508, 508, 510, 510, 512, 512, 514, 514,
        516, 516, 518, 518, 520, 520, 522, 522, 524, 524, 526, 526, 528, 528, 530, 530,
        532, 532, 534, 534, 536, 536, 538, 538, 540, 540, 542, 542, 544, 544, 546, 546,
        548, 548, 550, 550, 552, 552, 554, 554, 556, 556, 558, 558, 560, 560, 562, 562,
        570, 571, 573, 574, 577, 577, 579, 582, 584, 584, 586, 586, 588, 588, 590, 590,
        880, 880, 882, 882, 886, 886, 895, 895, 902, 902, 904, 906, 908, 908, 910, 911,
        913, 929, 931, 939, 975, 975, 978, 980, 984, 984, 986, 986, 988, 988, 990, 990,
        992, 992, 994, 994, 996, 996, 998, 998, 1000, 1000, 1002, 1002, 1004, 1004, 1006, 1006,
        1012, 1012, 1015, 1015, 1017, 1018, 1021, 1071, 1120, 1120, 1122, 1122, 1124, 1124, 1126, 1126,
        1128, 1128, 1130, 1130, 1132, 1132, 1134, 1134, 1136, 1136, 1138, 1138, 1140, 1140, 1142, 1142,
        1144, 1144, 1146, 1146, 1148, 1148, 1150, 1150, 1152, 1152, 1162, 1162, 1164, 1164, 1166, 1166,
        1168, 1168, 1170, 1170, 1172, 1172, 1174, 1174, 1176, 1176, 1178, 1178, 1180, 1180, 1182, 1182,
        1184, 1184, 1186, 1186, 1188, 1188, 1190, 1190, 1192, 1192, 1194, 1194, 1196, 1196, 1198, 1198,
        1200, 1200, 1202, 1202, 1204, 1204, 1206, 1206, 1208, 1208, 1210, 1210, 1212, 1212, 1214, 1214,
        1216, 1217, 1219, 1219, 1221, 1221, 1223, 1223, 1225, 1225, 1227, 1227, 1229, 1229, 1232, 1232,
        1234, 1234, 1236, 1236, 1238, 1238, 1240, 1240, 1242, 1242, 1244, 1244, 1246, 1246, 1248, 1248,
        1250, 1250, 1252, 1252, 1254, 1254, 1256, 1256, 1258, 1258, 1260, 1260, 1262, 1262, 1264, 1264,
        1266, 1266, 1268, 1268, 1270, 1270, 1272, 1272, 1274, 1274, 1276, 1276, 1278, 1278, 1280, 1280,
        1282, 1282, 1284, 1284, 1286, 1286, 1288, 1288, 1290, 1290, 1292, 1292, 1294, 1294, 1296, 1296,
        1298, 1298, 1300, 1300, 1302, 1302, 1304, 1304, 1306, 1306, 1308, 1308, 1310, 1310, 1312, 1312,
        1314, 1314, 1316, 1316, 1318, 1318, 1320, 1320, 1322, 1322, 1324, 1324, 1326, 1326, 1329, 1366,
        4256, 4293, 4295, 4295, 4301, 4301, 5024, 5109, 7312, 7354, 7357, 7359, 7680, 7680, 7682, 7682,
        7684, 7684, 7686, 7686, 7688, 7688, 7690, 7690, 7692, 7692, 7694, 7694, 7696, 7696, 7698, 7698,
        7700, 7700, 7702, 7702, 7704, 7704, 7706, 7706, 7708, 7708, 7710, 7710, 7712, 7712, 7714, 7714,
        7716, 7716, 7718, 7718, 7720, 7720, 7722, 7722, 7724, 7724, 7726, 7726, 7728, 7728, 7730, 7730,
        7732, 7732, 7734, 7734, 7736, 7736, 7738, 7738, 7740, 7740, 7742, 7742, 7744, 7744, 7746, 7746,
        7748, 7748, 7750, 7750, 7752, 7752, 7754, 7754, 7756, 7756, 7758, 7758, 7760, 7760, 7762, 7762,
        7764, 7764, 7766, 7766, 7768, 7768, 7770, 7770, 7772, 7772, 7774, 7774, 7776, 7776, 7778, 7778,
        7780, 7780, 7782, 7782, 7784, 7784, 7786, 7786, 7788, 7788, 7790, 7790, 7792, 7792, 7794, 7794,
        7796, 7796, 7798, 7798, 7800, 7800, 7802, 7802, 7804, 7804, 7806, 7806, 7808, 7808, 7810, 7810,
        7812, 7812, 7814, 7814, 7816, 7816, 7818, 7818, 7820, 7820, 7822, 7822, 7824, 7824, 7826, 7826,
        7828, 7828, 7838, 7838, 7840, 7840, 7842, 7842, 7844, 7844, 7846, 7846, 7848, 7848, 7850, 7850,
        7852, 7852, 7854, 7854, 7856, 7856, 7858, 7858, 7860, 7860, 7862, 7862, 7864, 7864, 7866, 7866,
        7868, 7868, 7870, 7870, 7872, 7872, 7874, 7874, 7876, 7876, 7878, 7878, 7880, 7880, 7882, 7882,
        7884, 7884, 7886, 7886, 7888, 7888, 7890, 7890, 7892, 7892, 7894, 7894, 7896, 7896, 7898, 7898,
        7900, 7900, 7902, 7902, 7904, 7904, 7906, 7906, 7908, 7908, 7910, 7910, 7912, 7912, 7914, 7914,
        7916, 7916, 7918, 7918, 7920, 7920, 7922, 7922, 7924, 7924, 7926, 7926, 7928, 7928, 7930, 7930,
        7932, 7932, 7934, 7934, 7944, 7951, 7960, 7965, 7976, 7983, 7992, 7999, 8008, 8013, 8025, 8025,
        8027, 8027, 8029, 8029, 8031, 8031, 8040, 8047, 8120, 8123, 8136, 8139, 8152, 8155, 8168, 8172,
        8184, 8187, 8450, 8450, 8455, 8455, 8459, 8461, 8464, 8466, 8469, 8469, 8473, 8477, 8484, 8484,
        8486, 8486, 8488, 8488, 8490, 8493, 8496, 8499, 8510, 8511, 8517, 8517, 8579, 8579, 11264, 11311,
        11360, 11360, 11362, 11364, 11367, 11367, 11369, 11369, 11371, 11371, 11373, 11376, 11378, 11378, 11381, 11381,
        11390, 11392, 11394, 11394, 11396, 11396, 11398, 11398, 11400, 11400, 11402, 11402, 11404, 11404, 11406, 11406,
        11408, 11408, 11410, 11410, 11412, 11412, 11414, 11414, 11416, 11416, 11418, 11418, 11420, 11420, 11422, 11422,
        11424, 11424, 11426, 11426, 11428, 11428, 11430, 11430, 11432, 11432, 11434, 11434, 11436, 11436, 11438, 11438,
        11440, 11440, 11442, 11442, 11444, 11444, 11446, 11446, 11448, 11448, 11450, 11450, 11452, 11452, 11454, 11454,
        11456, 11456, 11458, 11458, 11460, 11460, 11462, 11462, 11464, 11464, 11466, 11466, 11468, 11468, 11470, 11470,
        11472, 11472, 11474, 11474, 11476, 11476, 11478, 11478, 11480, 11480, 11482, 11482, 11484, 11484, 11486, 11486,
        11488, 11488, 11490, 11490, 11499, 11499, 11501, 11501, 11506, 11506, 42560, 42560, 42562, 42562, 42564, 42564,
        42566, 42566, 42568, 42568, 42570, 42570, 42572, 42572, 42574, 42574, 42576, 42576, 42578, 42578, 42580, 42580,
        42582, 42582, 42584, 42584, 42586, 42586, 42588, 42588, 42590, 42590, 42592, 42592, 42594, 42594, 42596, 42596,
        42598, 42598, 42600, 42600, 42602, 42602, 42604, 42604, 42624, 42624, 42626, 42626, 42628, 42628, 42630, 42630,
        42632, 42632, 42634, 42634, 42636, 42636, 42638, 42638, 42640, 42640, 42642, 42642, 42644, 42644, 42646, 42646,
        42648, 42648, 42650, 42650, 42786, 42786, 42788, 42788, 42790, 42790, 42792, 42792, 42794, 42794, 42796, 42796,
        42798, 42798, 42802, 42802, 42804, 42804, 42806, 42806, 42808, 42808, 42810, 42810, 42812, 42812, 42814, 42814,
        42816, 42816, 42818, 42818, 42820, 42820, 42822, 42822, 42824, 42824, 42826, 42826, 42828, 42828, 42830, 42830,
        42832, 42832, 42834, 42834, 42836, 42836, 42838, 42838, 42840, 42840, 42842, 42842, 42844, 42844, 42846, 42846,
        42848, 42848, 42850, 42850, 42852, 42852, 42854, 42854, 42856, 42856, 42858, 42858, 42860, 42860, 42862, 42862,
        42873, 42873, 42875, 42875, 42877, 42878, 42880, 42880, 42882, 42882, 42884, 42884, 42886, 42886, 42891, 42891,
        42893, 42893, 42896, 42896, 42898, 42898, 42902, 42902, 42904, 42904, 42906, 42906, 42908, 42908, 42910, 42910,
        42912, 42912, 42914, 42914, 42916, 42916, 42918, 42918, 42920, 42920, 42922, 42926, 42928, 42932, 42934, 42934,
        42936, 42936, 42938, 42938, 42940, 42940, 42942, 42942, 42944, 42944, 42946, 42946, 42948, 42951, 42953, 42953,
        42960, 42960, 42966, 42966, 42968, 42968, 42997, 42997, 65313, 65338, 66560, 66599, 66736, 66771, 66928, 66938,
        66940, 66954, 66956, 66962, 66964, 66965, 68736, 68786, 71840, 71871, 93760, 93791, 119808, 119833, 119860, 119885,
        119912, 119937, 119964, 119964, 119966, 119967, 119970, 119970, 119973, 119974, 119977, 119980, 119982, 119989, 120016, 120041,
        120068, 120069, 120071, 120074, 120077, 120084, 120086, 120092, 120120, 120121, 120123, 120126, 120128, 120132, 120134, 120134,
        120138, 120144, 120172, 120197, 120224, 120249, 120276, 120301, 120328, 120353, 120380, 120405, 120432, 120457, 120488, 120512,
        120546, 120570, 120604, 120628, 120662, 120686, 120720, 120744, 120778, 120778, 125184, 125217,
    ),
    # Word: 137416 code points in 712 ranges
    "Word": (
        48, 57, 65, 90, 95, 95, 97, 122, 170, 170, 181, 181, 186, 186, 192, 214,
        216, 246, 248, 705, 710, 721, 736, 740, 748, 748, 750, 750, 880, 884, 886, 887,
        890, 893, 895, 895, 902, 902, 904, 906, 908, 908, 910, 929, 931, 1013, 1015, 1153,
        1162, 1327, 1329, 1366, 1369, 1369, 1376, 1416, 1488, 1514, 1519, 1522, 1568, 1610, 1632, 1641,
        1646, 1647, 1649, 1747, 1749, 1749, 1765, 1766, 1774, 1788, 1791, 1791, 1808, 1808, 1810, 1839,
        1869, 1957, 1969, 1969, 1984, 2026, 2036, 2037, 2042, 2042, 2048, 2069, 2074, 2074, 2084, 2084,
        2088, 2088, 2112, 2136, 2144, 2154, 2160, 2183, 2185, 2190, 2208, 2249, 2308, 2361, 2365, 2365,
        2384, 2384, 2392, 2401, 2406, 2415, 2417, 2432, 2437, 2444, 2447, 2448, 2451, 2472, 2474, 2480,
        2482, 2482, 2486, 2489, 2493, 2493, 2510, 2510, 2524, 2525, 2527, 2529, 2534, 2545, 2556, 2556,
        2565, 2570, 2575, 2576, 2579, 2600, 2602, 2608, 2610, 2611, 2613, 2614, 2616, 2617, 2649, 2652,
        2654, 2654, 2662, 2671, 2674, 2676, 2693, 2701, 2703, 2705, 2707, 2728, 2730, 2736, 2738, 2739,
        2741, 2745, 2749, 2749, 2768, 2768, 2784, 2785, 2790, 2799, 2809, 2809, 2821, 2828, 2831, 2832,
        2835, 2856, 2858, 2864, 2866, 2867, 2869, 2873, 2877, 2877, 2908, 2909, 2911, 2913, 2918, 2927,
        2929, 2929, 2947, 2947, 2949, 2954, 2958, 2960, 2962, 2965, 2969, 2970, 2972, 2972, 2974, 2975,
        2979, 2980, 2984, 2986, 2990, 3001, 3024, 3024, 3046, 3055, 3077, 3084, 3086, 3088, 3090, 3112,
        3114, 3129, 3133, 3133, 3160, 3162, 3165, 3165, 3168, 3169, 3174, 3183, 3200, 3200, 3205, 3212,
        3214, 3216, 3218, 3240, 3242, 3251, 3253, 3257, 3261, 3261, 3293, 3294, 3296, 3297, 3302, 3311,
        3313, 3314, 3332, 3340, 3342, 3344, 3346, 3386, 3389, 3389, 3406, 3406, 3412, 3414, 3423, 3425,
        3430, 3439, 3450, 3455, 3461, 3478, 3482, 3505, 3507, 3515, 3517, 3517, 3520, 3526, 3558, 3567,
        3585, 3632, 3634, 3635, 3648, 3654, 3664, 3673, 3713, 3714, 3716, 3716, 3718, 3722, 3724, 3747,
        3749, 3749, 3751, 3760, 3762, 3763, 3773, 3773, 3776, 3780, 3782, 3782, 3792, 3801, 3804, 3807,
        3840, 3840, 3872, 3881, 3904, 3911, 3913, 3948, 3976, 3980, 4096, 4138, 4159, 4169, 4176, 4181,
        4186, 4189, 4193, 4193, 4197, 4198, 4206, 4208, 4213, 4225, 4238, 4238, 4240, 4249, 4256, 4293,
        4295, 4295, 4301, 4301, 4304, 4346, 4348, 4680, 4682, 4685, 4688, 4694, 4696, 4696, 4698, 4701,
        4704, 4744, 4746, 4749, 4752, 4784, 4786, 4789, 4792, 4798, 4800, 4800, 4802, 4805, 4808, 4822,
        4824, 4880, 4882, 4885, 4888, 4954, 4992, 5007, 5024, 5109, 5112, 5117, 5121, 5740, 5743, 5759,
        5761, 5786, 5792, 5866, 5873, 5880, 5888, 5905, 5919, 5937, 5952, 5969, 5984, 5996, 5998, 6000,
        6016, 6067, 6103, 6103, 6108, 6108, 6112, 6121, 6160, 6169, 6176, 6264, 6272, 6276, 6279, 6312,
        6314, 6314, 6320, 6389, 6400, 6430, 6470, 6509, 6512, 6516, 6528, 6571, 6576, 6601, 6608, 6617,
        6656, 6678, 6688, 6740, 6784, 6793, 6800, 6809, 6823, 6823, 6917, 6963, 6981, 6988, 6992, 7001,
        7043, 7072, 7086, 7141, 7168, 7203, 7232, 7241, 7245, 7293, 7296, 7304, 7312, 7354, 7357, 7359,
        7401, 7404, 7406, 7411, 7413, 7414, 7418, 7418, 7424, 7615, 7680, 7957, 7960, 7965, 7968, 8005,
        8008, 8013, 8016, 8023, 8025, 8025, 8027, 8027, 8029, 8029, 8031, 8061, 8064, 8116, 8118, 8124,
        8126, 8126, 8130, 8132, 8134, 8140, 8144, 8147, 8150, 8155, 8160, 8172, 8178, 8180, 8182, 8188,
        8255, 8256, 8276, 8276, 8305, 8305, 8319, 8319, 8336, 8348, 8450, 8450, 8455, 8455, 8458, 8467,
        8469, 8469, 8473, 8477, 8484, 8484, 8486, 8486, 8488, 8488, 8490, 8493, 8495, 8505, 8508, 8511,
        8517, 8521, 8526, 8526, 8579, 8580, 11264, 11492, 11499, 11502, 11506, 11507, 11520, 11557, 11559, 11559,
        11565, 11565, 11568, 11623, 11631, 11631, 11648, 11670, 11680, 11686, 11688, 11694, 11696, 11702, 11704, 11710,
        11712, 11718, 11720, 11726, 11728, 11734, 11736, 11742, 11823, 11823, 12293, 12294, 12337, 12341, 12347, 12348,
        12353, 12438, 12445, 12447, 12449, 12538, 12540, 12543, 12549, 12591, 12593, 12686, 12704, 12735, 12784, 12799,
        13312, 19903, 19968, 42124, 42192, 42237, 42240, 42508, 42512, 42539, 42560, 42606, 42623, 42653, 42656, 42725,
        42775, 42783, 42786, 42888, 42891, 42954, 42960, 42961, 42963, 42963, 42965, 42969, 42994, 43009, 43011, 43013,
        43015, 43018, 43020, 43042, 43072, 43123, 43138, 43187, 43216, 43225, 43250, 43255, 43259, 43259, 43261, 43262,
        43264, 43301, 43312, 43334, 43360, 43388, 43396, 43442, 43471, 43481, 43488, 43492, 43494, 43518, 43520, 43560,
        43584, 43586, 43588, 43595, 43600, 43609, 43616, 43638, 43642, 43642, 43646, 43695, 43697, 43697, 43701, 43702,
        43705, 43709, 43712, 43712, 43714, 43714, 43739, 43741, 43744, 43754, 43762, 43764, 43777, 43782, 43785, 43790,
        43793, 43798, 43808, 43814, 43816, 43822, 43824, 43866, 43868, 43881, 43888, 44002, 44016, 44025, 44032, 55203,
        55216, 55238, 55243, 55291, 63744, 64109, 64112, 64217, 64256, 64262, 64275, 64279, 64285, 64285, 64287, 64296,
        64298, 64310, 64312, 64316, 64318, 64318, 64320, 64321, 64323, 64324, 64326, 64433, 64467, 64829, 64848, 64911,
        64914, 64967, 65008, 65019, 65075, 65076, 65101, 65103, 65136, 65140, 65142, 65276, 65296, 65305, 65313, 65338,
        65343, 65343, 65345, 65370, 65382, 65470, 65474, 65479, 65482, 65487, 65490, 65495, 65498, 65500, 65536, 65547,
        65549, 65574, 65576, 65594, 65596, 65597, 65599, 65613, 65616, 65629, 65664, 65786, 66176, 66204, 66208, 66256,
        66304, 66335, 66349, 66368, 66370, 66377, 66384, 66421, 66432, 66461, 66464, 66499, 66504, 66511, 66560, 66717,
        66720, 66729, 66736, 66771, 66776, 66811, 66816, 66855, 66864, 66915, 66928, 66938, 66940, 66954, 66956, 66962,
        66964, 66965, 66967, 66977, 66979, 66993, 66995, 67001, 67003, 67004, 67072, 67382, 67392, 67413, 67424, 67431,
        67456, 67461, 67463, 67504, 67506, 67514, 67584, 67589, 67592, 67592, 67594, 67637, 67639, 67640, 67644, 67644,
        67647, 67669, 67680, 67702, 67712, 67742, 67808, 67826, 67828, 67829, 67840, 67861, 67872, 67897, 67968, 68023,
        68030, 68031, 68096, 68096, 68112, 68115, 68117, 68119, 68121, 68149, 68192, 68220, 68224, 68252, 68288, 68295,
        68297, 68324, 68352, 68405, 68416, 68437, 68448, 68466, 68480, 68497, 68608, 68680, 68736, 68786, 68800, 68850,
        68864, 68899, 68912, 68921, 69248, 69289, 69296, 69297, 69376, 69404, 69415, 69415, 69424, 69445, 69488, 69505,
        69552, 69572, 69600, 69622, 69635, 69687, 69734, 69743, 69745, 69746, 69749, 69749, 69763, 69807, 69840, 69864,
        69872, 69881, 69891, 69926, 69942, 69951, 69956, 69956, 69959, 69959, 69968, 70002, 70006, 70006, 70019, 70066,
        70081, 70084, 70096, 70106, 70108, 70108, 70144, 70161, 70163, 70187, 70207, 70208, 70272, 70278, 70280, 70280,
        70282, 70285, 70287, 70301, 70303, 70312, 70320, 70366, 70384, 70393, 70405, 70412, 70415, 70416, 70419, 70440,
        70442, 70448, 70450, 70451, 70453, 70457, 70461, 70461, 70480, 70480, 70493, 70497, 70656, 70708, 70727, 70730,
        70736, 70745, 70751, 70753, 70784, 70831, 70852, 70853, 70855, 70855, 70864, 70873, 71040, 71086, 71128, 71131,
        71168, 71215, 71236, 71236, 71248, 71257, 71296, 71338, 71352, 71352, 71360, 71369, 71424, 71450, 71472, 71481,
        71488, 71494, 71680, 71723, 71840, 71913, 71935, 71942, 71945, 71945, 71948, 71955, 71957, 71958, 71960, 71983,
        71999, 71999, 72001, 72001, 72016, 72025, 72096, 72103, 72106, 72144, 72161, 72161, 72163, 72163, 72192, 72192,
        72203, 72242, 72250, 72250, 72272, 72272, 72284, 72329, 72349, 72349, 72368, 72440, 72704, 72712, 72714, 72750,
        72768, 72768, 72784, 72793, 72818, 72847, 72960, 72966, 72968, 72969, 72971, 73008, 73030, 73030, 73040, 73049,
        73056, 73061, 73063, 73064, 73066, 73097, 73112, 73112, 73120, 73129, 73440, 73458, 73474, 73474, 73476, 73488,
        73490, 73523, 73552, 73561, 73648, 73648, 73728, 74649, 74880, 75075, 77712, 77808, 77824, 78895, 78913, 78918,
        82944, 83526, 92160, 92728, 92736, 92766, 92768, 92777, 92784, 92862, 92864, 92873, 92880, 92909, 92928, 92975,
        92992, 92995, 93008, 93017, 93027, 93047, 93053, 93071, 93760, 93823, 93952, 94026, 94032, 94032, 94099, 94111,
        94176, 94177, 94179, 94179, 94208, 100343, 100352, 101589, 101632, 101640, 110576, 110579, 110581, 110587, 110589, 110590,
        110592, 110882, 110898, 110898, 110928, 110930, 110933, 110933, 110948, 110951, 110960, 111355, 113664, 113770, 113776, 113788,
        113792, 113800, 113808, 113817, 119808, 119892, 119894, 119964, 119966, 119967, 119970, 119970, 119973, 119974, 119977, 119980,
        119982, 119993, 119995, 119995, 119997, 120003, 120005, 120069, 120071, 120074, 120077, 120084, 120086, 120092, 120094, 120121,
        120123, 120126, 120128, 120132, 120134, 120134, 120138, 120144, 120146, 120485, 120488, 120512, 120514, 120538, 120540, 120570,
        120572, 120596, 120598, 120628, 120630, 120654, 120656, 120686, 120688, 120712, 120714, 120744, 120746, 120770, 120772, 120779,
        120782, 120831, 122624, 122654, 122661, 122666, 122928, 122989, 123136, 123180, 123191, 123197, 123200, 123209, 123214, 123214,
        123536, 123565, 123584, 123627, 123632, 123641, 124112, 124139, 124144, 124153, 124896, 124902, 124904, 124907, 124909, 124910,
        124912, 124926, 124928, 125124, 125184, 125251, 125259, 125259, 125264, 125273, 126464, 126467, 126469, 126495, 126497, 126498,
        126500, 126500, 126503, 126503, 126505, 126514, 126516, 126519, 126521, 126521, 126523, 126523, 126530, 126530, 126535, 126535,
        126537, 126537, 126539, 126539, 126541, 126543, 126545, 126546, 126548, 126548, 126551, 126551, 126553, 126553, 126555, 126555,
        126557, 126557, 126559, 126559, 126561, 126562, 126564, 126564, 126567, 126570, 126572, 126578, 126580, 126583, 126585, 126588,
        126590, 126590, 126592, 126601, 126603, 126619, 126625, 126627, 126629, 126633, 126635, 126651, 130032, 130041, 131072, 173791,
        173824, 177977, 177984, 178205, 178208, 183969, 183984, 191456, 191472, 192093, 194560, 195101, 196608, 201546, 201552, 205743,
    ),
}
# END UNICODE CLASSES


if __name__ == "__main__":
    main(sys.argv[1:])