# Minimal lexer over a flat DFA table (UTF-32 table, input is a str: one codepoint per character).

DFA = [
    10, -1, 44, -1, 13, 9, 10, 87, 13, 13, 87, 32, 32, 87, 35, 35,
    100, 40, 43, 100, 45, 45, 100, 47, 47, 104, 48, 57, 114, 59, 59, 100,
    61, 61, 100, 65, 90, 121, 95, 95, 121, 97, 122, 121, -1, -1, -1, 13,
    9, 10, 87, 13, 13, 87, 32, 32, 87, 35, 35, 137, 40, 43, 100, 45,
    45, 100, 47, 47, 104, 48, 57, 114, 59, 59, 100, 61, 61, 100, 65, 90,
    121, 95, 95, 121, 97, 122, 121, 5, -1, -1, 3, 9, 10, 87, 13, 13,
    87, 32, 32, 87, 6, -1, -1, 0, 6, -1, -1, 2, 42, 42, 147, 47,
    47, 160, 4, -1, -1, 1, 48, 57, 114, 3, -1, -1, 4, 48, 57, 121,
    65, 90, 121, 95, 95, 121, 97, 122, 121, 6, -1, 170, 2, 0, 9, 180,
    11, 1114111, 180, -1, -1, -1, 3, 0, 41, 147, 42, 42, 190, 43, 1114111, 147,
    -1, -1, 209, 2, 0, 9, 160, 11, 1114111, 160, 0, -1, -1, 2, 0, 9,
    180, 11, 1114111, 180, -1, -1, 170, 2, 0, 9, 180, 11, 1114111, 180, -1, -1,
    -1, 5, 0, 41, 147, 42, 42, 190, 43, 46, 147, 47, 47, 219, 48, 1114111,
    147, 2, -1, -1, 2, 0, 9, 160, 11, 1114111, 160, 1, -1, -1, 0
]

NAMES = ["directive", "block", "line", "ident", "number", "ws", "op"]

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

text = "#define X 1\nx = a1 + 42; /* héllo 😀 */ y /* z */\n  # not a directive\n// bye ✓\n"
pos = 0
while pos < len(text):
    at_line_start = pos == 0 or ord(text[pos - 1]) == DFA[0]
    tok, length = match(DFA, text, pos, at_line_start)
    if length == 0:
        print(f"error at {pos}")
        pos += 1
        continue
    if tok != 5:
        print(f"{NAMES[tok]:<9} " + text[pos:pos + length].replace("\n", "\\n"))
    pos += length
