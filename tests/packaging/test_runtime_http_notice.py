"""Pure controlled HTTP provenance checks; no network or published payload edits.
SPDX-License-Identifier: GPL-3.0-or-later
"""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('runtime_notices', ROOT/'scripts/packaging/Prepare-RuntimeNotices.py')
notices = importlib.util.module_from_spec(spec)
spec.loader.exec_module(notices)

class HttpSourceManifestTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='qa-http-notice-',dir=ROOT/'.runtime')
        self.root = Path(self.temp.name)
        self.runtime = self.root/'receiver'
        binary = self.runtime/'lib/gstreamer-1.0/libgstsoup.dll'
        binary.parent.mkdir(parents=True)
        binary.write_bytes(b'fixture-only binary')
        self.patch = self.root/notices.HTTP_PATCH
        self.patch.parent.mkdir(parents=True)
        self.patch.write_bytes(b'fixture-only controlled patch\n')
        self.manifest = {'schemaVersion':1,'version':'1.28.9',
            'sourceUrl':notices.HTTP_SOURCE_URL,'sourceSha256':notices.HTTP_SOURCE_HASH.upper(),
            'sourcePatch':{'relativePath':notices.HTTP_PATCH,'sha256':notices.file_sha(self.patch)},
            'files':[{'relativePath':'lib/gstreamer-1.0/libgstsoup.dll','sha256':notices.file_sha(binary)}]}
    def tearDown(self): self.temp.cleanup()
    def read(self):
        (self.runtime/'gstreamer-http-security-manifest.json').write_text(json.dumps(self.manifest),encoding='utf-8')
        return notices.http_security_manifest(self.runtime,self.root)
    def test_pinned_provenance(self): self.assertEqual(self.read(),self.manifest)
    def test_wrong_source(self):
        self.manifest['sourceSha256']='0'*64
        with self.assertRaisesRegex(ValueError,'pinned source'): self.read()
    def test_non_official_source(self):
        self.manifest['sourceUrl']='http://example.invalid/source.tar.xz'
        with self.assertRaisesRegex(ValueError,'pinned source'): self.read()
    def test_duplicate_files(self):
        self.manifest['files']*=2
        with self.assertRaisesRegex(ValueError,'pinned source'): self.read()
    def test_unexpected_file(self):
        self.manifest['files'][0]['relativePath']='../libgstsoup.dll'
        with self.assertRaisesRegex(ValueError,'pinned source'): self.read()
    def test_changed_patch(self):
        self.patch.write_bytes(b'changed patch')
        with self.assertRaisesRegex(ValueError,'pinned source'): self.read()
    def test_patch_path_substitution(self):
        self.manifest['sourcePatch']['relativePath']='src/native/patches/other.patch'
        with self.assertRaisesRegex(ValueError,'pinned source'): self.read()
    def test_changed_binary(self):
        (self.runtime/'lib/gstreamer-1.0/libgstsoup.dll').write_bytes(b'changed binary')
        with self.assertRaisesRegex(ValueError,'binary mismatch'): self.read()

class ParserSourceManifestTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='qa-parser-notice-', dir=ROOT/'.runtime')
        self.root = Path(self.temp.name)
        self.runtime = self.root/'receiver'
        self.patch = self.root/notices.PARSER_PATCH
        self.patch.parent.mkdir(parents=True)
        self.patch.write_bytes(b'fixture-only adaptive timing patch\n')
        files = []
        for relative in sorted(notices.PARSER_FILES):
            binary = self.runtime/relative
            binary.parent.mkdir(parents=True, exist_ok=True)
            binary.write_bytes(b'fixture-only controlled binary')
            files.append({'relativePath': relative, 'sha256': notices.file_sha(binary)})
        self.manifest = {'schemaVersion': 1, 'version': '1.28.9',
            'sourceUrl': notices.PARSER_SOURCE_URL, 'sourceSha256': notices.PARSER_SOURCE_HASH,
            'sourcePatch': {'relativePath': notices.PARSER_PATCH, 'sha256': notices.file_sha(self.patch)},
            'files': files}
    def tearDown(self): self.temp.cleanup()
    def read(self):
        (self.runtime/'gstreamer-security-manifest.json').write_text(json.dumps(self.manifest))
        return notices.parser_security_manifest(self.runtime, self.root)
    def test_pinned_three_component_provenance(self): self.assertEqual(self.read(), self.manifest)
    def test_adaptive_component_is_required(self):
        self.manifest['files'] = [row for row in self.manifest['files'] if row['relativePath'] != 'libgstadaptivedemux-1.0-0.dll']
        with self.assertRaisesRegex(ValueError, 'pinned source'): self.read()
    def test_duplicate_component_rejected(self):
        self.manifest['files'][0] = self.manifest['files'][1]
        with self.assertRaisesRegex(ValueError, 'pinned source'): self.read()
    def test_source_substitution_rejected(self):
        self.manifest['sourceUrl'] = 'http://example.invalid/source.tar.xz'
        with self.assertRaisesRegex(ValueError, 'pinned source'): self.read()
    def test_changed_adaptive_binary_rejected(self):
        (self.runtime/'libgstadaptivedemux-1.0-0.dll').write_bytes(b'changed binary')
        with self.assertRaisesRegex(ValueError, 'binary mismatch'): self.read()
    def test_changed_timing_patch_rejected(self):
        self.patch.write_bytes(b'changed patch')
        with self.assertRaisesRegex(ValueError, 'pinned source'): self.read()
    def test_component_escape_rejected(self):
        self.manifest['files'][0]['relativePath'] = '../outside.dll'
        with self.assertRaisesRegex(ValueError, 'pinned source'): self.read()

if __name__ == '__main__': unittest.main()
