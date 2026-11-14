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
        config = self.work / 'app.runtimeconfig.json'
        config.write_text(json.dumps({'runtimeOptions': {'includedFrameworks': [
            {'name': 'Microsoft.NETCore.App', 'version': '10.0.15'}]}}))
        package = self.runtime_package()
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
            '--app-runtimeconfig', str(config), '--dotnet-runtime-package', str(package),
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

    def check_release_intro(self, baseline_version, newline='\n'):
        retained = ('Third-party works retain their own notices and license terms.\n'
            f'Dependency {baseline_version}; legacy dependency 1.1.0.\n'
            '[Dependency source](https://example.test/releases/tag/v1.1.0)\n'
            f'[Other source](https://github.com/other/ClearCast/releases/tag/v{baseline_version})\n')
        baseline = (f'# ClearCast {baseline_version} third-party components\n\n'
            f'[ClearCast {baseline_version} downloads and source]'
            f'(https://github.com/NCFXZ/ClearCast/releases/tag/v{baseline_version})\n' + retained +
            '\n## Native runtime inventory\nold inventory\n')
        expected = ('# ClearCast 1.3.0.0 third-party components\n\n'
            '[ClearCast 1.3.0.0 downloads and source]'
            '(https://github.com/NCFXZ/ClearCast/releases/tag/v1.3.0.0)\n' + retained + '\n')
        self.assertEqual(notices.release_intro(baseline.replace('\n', newline), '1.3.0.0'), expected)

    def test_latest_release_intro_keeps_dependency_versions(self):
        self.check_release_intro('1.2.2.0')

    def test_older_release_intro_keeps_dependency_versions(self):
        self.check_release_intro('1.2.0.0')

    def test_release_intro_normalizes_crlf(self):
        self.check_release_intro('1.2.2.0', '\r\n')

    def test_release_intro_normalizes_repeated_carriage_returns(self):
        for version in ('1.2.2.0', '1.2.0.0'):
            for newline in ('\r\r\n', '\r\r\r\n'):
                with self.subTest(version=version, newline=repr(newline)):
                    self.check_release_intro(version, newline)

    def runtime_package(self, version='10.0.15', duplicate=False, oversize=False):
        path = self.work / 'runtime.nupkg'
        with zipfile.ZipFile(path, 'w') as archive:
            archive.writestr('Microsoft.NETCore.App.Runtime.win-x64.nuspec',
                '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata>'
                '<id>Microsoft.NETCore.App.Runtime.win-x64</id><version>' + version + '</version></metadata></package>')
            archive.writestr('LICENSE.TXT', b'Exact license\r\n')
            archive.writestr('THIRD-PARTY-NOTICES.TXT', b'x' * (2 * 1024 * 1024 + 1) if oversize else b'Exact third party\n')
            if duplicate:
                archive.writestr('license.txt', b'alias')
        return path

    def test_runtime_package_preserves_bytes_and_replaces_old_notices(self):
        legal = notices.dotnet_package_notices(self.runtime_package(), '10.0.15')
        previous = self.work / 'notices/licenses/dotnet-runtime-10.0.14'
        previous.mkdir(parents=True)
        (previous / 'LICENSE.TXT').write_bytes(b'old')
        notices.write_dotnet_notices(self.work / 'notices', '10.0.15', legal)
        self.assertFalse(previous.exists())
        self.assertEqual((self.work / 'notices/licenses/dotnet-runtime-10.0.15/LICENSE.TXT').read_bytes(), b'Exact license\r\n')

    def test_runtime_package_rejects_version_alias_and_excess_size(self):
        for options in ({'version': '10.0.14'}, {'duplicate': True}, {'oversize': True}):
            with self.subTest(options=options), self.assertRaises(ValueError):
                notices.dotnet_package_notices(self.runtime_package(**options), '10.0.15')

    def test_runtimeconfig_requires_unique_canonical_self_contained_core(self):
        path = self.work / 'runtimeconfig.json'
        core = {'name': 'Microsoft.NETCore.App', 'version': '10.0.15'}
        path.write_text(json.dumps({'runtimeOptions': {'includedFrameworks': [core]}}))
        self.assertEqual(notices.app_runtime_version(path), '10.0.15')
        invalid = [None, {}, {'framework': core}, {'includedFrameworks': []},
            {'includedFrameworks': [core, core]}, {'includedFrameworks': [{**core, 'version': '10.00.15'}]}]
        for options in invalid:
            path.write_text(json.dumps({'runtimeOptions': options}))
            with self.subTest(options=options), self.assertRaises(ValueError):
                notices.app_runtime_version(path)
        path.write_text('{"runtimeOptions":{},"runtimeOptions":{}}')
        with self.assertRaises(ValueError):
            notices.app_runtime_version(path)

    def test_runtime_intro_updates_exact_runtime_and_source_only(self):
        baseline = ('Microsoft .NET Runtime 10.0.14\n'
            '[Runtime source](https://github.com/dotnet/runtime/tree/v10.0.14)\n'
            'Other dependency 10.0.14\n## Native runtime inventory\n')
        updated = notices.release_intro(baseline, '1.3.1.0', '10.0.15')
        self.assertIn('Microsoft .NET Runtime 10.0.15', updated)
        self.assertIn('/v10.0.15)', updated)
        self.assertIn('Other dependency 10.0.14', updated)
        with self.assertRaises(ValueError):
            notices.release_intro('no runtime notice', '1.3.1.0', '10.0.15')


if __name__ == '__main__':
    unittest.main()
