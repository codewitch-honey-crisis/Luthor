/* Minimal lexer over a flat DFA table (UTF-32 table, input is code points).
   The table was built with the error rule, so every position yields a token of length >= 1.
   Needs C11 for U"" literals; output is written as UTF-8. */
#include <limits.h>
#include <stdio.h>
#include <uchar.h>

static const int dfa[] = {
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
};

static const char* names[] = { "directive", "block", "line", "ident", "number", "ws", "op", "error" }; /* "error": the built-in error rule */

/* Longest match at s[0..n). Returns the token id (-1 if none); *len gets the match length. */
static int match(const int* dfa, const char32_t* s, size_t n, int at_line_start, size_t* len)
{
    int state = 1, accept = -1, bol = at_line_start;
    size_t i = 0;
    *len = 0;
    for (;;) {
        if (bol && dfa[state + 1] != -1) state = dfa[state + 1];                            /* ^ */
        if ((i == n || (int)s[i] == dfa[0]) && dfa[state + 2] != -1) state = dfa[state + 2]; /* $ */
        if (dfa[state] != -1) { accept = dfa[state]; *len = i; }
        if (i == n) break;
        /* Clamp: values above INT_MAX would wrap negative and match no range; the table's
           error range ends at INT_MAX, so clamping makes them error tokens like 0x110000.. */
        int c = s[i] > INT_MAX ? INT_MAX : (int)s[i], next = -1;
        const int* r = dfa + state + 4;             /* (min, max, target) triples, sorted */
        for (int k = 0; k < dfa[state + 3] && c >= r[0]; k++, r += 3)
            if (c <= r[1]) { next = r[2]; break; }
        if (next == -1) break;
        state = next;
        bol = (c == dfa[0]);
        i++;
    }
    return accept;
}

/* Write one code point to stdout as UTF-8. */
static void put_utf8(unsigned long c)
{
    if (c < 0x80) {
        putchar((int)c);
    } else if (c < 0x800) {
        putchar((int)(0xC0 | (c >> 6)));
        putchar((int)(0x80 | (c & 0x3F)));
    } else if (c < 0x10000) {
        putchar((int)(0xE0 | (c >> 12)));
        putchar((int)(0x80 | ((c >> 6) & 0x3F)));
        putchar((int)(0x80 | (c & 0x3F)));
    } else {
        putchar((int)(0xF0 | (c >> 18)));
        putchar((int)(0x80 | ((c >> 12) & 0x3F)));
        putchar((int)(0x80 | ((c >> 6) & 0x3F)));
        putchar((int)(0x80 | (c & 0x3F)));
    }
}

int main(void)
{
    static const char32_t text[] = U"#define X 1\nx = a1 + 42; /* héllo 😀 */ y /* z */\n  # not a directive\n// bye ✓\n@ café `\n";
    const char32_t* s = text;
    size_t n = sizeof text / sizeof text[0] - 1, pos = 0, len;
    while (pos < n) {
        int at_line_start = pos == 0 || (int)s[pos - 1] == dfa[0];
        int tok = match(dfa, s + pos, n - pos, at_line_start, &len);
        if (tok != 5) {
            printf("%-9s ", names[tok]);
            for (size_t k = pos; k < pos + len; k++) {
                unsigned long c = s[k];
                if ((c >= 0xD800 && c <= 0xDFFF) || c > 0x10FFFF) c = 0xFFFD;   /* not a scalar value */
                if (c == '\n') fputs("\\n", stdout); else put_utf8(c);
            }
            putchar('\n');
        }
        pos += len;
    }
    return 0;
}
