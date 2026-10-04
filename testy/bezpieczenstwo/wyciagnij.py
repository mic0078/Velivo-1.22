# Wyciaga skrypty stron (hasla, formularze) z kodu C# do plikow .js dla testow ataku.
import os, sys
src = os.path.join(os.path.dirname(__file__), "..", "..", "src")
out = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(__file__)
s = open(os.path.join(src, "PasswordVault.cs"), encoding="utf-8").read()
a = s.index('PasswordVaultDocumentCreatedScript = """') + len('PasswordVaultDocumentCreatedScript = """')
js = s[a:s.index('"""', a)]
for k in ["__VT_FILL_TIP__", "__VT_FILL__", "__VT_GEN_TIP__", "__VT_GEN__", "__VT_OTHER__"]: js = js.replace(k, "x")
open(os.path.join(out, "pv.js"), "w", encoding="utf-8").write(js.replace("__VT_TOKEN__", "TOK"))
s = open(os.path.join(src, "Autofill.cs"), encoding="utf-8").read()
a = s.index('AutofillPageScript = @"') + len('AutofillPageScript = @"')
b = s.index('})();";', a) + len('})();')
open(os.path.join(out, "af.js"), "w", encoding="utf-8").write(s[a:b].replace('""', '"').replace("__VT_TOKEN__", "TOK"))
