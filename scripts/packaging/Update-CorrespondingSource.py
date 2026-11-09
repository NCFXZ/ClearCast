"""Refresh application source in a verified corresponding-source release bundle.

The unchanged, already compressed dependency source archives are copied without
downloading them again. Application source, exact dependency notices, additional
upstream source archives and their hash metadata can be refreshed together.
SPDX-License-Identifier: GPL-3.0-or-later
"""
import argparse
from copy import copy
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import tempfile
import zipfile


def digest(stream):
    value = hashlib.sha256()
    for block in iter(lambda: stream.read(1024 * 1024), b""):
        value.update(block)
    return value.hexdigest()


def safe_name(name):
    path = PurePosixPath(name)
    if (not path.parts or path.is_absolute() or ".." in path.parts or "\\" in name or ":" in name or
            path.as_posix() != name.removesuffix("/")):
        raise ValueError(f"Unsafe archive path: {name}")
    return path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", type=Path, required=True)
    parser.add_argument("--app-source", type=Path, required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--version", help="Release version used in the source-bundle title and download link")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--compliance-dir", type=Path)
    parser.add_argument("--notice-directory", type=Path,
                        help="Exact distribution notices for the new binaries; replaces the base notices")
    parser.add_argument("--additional-source", action="append", default=[], metavar="MEMBER=FILE",
                        help="Additional official source archive to include, e.g. gstreamer/package.tar.xz=C:/source.tar.xz")
    args = parser.parse_args()
    if not re.fullmatch(r"[0-9a-fA-F]{40}", args.commit):
        parser.error("--commit must be a full Git commit SHA")
    if args.version and not re.fullmatch(r"\d+\.\d+\.\d+(?:\.\d+)?", args.version):
        parser.error("--version must have the form major.minor.patch or major.minor.patch.revision")
    if args.output.resolve() in (args.base.resolve(), args.app_source.resolve()):
        parser.error("--output must differ from both input archives")

    additional = {}
    for specification in args.additional_source:
        name, separator, filename = specification.partition("=")
        safe_name(name)
        if not separator or not name or name.endswith("/"):
            parser.error("--additional-source requires MEMBER=FILE")
        folded = name.casefold()
        if folded in {value.casefold() for value in additional} or folded in {"clearcast-source.zip", "readme.txt", "sha256sums", "third-party-components.md"} or folded.startswith("distribution-notices/"):
            parser.error(f"Conflicting additional source member: {name}")
        source_path = Path(filename)
        if source_path.resolve() == args.output.resolve():
            parser.error("--output must differ from additional source archives")
        with source_path.open("rb") as source:
            additional[name] = (source_path, digest(source))

    notices = {}
    if args.notice_directory:
        if not (args.notice_directory / "THIRD-PARTY-COMPONENTS.md").is_file() or not (args.notice_directory / "licenses").is_dir():
            parser.error("--notice-directory must contain THIRD-PARTY-COMPONENTS.md and licenses/")
        for path in sorted(args.notice_directory.rglob("*")):
            if path.is_symlink():
                raise ValueError(f"Symbolic links are not allowed in distribution notices: {path}")
            if path.is_file():
                relative = path.relative_to(args.notice_directory).as_posix()
                safe_name(relative)
                if {part.casefold() for part in PurePosixPath(relative).parts} & {".clearcast-state", ".runtime", ".git"}:
                    raise ValueError(f"Private/generated state in notices: {relative}")
                with path.open("rb") as source:
                    notices["distribution-notices/" + relative] = (path, digest(source))

    with args.app_source.open("rb") as source:
        app_hash = digest(source)
    with zipfile.ZipFile(args.app_source) as application:
        raw_names = application.namelist()
        if len(raw_names) != len({name.casefold() for name in raw_names}):
            raise ValueError("Application archive has duplicate or case-aliased paths")
        names = set(raw_names)
        for name in names:
            parts = safe_name(name).parts
            if {part.casefold() for part in parts} & {".clearcast-state", ".runtime", ".git"}:
                raise ValueError(f"Private/generated state in application source: {name}")
        for required in ("ClearCast/README.md", "ClearCast/LICENSE",
                         "ClearCast/src/ClearCast/ClearCast.csproj",
                         "ClearCast/scripts/native/Build-Receiver.ps1"):
            if required not in names:
                raise ValueError(f"Application source is missing {required}; use git archive --prefix=ClearCast/")

    args.output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(args.base) as original:
        names = original.namelist()
        if len(names) != len({name.casefold() for name in names}):
            raise ValueError("Base archive has duplicate or case-aliased paths")
        for name in names:
            safe_name(name)
        if {name.casefold() for name in additional} & {name.casefold() for name in names}:
            raise ValueError("Additional source paths must not replace existing base members")
        checksums = {}
        for row in original.read("SHA256SUMS").decode("utf-8").splitlines():
            if not row.strip():
                continue
            expected, name = row.split(None, 1)
            name = name.lstrip(" *")
            if not re.fullmatch(r"[0-9a-fA-F]{64}", expected):
                raise ValueError(f"Invalid checksum: {name}")
            with original.open(name) as source:
                if digest(source) != expected.lower():
                    raise ValueError(f"Checksum mismatch in base archive: {name}")
            checksums[name] = expected.lower()
        if "clearcast-source.zip" not in checksums:
            raise ValueError("Base checksums omit clearcast-source.zip")
        verified_base_members = len(checksums)
        checksums["clearcast-source.zip"] = app_hash
        if notices:
            checksums = {name: sha for name, sha in checksums.items()
                         if not name.startswith("distribution-notices/") and name != "THIRD-PARTY-COMPONENTS.md"}
            checksums.update({name: sha for name, (_, sha) in notices.items()})
            checksums["THIRD-PARTY-COMPONENTS.md"] = notices["distribution-notices/THIRD-PARTY-COMPONENTS.md"][1]
        checksums.update({name: sha for name, (_, sha) in additional.items()})
        readme, substitutions = re.subn(
            r"(released Git commit )[0-9a-fA-F]{40}",
            r"\g<1>" + args.commit.lower(),
            original.read("README.txt").decode("utf-8"),
        )
        if substitutions != 1:
            raise ValueError("Base README does not identify exactly one application commit")
        if args.version:
            readme, titles = re.subn(r"^ClearCast \d+\.\d+\.\d+(?:\.\d+)? corresponding source", f"ClearCast {args.version} corresponding source", readme, flags=re.MULTILINE)
            readme, links = re.subn(r"https://github\.com/NCFXZ/ClearCast/releases/tag/v\d+\.\d+\.\d+(?:\.\d+)?", f"https://github.com/NCFXZ/ClearCast/releases/tag/v{args.version}", readme)
            if titles != 1 or links != 1:
                raise ValueError("Base README does not identify exactly one release title and download link")
        if additional:
            readme += "\nAdditional corresponding source archives for rebuilt components:\n"
            readme += "\n".join("  " + name for name in additional) + "\n"
        if notices:
            readme += "\nThe distribution notices and THIRD-PARTY-COMPONENTS.md describe the exact newly shipped binaries.\n"
        checksum_bytes = ("\n".join(f"{sha}  {name}" for name, sha in checksums.items()) + "\n").encode()
        temporary = None
        try:
            with tempfile.NamedTemporaryFile(prefix=args.output.name + ".", suffix=".tmp", dir=args.output.parent, delete=False) as file:
                temporary = Path(file.name)
            with zipfile.ZipFile(temporary, "w", allowZip64=True) as updated:
                for entry in original.infolist():
                    if notices and (entry.filename.startswith("distribution-notices/") or entry.filename == "THIRD-PARTY-COMPONENTS.md"):
                        continue
                    output_entry = copy(entry)
                    if entry.filename == "clearcast-source.zip":
                        with args.app_source.open("rb") as source, updated.open(output_entry, "w", force_zip64=True) as target:
                            shutil.copyfileobj(source, target, 1024 * 1024)
                    elif entry.filename == "README.txt":
                        updated.writestr(output_entry, readme.encode("utf-8"))
                    elif entry.filename == "SHA256SUMS":
                        updated.writestr(output_entry, checksum_bytes)
                    else:
                        with original.open(entry) as source, updated.open(output_entry, "w", force_zip64=True) as target:
                            shutil.copyfileobj(source, target, 1024 * 1024)
                    if not notices and args.compliance_dir and entry.filename.startswith("distribution-notices/") and not entry.is_dir():
                        relative = PurePosixPath(entry.filename).relative_to("distribution-notices")
                        destination = args.compliance_dir.joinpath(*relative.parts)
                        destination.parent.mkdir(parents=True, exist_ok=True)
                        with original.open(entry) as source, destination.open("wb") as target:
                            shutil.copyfileobj(source, target)
                if notices:
                    updated.write(notices["distribution-notices/THIRD-PARTY-COMPONENTS.md"][0], "THIRD-PARTY-COMPONENTS.md")
                for name, (path, _) in additional.items():
                    updated.write(path, name)
                for name, (path, _) in notices.items():
                    updated.write(path, name)
                    if args.compliance_dir:
                        relative = PurePosixPath(name).relative_to("distribution-notices")
                        destination = args.compliance_dir.joinpath(*relative.parts)
                        if destination.resolve() != path.resolve():
                            destination.parent.mkdir(parents=True, exist_ok=True)
                            shutil.copyfile(path, destination)
            temporary.replace(args.output)
            temporary = None
        finally:
            if temporary:
                temporary.unlink(missing_ok=True)
    with args.output.open("rb") as source:
        output_hash = digest(source)
    print(json.dumps({"output": str(args.output.resolve()), "sha256": output_hash,
                      "application_commit": args.commit.lower(), "application_source_sha256": app_hash,
                      "verified_base_members": verified_base_members,
                      "verified_source_archives": len([name for name in checksums if name.endswith((".zip", ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst"))]),
                      "additional_sources": {name: sha for name, (_, sha) in additional.items()},
                      "distribution_notice_files": len(notices)}, indent=2))


if __name__ == "__main__":
    main()
