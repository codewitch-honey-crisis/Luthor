# C example
directive = ^#.*$
block = /\*(.|\n)*?\*/
line = //.*$
ident = [A-Za-z_\u00C0-\uFFFF][A-Za-z0-9_\u00C0-\uFFFF]*
number = [0-9]+
string = "(\\.|[^"\\\n])*?"
ws = [ \t\r\n]+
operator = [-+*/=;#]