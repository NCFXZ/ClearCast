"""Archive and metadata boundaries for exact runtime notice preparation."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
TOOL = ROOT / 'scripts/packaging/Prepare-RuntimeNotices.py'
SPEC = importlib.util.spec_from_file_location('runtime_notices', TOOL)
notices = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(notices)


class RuntimeNoticeBoundaries(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='notice-bounds-', dir=ROOT / '.runtime')
        self.work = Path(self.temporary.name)

    def tearDown(self):
        self.temporary.cleanup()

    def archive(self, members):
        path = self.work / 'source.tar'
        with tarfile.open(path, 'w') as archive:
            for name, value in members.items():
                entry = tarfile.TarInfo(name)
                if isinstance(value, tuple):
                    entry.type = tarfile.SYMTYPE
                    entry.linkname = value[0]
                    archive.addfile(entry)
                else:
                    entry.size = len(value)
                    archive.addfile(entry, io.BytesIO(value))
        return path

    @unittest.skipUnless(shutil.which('tar.exe'), 'Windows libarchive tar is required')
    def test_regular_license_preserves_exact_bytes(self):
        value = b'License text\n\xff\x00not decoded as console output'
        source = self.archive({'pkg/LICENSE': value})
        self.assertEqual(notices.source_licenses(source, 'pkg')['LICENSE'], value)

    @unittest.skipUnless(shutil.which('tar.exe'), 'Windows libarchive tar is required')
    def test_option_shaped_member_is_only_a_literal(self):
        member = '--exclude=pkg/notfound/LICENSE'
        source = self.archive({member: b'literal', 'pkg/payload': b'not a license'})
        self.assertEqual(notices.tar_bytes(source, member), b'literal')
        with self.assertRaises(ValueError):
            notices.source_licenses(source, 'pkg')

    @unittest.skipUnless(shutil.which('tar.exe'), 'Windows libarchive tar is required')
    def test_symlink_cannot_supply_license_contents(self):
        source = self.archive({'pkg/LICENSE': ('pkg/payload',), 'pkg/payload': b'not a license'})
        with self.assertRaises(ValueError):
            notices.source_licenses(source, 'pkg')

    @unittest.skipUnless(shutil.which('tar.exe'), 'Windows libarchive tar is required')
    def test_noncanonical_member_is_rejected(self):
        source = self.archive({'pkg//LICENSE': b'terms'})
        with self.assertRaises(ValueError):
            notices.source_licenses(source, 'pkg')

    def run_controlled_metadata(self, components):
        baseline = self.work / 'base.zip'
        with zipfile.ZipFile(baseline, 'w') as archive:
            archive.writestr('SHA256SUMS', '')
            archive.writestr('distribution-notices/THIRD-PARTY-COMPONENTS.md', '# fixture\n## Native runtime inventory\n')
        audit = self.work / 'audit.json'
        audit.write_text(json.dumps({'files': [], 'packages': {}, 'unmatched': []}))
        runtime = self.work / 'receiver'
        runtime.mkdir()
        # Satisfy the independent HTTP provenance gate so these cases still
        # reach the controlled-component/path validation they are testing.
        binary = runtime / 'lib/gstreamer-1.0/libgstsoup.dll'
        binary.parent.mkdir(parents=True)
        binary.write_bytes(b'fixture-only HTTP binary')
        manifest = {'schemaVersion': 1, 'version': '1.28.9',
            'sourceUrl': notices.HTTP_SOURCE_URL, 'sourceSha256': notices.HTTP_SOURCE_HASH,
            'sourcePatch': {'relativePath': notices.HTTP_PATCH,
                            'sha256': notices.file_sha(ROOT / notices.HTTP_PATCH)},
            'files': [{'relativePath': 'lib/gstreamer-1.0/libgstsoup.dll',
                       'sha256': notices.file_sha(binary)}]}
        (runtime / 'gstreamer-http-security-manifest.json').write_text(json.dumps(manifest))
        parser_files = []
        for relative in sorted(notices.PARSER_FILES):
            binary = runtime / relative
            binary.parent.mkdir(parents=True, exist_ok=True)
            binary.write_bytes(b'fixture-only parser/adaptive binary')
            parser_files.append({'relativePath': relative, 'sha256': notices.file_sha(binary)})
        parser_manifest = {'schemaVersion': 1, 'version': '1.28.9',
            'sourceUrl': notices.PARSER_SOURCE_URL, 'sourceSha256': notices.PARSER_SOURCE_HASH,
            'sourcePatch': {'relativePath': notices.PARSER_PATCH,
                            'sha256': notices.file_sha(ROOT / notices.PARSER_PATCH)},
            'files': parser_files}
        (runtime / 'gstreamer-security-manifest.json').write_text(json.dumps(parser_manifest))
        (runtime / 'security-dependencies-manifest.json').write_text(json.dumps({'schemaVersion': 1, 'components': components}))
        return subprocess.run([sys.executable, str(TOOL), '--base', str(baseline), '--base-sha256', hashlib.sha256(baseline.read_bytes()).hexdigest(),
            '--package-audit', str(audit), '--msys-root', str(self.work / 'msys'), '--runtime-directory', str(runtime),
            '--additional-source-directory', str(self.work), '--output-directory', str(self.work / 'notices'), '--version', '1.2.0.0'], capture_output=True, text=True)

    def test_controlled_component_cannot_escape_output(self):
        result = self.run_controlled_metadata([{'name': '../../escaped', 'version': '1.0.0', 'files': []}])
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('exactly once', result.stderr)
        self.assertFalse((self.work.parent / 'escaped').exists())

    def test_controlled_file_cannot_escape_runtime(self):
        result = self.run_controlled_metadata([
            {'name': 'libplist', 'version': '2.8.0', 'files': [{'relativePath': '../outside.dll'}]},
            {'name': 'openssl', 'version': '3.5.9', 'files': [{'relativePath': 'libcrypto-3-x64.dll'}]},
        ])
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('runtime file set', result.stderr)


if __name__ == '__main__':
    unittest.main()
