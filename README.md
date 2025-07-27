# luthor

An experimental lexer generator that produces simple flat integer arrays that can be used to match text.

Copyright (c) 2025 by honey the codewitch. MIT License

Lazy matching implemented using Dr. Robert van Engelen's algorithm that he uses in RE/FLEX, and which he was kind enough to help me with via email. All of his respective work is all rights reserved for him.

This lexer relies on direct to DFA conversion as indicated by the Aho-Sethi-Ullman algorithm in the dragon book.

There is no Thompson construction or NFA intermediary using this technique.

Furthermore, this code implements lazy matching using a pure DFA algorithm pioneered by Dr. Robert van Engelen as part of his RE/FLEX project. https://github.com/Genivia/RE-flex

Next the result is rencoded to the desired encoding using a range splitting algorithm I devised, but honestly I'm probably not the first to do it this way.

Finally, the result is minimized using Hopcroft's algorithm before being serialized to an array.

The point of this code is to provide a means to generate flat integer arrays from single regular expressions or entire lexers that can easily be traversed to match unicode text in any language.

Ergo, this code does not contain matching logic, aside from the tests. That is meant for whatever uses the arrays this code can generate.

## Lexer input format

I'm working toward POSIX compliance, which is the eventual goal, at least for the subset that this engine supports. It's a work in progress.
Currently, since it's primarily for lexing, all matches are multiline mode, which means ^ and $ match the beginning and end of a *line* respectively, although the implementor can change that behavior in their own matcher code.

? lazy quantifiers are supported, such as ??, *? and +?

Each expression must be on a single line. Comments are on their own line and must be either `# example` or `#`. `#foo` (no space) will be treated as a regular expression.

For a lexer, rules are each on a line, and accept indexes are assigned in expression order. There is no specifier for a symbolic name, you just have to use the indices/ids.

```
# Test Lexer Example
# C comment
/\*(.|\n)*?\*/
# C identifier
[A-Za-z_][A-Za-z0-9_]*
# more follows... (omitted here)
```

## Lexer array format

Luthor arrays come in two flavors: Range arrays and non-range arrays. This is so the smallest possible array and matching code can be used for the situation. Sometimes range arrays end up smaller, sometimes non-range arrays end up smaller, especially for simple expressions without a lot of character sets.

Range arrays are always even in length. Non range arrays are always odd in length, and may be padded with a trailing -1 element to enforce that.

The array is simply a list of states, each of which has lists of transitions to other states. Each value is a signed integer. The width of the integer must be at least high enough to hold the maximum value for the encoding used. For example, the utf32 range is 0-0x10FFFF, so it requires 32 bits. UTF-16 would require 16 bits and futhermore, the size must also be wide enough to hold the maximum number of elements in the array, so if the array is 129 entries, you need something wider than a signed 8 bit int to support that. It's always safe to use wider values, it's just potentially more wasteful.

For each state:

The first integer is the accept id, or -1 if not accepting.
The second integer is the set of anchor flags: 1= start line anchor, 2 = end line anchor
The next integer is the number of unique destination states this state may transition to.

- For each destination state:
    - the next integer is the index of the destination state in the array
    - the next integer is the number of codepoints or two-codepoint range pairs that transition to that state which follows. Whether it's codepoint min/max pairs or single codepoints depends if this is a range array or non-range array
        - The next integer is the codepoint minimum or simple the codepoint for a non-range array
        - the next integer is the maximum codepoint (range array only)

This is repeated for every state.

## Using the CLI utility

```
luthor

A DFA lexer generator tool

Usage:

luthor <input> [ --enc <encoding> ] [ --graph <graph> ]

<input>        The input expression or file to use
<encoding>     The encoding to use (ASCII, UTF-8, UTF-16, or UTF-32). Defaults to UTF-8
<graph>        Generate a DFA state graph to the specified file (requires GraphViz)

luthor [ --? ]

--?            Displays this help screen
```

Example for `dotnet luthor [A-Z_a-z][A-Z_a-z0-9]*`
```
Processing the following input:
[A-Z_a-z][A-Z_a-z0-9]*

Created initial machine with 2 states.
Minimizing...done! 0% size savings.
Minimized machine has 2 states.
Transforming to UTF-8...done! 0% larger than minimized.
Net effect: 100% of original length*.

The array takes a minimum of 24 bytes to store

Emitting ranged jump table array with a length of 24 and an element width of 1 byte

* values do not reflect the relative width of the elements, only the total length of the array.

-1, 0, 1, 11, 3, 65, 90, 95, 95, 97, 122, 0, 0, 1, 11, 4, 48, 57, 65, 90,
95, 95, 97, 122
```

Those integers are your magic sauce. In the language of your choice, wrap them in the syntax for an integer array. You then create simple matching code for it like so (example lexer in C)

```c
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
```

For the non-ranged array version it would be like this

```c
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
```

and using them is like this:

```c
int main()
{
    static const int ranged_dfa[] = {
        -1, 1, 10, 3, 65, 90, 95, 95, 97, 122, 0, 1, 10, 4, 48, 57, 65, 90, 95, 95, 
        97, 122
    };

    const char* test = "foo_bar123";
    const char* input = test;
    printf("%s = %d\n", test, lex_ranged(&input,ranged_dfa));

}
```