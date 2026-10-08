#ifndef DFA_TABLE_H
#define DFA_TABLE_H
#include <stdint.h>
#define DFA_TYPE int%WIDTH%_t
extern DFA_TYPE dfa[];
#endif
#ifdef DFA_TABLE_IMPLEMENTATION
int%WIDTH%_t dfa[] = {
    %TABLE%
};
#endif