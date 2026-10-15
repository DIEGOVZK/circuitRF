#!/bin/sh
# brief-em3d-135 R-em3d135-2 -- meshes the corpus with one Gmsh, exactly as a run does
# (`gmsh model.geo -3 -o model.msh` in the script's own directory), and compares what it wrote with the
# reference. Usage:  run.sh <path to gmsh> <output directory>
#
#   corpus/<name>/        circuitRF's own scripts: the four palace-goldens, every example .c3d's Palace and
#                         thermal script (written by the writer a run uses), each with its groups.json and
#                         any kernel .brep files; entities.txt is 4.15.2's, the reference.
#   ../f4b/q4/*.geo       the kernel-import (B-rep / STEP) probes; they print "Q4 ..." lines, not a table,
#                         and the reference is q4/<name>.txt here.
#   ../../thermal/**/mesh/ the thermal references' scripts (written by their Python generators, not by
#                         circuitRF): exit code and element count only.
#
# Writes <output>/summary.tsv (script, exit, entities verdict, elements, seconds) and, per script, the
# entities.txt / printed lines and the log. Exit 0 only when every script exits 0 and matches.
set -u
gmsh=$1
out=$(mkdir -p "$2" && cd "$2" && pwd)
here=$(cd "$(dirname "$0")" && pwd)
"$gmsh" -info 2>&1 | grep -E '^(Version|OCC version)' > "$out/gmsh-info.txt"
printf 'script\texit\tentities\telements\tseconds\n' > "$out/summary.tsv"
fail=0

mesh() {  # mesh <id> <source dir> <geo file> <reference or ""> <kind: table|q4|none>
  id=$1; src=$2; geo=$3; ref=$4; kind=$5
  work=$(mktemp -d); cp -R "$src"/. "$work"/; rm -f "$work/entities.txt"
  mkdir -p "$out/$id"
  start=$(date +%s)
  (cd "$work" && "$gmsh" "$geo" -3 -o model.msh > "$out/$id/gmsh.log" 2>&1); code=$?
  secs=$(( $(date +%s) - start ))
  els=$(grep -Eo '[0-9]+ nodes [0-9]+ elements' "$out/$id/gmsh.log" | tail -1 | awk '{print $3}')
  case $kind in
    table) cp "$work/entities.txt" "$out/$id/entities.txt" 2>/dev/null ;;
    q4)    grep -o 'Q4 .*' "$out/$id/gmsh.log" > "$out/$id/entities.txt" ;;
  esac
  verdict=-
  if [ "$kind" != none ]; then
    if [ ! -s "$out/$id/entities.txt" ]; then verdict=missing
    elif [ ! -f "$ref" ]; then verdict=no-reference
    elif cmp -s "$out/$id/entities.txt" "$ref"; then verdict=identical
    else verdict=DIFFERENT; diff "$ref" "$out/$id/entities.txt" > "$out/$id/entities.diff"; fi
  fi
  [ "$code" -ne 0 ] && fail=1
  [ "$verdict" = DIFFERENT ] || [ "$verdict" = missing ] && fail=1
  printf '%s\t%s\t%s\t%s\t%s\n' "$id" "$code" "$verdict" "${els:-}" "$secs" >> "$out/summary.tsv"
  rm -rf "$work"
}

for d in "$here"/corpus/*/; do
  n=$(basename "$d"); mesh "corpus/$n" "$d" model.geo "$d/entities.txt" table
done
for g in "$here"/../f4b/q4/*.geo; do
  n=$(basename "$g" .geo); mesh "q4/$n" "$here/../f4b/q4" "$n.geo" "$here/q4/$n.txt" q4
done
find "$here/../../thermal" -path '*/mesh/*.geo' | sort > "$out/thermal-ref.list"
while read -r g; do
  n=$(echo "${g#"$here/../../thermal/"}" | sed 's|/mesh/|/|; s|\.geo$||')
  mesh "thermal-ref/$n" "$(dirname "$g")" "$(basename "$g")" "" none
done < "$out/thermal-ref.list"
cat "$out/summary.tsv"
exit $fail
