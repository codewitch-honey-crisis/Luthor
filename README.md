# Luthor

A fast, compact lexer generator that produces simple integer arrays for efficient text matching in any programming language.

**Key Features:**
- Direct-to-DFA conversion (no intermediate NFA)
- Lazy quantifier support (`??`, `*?`, `+?`)
- Unicode support (UTF-8, UTF-16, UTF-32)
- Compact array output suitable for embedded systems
- Language-agnostic - use the arrays in C, C++, Rust, etc.

## What Luthor Does

Luthor takes regular expressions or lexer grammars and converts them into simple integer arrays that can be embedded in any program. Instead of shipping a complex regex engine, you get a small array that can be walked with a simple matching loop.

**Example:** The pattern `[A-Za-z_][A-Za-z0-9_]*` becomes:
```c
int dfa[] = {-1, 0, 1, 11, 3, 65, 90, 95, 95, 97, 122, 0, 0, 1, 11, 4, 48, 57, 65, 90, 95, 95, 97, 122};
```

## Technical Background

Luthor implements several advanced algorithms:

- Direct-to-DFA construction using the Aho-Sethi-Ullman algorithm (Dragon Book)
- Lazy matching using Dr. Robert van Engelen's algorithm from RE/FLEX
- Hopcroft minimization for optimal state count
- Unicode range splitting for efficient multi-byte encoding support

## Acknowledgements

Special thanks to Dr. Robert van Engelen for his guidance on implementing lazy matching in pure DFA form.

## Quick Start

### Installation

```bash
dotnet tool install luthor-tool -g
```

### Basic Usage
```
# Single expression
luthor "[0-9]+"
```
### Lexer from file
```bash
luthor mylexer.txt
```

### Generate state diagram
```bash
luthor mylexer.txt --graph output.png
```

## Lexer Input Format

### Single Expression

For a single regular expression, just provide the pattern:

```
luthor "if|while|for"
```

## Lexer Grammar
For a full lexer, create a text file with one rule per line:

```
# C-like Language Lexer
# Comments start with # (must have space or be alone)

# Keywords
if|while|for|int|void

# Identifiers  
[A-Za-z_][A-Za-z0-9_]*

# Integers (decimal and hex)
0x[0-9a-fA-F]+|[0-9]+

# C-style comments
/\*(.|\n)*?\*/

# Line comments  
//.*$

# Whitespace (usually ignored)
[ \t\n\r]+
```

### Rules:

- Each expression on its own line
- Comments: `# text` or `#` alone. `#foo` (no space) will be interpreted as a regular expression
- Accept IDs assigned by line order (0, 1, 2, ...)
- Supports POSIX character classes: [[:digit:]], [[:alpha:]], etc.
- Lazy quantifiers: *?, +?, ??, {1,3}?


## Array Format
Luthor generates two array types:

- Range arrays: Use min/max pairs [65,90] for [A-Z] (more compact for large character sets)
- Non-range arrays: List individual codepoints (better for sparse patterns)

They are distinguished by having an even length (range arrays), or an odd length (non range arrays), and may be padded with a single -1 at the end to enforce the length.

## Array Structure

```
[accept_id, anchor_mask, transition_count, ...]
```

For each state:

1. accept_id: Token ID (-1 if non-accepting)
2. anchor_mask: 1=line start ^, 2=line end $
3. transition_count: Number of destination states
4. For each destination:
    - dest_state_index: Target state index
    - transition_entry__count: Number of entries
    - transition_entry_: Either [min,max] pairs (range array) or individual codepoints (non range array)

### Example Breakdown

```c
// Pattern: [A-Z__]+
int dfa[] = {
    -1, 0, 1,        // State 0: not accepting, no anchors, 1 transition
    11, 3,           // -> go to state 11 on 3 ranges:
    65, 90,          //    A-Z (65-90)
    95, 95,          //    _ (95)  
    97, 122,         //    a-z (97-122)
    
    0, 0, 1,         // State 11: accepting (token 0), no anchors, 1 transition
    11, 4,           // -> loop to state 11 on 4 ranges:
    48, 57,          //    0-9 (48-57)
    65, 90,          //    A-Z (65-90)  
    95, 95,          //    _ (95)
    97, 122          //    a-z (97-122)
};
```

## Using the Generated Arrays

### Simple C Lexer (Ranged arrays)

```c
#include <stdio.h>
#include <stdbool.h>

static int lex_ranged(const char** data, const int* dfa) {
    const char* sz = *data;
    int current_state = 0;
    
    while (*sz) {
        int idx = current_state;
        int accept = dfa[idx++];
        int anchors = dfa[idx++];  
        int trans_count = dfa[idx++];
        
        bool found = false;
        for (int t = 0; t < trans_count && !found; t++) {
            int dest_state = dfa[idx++];
            int range_count = dfa[idx++];
            
            for (int r = 0; r < range_count; r++) {
                int min = dfa[idx++];
                int max = dfa[idx++];
                
                if (*sz >= min && *sz <= max) {
                    current_state = dest_state;
                    sz++;
                    found = true;
                    break;
                }
            }
            if (!found) {
                idx += (range_count * 2); // skip remaining ranges
            }
        }
        
        if (!found) break;
    }
    
    // Check if current state accepts
    if (dfa[current_state] != -1) {
        *data = sz;
        return dfa[current_state]; // return token ID
    }
    
    return -1; // no match
}

int main() {
    static const int identifier_dfa[] = {
        -1, 0, 1, 11, 3, 65, 90, 95, 95, 97, 122,
        0, 0, 1, 11, 4, 48, 57, 65, 90, 95, 95, 97, 122
    };
    
    const char* input = "hello_world123";
    const char* pos = input;
    
    int token = lex_ranged(&pos, identifier_dfa);
    printf("Matched token %d, consumed: '%.*s'\n", 
           token, (int)(pos - input), input);
    
    return 0;
}
```

## Multi-Language Support

### The integer arrays work in any language:

Rust:
```rust
const DFA: &[i32] = &[-1, 0, 1, 11, 3, 65, 90, 95, 95, 97, 122, /*...*/];
```

Python:
```python
dfa = [-1, 0, 1, 11, 3, 65, 90, 95, 95, 97, 122, ...]
```
JavaScript:
```js
const dfa = [-1, 0, 1, 11, 3, 65, 90, 95, 95, 97, 122, /*...*/];
```

## CLI Reference

```
luthor

A DFA lexer generator tool

Usage:

luthor <input> [ --enc <encoding> ] [ --graph <graph> ]

<input>        The input expression or file to use
<encoding>     The encoding to use (ASCII, UTF-8, UTF-16, or UTF-32, or a single byte encoding). Defaults to UTF-8
<graph>        Generate a DFA state graph to the specified file (requires GraphViz)

luthor [ --? ]

--?            Displays this help screen
```

```
# Generate UTF-8 lexer for C identifiers
luthor "[A-Za-z_][A-Za-z0-9_]*"

# Create lexer from grammar file  
luthor c_lexer.txt --enc UTF-16

# Generate lexer with state diagram
luthor json_lexer.txt --graph json_states.png
```

## Performance Characteristics

- Memory: Compact arrays, typically KB-sized even for complex lexers
- Speed: Simple array traversal, no backtracking
- Predictable: DFA guarantees O(n) matching time
- Embeddable: No external dependencies, just integer arrays

### License

MIT License - Copyright (c) 2025 honey the codewitch

Lazy matching implementation based on Dr. Robert van Engelen's algorithm from RE/FLEX (all rights reserved to Dr. van Engelen)