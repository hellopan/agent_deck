#!/usr/bin/env python3
"""Embed assets/agentdeck-logo-32.png into design/v3-windows.html as a CSS var.

The design board is the single source of truth and must stay a plain HTML file
that opens in any browser, so the mark is inlined as a data URI rather than
shipped as a fourth UI file (Install() only unpacks index.html/app.js/shell.css).

Run after make-icons.ps1, then scripts/sync-ui.sh. Idempotent: it replaces the
anchor or a previous --app-icon line.
"""
import base64
import io
import re
from pathlib import Path

root = Path(__file__).resolve().parent.parent
png = root / "assets" / "agentdeck-logo-32.png"
board = root / "design" / "v3-windows.html"

raw = png.read_bytes()
encoded = base64.b64encode(raw).decode("ascii")
line = '  --app-icon:url("data:image/png;base64,%s");' % encoded

with io.open(board, encoding="utf-8") as handle:
    html = handle.read()

anchor = re.compile(r"^  (?:--app-icon:[^\n]*|/\*APP_ICON\*/)$", re.M)
found = anchor.findall(html)
if len(found) != 1:
    raise SystemExit("expected exactly one icon anchor in the board, found %d" % len(found))

html = anchor.sub(lambda _: line, html, count=1)
with io.open(board, "w", encoding="utf-8", newline="\n") as handle:
    handle.write(html)

print("embedded %d PNG bytes as %d characters of CSS" % (len(raw), len(line)))
