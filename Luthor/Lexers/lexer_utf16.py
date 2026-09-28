# Minimal lexer over a flat DFA table (UTF-16 table, input is a list of UTF-16 code units).
# The table was built with the error rule, so every position yields a token of length >= 1.

DFA = [
    10, -1, 89, -1, 28, 0, 8, 177, 9, 10, 181, 11, 12, 177, 13, 13,
    181, 14, 31, 177, 32, 32, 181, 33, 34, 177, 35, 35, 194, 36, 39, 177,
    40, 43, 194, 44, 44, 177, 45, 45, 194, 46, 46, 177, 47, 47, 198, 48,
    57, 208, 58, 58, 177, 59, 59, 194, 60, 60, 177, 61, 61, 194, 62, 64,
    177, 65, 90, 215, 91, 94, 177, 95, 95, 215, 96, 96, 177, 97, 122, 215,
    123, 55295, 177, 55296, 56319, 231, 56320, 65535, 177, -1, -1, -1, 28, 0, 8, 177,
    9, 10, 181, 11, 12, 177, 13, 13, 181, 14, 31, 177, 32, 32, 181, 33,
    34, 177, 35, 35, 238, 36, 39, 177, 40, 43, 194, 44, 44, 177, 45, 45,
    194, 46, 46, 177, 47, 47, 198, 48, 57, 208, 58, 58, 177, 59, 59, 194,
    60, 60, 177, 61, 61, 194, 62, 64, 177, 65, 90, 215, 91, 94, 177, 95,
    95, 215, 96, 96, 177, 97, 122, 215, 123, 55295, 177, 55296, 56319, 231, 56320, 65535,
    177, 7, -1, -1, 0, 5, -1, -1, 3, 9, 10, 181, 13, 13, 181, 32,
    32, 181, 6, -1, -1, 0, 6, -1, -1, 2, 42, 42, 254, 47, 47, 273,
    4, -1, -1, 1, 48, 57, 208, 3, -1, -1, 4, 48, 57, 215, 65, 90,
    215, 95, 95, 215, 97, 122, 215, 7, -1, -1, 1, 56320, 57343, 177, 6, -1,
    289, 4, 0, 9, 305, 11, 55295, 305, 55296, 56319, 321, 57344, 65535, 305, -1, -1,
    -1, 5, 0, 41, 254, 42, 42, 328, 43, 55295, 254, 55296, 56319, 353, 57344, 65535,
    254, -1, -1, 360, 4, 0, 9, 273, 11, 55295, 273, 55296, 56319, 376, 57344, 65535,
    273, 0, -1, -1, 4, 0, 9, 305, 11, 55295, 305, 55296, 56319, 321, 57344, 65535,
    305, -1, -1, 289, 4, 0, 9, 305, 11, 55295, 305, 55296, 56319, 321, 57344, 65535,
    305, -1, -1, -1, 1, 56320, 57343, 305, -1, -1, -1, 7, 0, 41, 254, 42,
    42, 328, 43, 46, 254, 47, 47, 383, 48, 55295, 254, 55296, 56319, 353, 57344, 65535,
    254, -1, -1, -1, 1, 56320, 57343, 254, 2, -1, -1, 4, 0, 9, 273, 11,
    55295, 273, 55296, 56319, 376, 57344, 65535, 273, -1, -1, -1, 1, 56320, 57343, 273, 1,
    -1, -1, 0
]

NAMES = ["directive", "block", "line", "ident", "number", "ws", "op", "error"]  # "error": the built-in error rule

def match(dfa, units, pos, at_line_start):
    """Longest match at units[pos:]. Returns (token id or -1, length)."""
    state, accept, length, i, bol = 1, -1, 0, pos, at_line_start
    while True:
        if bol and dfa[state + 1] != -1:                                  # ^
            state = dfa[state + 1]
        if (i == len(units) or units[i] == dfa[0]) and dfa[state + 2] != -1:  # $
            state = dfa[state + 2]
        if dfa[state] != -1:
            accept, length = dfa[state], i - pos
        if i == len(units):
            break
        c, nxt, r = units[i], -1, state + 4     # (min, max, target) triples, sorted
        for _ in range(dfa[state + 3]):
            if c < dfa[r]:
                break
            if c <= dfa[r + 1]:
                nxt = dfa[r + 2]
                break
            r += 3
        if nxt == -1:
            break
        state, bol, i = nxt, c == dfa[0], i + 1
    return accept, length

text = "#define X 1\nx = a1 + 42; /* héllo 😀 */ y /* z */\n  # not a directive\n// bye ✓\n@ café `\n"
raw = text.encode("utf-16-le")
units = [raw[k] | raw[k + 1] << 8 for k in range(0, len(raw), 2)]
pos = 0
while pos < len(units):
    at_line_start = pos == 0 or units[pos - 1] == DFA[0]
    tok, length = match(DFA, units, pos, at_line_start)
    if tok != 5:
        print(f"{NAMES[tok]:<9} " + raw[2 * pos:2 * (pos + length)].decode("utf-16-le", "replace").replace("\n", "\\n"))
    pos += length
