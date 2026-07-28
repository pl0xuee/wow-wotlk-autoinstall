#!/usr/bin/env bash
# Renders the Steam library artwork and app icon from scratch with ImageMagick.
# Steam wants four sizes per shortcut plus an icon; regenerate with:
#   scripts/gen-steam-grid.sh
set -euo pipefail

out="$(dirname "$0")/../src/WowWotlk.Gui/Assets/SteamGrid"
mkdir -p "$out"

# Northrend palette, matching App.axaml.
bg='#0B1017'
deep='#111A24'
ice='#3B9BF0'
gold='#E6CC80'
muted='#7C93A6'

# A vertical night-to-ice wash, then a hairline rule and the wordmark. The same recipe at
# four aspect ratios so the capsules read as one set in the library.
render() {
  local file=$1 w=$2 h=$3 title=$4 sub=$5 titlept=$6 subpt=$7
  magick -size "${w}x${h}" \
    gradient:"${deep}-${bg}" \
    -fill "$ice" -colorize 6% \
    \( -size "${w}x2" xc:"$ice" \) -gravity center -geometry "+0+$((h/14))" -composite \
    -gravity center \
    -font DejaVu-Sans-Bold -pointsize "$titlept" -fill "$gold" \
    -annotate "+0-$((h/9))" "$title" \
    -font DejaVu-Sans -pointsize "$subpt" -fill "$muted" \
    -annotate "+0+$((h/5))" "$sub" \
    "$out/$file"
}

# Wide capsule (library grid), tall capsule (library sidebar), hero banner, logo, icon.
render landscape.png 920 430  "WRATH OF THE LICH KING" "3.3.5a  ·  LOCAL REALM" 42 20
render portrait.png  600 900  "WRATH OF THE LICH KING" "3.3.5a  ·  LOCAL REALM" 40 22
render hero.png      1920 620 "WRATH OF THE LICH KING" "3.3.5a  ·  LOCAL REALM" 64 28

# The logo overlays the hero, so it is transparent and carries no background wash.
magick -size 800x300 xc:none -gravity center \
  -font DejaVu-Sans-Bold -pointsize 54 -fill "$gold" -annotate "+0-20" "WOTLK" \
  -font DejaVu-Sans -pointsize 22 -fill "$ice" -annotate "+0+40" "3.3.5a" \
  "$out/logo.png"

# Icon: a runeblade mark — gold ring, ice rune. Geometry only, no text, so it still reads
# as something at the 16px Steam and taskbar sizes where a patch number turns to mush.
magick -size 256x256 xc:"$bg" \
  -stroke "$gold" -strokewidth 7 -fill none -draw "circle 128,128 128,20" \
  -stroke "$ice" -strokewidth 13 -draw "line 128,62 128,194" \
  -draw "line 128,104 180,66" -draw "line 128,152 76,190" \
  -stroke none -fill "$gold" -draw "circle 128,62 128,52" \
  "$out/icon.png"

cp "$out/icon.png" "$(dirname "$0")/../packaging/icon-256.png"
echo "Wrote $(ls "$out" | tr '\n' ' ')"
