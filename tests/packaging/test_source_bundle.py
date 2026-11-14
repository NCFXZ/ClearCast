"""Release-source integrity and private-state rejection checks."""
import hashlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import tarfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
TOOL = ROOT / "scripts/packaging/Update-CorrespondingSource.py"
NOTICES_TOOL = ROOT / "scripts/packaging/Prepare-DependencyNotices.py"


def app_archive(extra=None):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as archive:
        for name in ("README.md", "LICENSE", "src/ClearCast/ClearCast.csproj",
                     "scripts/native/Build-Receiver.ps1"):
            archive.writestr("ClearCast/" + name, "fixture")
        for name, value in (extra or {}).items():
            archive.writestr(name, value)
    return output.getvalue()


class SourceBundleTests(unittest.TestCase):
    def setUp(self):
        (ROOT / ".runtime").mkdir(exist_ok=True)
        self.directory = tempfile.TemporaryDirectory(prefix="source-tool-qa-", dir=ROOT / ".runtime")
        self.work = Path(self.directory.name)
        self.base = self.work / "base.zip"
        self.app = self.work / "app.zip"
        self.output = self.work / "updated.zip"
        self.app.write_bytes(app_archive({"ClearCast/new.txt": "replacement"}))

    def tearDown(self):
        self.directory.cleanup()

    def make_base(self, corrupt=False, extra=None, version="1.0.0"):
        members = {"clearcast-source.zip": app_archive(), "dependency.tar.zst": b"unchanged compressed source"}
        hashes = {name: hashlib.sha256(value).hexdigest() for name, value in members.items()}
        if corrupt:
            hashes["dependency.tar.zst"] = "0" * 64
        with zipfile.ZipFile(self.base, "w") as archive:
            for name, value in members.items():
                archive.writestr(name, value)
            archive.writestr("README.txt", f"ClearCast {version} corresponding source\n\nThe ClearCast source archive contains the released Git commit " + "1" * 40 + f".\nSee ClearCast/native/ for build instructions.\n\nhttps://github.com/NCFXZ/ClearCast/releases/tag/v{version}\n")
            archive.writestr("SHA256SUMS", "\n".join(sha + "  " + name for name, sha in hashes.items()) + "\n")
            archive.writestr("distribution-notices/THIRD-PARTY-COMPONENTS.md", "fixture inventory")
            archive.writestr("distribution-notices/licenses/dependency/LICENSE", "fixture terms")
            for name, value in (extra or {}).items():
                archive.writestr(name, value)

    def run_tool(self, extra_args=()):
        return subprocess.run([sys.executable, str(TOOL), "--base", str(self.base), "--app-source", str(self.app),
                               "--commit", "a" * 40, "--output", str(self.output),
                               "--compliance-dir", str(self.work / "compliance"), *extra_args], capture_output=True, text=True)

    def test_updates_source_bundle_release_title_and_link(self):
        self.make_base()
        result = self.run_tool(("--version", "1.1.0"))
        self.assertEqual(result.returncode, 0, result.stderr)
        with zipfile.ZipFile(self.output) as archive:
            readme = archive.read("README.txt").decode()
            self.assertIn("ClearCast 1.1.0 corresponding source", readme)
            self.assertIn("https://github.com/NCFXZ/ClearCast/releases/tag/v1.1.0", readme)
            self.assertNotIn("1.0.0", readme)
            self.assertIn('ClearCast/src/native/ and ClearCast/scripts/native/', readme)
            self.assertNotIn('ClearCast/native/', readme)

    def test_rejects_invalid_release_version(self):
        self.make_base()
        result = self.run_tool(("--version", "1.1.0/../../private"))
        self.assertNotEqual(result.returncode, 0)

    def test_updates_four_part_release_version(self):
        self.make_base()
        result = self.run_tool(("--version", "1.2.0.0"))
        self.assertEqual(result.returncode, 0, result.stderr)
        with zipfile.ZipFile(self.output) as archive:
            readme = archive.read("README.txt").decode()
        self.assertIn("ClearCast 1.2.0.0 corresponding source", readme)
        self.assertIn("https://github.com/NCFXZ/ClearCast/releases/tag/v1.2.0.0", readme)

    def test_replaces_four_part_release_version(self):
        self.make_base(version="1.1.0.0")
        result = self.run_tool(("--version", "1.2.0.0"))
        self.assertEqual(result.returncode, 0, result.stderr)
        with zipfile.ZipFile(self.output) as archive:
            readme = archive.read("README.txt").decode()
        self.assertIn("https://github.com/NCFXZ/ClearCast/releases/tag/v1.2.0.0\n", readme)
        self.assertNotIn("v1.2.0.0.0", readme)
        self.assertNotIn("1.1.0.0", readme)

    def test_rejects_reserved_application_source_alias(self):
        self.make_base()
        additional = self.work / "extra.tar.xz"
        additional.write_bytes(b"fixture")
        result = self.run_tool(("--additional-source", "./clearcast-source.zip=" + str(additional)))
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.output.exists())

    def test_rejects_noncanonical_source_member(self):
        self.make_base()
        additional = self.work / "extra.tar.xz"
        additional.write_bytes(b"fixture")
        result = self.run_tool(("--additional-source", "gstreamer//extra.tar.xz=" + str(additional)))
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.output.exists())

    def test_rejects_reserved_case_alias(self):
        self.make_base()
        additional = self.work / "extra.tar.xz"
        additional.write_bytes(b"fixture")
        result = self.run_tool(("--additional-source", "CLEARCAST-SOURCE.ZIP=" + str(additional)))
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.output.exists())

    def test_rejects_private_state_case_alias(self):
        self.make_base()
        self.app.write_bytes(app_archive({"ClearCast/.RUNTIME/private.txt": "must not ship"}))
        result = self.run_tool()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.output.exists())
        self.assertFalse(self.output.exists())

    def test_updates_only_application_and_metadata(self):
        self.make_base()
        result = self.run_tool()
        self.assertEqual(result.returncode, 0, result.stderr)
        metadata = json.loads(result.stdout)
        self.assertEqual(metadata["verified_source_archives"], 2)
        with zipfile.ZipFile(self.output) as archive:
            self.assertEqual(archive.read("clearcast-source.zip"), self.app.read_bytes())
            self.assertEqual(archive.read("dependency.tar.zst"), b"unchanged compressed source")
            self.assertIn(("a" * 40).encode(), archive.read("README.txt"))
            self.assertIn(hashlib.sha256(self.app.read_bytes()).hexdigest().encode(), archive.read("SHA256SUMS"))
        self.assertEqual((self.work / "compliance/licenses/dependency/LICENSE").read_text(), "fixture terms")

    def test_rejects_changed_dependency_without_replacing_output(self):
        self.make_base(corrupt=True)
        self.output.write_bytes(b"existing release")
        result = self.run_tool()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Checksum mismatch", result.stderr)
        self.assertEqual(self.output.read_bytes(), b"existing release")

    def test_rejects_private_pairing_state(self):
        self.make_base()
        self.app.write_bytes(app_archive({"ClearCast/.clearcast-state/receiver.pem": "private fixture"}))
        result = self.run_tool()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Private/generated state", result.stderr)
        self.assertFalse(self.output.exists())

    def test_rejects_private_state_in_base_without_replacing_output(self):
        for member in (".clearcast-state/receiver.pem", "distribution-notices/licenses/.RUNTIME/ui-error.log",
                       "source/.Git/config"):
            with self.subTest(member=member):
                self.make_base(extra={member: b"private fixture; must not ship"})
                self.output.write_bytes(b"existing release")
                result = self.run_tool()
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("Private/generated state in base source", result.stderr)
                self.assertEqual(self.output.read_bytes(), b"existing release")
                self.assertFalse((self.work / "compliance").exists())

    def test_rejects_archive_traversal(self):
        self.make_base(extra={"distribution-notices/../../escape": b"fixture"})
        result = self.run_tool()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Unsafe archive path", result.stderr)
        self.assertFalse((self.work / "escape").exists())

    def test_adds_official_source_and_replaces_exact_notices(self):
        self.make_base()
        source = self.work / "parser.tar.xz"
        source.write_bytes(b"new official parser source fixture")
        notices = self.work / "new-notices"
        (notices / "licenses/new-parser").mkdir(parents=True)
        (notices / "THIRD-PARTY-COMPONENTS.md").write_text("new exact inventory")
        (notices / "licenses/new-parser/COPYING").write_text("new parser license")
        result = self.run_tool(["--additional-source", "gstreamer/parser.tar.xz=" + str(source),
                                "--notice-directory", str(notices)])
        self.assertEqual(result.returncode, 0, result.stderr)
        with zipfile.ZipFile(self.output) as archive:
            self.assertEqual(len(archive.namelist()), len(set(archive.namelist())))
            self.assertEqual(archive.read("gstreamer/parser.tar.xz"), source.read_bytes())
            self.assertEqual(archive.read("THIRD-PARTY-COMPONENTS.md"), b"new exact inventory")
            self.assertEqual(archive.read("distribution-notices/THIRD-PARTY-COMPONENTS.md"), b"new exact inventory")
            self.assertNotIn("distribution-notices/licenses/dependency/LICENSE", archive.namelist())
            for row in archive.read("SHA256SUMS").decode().splitlines():
                expected, name = row.split(None, 1)
                self.assertEqual(hashlib.sha256(archive.read(name)).hexdigest(), expected)
        self.assertEqual((self.work / "compliance/licenses/new-parser/COPYING").read_text(), "new parser license")

    def test_rejects_extra_source_path_traversal(self):
        self.make_base()
        result = self.run_tool(["--additional-source", "../escape=" + str(self.app)])
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Unsafe archive path", result.stderr)
        self.assertFalse(self.output.exists())

    def test_rejects_private_additional_source_member(self):
        self.make_base()
        source = self.work / "extra.tar.xz"
        source.write_bytes(b"fixture source")
        for member in (".clearcast-state/source.tar.xz", "source/.RUNTIME/extra.tar.xz", ".Git/extra.tar.xz"):
            with self.subTest(member=member):
                self.output.write_bytes(b"existing release")
                result = self.run_tool(["--additional-source", member + "=" + str(source)])
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("Private/generated state in additional source", result.stderr)
                self.assertEqual(self.output.read_bytes(), b"existing release")

    def test_adds_http_source_with_exact_patch_and_manifest_notices(self):
        self.make_base()
        source = self.work / "gst-plugins-good-1.28.9.tar.xz"
        source.write_bytes(b"fixture official HTTP source archive")
        notices = self.work / "http-notices"
        component = notices / "licenses/gst-plugins-good-1.28.9"
        component.mkdir(parents=True)
        (notices / "THIRD-PARTY-COMPONENTS.md").write_text("fixture controlled HTTP inventory")
        (component / "COPYING").write_bytes(b"fixture original LGPL notice")
        patch = b"fixture controlled HTTP destination patch\n"
        (component / "gstreamer-soup-destination.patch").write_bytes(patch)
        manifest = {"schemaVersion": 1, "version": "1.28.9", "sourcePatch": {
            "relativePath": "src/native/patches/gstreamer-soup-destination.patch",
            "sha256": hashlib.sha256(patch).hexdigest()}}
        (component / "source-build.json").write_text(json.dumps(manifest))
        member = "gstreamer/gst-plugins-good-1.28.9.tar.xz"
        result = self.run_tool(["--additional-source", member + "=" + str(source),
                                "--notice-directory", str(notices)])
        self.assertEqual(result.returncode, 0, result.stderr)
        with zipfile.ZipFile(self.output) as archive:
            self.assertEqual(archive.read(member), source.read_bytes())
            prefix = "distribution-notices/licenses/gst-plugins-good-1.28.9/"
            self.assertEqual(archive.read(prefix + "gstreamer-soup-destination.patch"), patch)
            self.assertEqual(json.loads(archive.read(prefix + "source-build.json")), manifest)
            for row in archive.read("SHA256SUMS").decode().splitlines():
                expected, name = row.split(None, 1)
                self.assertEqual(hashlib.sha256(archive.read(name)).hexdigest(), expected)

    def test_rejects_replacing_existing_dependency_as_extra_source(self):
        self.make_base()
        result = self.run_tool(["--additional-source", "dependency.tar.zst=" + str(self.app)])
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("must not replace existing", result.stderr)
        self.assertFalse(self.output.exists())

    def test_rejects_private_state_in_notices(self):
        self.make_base()
        notices = self.work / "unsafe-notices"
        (notices / "licenses/.clearcast-state").mkdir(parents=True)
        (notices / "THIRD-PARTY-COMPONENTS.md").write_text("fixture")
        (notices / "licenses/.clearcast-state/receiver.pem").write_text("private fixture")
        result = self.run_tool(["--notice-directory", str(notices)])
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Private/generated state in notices", result.stderr)
        self.assertFalse(self.output.exists())


class DependencyNoticesTests(unittest.TestCase):
    def setUp(self):
        (ROOT / ".runtime").mkdir(exist_ok=True)
        self.directory = tempfile.TemporaryDirectory(prefix="notice-tool-qa-", dir=ROOT / ".runtime")
        self.work = Path(self.directory.name)
        self.base = self.work / "base.zip"
        inventory = ("# Fixture\n\n## Native runtime inventory\n\n"
                     "| Package | Version | License | Files | Source |\n| --- | --- | --- | --- | --- |\n"
                     "| mingw-w64-ucrt-x86_64-gst-plugins-bad | 1.28.8-4 | LGPL-2.1-or-later | lib/gstreamer-1.0/libgstd3d11.dll, lib/gstreamer-1.0/libgstvideoparsersbad.dll | old-source |\n"
                     "| mingw-w64-ucrt-x86_64-gst-plugins-bad-libs | 1.28.8-4 | LGPL-2.1-or-later | libgstcodecparsers-1.0-0.dll, libgstcodecs-1.0-0.dll | old-source |\n")
        with zipfile.ZipFile(self.base, "w") as archive:
            archive.writestr("distribution-notices/THIRD-PARTY-COMPONENTS.md", inventory)
            archive.writestr("distribution-notices/licenses/old-component/COPYING", "unchanged license")
        self.source = self.work / "gst-plugins-bad-1.28.9.tar.xz"
        with tarfile.open(self.source, "w:xz") as archive:
            license_data = b"fixture upstream license"
            entry = tarfile.TarInfo("gst-plugins-bad-1.28.9/COPYING")
            entry.size = len(license_data)
            archive.addfile(entry, io.BytesIO(license_data))
        self.security = self.work / "security"
        (self.security / "lib/gstreamer-1.0").mkdir(parents=True)
        paths = ["libgstcodecparsers-1.0-0.dll", "lib/gstreamer-1.0/libgstvideoparsersbad.dll"]
        files = []
        for path in paths:
            data = ("rebuilt fixture: " + path).encode()
            (self.security / path).write_bytes(data)
            files.append({"relativePath": path, "sha256": hashlib.sha256(data).hexdigest()})
        self.manifest = {"schemaVersion": 1, "version": "1.28.9", "license": "LGPL-2.1-or-later", "configuration": "fixture build",
                         "sourceUrl": "https://gstreamer.freedesktop.org/src/gst-plugins-bad/gst-plugins-bad-1.28.9.tar.xz",
                         "sourceSha256": hashlib.sha256(self.source.read_bytes()).hexdigest(), "files": files}
        self.output = self.work / "notices"

    def tearDown(self):
        self.directory.cleanup()

    def run_tool(self):
        (self.security / "manifest.json").write_text(json.dumps(self.manifest))
        return subprocess.run([sys.executable, str(NOTICES_TOOL), "--base", str(self.base),
                               "--gstreamer-source", str(self.source), "--security-directory", str(self.security),
                               "--output-directory", str(self.output)], capture_output=True, text=True)

    def test_preserves_unmodified_components_and_adds_exact_parser_provenance(self):
        result = self.run_tool()
        self.assertEqual(result.returncode, 0, result.stderr)
        inventory = (self.output / "THIRD-PARTY-COMPONENTS.md").read_text()
        old_rows = [row for row in inventory.splitlines() if row.startswith("| mingw-")]
        self.assertEqual(len(old_rows), 2)
        self.assertTrue(all("libgstvideoparsersbad.dll" not in row and "libgstcodecparsers-1.0-0.dll" not in row for row in old_rows))
        self.assertIn("libgstd3d11.dll", old_rows[0])
        self.assertIn("libgstcodecs-1.0-0.dll", old_rows[1])
        self.assertIn("| gst-plugins-bad (official source build) | 1.28.9 |", inventory)
        self.assertEqual((self.output / "licenses/old-component/COPYING").read_text(), "unchanged license")
        self.assertEqual((self.output / "licenses/gst-plugins-bad-1.28.9/COPYING").read_bytes(), b"fixture upstream license")
        self.assertEqual(json.loads((self.output / "licenses/gst-plugins-bad-1.28.9/source-build.json").read_text()), self.manifest)

    def test_rejects_mismatched_official_source_hash(self):
        self.manifest["sourceSha256"] = "0" * 64
        result = self.run_tool()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("source checksum does not match", result.stderr)
        self.assertFalse(self.output.exists())

    def test_rejects_changed_parser_binary(self):
        (self.security / "libgstcodecparsers-1.0-0.dll").write_bytes(b"unexpected binary")
        result = self.run_tool()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("component checksum mismatch", result.stderr)
        self.assertFalse(self.output.exists())

    def test_rejects_unexpected_component_paths(self):
        self.manifest["files"][0]["relativePath"] = "../outside.dll"
        result = self.run_tool()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("exactly the two parser components", result.stderr)
        self.assertFalse(self.output.exists())


if __name__ == "__main__":
    unittest.main(verbosity=2)
