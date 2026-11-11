"""Publish only the explicitly requested ClearCast release; never logs credentials."""
import argparse
import hashlib
import http.client
import json
import os
import re
from pathlib import Path
import subprocess
import urllib.error
import urllib.parse
import urllib.request

REPO = 'NCFXZ/ClearCast'
ROOT = Path(__file__).resolve().parents[2]


def credentials():
    environment = dict(os.environ, GIT_TERMINAL_PROMPT='0', GCM_INTERACTIVE='Never')
    result = subprocess.run(['git', 'credential', 'fill'], input='protocol=https\nhost=github.com\n\n',
                            text=True, capture_output=True, timeout=30, env=environment, cwd=ROOT)
    values = dict(line.split('=', 1) for line in result.stdout.splitlines() if '=' in line)
    if result.returncode or not values.get('password'):
        raise RuntimeError('Existing GitHub credential unavailable; no credential was logged or saved.')
    return values['password']


def digest(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(block)
    return value.hexdigest()


def verify_assets(assets, paths):
    if not paths:
        raise RuntimeError('Expected assets must be provided for verification.')
    present = {item['name']: item for item in assets}
    expected = {path.name for path in paths}
    if len(present) != len(assets) or len(expected) != len(paths) or set(present) != expected:
        raise RuntimeError('Release asset set is not exact.')
    for path in paths:
        item = present[path.name]
        if item.get('digest') != 'sha256:' + digest(path) or item.get('size') != path.stat().st_size:
            raise RuntimeError('Release asset digest or size mismatch: ' + path.name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=('draft', 'publish', 'verify'))
    parser.add_argument('--version', required=True)
    parser.add_argument('--commit', required=True)
    parser.add_argument('--asset', action='append', default=[], type=Path)
    args = parser.parse_args()
    if not re.fullmatch(r'\d+\.\d+\.\d+\.0', args.version):
        parser.error('Use a four-part version with zero Store revision.')
    VERSION = args.version
    TAG = 'v' + VERSION
    QA = ROOT / '.runtime' / ('qa-release-' + VERSION)
    QA.mkdir(parents=True, exist_ok=True)
    if len(args.commit) != 40 or any(c not in '0123456789abcdef' for c in args.commit):
        parser.error('A full lowercase commit SHA is required.')
    token = credentials()
    headers = {'Authorization': 'Bearer ' + token, 'User-Agent': 'ClearCast-release-validation',
               'Accept': 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28'}

    def request(method, path, data=None):
        encoded = None if data is None else json.dumps(data).encode('utf-8')
        selected = dict(headers)
        if encoded is not None:
            selected['Content-Type'] = 'application/json'
        req = urllib.request.Request('https://api.github.com' + path, data=encoded, method=method, headers=selected)
        try:
            with urllib.request.urlopen(req, timeout=60) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            if error.code == 404 and method == 'GET':
                return None
            raise RuntimeError(f'GitHub {method} {path} failed with HTTP {error.code}') from None

    remote = request('GET', f'/repos/{REPO}/commits/{args.commit}')
    if not remote or remote['sha'] != args.commit:
        raise RuntimeError('The exact release source commit is not present on GitHub.')
    tag = request('GET', f'/repos/{REPO}/git/ref/tags/{TAG}')
    if tag and (tag['object'].get('type') != 'commit' or tag['object'].get('sha') != args.commit):
        raise RuntimeError('Existing tag does not point directly to the requested release commit.')
    release = request('GET', f'/repos/{REPO}/releases/tags/{TAG}')
    if release is None:
        drafts = request('GET', f'/repos/{REPO}/releases?per_page=100')
        # GitHub can give an unpublished release a provisional untagged-* name.
        matches = [item for item in drafts if item.get('draft') and
                   (item.get('tag_name') == TAG or (item.get('tag_name') or '').startswith('untagged-')) and
                   item.get('target_commitish') == args.commit and item.get('name') == 'ClearCast ' + VERSION]
        if len(matches) > 1:
            raise RuntimeError('Ambiguous release draft; refusing to choose one.')
        if matches:
            release = matches[0]
    if release and release.get('target_commitish') != args.commit:
        raise RuntimeError('Existing release targets a different source commit.')
    if args.action == 'draft':
        if release and not release['draft']:
            raise RuntimeError('A published release already exists; refusing to replace it.')
        notes = (ROOT / ('shared/docs/releases/release-notes-' + VERSION + '.md')).read_text(encoding='utf-8-sig')
        notes += '\n## 音乐界面 / Music view\n\n'
        for locale, label in (('zh-CN', '中文'), ('en-US', 'English')):
            notes += f'![{label}](https://raw.githubusercontent.com/{REPO}/{TAG}/shared/docs/screenshots/{VERSION}/{locale}-music-dark.png)\n\n'
            notes += f'![{label} lyrics](https://raw.githubusercontent.com/{REPO}/{TAG}/shared/docs/screenshots/{VERSION}/{locale}-lyrics-dark.png)\n\n'
        if release is None:
            release = request('POST', f'/repos/{REPO}/releases', {
                'tag_name': TAG, 'target_commitish': args.commit, 'name': 'ClearCast ' + VERSION,
                'body': notes, 'draft': True, 'prerelease': False})
        else:
            release = request('PATCH', f'/repos/{REPO}/releases/{release["id"]}', {
                'tag_name': TAG, 'target_commitish': args.commit, 'body': notes})
        known = {item['name']: item for item in request('GET', release['assets_url'].removeprefix('https://api.github.com'))}
        upload = urllib.parse.urlsplit(release['upload_url'].split('{', 1)[0])
        if upload.scheme != 'https' or upload.hostname != 'uploads.github.com':
            raise RuntimeError('Unexpected GitHub upload host.')
        for path in args.asset:
            path = path.resolve(strict=True)
            if not path.is_relative_to(ROOT / ('.runtime/release-' + VERSION)):
                raise RuntimeError('Asset is outside the current release output.')
            expected = 'sha256:' + digest(path)
            prior = known.get(path.name)
            if prior:
                if prior.get('digest') != expected or prior['size'] != path.stat().st_size:
                    raise RuntimeError('An existing draft asset differs; refusing silent replacement: ' + path.name)
                continue
            connection = http.client.HTTPSConnection(upload.hostname, timeout=600)
            try:
                target = upload.path + '?name=' + urllib.parse.quote(path.name, safe='')
                connection.putrequest('POST', target)
                for key, value in headers.items():
                    connection.putheader(key, value)
                connection.putheader('Content-Type', 'application/octet-stream')
                connection.putheader('Content-Length', str(path.stat().st_size))
                connection.endheaders()
                with path.open('rb') as source:
                    for block in iter(lambda: source.read(1024 * 1024), b''):
                        connection.send(block)
                response = connection.getresponse()
                result = json.loads(response.read())
                if response.status != 201 or result.get('digest') != expected or result.get('size') != path.stat().st_size:
                    raise RuntimeError(f'Upload or remote digest verification failed: {path.name} HTTP {response.status}')
                print(json.dumps({'uploaded': path.name, 'bytes': result['size'], 'digest': result['digest']}), flush=True)
            finally:
                connection.close()
    elif args.action == 'publish':
        if not release or not release['draft']:
            raise RuntimeError('Expected an existing verified draft release.')
        verify_assets(request('GET', release['assets_url'].removeprefix('https://api.github.com')), args.asset)
        release = request('PATCH', f'/repos/{REPO}/releases/{release["id"]}', {
            'tag_name': TAG, 'target_commitish': args.commit, 'draft': False, 'make_latest': 'true'})
    if not release:
        raise RuntimeError('Release not found.')
    assets = request('GET', release['assets_url'].removeprefix('https://api.github.com'))
    if args.action == 'verify':
        verify_assets(assets, args.asset)
    tag = request('GET', f'/repos/{REPO}/git/ref/tags/{TAG}')
    if not release['draft'] and (not tag or tag['object'].get('type') != 'commit' or tag['object'].get('sha') != args.commit):
        raise RuntimeError('Published release tag does not match the source commit.')
    report = {'releaseId': release['id'], 'tag': TAG, 'actualTagName': release.get('tag_name'),
              'htmlUrl': release['html_url'], 'draft': release['draft'],
              'commit': args.commit, 'tagObject': tag.get('object') if tag else None,
              'assets': [{key: item.get(key) for key in ('name', 'size', 'digest', 'browser_download_url')} for item in assets]}
    (QA / ('github-' + args.action + '-results.json')).write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
