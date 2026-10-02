#!/bin/bash
# icons.txt の対応表のとおりにアイコンを取ってきて、Editor/Icons に白・透明背景・48 px の PNG として置く。
# 要るもの: curl、rsvg-convert（librsvg2-bin）。.meta は Unity で取り込み設定（GUI 用・ミップマップ無し・圧縮無し）を付けて作る。
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="$here/../../Editor/Icons"
size="${ICON_SIZE:-48}"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
FLUENT=https://raw.githubusercontent.com/microsoft/fluentui-system-icons/main/assets
PHOSPHOR=https://raw.githubusercontent.com/phosphor-icons/core/main/assets
title() { local s="" w; for w in ${1//_/ }; do s+="${s:+%20}${w^}"; done; echo "$s"; }
render() { # svg-url png
  curl -fsS -o "$tmp/i.svg" "$1" || { echo "not found: $1" >&2; return 1; }
  sed -i 's/#212121/#FFFFFF/g; s/currentColor/#FFFFFF/g' "$tmp/i.svg"
  grep -q 'fill=' "$tmp/i.svg" || sed -i 's/<svg /<svg fill="#FFFFFF" /' "$tmp/i.svg"
  mkdir -p "$(dirname "$2")"; rsvg-convert -w "$size" -h "$size" "$tmp/i.svg" -o "$2"
}
url() { # set name weight(regular|filled|bold)
  if [[ $1 == fluent ]]; then echo "$FLUENT/$(title "$2")/SVG/ic_fluent_${2}_24_$3.svg"
  elif [[ $3 == regular ]]; then echo "$PHOSPHOR/regular/$2.svg"
  elif [[ $3 == bold ]]; then echo "$PHOSPHOR/bold/$2-bold.svg"
  else echo "$PHOSPHOR/fill/$2-fill.svg"; fi
}
count=0
while read -r name set source selected; do
  [[ -z "$name" || "$name" == \#* ]] && continue
  if [[ $name == tools/* ]]; then
    id="${name#tools/}"
    render "$(url "$set" "$source" regular)" "$out/Tools/$id.png"
    render "$(url "$set" "$source" "${selected:-filled}")" "$out/Tools/${id}_selected.png"
  else render "$(url "$set" "$source" regular)" "$out/$name.png"; fi
  count=$((count + 1))
done < "$here/icons.txt"
echo "ok: $count icons"
