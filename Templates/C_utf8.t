#pragma once
#include <stdint.h>
#include <string.h>

static int %NAME%_match(const unsigned char* s, size_t n, int at_line_start, size_t* len)
{
    static const int%WIDTH%_t dfa[] = {
        %TABLE%
    };
    int state = 1, accept = -1, bol = at_line_start;
    size_t i = 0;
    *len = 0;
    for (;;) {
        if (bol && dfa[state + 1] != -1) state = dfa[state + 1];                       /* ^ */
        if ((i == n || s[i] == dfa[0]) && dfa[state + 2] != -1) state = dfa[state + 2]; /* $ */
        if (dfa[state] != -1) { accept = dfa[state]; *len = i; }
        if (i == n) break;
        int c = s[i], next = -1;
        const int%WIDTH%_t* r = dfa + state + 4;             /* (min, max, target) triples, sorted */
        for (int k = 0; k < dfa[state + 3] && c >= r[0]; k++, r += 3)
            if (c <= r[1]) { next = r[2]; break; }
        if (next == -1) break;
        state = next;
        bol = (c == dfa[0]);
        i++;
    }
    return accept;
}
