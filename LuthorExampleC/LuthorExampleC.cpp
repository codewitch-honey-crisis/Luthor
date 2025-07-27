#include <stdio.h>
#include <stdlib.h>
#include <stdbool.h>

static int lex_non_ranged(const char** data, const int* dfa) {
    const char* sz = *data;
    int current_state_index = 0;
    bool at_line_start = true;
    bool at_line_end = (*sz == '\0') || (*sz != '\0' && sz[1] == '\0' && *sz == '\n');

    while (*sz != '\0' || at_line_end) {
        int machine_index = current_state_index;
        int accept_id = dfa[machine_index++];
        int transition_count = dfa[machine_index++];

        for (int t = 0; t < transition_count; t++) {
            int dest_state_index = dfa[machine_index++];
            int range_count = dfa[machine_index++];

            for (int r = 0; r < range_count; r++) {
                int min = dfa[machine_index++];
                
                if (min == -2 && at_line_start) {
                    current_state_index = dest_state_index;
                    at_line_start = false;
                    goto found;
                }
                if (min == -3 && at_line_end) {
                    current_state_index = dest_state_index;
                    goto found;
                }
                if (min >= 0 && *sz != '\0') {
                    int c = (unsigned char)*sz;
                    if (c < min) {
                        machine_index += (range_count - (r + 1));
                        break;
                    }
                    if (c == min) {
                        current_state_index = dest_state_index;
                        sz++;
                        at_line_end = (*sz == '\0') || (*sz == '\n');
                        at_line_start = (c == '\n');
                        goto found;
                    }
                }
            }
        }
        break; // No transition found

    found:
        if (dfa[current_state_index] != -1) {
            *data = sz;
            return dfa[current_state_index];
        }
        if (*sz == '\0') break;
    }

    *data = sz;
    return dfa[current_state_index] != -1 ? dfa[current_state_index] : -1;
}

static int lex_ranged(const char** data, const int* dfa) {
    const char* sz = *data;
    int current_state_index = 0;
    bool at_line_start = true;
    bool at_line_end = (*sz == '\0') || (*sz != '\0' && sz[1] == '\0' && *sz == '\n');

    while (*sz != '\0' || at_line_end) {
        int machine_index = current_state_index;
        int accept_id = dfa[machine_index++];
        int transition_count = dfa[machine_index++];

        for (int t = 0; t < transition_count; t++) {
            int dest_state_index = dfa[machine_index++];
            int range_count = dfa[machine_index++];

            for (int r = 0; r < range_count; r++) {
                int min = dfa[machine_index++];
                int max = dfa[machine_index++];

                if (min == -2 && at_line_start) {
                    current_state_index = dest_state_index;
                    at_line_start = false;
                    goto found;
                }
                if (min == -3 && at_line_end) {
                    current_state_index = dest_state_index;
                    goto found;
                }
                if (min >= 0 && *sz != '\0') {
                    int c = (unsigned char)*sz;
                    if (c < min) {
                        machine_index += ((range_count - (r + 1)) * 2);
                        break;
                    }
                    if (c <= max) {
                        current_state_index = dest_state_index;
                        sz++;
                        at_line_end = (*sz == '\0') || (*sz == '\n');
                        at_line_start = (c == '\n');
                        goto found;
                    }
                }
            }
        }
        break; // No transition found

    found:
        if (dfa[current_state_index] != -1) {
            *data = sz;
            return dfa[current_state_index];
        }
        if (*sz == '\0') break;
    }

    *data = sz;
    return dfa[current_state_index] != -1 ? dfa[current_state_index] : -1;
}
int main()
{
    static const int ranged_dfa[] = {
        -1, 1, 6, 1, 47, 47, -1, 1, 12, 1, 42, 42, -1, 3, 28, 1, 42, 42, 12, 2,
        10, 41, 43, 127, 52, 1, 194, 195, -1, 4, 50, 1, 47, 47, 28, 1, 42, 42, 12, 3,
        10, 41, 43, 46, 48, 127, 52, 1, 194, 195, 0, 0, -1, 1, 12, 1, 128, 191
    };

    const char* test = "/*** foo *** */";
    const char* input = test;
    printf("%s = %d\n", test, lex_ranged(&input,ranged_dfa));

}
