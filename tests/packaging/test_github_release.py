"""Isolated release retries: fake HTTP/credentials and disposable output only."""
import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock
import urllib.error
import urllib.parse

ROOT = Path(__file__).resolve().parents[2]
TOOL = ROOT / 'scripts/packaging/Publish-GitHubRelease.py'
VERSION = '1.2.2.0'
TAG = 'v' + VERSION
COMMIT = 'a' * 40
PREFIX = '/repos/NCFXZ/ClearCast'


class GitHubReleaseTests(unittest.TestCase):
    def setUp(self):
        (ROOT / '.runtime').mkdir(exist_ok=True)
        self.directory = tempfile.TemporaryDirectory(prefix='github-release-unit-', dir=ROOT / '.runtime')
        self.addCleanup(self.directory.cleanup)
        self.work = Path(self.directory.name)
        spec = importlib.util.spec_from_file_location('publisher_fixture', TOOL)
        self.publisher = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.publisher)
        self.publisher.ROOT = self.work
        notes = self.work / 'shared/docs/releases' / ('release-notes-' + VERSION + '.md')
        notes.parent.mkdir(parents=True)
        notes.write_text('Isolated release notes.\n', encoding='utf-8')
        self.asset = self.work / ('.runtime/release-' + VERSION) / 'fixture.zip'
        self.asset.parent.mkdir(parents=True)
        self.asset.write_bytes(b'isolated fixture asset')
        self.assets = [{'name': self.asset.name, 'size': self.asset.stat().st_size,
                        'digest': 'sha256:' + hashlib.sha256(self.asset.read_bytes()).hexdigest()}]
        self.release = {'id': 47, 'name': 'ClearCast ' + VERSION, 'tag_name': 'untagged-fixture',
                        'target_commitish': COMMIT, 'draft': True,
                        'assets_url': 'https://api.github.com' + PREFIX + '/releases/47/assets',
                        'upload_url': 'https://uploads.github.com' + PREFIX + '/releases/47/assets{?name,label}',
                        'html_url': 'https://github.com/NCFXZ/ClearCast/releases/tag/untagged-fixture'}
        self.drafts = [self.release.copy()]
        self.direct_release = None
        self.tag = None
        self.calls = []

    def urlopen(self, request, timeout):
        parsed = urllib.parse.urlsplit(request.full_url)
        self.assertEqual((parsed.scheme, parsed.hostname), ('https', 'api.github.com'))
        path = parsed.path + ('?' + parsed.query if parsed.query else '')
        method = request.get_method()
        data = None if request.data is None else json.loads(request.data)
        self.calls.append((method, path, data))
        if method == 'GET' and path == PREFIX + '/commits/' + COMMIT:
            value = {'sha': COMMIT}
        elif method == 'GET' and path == PREFIX + '/git/ref/tags/' + TAG:
            value = self.tag
        elif method == 'GET' and path == PREFIX + '/releases/tags/' + TAG:
            value = self.direct_release
        elif method == 'GET' and path == PREFIX + '/releases?per_page=100':
            value = self.drafts
        elif method == 'GET' and path == PREFIX + '/releases/47/assets':
            value = self.assets
        elif method == 'PATCH' and path == PREFIX + '/releases/47':
            if data.get('draft') is False:
                self.release.update(draft=False, tag_name=data['tag_name'], target_commitish=data['target_commitish'])
                self.tag = {'object': {'sha': COMMIT, 'type': 'commit'}}
            # Draft API responses may still expose the provisional name. The
            # report must reflect the actual returned value, not just TAG.
            value = self.release
        else:
            self.fail('Unexpected fake HTTP request: ' + method + ' ' + path)
        if value is None:
            raise urllib.error.HTTPError(request.full_url, 404, 'fixture missing', {}, None)
        return io.BytesIO(json.dumps(value).encode('utf-8'))

    def run_publisher(self, action, assets=None):
        arguments = [str(TOOL), action, '--version', VERSION, '--commit', COMMIT]
        for asset in [self.asset] if assets is None else assets:
            arguments += ['--asset', str(asset)]
        output = io.StringIO()
        with mock.patch.object(self.publisher, 'credentials', return_value='fake-test-token') as credentials, \
                mock.patch.object(self.publisher.urllib.request, 'urlopen', side_effect=self.urlopen), \
                mock.patch.object(self.publisher.http.client, 'HTTPSConnection', side_effect=AssertionError('Real upload forbidden')), \
                mock.patch.object(self.publisher.subprocess, 'run', side_effect=AssertionError('Real Git/GCM forbidden')), \
                mock.patch.object(sys, 'argv', arguments), contextlib.redirect_stdout(output):
            self.publisher.main()
        credentials.assert_called_once_with()
        self.assertNotIn('fake-test-token', output.getvalue())
        report = self.work / ('.runtime/qa-release-' + VERSION) / ('github-' + action + '-results.json')
        return json.loads(report.read_text(encoding='utf-8'))

    def assert_no_mutations(self):
        self.assertTrue(all(method == 'GET' for method, _, _ in self.calls))

    def test_unique_untagged_draft_is_reused_without_post(self):
        report = self.run_publisher('draft')
        self.assertEqual(report['releaseId'], 47)
        self.assertEqual(report['tag'], TAG)
        self.assertEqual(report['actualTagName'], 'untagged-fixture')
        patches = [data for method, _, data in self.calls if method == 'PATCH']
        self.assertEqual(len(patches), 1)
        self.assertEqual((patches[0]['tag_name'], patches[0]['target_commitish']), (TAG, COMMIT))
        self.assertIn('Isolated release notes.', patches[0]['body'])
        for locale in ('zh-CN', 'en-US'):
            self.assertIn(f'/shared/docs/screenshots/{VERSION}/{locale}-lyrics-dark.png', patches[0]['body'])
        self.assertFalse(any(method == 'POST' for method, _, _ in self.calls))

    def test_normal_tag_draft_is_still_reused(self):
        self.drafts[0]['tag_name'] = TAG
        self.release['tag_name'] = TAG
        report = self.run_publisher('draft')
        self.assertEqual(report['actualTagName'], TAG)
        self.assertFalse(any(method == 'POST' for method, _, _ in self.calls))

    def test_publish_explicitly_binds_tag_and_commit(self):
        report = self.run_publisher('publish')
        payloads = [data for method, _, data in self.calls if method == 'PATCH']
        self.assertEqual(payloads, [{'tag_name': TAG, 'target_commitish': COMMIT,
                                     'draft': False, 'make_latest': 'true'}])
        self.assertFalse(report['draft'])
        self.assertEqual(report['actualTagName'], TAG)
        self.assertEqual(report['tagObject']['sha'], COMMIT)

    def test_multiple_matching_drafts_are_rejected(self):
        self.drafts.append(dict(self.release, id=48, tag_name=TAG))
        with self.assertRaisesRegex(RuntimeError, 'Ambiguous release draft'):
            self.run_publisher('draft')
        self.assert_no_mutations()

    def test_fallback_ignores_other_name_target_tag_and_published_items(self):
        self.drafts = [dict(self.release, name='Other release'),
                       dict(self.release, target_commitish='b' * 40),
                       dict(self.release, tag_name='untaggedness'),
                       dict(self.release, draft=False)]
        with self.assertRaisesRegex(RuntimeError, 'Release not found'):
            self.run_publisher('verify')
        self.assert_no_mutations()

    def test_wrong_target_on_existing_tag_is_rejected(self):
        self.direct_release = dict(self.release, tag_name=TAG, target_commitish='b' * 40)
        with self.assertRaisesRegex(RuntimeError, 'targets a different source commit'):
            self.run_publisher('draft')
        self.assert_no_mutations()

    def test_wrong_existing_tag_commit_is_rejected(self):
        self.tag = {'object': {'type': 'commit', 'sha': 'b' * 40}}
        with self.assertRaisesRegex(RuntimeError, 'tag does not point directly'):
            self.run_publisher('publish')
        self.assert_no_mutations()

    def test_published_release_cannot_be_replaced_or_republished(self):
        self.direct_release = dict(self.release, draft=False, tag_name=TAG)
        for action, message in [('draft', 'published release already exists'),
                                ('publish', 'Expected an existing verified draft')]:
            with self.subTest(action=action), self.assertRaisesRegex(RuntimeError, message):
                self.run_publisher(action)
        self.assert_no_mutations()

    def test_draft_rejects_changed_existing_asset_bytes(self):
        original = self.assets[0].copy()
        for change in [{'digest': 'sha256:' + '0' * 64}, {'size': original['size'] + 1}]:
            self.assets = [dict(original, **change)]
            with self.subTest(change=change), self.assertRaisesRegex(RuntimeError, 'existing draft asset differs'):
                self.run_publisher('draft')
        self.assertFalse(any(method == 'POST' for method, _, _ in self.calls))

    def test_publish_rejects_extra_asset_before_mutation(self):
        self.assets.append(dict(self.assets[0], name='unexpected.zip'))
        with self.assertRaisesRegex(RuntimeError, 'asset set is not exact'):
            self.run_publisher('publish')
        self.assert_no_mutations()

    def test_publish_rejects_changed_asset_before_mutation(self):
        self.assets[0]['digest'] = 'sha256:' + '0' * 64
        with self.assertRaisesRegex(RuntimeError, 'Release asset digest or size mismatch'):
            self.run_publisher('publish')
        self.assert_no_mutations()

    def test_verify_checks_published_assets_without_mutation(self):
        self.direct_release = dict(self.release, draft=False, tag_name=TAG)
        self.tag = {'object': {'sha': COMMIT, 'type': 'commit'}}
        report = self.run_publisher('verify')
        self.assertFalse(report['draft'])
        self.assertEqual(report['assets'][0]['digest'], self.assets[0]['digest'])
        self.assert_no_mutations()

    def test_verify_rejects_mismatched_remote_assets_without_mutation(self):
        original = self.assets[0].copy()
        self.direct_release = dict(self.release, draft=False, tag_name=TAG)
        self.tag = {'object': {'sha': COMMIT, 'type': 'commit'}}
        for assets in ([], [dict(original, name='unexpected.zip')], [original, original],
                       [dict(original, digest='sha256:' + '0' * 64)],
                       [dict(original, size=original['size'] + 1)]):
            with self.subTest(assets=assets):
                self.assets = assets
                with self.assertRaisesRegex(RuntimeError, 'Release asset (set|digest or size)'):
                    self.run_publisher('verify')
                self.assert_no_mutations()

    def test_verify_requires_unambiguous_explicit_expected_assets(self):
        for assets in ([], [self.asset, self.asset]):
            with self.subTest(assets=assets), self.assertRaisesRegex(RuntimeError, 'Expected assets|asset set'):
                self.run_publisher('verify', assets=assets)
        self.assert_no_mutations()


if __name__ == '__main__':
    unittest.main()
