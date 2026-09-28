// Minimal lexer over a flat DFA table (UTF-32 table, input is code points).
// The table was built with the error rule, so every position yields a token of length >= 1.

const DFA: &[i32] = &[
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

const NAMES: [&str; 8] = ["directive", "block", "line", "ident", "number", "ws", "op", "error"]; // "error": the built-in error rule

/// Longest match at the start of `s`. Returns (token id or -1, length).
fn match_at(dfa: &[i32], s: &[u32], at_line_start: bool) -> (i32, usize) {
    let (mut state, mut accept, mut len, mut i, mut bol) = (1usize, -1i32, 0usize, 0usize, at_line_start);
    loop {
        if bol && dfa[state + 1] != -1 { state = dfa[state + 1] as usize; }                              // ^
        if (i == s.len() || s[i] as i32 == dfa[0]) && dfa[state + 2] != -1 { state = dfa[state + 2] as usize; } // $
        if dfa[state] != -1 { accept = dfa[state]; len = i; }
        if i == s.len() { break; }
        // Clamp: values above i32::MAX would wrap negative and match no range; the table's
        // error range ends at i32::MAX, so clamping makes them error tokens like 0x110000..
        let c = s[i].min(i32::MAX as u32) as i32;
        let mut next = -1;
        let mut r = state + 4; // (min, max, target) triples, sorted
        for _ in 0..dfa[state + 3] {
            if c < dfa[r] { break; }
            if c <= dfa[r + 1] { next = dfa[r + 2]; break; }
            r += 3;
        }
        if next == -1 { break; }
        state = next as usize;
        bol = c == dfa[0];
        i += 1;
    }
    (accept, len)
}

fn show(units: &[u32]) -> String {
    units.iter().map(|&c| char::from_u32(c).unwrap_or('\u{FFFD}')).collect::<String>().replace('\n', "\\n")
}

fn main() {
    let text = "#define X 1\nx = a1 + 42; /* héllo 😀 */ y /* z */\n  # not a directive\n// bye ✓\n@ café `\n";
    let s: Vec<u32> = text.chars().map(|c| c as u32).collect();
    let mut pos = 0;
    while pos < s.len() {
        let at_line_start = pos == 0 || s[pos - 1] as i32 == DFA[0];
        let (tok, len) = match_at(DFA, &s[pos..], at_line_start);
        if tok != 5 {
            println!("{:<9} {}", NAMES[tok as usize], show(&s[pos..pos + len]));
        }
        pos += len;
    }
}
