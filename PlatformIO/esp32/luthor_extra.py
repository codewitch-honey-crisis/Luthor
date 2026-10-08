# the following is platformIO specific, and despite Import not being
# found by the VS Code LSP, this works. Consider it boilerplate.
Import("env")

print("Luthor integration enabled")

env.Execute("python luthor.py demo.lex -t dfa_template.tpl > include/dfa_table.h")