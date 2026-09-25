# C example
# directive
^#.*$
# block comment
/\*(.|\n)*?\*/
# line comment
//.*$
# identifier
[A-Za-z_\u00C0-\uFFFF][A-Za-z0-9_\u00C0-\uFFFF]*
# number
[0-9]+
# string
"(\\.|[^"\\\n])*?"
# whitespace
[ \t\r\n]+
# operator
[-+*/=;#]