#include <Arduino.h>
#define TYPE int16_t
static const TYPE dfa[] = {
    #include "lex_table.dfa"
};

/* Longest match at s[0..n). Returns the token id (-1 if none); *len gets the match length. */
static int match(const TYPE* dfa, const unsigned char* s, size_t n, int at_line_start, size_t* len)
{
    int state = 1, accept = -1, bol = at_line_start;
    size_t i = 0;
    *len = 0;
    for (;;) {
        if (bol && dfa[state + 1] != -1) state = dfa[state + 1];                       /* ^ */
        if ((i == n || s[i] == dfa[0]) && dfa[state + 2] != -1) state = dfa[state + 2]; /* $ */
        if (dfa[state] != -1) { accept = dfa[state]; *len = i; }
        if (i == n) break;
        int c = s[i], next = -1;
        const TYPE* r = dfa + state + 4;             /* (min, max, target) triples, sorted */
        for (int k = 0; k < dfa[state + 3] && c >= r[0]; k++, r += 3)
            if (c <= r[1]) { next = r[2]; break; }
        if (next == -1) break;
        state = next;
        bol = (c == dfa[0]);
        i++;
    }
    return accept;
}


void setup() {
    Serial.begin(115200);
}

void loop() {
    const char* text = "#define X 1\nx = a1 + 42; /* héllo 😀 */ y /* z */\n  # not a directive\n// bye ✓\n@ café `\n";
    const unsigned char* s = (const unsigned char*)text;
    size_t n = strlen(text), pos = 0, len;
    char szbuf[16];
    while (pos < n) {
        int at_line_start = pos == 0 || s[pos - 1] == dfa[0];
        int tok = match(dfa, s + pos, n - pos, at_line_start, &len);
        sprintf(szbuf,"%02d",tok);
        Serial.print(szbuf);
        Serial.print(" at pos ");
        sprintf(szbuf,"%03d: ", pos);
        Serial.print(szbuf);
        char sztmp[2]={0};
        for (size_t k = pos; k < pos + len; k++)
            if (s[k] == '\n') Serial.print("\\n"); else { sztmp[0]=s[k]; Serial.print(sztmp);}
        Serial.println();
    
        pos += len;
    }
    delay(5000);
    Serial.println();
}
