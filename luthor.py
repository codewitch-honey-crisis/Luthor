#!/usr/bin/env python3
"""Luthor: a lexer generator that compiles regular expressions into flat int arrays.

Single-file Python port of the C# tool (Program.cs, FileParser.cs, Builder.cs, Compiler.cs).
It produces the same arrays as the C# version. Single-byte code pages use Python codec
names (cp1252, cp037, iso8859-15, ...), so their byte mappings follow Python's tables.

Usage: luthor.py <rules-file|pattern> [encoding]
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
# '.', [...] / [^...], ( ), (?: ), |, *, +, ?, {n}, {n,}, {n,m}, lazy forms of all
# quantifiers, and the anchors ^ (line start) and $ (line end).
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
    def __init__(self):
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
        """At '\\'. Returns (set, is_class); is_class is true for \\d \\w \\s and negations."""
        self.i += 1
        if self.i >= len(self.re):
            raise LuthorFormatError("trailing backslash")
        c = self.re[self.i]
        self.i += 1
        simple = {"n": 10, "r": 13, "t": 9, "f": 12, "v": 11, "0": 0}
        if c in simple:
            v = simple[c]
            return [(v, v)], False
        if c == "d":
            return list(_DIGIT), True
        if c == "w":
            return list(_WORD), True
        if c == "s":
            return list(_SPACE), True
        if c == "D":
            return _complement(_DIGIT), True
        if c == "W":
            return _complement(_WORD), True
        if c == "S":
            return _complement(_SPACE), True
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
    def build(rules, error_rule=False):
        """Rule 0 has the highest priority when several rules accept the same length.
        error_rule adds a catch-all rule with the lowest priority (accept id = len(rules))
        that matches any single character."""
        builder = Builder()
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


def compile_dfa(cp_dfa, encoding="UTF-8", minimize=True):
    states, newline = _transform(cp_dfa.states, encoding, cp_dfa.error_id)
    if minimize:
        states = _minimize(states)
    return _flatten(states, newline)


# ---------------- encoding transform ----------------

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
        states = _copy(cp, keep_moves=True)
        max_unit = 0x7FFFFFFF
    elif e == "UTF8":
        states = _sequenced(cp, _utf8_sequences, starts, error_id)
        max_unit = 0xFF
    elif e in ("UTF16", "UTF16LE", "UTF16BE", "UNICODE"):
        states = _sequenced(cp, _utf16_sequences, starts, error_id)
        max_unit = 0xFFFF
    else:
        states, newline = _single_byte(cp, encoding)
        max_unit = 0xFF

    # Any code unit a start state has no move for (an invalid byte, a lone surrogate, a byte the
    # code page doesn't define, ...) is a one-unit error token.
    if error_id >= 0:
        error = len(states)
        states.append(DfaState(accept=error_id))
        for s in starts:
            states[s].moves = _fill_gaps(states[s].moves, max_unit, error)
    return states, newline


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


def _copy(cp, keep_moves):
    return [DfaState(s.accept, s.bol, s.eol, list(s.moves) if keep_moves else []) for s in cp]


def _sequenced(cp, seqs, starts, error_id):
    """Multi-unit encodings: every codepoint range becomes one or more sequences of code-unit
    ranges; sequences leaving a state are merged into a trie of new states."""
    states = _copy(cp, keep_moves=False)
    memo = {}
    for s in range(len(cp)):
        items = []
        for lo, hi, to in cp[s].moves:
            for seq in seqs(lo, hi):
                items.append((seq, to))
        if s not in starts:
            states[s].moves = _build_trie(items, 0, states, memo)
            continue
        # A start state gets its own, unshared trie whose partial-character states accept as the
        # error rule: a truncated or malformed sequence becomes one error token covering the
        # units read so far.
        first = len(states)
        states[s].moves = _build_trie(items, 0, states, {})
        for k in range(first, len(states)):
            states[k].accept = error_id
    return states


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
                states[to].moves = _build_trie(group, depth + 1, states, memo)
        if moves and moves[-1][2] == to and moves[-1][1] + 1 == lo:
            moves[-1] = (moves[-1][0], hi, to)
        else:
            moves.append((lo, hi, to))
    return moves


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


def _utf8_sequences(lo, hi):
    res = []
    for a, b in _no_surrogates(lo, hi):
        s = a  # split where the encoded length changes
        for limit in (0x7F, 0x7FF, 0xFFFF, 0x10FFFF):
            if s > b:
                break
            if s > limit:
                continue
            _utf8_split(s, min(b, limit), res)
            s = limit + 1
    return res


def _utf8_split(lo, hi, res):
    """Same encoded length assumed. Splits until every byte position is an independent range."""
    n = len(_utf8_encode(lo))
    for k in range(1, n):
        m = (1 << (6 * k)) - 1
        if (lo & ~m) != (hi & ~m):
            if (lo & m) != 0:
                _utf8_split(lo, lo | m, res)
                _utf8_split((lo | m) + 1, hi, res)
                return
            if (hi & m) != m:
                _utf8_split(lo, (hi & ~m) - 1, res)
                _utf8_split(hi & ~m, hi, res)
                return
    res.append(tuple(zip(_utf8_encode(lo), _utf8_encode(hi))))


def _utf8_encode(cp):
    if cp < 0x80:
        return [cp]
    if cp < 0x800:
        return [0xC0 | cp >> 6, 0x80 | cp & 0x3F]
    if cp < 0x10000:
        return [0xE0 | cp >> 12, 0x80 | cp >> 6 & 0x3F, 0x80 | cp & 0x3F]
    return [0xF0 | cp >> 18, 0x80 | cp >> 12 & 0x3F, 0x80 | cp >> 6 & 0x3F, 0x80 | cp & 0x3F]


def _utf16_sequences(lo, hi):
    res = []
    for a, b in _no_surrogates(lo, hi):
        if a <= 0xFFFF:
            res.append(((a, min(b, 0xFFFF)),))
        if b >= 0x10000:
            _utf16_split(max(a, 0x10000) - 0x10000, b - 0x10000, res)
    return res


def _utf16_split(lo, hi, res):
    m = 0x3FF
    if (lo & ~m) != (hi & ~m):
        if (lo & m) != 0:
            _utf16_split(lo, lo | m, res)
            _utf16_split((lo | m) + 1, hi, res)
            return
        if (hi & m) != m:
            _utf16_split(lo, (hi & ~m) - 1, res)
            _utf16_split(hi & ~m, hi, res)
            return
    res.append(((0xD800 + (lo >> 10), 0xD800 + (hi >> 10)), (0xDC00 + (lo & m), 0xDC00 + (hi & m))))


def _is_single_byte_codec(info):
    # Python has no IsSingleByte flag. Its single-byte codecs are the charmap codecs, whose
    # modules carry a decoding_table, plus the built-in ascii and latin-1.
    if info.name in ("ascii", "iso8859-1", "latin-1"):
        return True
    module = sys.modules.get(getattr(info.incrementaldecoder, "__module__", ""), None)
    return module is not None and hasattr(module, "decoding_table")


def _single_byte(cp, name):
    """Any single-byte Python codec (ascii, latin-1, iso8859-x, cp125x, EBCDIC cp037/cp500, ...)."""
    try:
        info = codecs.lookup(name)
    except LookupError:
        raise ValueError(f"'{name}' is not a known encoding name") from None
    if not _is_single_byte_codec(info):
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

    states = _copy(cp, keep_moves=False)
    for s in range(len(cp)):
        moves = states[s].moves
        for b in range(256):
            if cp_of[b] < 0:
                continue
            to = _lookup(cp[s].moves, cp_of[b])
            if to < 0:
                continue
            if moves and moves[-1][2] == to and moves[-1][1] + 1 == b:
                moves[-1] = (moves[-1][0], b, to)
            else:
                moves.append((b, b, to))
    return states, newline


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
                   tuple(_merge_by(st.moves, lambda t: cls[t])))
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
        result.append(DfaState(st.accept, bol, eol, _merge_by(st.moves, num)))
        k += 1
    return result


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


def _flatten(states, newline):
    off = []
    size = HEADER
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

def _print_usage():
    err = sys.stderr
    print("Usage: luthor.py <rules-file|pattern> [encoding]", file=err)
    print("  rules-file: text file containing regex rules, one per line, in the format "
          "'name = pattern' or '# comment' at the start of each line", file=err)
    print("  pattern: a single pattern to match", file=err)
    print("  encoding: character encoding to use (e.g., utf-8, utf-16, cp1252). "
          "default is UTF-8", file=err)


def main(argv):
    try:
        try:
            sys.stdout.reconfigure(encoding="utf-8")
        except (AttributeError, ValueError):
            pass
        if len(argv) < 1:
            raise ValueError("The rules file or a pattern is required.")
        if len(argv) > 2:
            raise ValueError("Too many arguments provided.")
        arg0 = argv[0]
        if arg0 == "-?" or arg0.lower() == "--help":
            _print_usage()
            return
        is_pattern = "\0" in arg0 or not os.path.isfile(arg0)
        kind = "expression" if is_pattern else "lexer"
        print(f"Luthor {kind} compiler", file=sys.stderr)
        print(file=sys.stderr)
        if not is_pattern:
            patterns = list(read_rules(_read_text(arg0)))
            print(f"There are {len(patterns)} patterns.", file=sys.stderr)
        else:
            patterns = [arg0]
        dfa = Builder.build(patterns, True)
        print(f"{len(dfa.states)} states were built", file=sys.stderr)

        array = compile_dfa(dfa, argv[1] if len(argv) == 2 else "UTF-8")
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
    except Exception as ex:  # mirrors the C# catch-all
        print(f"Error: {ex}", file=sys.stderr)
        print(file=sys.stderr)
        _print_usage()


if __name__ == "__main__":
    main(sys.argv[1:])
