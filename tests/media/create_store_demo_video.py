"""Create the original silent geometric video used by Store capture QA.

No user recording, third-party film or copyrighted album art is consumed.
SPDX-License-Identifier: GPL-3.0-or-later
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-directory", type=Path, default=ROOT / ".runtime/store-media")
    parser.add_argument("--ffmpeg", type=Path, default=ROOT / ".runtime/msys64/ucrt64/bin/ffmpeg.exe")
    args = parser.parse_args()
    output = args.output_directory.resolve()
    output.mkdir(parents=True, exist_ok=True)
    ffmpeg = args.ffmpeg.resolve(strict=True)
    video = output / "clearcast-original-demo.mp4"
    filters = "drawbox=x=130:y=110:w=1020:h=500:color=white@0.09:t=2," \
        "drawtext=fontfile=segoeui.ttf:text='ClearCast':fontsize=76:fontcolor=white:x=(w-tw)/2:y=(h-th)/2-32," \
        "drawtext=fontfile=segoeui.ttf:text='Your moments on a bigger screen':fontsize=26:fontcolor=white@0.75:x=(w-tw)/2:y=h/2+48"
    command = [str(ffmpeg), "-v", "error", "-y", "-f", "lavfi", "-i",
        "gradients=size=1280x720:rate=15:c0=0x173c60:c1=0xc18b78:duration=20:speed=0.03",
        "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-vf", filters, "-t", "20",
        "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "15",
        "-c:a", "aac", "-movflags", "+faststart", str(video)]
    environment = dict(os.environ, PATH=str(ffmpeg.parent) + os.pathsep + os.environ.get("PATH", ""))
    result = subprocess.run(command, cwd=Path(os.environ["SystemRoot"]) / "Fonts", env=environment,
        capture_output=True, text=True, timeout=60, creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode:
        raise RuntimeError(result.stderr[-2000:])
    report = {"file": str(video), "generator": str(Path(__file__).resolve()),
        "sha256": hashlib.sha256(video.read_bytes()).hexdigest(),
        "content": "Original mathematical color gradient, geometric frame and ClearCast demonstration text, with synthetic silence.",
        "containsUserOrThirdPartyMedia": False, "resolution": [1280, 720], "durationSeconds": 20}
    (output / "video-provenance.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
