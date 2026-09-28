// Minimal lexer over a flat DFA table (UTF-32 table, input is a Uint32Array of code points).
// The table was built with the error rule, so every position yields a token of length >= 1.
"use strict";

const DFA = [
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
];

const NAMES = ["directive", "block", "line", "ident", "number", "ws", "op", "error"]; // "error": the built-in error rule

// Longest match at s[pos..]. Returns [token id or -1, length].
function match(dfa, s, pos, atLineStart) {
    let state = 1, accept = -1, length = 0, i = pos, bol = atLineStart;
    for (;;) {
        if (bol && dfa[state + 1] !== -1) state = dfa[state + 1];                                    // ^
        if ((i === s.length || s[i] === dfa[0]) && dfa[state + 2] !== -1) state = dfa[state + 2]; // $
        if (dfa[state] !== -1) { accept = dfa[state]; length = i - pos; }
        if (i === s.length) break;
        // Clamp: the table's error range ends at 2^31-1, so larger Uint32 values would match
        // no range; clamping makes them error tokens like 0x110000.., same as C and Rust.
        const c = Math.min(s[i], 0x7FFFFFFF);
        let next = -1;
        for (let k = 0, r = state + 4; k < dfa[state + 3] && c >= dfa[r]; k++, r += 3) // (min, max, target) triples, sorted
            if (c <= dfa[r + 1]) { next = dfa[r + 2]; break; }
        if (next === -1) break;
        state = next;
        bol = c === dfa[0];
        i++;
    }
    return [accept, length];
}

// Code points back to a string; surrogates and values above U+10FFFF become U+FFFD.
const show = (units) => String.fromCodePoint(...Array.from(units, (c) => (c > 0x10FFFF || (c >= 0xD800 && c <= 0xDFFF) ? 0xFFFD : c)));

const text = "#define X 1\nx = a1 + 42; /* héllo 😀 */ y /* z */\n  # not a directive\n// bye ✓\n@ café `\n";
const s = Uint32Array.from(text, (ch) => ch.codePointAt(0)); // iterating a string yields code points
for (let pos = 0; pos < s.length;) {
    const atLineStart = pos === 0 || s[pos - 1] === DFA[0];
    const [tok, len] = match(DFA, s, pos, atLineStart);
    if (tok !== 5) console.log(NAMES[tok].padEnd(9) + " " + show(s.subarray(pos, pos + len)).replaceAll("\n", "\\n"));
    pos += len;
}
