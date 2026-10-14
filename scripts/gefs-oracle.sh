#!/usr/bin/env bash
# Builds 9front gefs's tree.c with Fog's 48-byte block pointers and records, for each fixture, a
# digest of the tree's exact shape after every tenth of a seeded run of upserts. The gefs tests
# replay the same upserts against the port and compare. Needs plan9port and a 9front clone.
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
front=${NINEFRONT:-$HOME/Repo/9front}
plan9=${PLAN9:-/usr/local/plan9}
out="$root/NinePSharp.Fog.Gefs.Tests/Oracle"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

rev=$(git -C "$front" rev-parse origin/front)
for f in tree.c dat.h fns.h; do
  git -C "$front" show "origin/front:sys/src/cmd/gefs/$f" > "$work/$f"
done

# gcc lacks 9front's atomic types, and an unnamed Limbo's next clashes with Mount's own.
sed -i '1i typedef long Along; typedef vlong Avlong; typedef ulong Aulong; typedef uvlong Auvlong; typedef int Aint; typedef uint Auint; typedef uintptr Aptr; typedef ulong usize;' "$work/dat.h"
sed -i 's/^\tMount\t\*next;/\tMount\t*mnext;/; s/^\tPtrsz\t= 24,/\tPtrsz\t= 48,/' "$work/dat.h"
grep -q $'^\tPtrsz\t= 48,' "$work/dat.h" || { echo "could not set Ptrsz in dat.h" >&2; exit 1; }
grep -q $'^\tMount\t\*mnext;' "$work/dat.h" || { echo "could not rename Mount's next in dat.h" >&2; exit 1; }

cp "$root/tools/gefs-oracle/harness.c" "$work/"
(cd "$work" && PATH="$plan9/bin:$PATH" 9c -fplan9-extensions tree.c harness.c && PATH="$plan9/bin:$PATH" 9l -o harness tree.o harness.o)

# name: seed upserts keys shortest-key longest-key longest-value most-messages growing-upserts
while read -r name args; do
  {
    echo "# 9front $rev: harness $args"
    # shellcheck disable=SC2086
    "$work/harness" $args | awk 'NR % 10 == 0'
  } > "$out/$name.txt"
done <<'FIXTURES'
three-high-and-back 2 4000 1000 150 256 512 8 800
two-high-and-back 5 3000 400 4 256 512 8 900
small-entries 4 3000 2000 4 64 128 8 1200
FIXTURES
