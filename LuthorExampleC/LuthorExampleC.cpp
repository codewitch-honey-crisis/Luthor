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
        int anchor_mask = dfa[machine_index++];
        int transition_count = dfa[machine_index++];

        for (int t = 0; t < transition_count; t++) {
            int dest_state_index = dfa[machine_index++];
            int range_count = dfa[machine_index++];
            for (int r = 0; r < range_count; r++) {
                int cmp = dfa[machine_index++];
                if (cmp >= 0 && *sz != '\0') {
                    int c = (unsigned char)*sz;
                    if (c < cmp) {
                        machine_index += (range_count - (r + 1));
                        break;
                    }
                    if (c <= cmp) {
                        current_state_index = dest_state_index;
                        sz++;
                        at_line_end = (*sz == '\0') || (*sz == '\n');
                        at_line_start = (c == '\n');
                        goto found;
                    }
                }
            }
        }
        break;
    found:
        accept_id = dfa[current_state_index];
        anchor_mask = dfa[current_state_index + 1];
        if (accept_id != -1) {
            bool anchor_valid = true;
            if ((anchor_mask & 1) && !at_line_start) {
                anchor_valid = false;
            }
            if ((anchor_mask & 2) && !at_line_end) {
                anchor_valid = false;
            }

            if (anchor_valid) {
                *data = sz;
                return accept_id;
            }
        }
        if (*sz == '\0') break;
    }
    *data = sz;
    return -1;  // No valid match found
}

static int lex_ranged(const char** data, const int* dfa) {
    const char* sz = *data;
    int current_state_index = 0;
    bool at_line_start = true;
    bool at_line_end = (*sz == '\0') || (*sz != '\0' && sz[1] == '\0' && *sz == '\n');

    while (*sz != '\0' || at_line_end) {
        int machine_index = current_state_index;
        int accept_id = dfa[machine_index++];
        int anchor_mask = dfa[machine_index++];
        int transition_count = dfa[machine_index++];

        for (int t = 0; t < transition_count; t++) {
            int dest_state_index = dfa[machine_index++];
            int range_count = dfa[machine_index++];
            for (int r = 0; r < range_count; r++) {
                int min = dfa[machine_index++];
                int max = dfa[machine_index++];
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
        break;
    found:
        accept_id = dfa[current_state_index];
        anchor_mask = dfa[current_state_index + 1];
        if (accept_id != -1) {
            bool anchor_valid = true;
            if ((anchor_mask & 1) && !at_line_start) {
                anchor_valid = false;
            }
            if ((anchor_mask & 2) && !at_line_end) {
                anchor_valid = false;
            }

            if (anchor_valid) {
                *data = sz;
                return accept_id;
            }
        }
        if (*sz == '\0') break;
    }
    *data = sz;
    return -1;  // No valid match found
}
int main()
{
    static const int ranged_dfa[] = {
-1, 0, 1, 7, 1, 47, 47, -1, 0, 1, 14, 1, 42, 42, -1, 0, 3, 31, 1, 42,
42, 83, 2, 10, 41, 43, 127, 121, 1, 194, 195, -1, 0, 4, 54, 1, 47, 47, 57, 1,
42, 42, 83, 3, 10, 41, 43, 46, 48, 127, 114, 1, 194, 195, 0, 0, 0, -1, 0, 4,
80, 1, 47, 47, 57, 1, 42, 42, 83, 3, 10, 41, 43, 46, 48, 127, 107, 1, 194, 195,
0, 0, 0, -1, 0, 3, 57, 1, 42, 42, 83, 2, 10, 41, 43, 127, 100, 1, 194, 195,
-1, 0, 1, 83, 1, 128, 191, -1, 0, 1, 83, 1, 128, 191, -1, 0, 1, 83, 1, 128,
191, -1, 0, 1, 83, 1, 128, 191
    };
    static const int non_ranged_dfa[]{
        -1, 0, 1, 6, 1, 104, -1, 0, 1, 12, 1, 101, -1, 0, 1, 18, 1, 108, -1, 0,
1, 24, 1, 108, -1, 0, 1, 30, 1, 111, 0, 0, 0
    };
    const char* test_ranged = "/* b */ ";
    const char* input_ranged = test_ranged;
    const char* test_non_ranged = "hello";
    const char* input_non_ranged = test_non_ranged;
    
    printf("%s = %d\n", test_ranged, lex_ranged(&input_ranged,ranged_dfa));
    
    printf("%s = %d\n", test_non_ranged, lex_non_ranged(&input_non_ranged, non_ranged_dfa));

}
