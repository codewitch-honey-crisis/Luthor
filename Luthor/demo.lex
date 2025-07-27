#
# Demo Lexer
#
# C Comment
/\*(.|\n)*?\*/|//.*$
# C Identifier
[A-Za-z_][A-Za-z_0-9]*
# C integer
(?:0x(?:[0-9a-f]+(?:'?[0-9a-f]+)*)|0b(?:[10]+(?:'?[10]+)*)|\d+(?:'?\d+)*)(?:ull|ll|ul|l|u)?

