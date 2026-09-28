/* Minimal lexer over a flat DFA table (UTF-16 table, input is UTF-16 code units).
   The table was built with the error rule, so every position yields a token of length >= 1.
   Needs C11 for u"" literals; output is written as UTF-8. */
#include <stdio.h>
#include <uchar.h>

static const int dfa[] = {
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
};

static const char* names[] = { "directive", "block", "line", "ident", "number", "ws", "op", "error" }; /* "error": the built-in error rule */

/* Longest match at s[0..n). Returns the token id (-1 if none); *len gets the match length. */
static int match(const int* dfa, const char16_t* s, size_t n, int at_line_start, size_t* len)
{
    int state = 1, accept = -1, bol = at_line_start;
    size_t i = 0;
    *len = 0;
    for (;;) {
        if (bol && dfa[state + 1] != -1) state = dfa[state + 1];                            /* ^ */
        if ((i == n || (int)s[i] == dfa[0]) && dfa[state + 2] != -1) state = dfa[state + 2]; /* $ */
        if (dfa[state] != -1) { accept = dfa[state]; *len = i; }
        if (i == n) break;
        int c = (int)s[i], next = -1;
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
    static const char16_t text[] = u"#define X 1\nx = a1 + 42; /* héllo 😀 */ y /* z */\n  # not a directive\n// bye ✓\n@ café `\n";
    const char16_t* s = text;
    size_t n = sizeof text / sizeof text[0] - 1, pos = 0, len;
    while (pos < n) {
        int at_line_start = pos == 0 || (int)s[pos - 1] == dfa[0];
        int tok = match(dfa, s + pos, n - pos, at_line_start, &len);
        if (tok != 5) {
            printf("%-9s ", names[tok]);
            for (size_t k = pos; k < pos + len; k++) {
                unsigned long c = s[k];
                if (c >= 0xD800 && c <= 0xDBFF && k + 1 < pos + len && s[k + 1] >= 0xDC00 && s[k + 1] <= 0xDFFF)
                    c = 0x10000 + ((c - 0xD800) << 10) + (s[++k] - 0xDC00);  /* surrogate pair */
                else if (c >= 0xD800 && c <= 0xDFFF)
                    c = 0xFFFD;                                               /* lone surrogate */
                if (c == '\n') fputs("\\n", stdout); else put_utf8(c);
            }
            putchar('\n');
        }
        pos += len;
    }
    return 0;
}
