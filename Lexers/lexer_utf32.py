# Minimal lexer over a flat DFA table (UTF-32 table, input is a str: one codepoint per character).
# The table was built with the error rule, so every position yields a token of length >= 1.

DFA = [
    10, -1, 83, -1, 26, 0, 8, 165, 9, 10, 169, 11, 12, 165, 13, 13,
    169, 14, 31, 165, 32, 32, 169, 33, 34, 165, 35, 35, 182, 36, 39, 165,
    40, 43, 182, 44, 44, 165, 45, 45, 182, 46, 46, 165, 47, 47, 186, 48,
    57, 196, 58, 58, 165, 59, 59, 182, 60, 60, 165, 61, 61, 182, 62, 64,
    165, 65, 90, 203, 91, 94, 165, 95, 95, 203, 96, 96, 165, 97, 122, 203,
    123, 2147483647, 165, -1, -1, -1, 26, 0, 8, 165, 9, 10, 169, 11, 12, 165,
    13, 13, 169, 14, 31, 165, 32, 32, 169, 33, 34, 165, 35, 35, 219, 36,
    39, 165, 40, 43, 182, 44, 44, 165, 45, 45, 182, 46, 46, 165, 47, 47,
    186, 48, 57, 196, 58, 58, 165, 59, 59, 182, 60, 60, 165, 61, 61, 182,
    62, 64, 165, 65, 90, 203, 91, 94, 165, 95, 95, 203, 96, 96, 165, 97,
    122, 203, 123, 2147483647, 165, 7, -1, -1, 0, 5, -1, -1, 3, 9, 10, 169,
    13, 13, 169, 32, 32, 169, 6, -1, -1, 0, 6, -1, -1, 2, 42, 42,
    229, 47, 47, 242, 4, -1, -1, 1, 48, 57, 196, 3, -1, -1, 4, 48,
    57, 203, 65, 90, 203, 95, 95, 203, 97, 122, 203, 6, -1, 252, 2, 0,
    9, 262, 11, 1114111, 262, -1, -1, -1, 3, 0, 41, 229, 42, 42, 272, 43,
    1114111, 229, -1, -1, 291, 2, 0, 9, 242, 11, 1114111, 242, 0, -1, -1, 2,
    0, 9, 262, 11, 1114111, 262, -1, -1, 252, 2, 0, 9, 262, 11, 1114111, 262,
    -1, -1, -1, 5, 0, 41, 229, 42, 42, 272, 43, 46, 229, 47, 47, 301,
    48, 1114111, 229, 2, -1, -1, 2, 0, 9, 242, 11, 1114111, 242, 1, -1, -1,
    0
]

NAMES = ["directive", "block", "line", "ident", "number", "ws", "op", "error"]  # "error": the built-in error rule

def match(dfa, text, pos, at_line_start):
    """Longest match at text[pos:]. Returns (token id or -1, length)."""
    state, accept, length, i, bol = 1, -1, 0, pos, at_line_start
    while True:
        if bol and dfa[state + 1] != -1:                                         # ^
            state = dfa[state + 1]
        if (i == len(text) or ord(text[i]) == dfa[0]) and dfa[state + 2] != -1:  # $
            state = dfa[state + 2]
        if dfa[state] != -1:
            accept, length = dfa[state], i - pos
        if i == len(text):
            break
        c, nxt, r = ord(text[i]), -1, state + 4     # (min, max, target) triples, sorted
        for _ in range(dfa[state + 3]):
            if c < dfa[r]:
                break
            if c <= dfa[r + 1]:
                nxt = dfa[r + 2]
                break
            r += 3
        if nxt == -1:
            break
        state, bol, i = nxt, ord(text[i]) == dfa[0], i + 1
    return accept, length

text = "#define X 1\nx = a1 + 42; /* héllo 😀 */ y /* z */\n  # not a directive\n// bye ✓\n@ café `\n"
pos = 0
while pos < len(text):
    at_line_start = pos == 0 or ord(text[pos - 1]) == DFA[0]
    tok, length = match(DFA, text, pos, at_line_start)
    if tok != 5:
        print(f"{NAMES[tok]:<9} " + text[pos:pos + length].replace("\n", "\\n"))
    pos += length