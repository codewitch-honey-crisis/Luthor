#include <stdio.h>
#include <stdlib.h>
#include <stdbool.h>

static int lex_non_ranged(const char** data, const int* dfa) {
    const char* sz = *data;
    int current_state_index = 0;
    bool at_line_start = true;
    bool at_line_end = (*sz == '\0') || (*sz != '\0' && sz[1] == '\0' && *sz == '\n');

    // Continue while we have input OR need to process end anchors
    while (*sz != '\0' || at_line_end) {
        bool found = false;

        // Parse current state's transitions from array
        int machine_index = current_state_index;
        int accept_id = dfa[machine_index++];
        int transition_count = dfa[machine_index++];

        // Check each transition
        for (int t = 0; t < transition_count; t++) {
            int dest_state_index = dfa[machine_index++];
            int range_count = dfa[machine_index++];

            // Check if any range in this transition matches
            bool transition_matches = false;
            for (int r = 0; r < range_count; r++) {
                // NON-RANGE: Read single codepoint, not min/max pair
                int min = max = dfa[machine_index++];

                // Check anchor transitions first 
                if (min == -2 && max == -2) { // START_ANCHOR ^
                    if (at_line_start) {
                        current_state_index = dest_state_index;
                        at_line_start = false;
                        found = true;
                        transition_matches = true;
                        break;
                    }
                }
                else if (min == -3 && max == -3) { // END_ANCHOR $
                    if (at_line_end) {
                        current_state_index = dest_state_index;
                        found = true;
                        transition_matches = true;
                        break;
                    }
                }
                // Check character transitions
                else if (min >= 0 && *sz != '\0') {
                    int c = (unsigned char)*sz;
                    if (c >= min && c <= max) { // Still works since min == max
                        current_state_index = dest_state_index;
                        sz++; // Advance string pointer
                        at_line_end = (*sz == '\0') || (*sz == '\n');
                        at_line_start = (c == '\n');
                        found = true;
                        transition_matches = true;
                        break;
                    }
                }
            }
            if (transition_matches) break;
        }

        if (!found) {
            // No transition found - end matching
            break;
        }

        // Check for acceptance
        int current_accept_id = dfa[current_state_index];
        if (current_accept_id != -1) {
            *data = sz; // Update output pointer to current position
            return current_accept_id;
        }

        // If we processed end anchors and no more input, break
        if (*sz == '\0') {
            break;
        }
    }

    // Check final acceptance state
    int final_accept_id = dfa[current_state_index];
    if (final_accept_id != -1) {
        *data = sz;
        return final_accept_id;
    }

    return -1; // No match found
}

static int lex_ranged(const char** data, const int* dfa) {

    const char* sz = *data;
    int current_state_index = 0;
    bool at_line_start = true;
    bool at_line_end = (*sz == '\0') || (*sz != '\0' && sz[1] == '\0' && *sz == '\n');

    // Continue while we have input OR need to process end anchors
    while (*sz != '\0' || at_line_end) {
        bool found = false;

        // Parse current state's transitions from array
        int machine_index = current_state_index;
        int accept_id = dfa[machine_index++];
        int transition_count = dfa[machine_index++];

        // Check each transition
        for (int t = 0; t < transition_count; t++) {
            int dest_state_index = dfa[machine_index++];
            int range_count = dfa[machine_index++];

            // Check if any range in this transition matches
            bool transition_matches = false;
            for (int r = 0; r < range_count; r++) {
                int min = dfa[machine_index++];
                int max = dfa[machine_index++];

                // Check anchor transitions first 
                if (min == -2 && max == -2) { // START_ANCHOR ^
                    if (at_line_start) {
                        current_state_index = dest_state_index;
                        at_line_start = false;
                        found = true;
                        transition_matches = true;
                        break;
                    }
                }
                else if (min == -3 && max == -3) { // END_ANCHOR $
                    if (at_line_end) {
                        current_state_index = dest_state_index;
                        found = true;
                        transition_matches = true;
                        break;
                    }
                }
                // Check character transitions
                else if (min >= 0 && *sz != '\0') {
                    int c = (unsigned char)*sz;
                    if (c >= min && c <= max) {
                        current_state_index = dest_state_index;
                        sz++; // Advance string pointer
                        at_line_end = (*sz == '\0') || (*sz == '\n');
                        at_line_start = (c == '\n');
                        found = true;
                        transition_matches = true;
                        break;
                    }
                }
            }
            if (transition_matches) break;
        }

        if (!found) {
            // No transition found - end matching
            break;
        }

        // Check for acceptance
        int current_accept_id = dfa[current_state_index];
        if (current_accept_id != -1) {
            *data = sz; // Update output pointer to current position
            return current_accept_id;
        }

        // If we processed end anchors and no more input, break
        if (*sz == '\0') {
            break;
        }
    }

    // Check final acceptance state
    int final_accept_id = dfa[current_state_index];
    if (final_accept_id != -1) {
        *data = sz;
        return final_accept_id;
    }

    return -1; // No match found
}

int main()
{
    static const int ranged_dfa[] = {
        -1, 1, 6, 1, 47, 47, -1, 1, 12, 1, 42, 42, -1, 3, 28, 1, 42, 42, 76, 2,
        10, 41, 43, 127, 110, 1, 194, 195, -1, 4, 50, 1, 47, 47, 52, 1, 42, 42, 76, 3,
        10, 41, 43, 46, 48, 127, 104, 1, 194, 195, 0, 0, -1, 4, 74, 1, 47, 47, 52, 1,
        42, 42, 76, 3, 10, 41, 43, 46, 48, 127, 98, 1, 194, 195, 0, 0, -1, 3, 52, 1,
        42, 42, 76, 2, 10, 41, 43, 127, 92, 1, 194, 195, -1, 1, 76, 1, 128, 191, -1, 1,
        76, 1, 128, 191, -1, 1, 76, 1, 128, 191, -1, 1, 76, 1, 128, 191
    };

    const char* test = "/* foo */";
    const char* input = test;
    printf("%s = %d\n", test, lex_ranged(&input,ranged_dfa));

}
