#!/usr/bin/env bash
set -euo pipefail

# Generate both native icon sets from the documentation favicon so every client shares
# one visual source. Inkscape performs deterministic SVG rasterization; no network access
# or design credentials are required.
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/../.." && pwd)"
source_svg="${repo_root}/docs/public/favicon.svg"
ios_icon="${repo_root}/mobile/native/ios/InfraAdvisorMobile/Assets.xcassets/AppIcon.appiconset/AppIcon-1024.png"
android_res="${repo_root}/mobile/native/android/app/src/main/res"

if [[ ! -f "${source_svg}" ]]; then
    echo "error: source icon not found: ${source_svg}" >&2
    exit 1
fi

if command -v inkscape >/dev/null 2>&1 && inkscape --version >/dev/null 2>&1; then
    rasterizer="inkscape"
    # iOS icons cannot contain transparency. The source's blue is used behind its rounded
    # favicon tile, producing a full-bleed square that iOS can mask into the device shape.
    inkscape "${source_svg}" --export-filename="${ios_icon}" --export-width=1024 --export-height=1024 --export-background="#1d4ed8" --export-background-opacity=255 --export-png-color-mode=RGB_8

    # Adaptive icons reserve an outer crop region. Exporting a larger SVG area adds the
    # required transparent padding while the adaptive background supplies full-bleed blue.
    inkscape "${source_svg}" --export-filename="${android_res}/drawable-nodpi/ic_launcher_foreground.png" --export-area=-8:-8:40:40 --export-width=432 --export-height=432 --export-background-opacity=0
elif command -v qlmanage >/dev/null 2>&1 && command -v sips >/dev/null 2>&1 && command -v perl >/dev/null 2>&1 && command -v ffmpeg >/dev/null 2>&1; then
    rasterizer="macos"
    # macOS fallback for the Xcode/CocoaPods development environment. Quick Look renders
    # SVG reliably but has no background flag, so temporary SVG variants provide the opaque
    # iOS canvas and the larger adaptive-icon view box.
    asset_tmp="$(mktemp -d)"
    trap 'rm -rf "${asset_tmp}"' EXIT
    opaque_svg="${asset_tmp}/opaque.svg"
    padded_svg="${asset_tmp}/padded.svg"
    perl -0pe 's!(<svg\b[^>]*>)!$1\n    <rect x="0" y="0" width="32" height="32" style="fill:rgb(29,78,216);"/>!' "${source_svg}" > "${opaque_svg}"
    perl -0pe 's/viewBox="0 0 32 32"/viewBox="-8 -8 48 48"/' "${source_svg}" > "${padded_svg}"
    qlmanage -t -s 1024 -o "${asset_tmp}" "${opaque_svg}" >/dev/null 2>&1
    qlmanage -t -s 432 -o "${asset_tmp}" "${padded_svg}" >/dev/null 2>&1
    mv "${asset_tmp}/opaque.svg.png" "${ios_icon}"
    mv "${asset_tmp}/padded.svg.png" "${android_res}/drawable-nodpi/ic_launcher_foreground.png"
    # Quick Look always writes RGBA, even when every pixel is opaque. App Store validation
    # rejects an alpha channel, so convert losslessly to an RGB PNG before deriving fallbacks.
    ffmpeg -hide_banner -loglevel error -y -i "${ios_icon}" -pix_fmt rgb24 "${asset_tmp}/AppIcon-1024-rgb.png"
    mv "${asset_tmp}/AppIcon-1024-rgb.png" "${ios_icon}"
else
    echo "error: install Inkscape, or use qlmanage, sips, perl, and ffmpeg on macOS." >&2
    exit 1
fi

# Legacy Android launchers select a density-specific raster. The 1024-pixel opaque
# source keeps all variants visually identical to the iOS icon.
while read -r density pixels; do
    if [[ "${rasterizer}" == "inkscape" ]]; then
        inkscape "${source_svg}" --export-filename="${android_res}/mipmap-${density}/ic_launcher.png" --export-width="${pixels}" --export-height="${pixels}" --export-background="#1d4ed8" --export-background-opacity=255
        inkscape "${source_svg}" --export-filename="${android_res}/mipmap-${density}/ic_launcher_round.png" --export-width="${pixels}" --export-height="${pixels}" --export-background="#1d4ed8" --export-background-opacity=255
    else
        sips -z "${pixels}" "${pixels}" "${ios_icon}" --out "${android_res}/mipmap-${density}/ic_launcher.png" >/dev/null
        sips -z "${pixels}" "${pixels}" "${ios_icon}" --out "${android_res}/mipmap-${density}/ic_launcher_round.png" >/dev/null
    fi
done <<'SIZES'
mdpi 48
hdpi 72
xhdpi 96
xxhdpi 144
xxxhdpi 192
SIZES

echo "Generated iOS and Android app icons from docs/public/favicon.svg."
