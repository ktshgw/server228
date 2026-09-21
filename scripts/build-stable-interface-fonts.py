"""Build the optional Legacy UI's original Aller bitmap fonts for osu!framework.

Requires Pillow and fontTools. Reads exported stable fonts and the explicitly named
Windows Cyrillic fallback; writes BMFont v3 and PNG atlases only inside this workspace.
The client never opens or installs these TTF files at runtime.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import struct
from pathlib import Path

from fontTools.ttLib import TTFont
from PIL import Image, ImageDraw, ImageFont, __version__ as pillow_version

WORKSPACE = Path(__file__).resolve().parent.parent
OUTPUT = WORKSPACE / ".sources/LazerAuthlibInjection/osu.Game.Rulesets.EnhancedAuth/Resources/StableFonts"
SIZE = 100
BASELINE = 80  # Same baseline/size convention as framework's Roboto-Regular.bin.
PAGE_SIZE = 1024
SPACING = 2
CODEPOINTS = sorted(set(range(32, 127)) | set(range(160, 384)) | set(range(0x400, 0x500)) | {
    0x2013, 0x2014, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2026, 0x2030,
    0x20AC, 0x2116, 0x2122, 0x2190, 0x2191, 0x2192, 0x2193, 0x2212, 0x221E,
    0x2260, 0x2264, 0x2265, 0x2605, 0x2665,
})


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def block(kind: int, payload: bytes) -> bytes:
    return bytes([kind]) + struct.pack("<I", len(payload)) + payload


class Face:
    def __init__(self, path: Path):
        self.path = path.resolve()
        self.font = ImageFont.truetype(str(self.path), SIZE)
        self.ttf = TTFont(self.path)
        self.cmap = self.ttf.getBestCmap()
        self.units = self.ttf["head"].unitsPerEm


def build(name: str, source_path: Path, fallback_path: Path, bold: bool) -> dict:
    source, fallback = Face(source_path), Face(fallback_path)
    pages = [Image.new("RGBA", (PAGE_SIZE, PAGE_SIZE), (255, 255, 255, 0))]
    glyphs = []
    selected = {}
    x = y = SPACING
    row_height = 0
    for code in CODEPOINTS:
        face = source if code in source.cmap else fallback if code in fallback.cmap else None
        if face is None:
            continue  # No tofu placeholder; the native client can use its Noto fallback.
        character = chr(code)
        left, top, right, bottom = face.font.getbbox(character, anchor="ls")
        width, height = max(1, right - left), max(1, bottom - top)
        if width + 2 * SPACING > PAGE_SIZE or height + 2 * SPACING > PAGE_SIZE:
            raise ValueError(f"Glyph exceeds atlas: {code}")
        if x + width + SPACING > PAGE_SIZE:
            x = SPACING
            y += row_height + SPACING
            row_height = 0
        if y + height + SPACING > PAGE_SIZE:
            pages.append(Image.new("RGBA", (PAGE_SIZE, PAGE_SIZE), (255, 255, 255, 0)))
            x = y = SPACING
            row_height = 0
        # FreeType's baseline anchor keeps real Aller and fallback Cyrillic aligned.
        glyph = Image.new("RGBA", (width, height), (255, 255, 255, 0))
        ImageDraw.Draw(glyph).text((-left, -top), character, font=face.font, anchor="ls", fill=(255, 255, 255, 255))
        pages[-1].paste(glyph, (x, y))
        advance = round(face.font.getlength(character))
        glyphs.append((code, x, y, width, height, left, BASELINE + top, advance, len(pages) - 1, 15))
        selected[code] = face
        x += width + SPACING
        row_height = max(row_height, height)

    required = set(ord(c) for c in "ИгратьРедакторНастройкиВыходМодыКоллекцииЁёЙй0123456789!?%")
    if missing := required - selected.keys():
        raise ValueError(f"Required Russian/Latin UI glyphs are missing: {sorted(missing)}")

    kerning = {}
    for face in (source, fallback):
        names = {}
        for code, picked in selected.items():
            if picked is face:
                names.setdefault(face.cmap[code], []).append(code)
        if "kern" in face.ttf:
            for table in face.ttf["kern"].kernTables:
                if table.version != 0 or not table.coverage & 1:
                    continue
                for (left, right), amount in table.kernTable.items():
                    value = round(amount * SIZE / face.units)
                    if value:
                        for first in names.get(left, []):
                            for second in names.get(right, []):
                                kerning[first, second] = value

    filenames = [f"{name}_{page}.png" for page in range(len(pages))]
    for filename, page in zip(filenames, pages):
        page.save(OUTPUT / filename, compress_level=9)
    info = struct.pack("<hBBHB4B2BB", SIZE, 0xC0 | (0x10 if bold else 0), 0, 100, 1, 0, 0, 0, 0, SPACING, SPACING, 0)
    info += name.encode("ascii") + b"\0"
    common = struct.pack("<5H5B", SIZE, BASELINE, PAGE_SIZE, PAGE_SIZE, len(pages), 0, 0, 4, 4, 4)
    character_block = b"".join(struct.pack("<I4H3h2B", *glyph) for glyph in glyphs)
    kern_block = b"".join(struct.pack("<IIh", first, second, amount) for (first, second), amount in sorted(kerning.items()))
    font_binary = b"BMF\3" + block(1, info) + block(2, common)
    font_binary += block(3, b"".join(filename.encode("ascii") + b"\0" for filename in filenames))
    font_binary += block(4, character_block) + block(5, kern_block)
    (OUTPUT / f"{name}.bin").write_bytes(font_binary)
    files = [OUTPUT / f"{name}.bin"] + [OUTPUT / filename for filename in filenames]
    return {
        "name": name, "pixel_size": SIZE, "baseline": BASELINE, "glyph_count": len(glyphs), "pages": len(pages),
        "source": {"file": source.path.name, "sha256": digest(source.path)},
        "fallback": {"file": fallback.path.name, "sha256": digest(fallback.path)},
        "source_glyphs": [code for code, picked in selected.items() if picked is source],
        "fallback_glyphs": [code for code, picked in selected.items() if picked is fallback],
        "kerning_pairs": len(kerning),
        "files": [{"file": path.name, "bytes": path.stat().st_size, "sha256": digest(path)} for path in files],
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stable-assets", type=Path, default=WORKSPACE / ".test-tmp/stable-inspection/assets/ui")
    parser.add_argument("--fallback", type=Path, default=Path("C:/Windows/Fonts/arial.ttf"))
    parser.add_argument("--fallback-bold", type=Path, default=Path("C:/Windows/Fonts/arialbd.ttf"))
    args = parser.parse_args()
    OUTPUT.mkdir(parents=True, exist_ok=True)
    fonts = [build("SomsAller-Regular", args.stable_assets / "Aller.ttf", args.fallback, False),
             build("SomsAller-Bold", args.stable_assets / "Aller_Bd.ttf", args.fallback_bold, True)]
    manifest = {"format": "BMFont v3", "pillow": pillow_version, "fonts": fonts}
    (OUTPUT / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    for font in fonts:
        print(f"{font['name']}: {font['glyph_count']} glyphs, {len(font['fallback_glyphs'])} explicit fallback glyphs, "
              f"{font['pages']} pages, {font['kerning_pairs']} kerning pairs, {sum(file['bytes'] for file in font['files'])} bytes")


if __name__ == "__main__":
    main()
