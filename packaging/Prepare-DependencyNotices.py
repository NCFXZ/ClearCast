"""Prepare exact release notices for the two rebuilt GStreamer parser components.

SPDX-License-Identifier: GPL-3.0-or-later
"""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import tarfile
from urllib.parse import urlparse
import zipfile


def sha256(path):
    with path.open("rb") as source:
        value = hashlib.sha256()
        for block in iter(lambda: source.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", type=Path, required=True, help="Previous corresponding-source ZIP")
    parser.add_argument("--gstreamer-source", type=Path, required=True)
    parser.add_argument("--security-directory", type=Path, required=True)
    parser.add_argument("--output-directory", type=Path, required=True)
    args = parser.parse_args()
    if args.output_directory.exists() and any(args.output_directory.iterdir()):
        parser.error("--output-directory must be new or empty")

    manifest = json.loads((args.security_directory / "manifest.json").read_text(encoding="utf-8-sig"))
    version = manifest["version"]
    if manifest.get("schemaVersion") != 1 or not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("Unsupported security-component manifest")
    expected_paths = {"libgstcodecparsers-1.0-0.dll", "lib/gstreamer-1.0/libgstvideoparsersbad.dll"}
    if {entry["relativePath"] for entry in manifest["files"]} != expected_paths or len(manifest["files"]) != 2:
        raise ValueError("The security manifest must identify exactly the two parser components")
    source_name = f"gst-plugins-bad-{version}.tar.xz"
    source_url = f"https://gstreamer.freedesktop.org/src/gst-plugins-bad/{source_name}"
    if manifest["sourceUrl"] != source_url or urlparse(source_url).scheme != "https":
        raise ValueError("Unexpected official GStreamer source URL")
    source_hash = sha256(args.gstreamer_source)
    if source_hash != manifest["sourceSha256"].lower():
        raise ValueError("GStreamer source checksum does not match the security manifest")
    for entry in manifest["files"]:
        if sha256(args.security_directory.joinpath(*PurePosixPath(entry["relativePath"]).parts)) != entry["sha256"].lower():
            raise ValueError(f"Rebuilt component checksum mismatch: {entry['relativePath']}")
    with tarfile.open(args.gstreamer_source, "r:xz") as source:
        license_bytes = source.extractfile(f"gst-plugins-bad-{version}/COPYING").read()

    with zipfile.ZipFile(args.base) as original:
        inventory = original.read("distribution-notices/THIRD-PARTY-COMPONENTS.md").decode("utf-8")
        inventory = re.sub(r"\r+\n", "\n", inventory)
        lines = inventory.splitlines()
        changed_rows = 0
        native_rows = []
        for index, line in enumerate(lines):
            if line.startswith("| mingw-w64-ucrt-x86_64-"):
                native_rows.append(index)
            if line.startswith(("| mingw-w64-ucrt-x86_64-gst-plugins-bad |", "| mingw-w64-ucrt-x86_64-gst-plugins-bad-libs |")):
                fields = [field.strip() for field in line.split("|")]
                paths = [path.strip() for path in fields[4].split(",")]
                remaining = [path for path in paths if path not in expected_paths]
                if len(remaining) != len(paths) - 1:
                    raise ValueError("Base inventory does not identify the expected parser files")
                fields[4] = ", ".join(remaining)
                lines[index] = "| " + " | ".join(fields[1:-1]) + " |"
                changed_rows += 1
        if changed_rows != 2 or not native_rows:
            raise ValueError("Base inventory is missing the two original MSYS2 parser component rows")
        row = (f"| gst-plugins-bad (official source build) | {version} | LGPL-2.1-or-later | "
               + ", ".join(sorted(expected_paths)) + f" | [Source]({source_url}) |")
        lines.insert(native_rows[-1] + 1, row)
        inventory = "\n".join(lines) + "\n"
        heading = "## Native runtime inventory"
        section = (f"## Rebuilt GStreamer parser components\n\n"
                   f"The codec parser library and video parser plugin are built from the unmodified official gst-plugins-bad {version} source archive. "
                   "Other GStreamer libraries and plugins retain the MSYS2 versions listed below. "
                   "The rebuild uses the installed UCRT64 GStreamer ABI and the controlled configuration in `native/Build-GStreamerSecurity.ps1`.\n\n"
                   f"Source: [{source_name}]({source_url})\n\n"
                   f"Source SHA-256: `{source_hash}`\n\n"
                   f"Configuration: `{manifest['configuration']}`\n\n"
                   f"License: LGPL-2.1-or-later; see `licenses/gst-plugins-bad-{version}/COPYING`. "
                   f"Exact component hashes and source provenance are in `licenses/gst-plugins-bad-{version}/source-build.json`.\n\n")
        if inventory.count(heading) != 1:
            raise ValueError("Base inventory must contain exactly one native inventory heading")
        inventory = inventory.replace(heading, section + heading, 1)
        inventory = inventory.replace(
            "Package metadata and file ownership are from the MSYS2 UCRT64 installation used to build this release. The installed library files were matched to the packaged files by SHA-256.",
            "The MSYS2 entries below describe unchanged files matched to the installed UCRT64 package files by SHA-256. "
            "The two parser components in the official-source-build entry are instead matched to the controlled build manifest above.")
        args.output_directory.mkdir(parents=True, exist_ok=True)
        for entry in original.infolist():
            if not entry.filename.startswith("distribution-notices/") or entry.is_dir():
                continue
            relative = PurePosixPath(entry.filename).relative_to("distribution-notices")
            if relative.is_absolute() or ".." in relative.parts or "\\" in entry.filename or ":" in entry.filename:
                raise ValueError(f"Unsafe base notice path: {entry.filename}")
            destination = args.output_directory.joinpath(*relative.parts)
            destination.parent.mkdir(parents=True, exist_ok=True)
            with original.open(entry) as source, destination.open("wb") as target:
                shutil.copyfileobj(source, target)
    (args.output_directory / "THIRD-PARTY-COMPONENTS.md").write_text(inventory, encoding="utf-8")
    license_directory = args.output_directory / "licenses" / f"gst-plugins-bad-{version}"
    license_directory.mkdir(parents=True)
    (license_directory / "COPYING").write_bytes(license_bytes)
    (license_directory / "source-build.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"noticeDirectory": str(args.output_directory.resolve()), "sourceVersion": version,
                      "sourceSha256": source_hash, "sourceArchiveMember": "gstreamer/" + source_name,
                      "additionalSourceFile": str(args.gstreamer_source.resolve()), "componentHashesVerified": 2}, indent=2))


if __name__ == "__main__":
    main()
