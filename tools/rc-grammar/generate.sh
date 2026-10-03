#!/bin/sh
# Regenerates NinePSharp.Fog.Rc/RcTables.cs: yacc's LALR(1) tables for 9front's rc grammar,
# sys/src/cmd/rc/syn.y, built by plan9port's yacc. The actions stay in RcParser.cs.
set -e
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
front=${NINEFRONT:-$repo/../9front}
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
git -C "$front" show origin/front:sys/src/cmd/rc/syn.y >"$work/syn.y"
(cd "$work" && "${PLAN9:-/usr/local/plan9}/bin/yacc" -d syn.y)
python3 "$here/tables.py" "$work/y.tab.c" "$(git -C "$front" rev-parse origin/front)" >"$repo/NinePSharp.Fog.Rc/RcTables.cs"
