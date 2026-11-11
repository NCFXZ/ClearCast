"""Validate and copy actual application captures for the Microsoft Store.

No image is resized, painted, or cropped here. The capture helpers must use
DWM visible-frame bounds, so invisible window resize gutters never enter PNGs.
SPDX-License-Identifier: GPL-3.0-or-later
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess

ROOT = Path(__file__).resolve().parents[2]
LOCALES = ("zh-CN", "en-US")
VIEWS = ("video-controls", "music-dark", "waiting-dark", "video-fullscreen",
         "music-light", "waiting-light", "settings-dark", "settings-light")
EXPECTED = {locale + "-" + view + ".png" for locale in LOCALES for view in VIEWS}


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def png_size(path):
    if not 0 < path.stat().st_size < 20 * 1024 * 1024:
        raise ValueError("Store capture exceeds the bounded PNG size: " + path.name)
    with path.open("rb") as source:
        header = source.read(24)
    if header[:8] != b"\x89PNG\r\n\x1a\n" or header[12:16] != b"IHDR":
        raise ValueError("Not a PNG application capture: " + path.name)
    width, height = struct.unpack(">II", header[16:24])
    if not (1366 <= width <= 8192 and 768 <= height <= 8192) or width * height > 32 * 1024 * 1024:
        raise ValueError("Unsupported Store capture dimensions: " + path.name)
    return width, height


def black_gutters(width, height, rgb):
    """Measure continuous near-black full edge strips, allowing rounded corners."""
    if len(rgb) != width * height * 3:
        raise ValueError("PNG decoder returned a truncated or oversized RGB frame.")
    result = {}
    for edge in ("left", "right", "top", "bottom"):
        extent = height if edge in ("left", "right") else width
        step = max(1, extent // 512)
        samples = range(0, extent, step)
        thickness = 0
        for offset in range(min(32, width, height)):
            black = 0
            for point in samples:
                x = (offset if edge == "left" else width - 1 - offset) if edge in ("left", "right") else point
                y = point if edge in ("left", "right") else (offset if edge == "top" else height - 1 - offset)
                index = (y * width + x) * 3
                black += max(rgb[index:index + 3]) <= 3
            if black / len(samples) < .98:
                break
            thickness += 1
        result[edge] = thickness
    return result


def inspect_capture(path, ffmpeg):
    width, height = png_size(path)
    process = subprocess.run([str(ffmpeg), "-v", "error", "-i", str(path), "-frames:v", "1",
        "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"], capture_output=True, timeout=40,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    if process.returncode:
        raise ValueError("Could not decode screenshot: " + process.stderr.decode(errors="replace")[-800:])
    gutters = black_gutters(width, height, process.stdout)
    fullscreen_video = path.stem.endswith(("-video-fullscreen", "-video-controls"))
    # Fullscreen video may legitimately letterbox its source. Other fullscreen
    # pages must not retain the old invisible resize strips.
    if not fullscreen_video and max(gutters.values()) > 1:
        raise ValueError("Invisible black capture gutters in " + path.name + ": " + json.dumps(gutters))
    return {"file": path.name, "locale": path.name[:5], "width": width, "height": height,
        "sha256": digest(path), "rgbSha256": hashlib.sha256(process.stdout).hexdigest(),
        "blackEdgePixels": gutters, "fullscreen": True, "fullscreenLetterboxingAllowed": fullscreen_video}


def validate_capture_set(records):
    if {record["file"] for record in records} != EXPECTED or len(records) != len(EXPECTED):
        raise ValueError("Expected eight fullscreen views for each locale.")
    if len({(record["width"], record["height"]) for record in records}) != 1:
        raise ValueError("All Store captures must use the same native fullscreen display dimensions.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, action="append", required=True,
        help="Directory containing named actual captures; repeat for music/pages and video.")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--ffmpeg", type=Path, default=ROOT / ".runtime/msys64/ucrt64/bin/ffmpeg.exe")
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    if not re.fullmatch(r"\d+\.\d+\.\d+\.0", args.version):
        parser.error("Expected a four-part application version with revision zero.")
    inputs = {}
    for directory in args.input:
        for path in directory.resolve(strict=True).glob("*.png"):
            if path.name not in EXPECTED:
                continue
            if path.name in inputs:
                raise ValueError("Duplicate capture input: " + path.name)
            inputs[path.name] = path
    if set(inputs) != EXPECTED:
        raise ValueError("Missing actual captures: " + ", ".join(sorted(EXPECTED - set(inputs))))
    records = [inspect_capture(inputs[name], args.ffmpeg.resolve(strict=True)) for name in sorted(EXPECTED)]
    validate_capture_set(records)
    output = args.output.resolve()
    if output.exists() and {path.name for path in output.glob("*.png")} - EXPECTED:
        raise ValueError("Output contains unrelated PNGs; choose a dedicated version directory.")
    output.mkdir(parents=True, exist_ok=True)
    for record in records:
        target = output / record["file"]
        if target != inputs[record["file"]]:
            shutil.copyfile(inputs[record["file"]], target)
        if digest(target) != record["sha256"]:
            raise ValueError("Capture changed while copying: " + target.name)
    report = {"applicationVersion": args.version,
        "capture": "Native-display fullscreen captures of the actual WinUI application's owned HWND only; exact DWM visible-frame pixels without desktop capture, resampling or painting.",
        "content": "Original geometric music artwork, native vector illustration, fictional track/device names, and an original mathematical-gradient video with synthetic silence.",
        "containsPrivateMedia": False, "captures": records}
    encoded = json.dumps(report, indent=2) + "\n"
    (output / "provenance.json").write_text(encoded, encoding="utf-8")
    (output / "README.md").write_text("# ClearCast " + args.version + " screenshots\n\n"
        "Actual fullscreen Chinese and English captures: waiting, music and settings in light/dark themes, plus video with controls and immersive video (8 per locale).\n\n"
        "Every screenshot fills the same native display with the application; no user desktop is captured. The helpers retain exact DWM visible-frame pixels without invisible resize gutters, rescaling or repainting. Video may naturally letterbox its source.\n\n"
        "Original demonstration content only; no private device content or third-party album art. Dimensions, PNG/RGB hashes and edge measurements are in [provenance.json](provenance.json).\n",
        encoding="utf-8")
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(encoded, encoding="utf-8")
    print(json.dumps({"passed": True, "captures": len(records), "perLanguage": len(VIEWS), "sourceHashesPreserved": True}))


if __name__ == "__main__":
    main()
