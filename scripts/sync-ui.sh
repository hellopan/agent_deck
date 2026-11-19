#!/usr/bin/env bash
# Regenerate the WebView2 page from the design board.
#
# design/v3-windows.html is the single source of truth for the PC interface.
# The shell runs that exact file, plus one extra stylesheet that makes a design
# board behave like a window (see ui/shell.css). Nothing else may be edited in
# ui/index.html: run this script after every change to the board instead.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/.." && pwd)"
from="$root/design/v3-windows.html"
to="$root/windows/AgentDeck.Tray/ui/index.html"

[ -f "$from" ] || { echo "missing $from" >&2; exit 1; }

python - "$from" "$to" <<'PY'
import io
import sys

source, target = sys.argv[1], sys.argv[2]
with io.open(source, encoding="utf-8") as handle:
    html = handle.read()

if html.count("</head>") != 1:
    raise SystemExit("expected exactly one </head> in the design board")

link = '<link rel="stylesheet" href="shell.css">\n</head>'
html = html.replace("</head>", link, 1)

if html.count("</body>") != 1:
    raise SystemExit("expected exactly one </body> in the design board")

# The app layer is injected here rather than in the board: the board stays a
# pure mock that opens in any browser, and only the shell binds it to state.
script = '<script src="app.js"></script>\n</body>'
html = html.replace("</body>", script, 1)

with io.open(target, "w", encoding="utf-8", newline="\n") as handle:
    handle.write(html)

print("wrote", target, len(html), "bytes")
PY
