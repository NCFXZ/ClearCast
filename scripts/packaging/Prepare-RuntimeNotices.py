"""Prepare exact native runtime notices from a verified release-source baseline.

Consumes the package-owner audit produced against the installed signed MSYS2
packages. New source archives must match those packages' BUILDINFO recipe hash.
No executable or build recipe is run. SPDX-License-Identifier: GPL-3.0-or-later.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tarfile
import zipfile


def sha(data):
    return hashlib.sha256(data).hexdigest()


def file_sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def safe_relative(value):
    path = PurePosixPath(value.replace('\\', '/'))
    if path.is_absolute() or '..' in path.parts or ':' in value or not path.parts:
        raise ValueError('Unsafe relative path: ' + value)
    return path


def tar_bytes(path, member):
    safe_relative(member)
    description = subprocess.run(['tar.exe', '-tvf', str(path), '--', member], capture_output=True)
    lines = description.stdout.splitlines()
    if description.returncode or len(lines) != 1 or not lines[0].startswith(b'-') or not lines[0].endswith(b' ' + member.encode('utf-8')):
        raise ValueError('Source members must be exact regular files: ' + member)
    result = subprocess.run(['tar.exe', '-xOf', str(path), '--', member], capture_output=True)
    if result.returncode:
        raise ValueError('Cannot read source member ' + member + ': ' + result.stderr.decode(errors='replace'))
    return result.stdout


def fields(text):
    result = {}
    for line in text.splitlines():
        key, separator, value = line.strip().partition(' = ')
        if separator:
            result.setdefault(key, []).append(value)
    return result


def pacman_fields(text):
    result, current = {}, None
    for line in text.splitlines():
        if line.startswith('%') and line.endswith('%'):
            current = line.strip('%')
            result[current] = []
        elif line and current:
            result[current].append(line)
    return {key: '\n'.join(value) for key, value in result.items()}


def audit_runtime(runtime, msys):
    packages, owners, controlled = {}, {}, {}
    for directory in (msys / 'var/lib/pacman/local').iterdir():
        if not (directory / 'desc').is_file():
            continue
        metadata = pacman_fields((directory / 'desc').read_text(encoding='utf-8'))
        packages[metadata['NAME']] = metadata
        if (directory / 'files').is_file():
            for name in (directory / 'files').read_text(encoding='utf-8').splitlines():
                if name and not name.startswith('%'):
                    owners[name.casefold()] = metadata['NAME']
    for component in ['openssl-security', 'libplist-security', 'gstreamer-security']:
        manifest = json.loads((runtime.parent / component / 'manifest.json').read_text(encoding='utf-8-sig'))
        for file in manifest['files']:
            controlled[file['relativePath'].casefold()] = (component, manifest, file)
    records = []
    for path in sorted(runtime.rglob('*')):
        if path.suffix.lower() not in {'.dll', '.exe'}:
            continue
        relative, digest = path.relative_to(runtime).as_posix(), file_sha(path)
        record = {'relativePath': relative, 'sha256': digest}
        custom = controlled.get(relative.casefold())
        installed = 'ucrt64/' + (relative if '/' in relative else 'bin/' + relative)
        if path.name == 'gst-plugin-scanner.exe':
            installed = 'ucrt64/libexec/gstreamer-1.0/gst-plugin-scanner.exe'
        owner = owners.get(installed.casefold())
        if custom and digest == custom[2]['sha256'].lower():
            component, manifest, _ = custom
            record.update(controlledComponent=component, version=manifest['version'],
                sourceUrl=manifest['sourceUrl'], sourceSha256=manifest['sourceSha256'])
        elif owner:
            metadata = packages[owner]
            record.update(package=owner, version=metadata['VERSION'], sourcePackage=metadata['BASE'],
                projectUrl=metadata.get('URL'), installedPath=installed,
                matchesInstalledPackage=(msys / installed).is_file() and file_sha(msys / installed) == digest)
        elif path.name == 'clearcast_receiver.dll':
            record.update(controlledComponent='ClearCast patched UxPlay', sourceCommit='d4b7373cb6107b2d8d6c5838991c19d39e2b96ae')
        else:
            record['unmatched'] = True
        records.append(record)
    return {'files': records, 'unmatched': [item['relativePath'] for item in records if item.get('unmatched') or item.get('matchesInstalledPackage') is False],
        'packages': {item['package']: packages[item['package']] for item in records if 'package' in item}}


def source_licenses(path, base):
    listing = subprocess.check_output(['tar.exe', '-tf', str(path)], text=True).splitlines()
    result = {}
    for member in listing:
        name = PurePosixPath(member)
        if ('\\' in member or any(ord(value) < 32 for value in member) or not name.parts or name.parts[0] != base or
                name.as_posix() != member.removesuffix('/') or name.is_absolute() or '..' in name.parts):
            raise ValueError('Unsafe or unexpected source package member: ' + member)
        if member.endswith('/'):
            continue
        if name.name.upper().startswith(('LICENSE', 'COPYING', 'NOTICE', 'COPYRIGHT')):
            result[name.name] = tar_bytes(path, member)
        elif member.endswith(('.tar.xz', '.tar.gz', '.tar.bz2', '.tgz', '.zip')):
            data = tar_bytes(path, member)
            if member.endswith('.zip'):
                with zipfile.ZipFile(io.BytesIO(data)) as upstream:
                    for entry in upstream.infolist():
                        candidate = PurePosixPath(entry.filename)
                        if not entry.is_dir() and len(candidate.parts) <= 3 and candidate.name.upper().startswith(('LICENSE', 'COPYING', 'NOTICE', 'COPYRIGHT')):
                            result[str(len(result) + 1) + '-' + candidate.name] = upstream.read(entry)
            else:
                with tarfile.open(fileobj=io.BytesIO(data), mode='r:*') as upstream:
                    for entry in upstream:
                        candidate = PurePosixPath(entry.name)
                        if entry.isfile() and len(candidate.parts) <= 3 and candidate.name.upper().startswith(('LICENSE', 'COPYING', 'NOTICE', 'COPYRIGHT')):
                            result[str(len(result) + 1) + '-' + candidate.name] = upstream.extractfile(entry).read()
    if not result:
        raise ValueError('No source license notices found for ' + base)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base', type=Path, required=True)
    parser.add_argument('--base-sha256', required=True)
    parser.add_argument('--package-audit', type=Path, help='Optional independently prepared owner audit; otherwise audit the installed MSYS2 database directly.')
    parser.add_argument('--msys-root', type=Path, required=True)
    parser.add_argument('--runtime-directory', type=Path, required=True)
    parser.add_argument('--additional-source-directory', type=Path, required=True)
    parser.add_argument('--output-directory', type=Path, required=True)
    parser.add_argument('--version', required=True)
    args = parser.parse_args()
    if not re.fullmatch(r'\d+\.\d+\.\d+\.0', args.version):
        parser.error('A four-part release version with zero Store revision is required.')
    if not re.fullmatch(r'[0-9a-fA-F]{64}', args.base_sha256) or file_sha(args.base) != args.base_sha256.lower():
        raise ValueError('Public source baseline digest mismatch.')
    output = args.output_directory.resolve()
    if output.exists():
        raise ValueError('Use a new output directory; existing notices are never overwritten.')
    audit = json.loads(args.package_audit.read_text(encoding='utf-8-sig')) if args.package_audit else audit_runtime(args.runtime_directory, args.msys_root)
    if audit['unmatched']:
        raise ValueError('Package audit contains unmatched runtime files.')
    # The certificate trust bundle is a distributed third-party data file,
    # although the PE owner audit intentionally contains only DLLs and EXEs.
    certificate = args.runtime_directory / 'certs/ca-bundle.crt'
    if certificate.is_file():
        entries = list((args.msys_root / 'var/lib/pacman/local').glob('mingw-w64-ucrt-x86_64-ca-certificates-*/desc'))
        if len(entries) != 1:
            raise ValueError('Certificate package identity is missing or ambiguous.')
        metadata = pacman_fields(entries[0].read_text(encoding='utf-8'))
        name = metadata['NAME']
        audit['packages'][name] = metadata
        audit['files'].append({'relativePath': 'certs/ca-bundle.crt', 'sha256': file_sha(certificate),
            'package': name, 'version': metadata['VERSION'], 'sourcePackage': metadata['BASE'],
            'installedPath': 'ucrt64/etc/ssl/certs/ca-bundle.crt', 'matchesInstalledPackage': True})
    with zipfile.ZipFile(args.base) as archive:
        sums = {}
        for row in archive.read('SHA256SUMS').decode().splitlines():
            if row.strip():
                digest, name = row.split(None, 1)
                sums[name.lstrip(' *')] = digest.lower()
        baseline = archive.read('distribution-notices/THIRD-PARTY-COMPONENTS.md').decode('utf-8-sig')
        for entry in archive.infolist():
            if entry.is_dir() or not entry.filename.startswith('distribution-notices/'):
                continue
            relative = safe_relative(str(PurePosixPath(entry.filename).relative_to('distribution-notices')))
            target = output.joinpath(*relative.parts)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(archive.read(entry))
        members = set(archive.namelist())

    grouped = {}
    records = []
    for item in audit['files']:
        relative = safe_relative(item['relativePath'])
        runtime_file = args.runtime_directory.joinpath(*relative.parts)
        if file_sha(runtime_file) != item['sha256'].lower():
            raise ValueError('Runtime changed after owner audit: ' + str(relative))
        record = {key: value for key, value in item.items() if key != 'installedPath'}
        records.append(record)
        if 'package' in item:
            installed = args.msys_root.joinpath(*safe_relative(item['installedPath']).parts)
            if file_sha(installed) != item['sha256'].lower():
                raise ValueError('Installed package no longer matches runtime: ' + str(relative))
            grouped.setdefault(item['package'], []).append(item)

    rows = []
    additions = {}
    for name, files in sorted(grouped.items()):
        metadata = audit['packages'][name]
        version, base = metadata['VERSION'], metadata['BASE']
        if (not re.fullmatch(r'mingw-w64-ucrt-x86_64-[A-Za-z0-9+_.-]+', name) or
                not re.fullmatch(r'mingw-w64-[A-Za-z0-9+_.-]+', base) or
                not re.fullmatch(r'(?:\d+:)?[A-Za-z0-9][A-Za-z0-9.+_~-]*-\d+', version)):
            raise ValueError('Unsafe or invalid package identity: ' + name)
        member = 'msys2/' + base + '-' + version.replace(':', '~') + '.src.tar.zst'
        package_dir = output / 'licenses' / name
        package_dir.mkdir(parents=True, exist_ok=True)
        source_path = args.additional_source_directory / PurePosixPath(member).name
        if member not in members:
            if not source_path.is_file():
                raise ValueError('Missing exact corresponding source: ' + member)
            info = fields(tar_bytes(source_path, base + '/.SRCINFO').decode())
            source_version = (info.get('epoch', ['0'])[0] + ':' if info.get('epoch', ['0'])[0] != '0' else '') + info['pkgver'][0] + '-' + info['pkgrel'][0]
            if info['pkgbase'][0] != base or source_version != version or name not in info['pkgname']:
                raise ValueError('Source package identity mismatch: ' + member)
            recipe_hash = sha(tar_bytes(source_path, base + '/PKGBUILD'))
            binary = args.msys_root / 'var/cache/pacman/pkg' / (name + '-' + version.replace(':', '~') + '-any.pkg.tar.zst')
            build = fields(tar_bytes(binary, '.BUILDINFO').decode())
            if build['pkgname'][0] != name or build['pkgver'][0] != version or build['pkgbuild_sha256sum'][0] != recipe_hash:
                raise ValueError('Source recipe does not match installed package BUILDINFO: ' + name)
            source_hash = file_sha(source_path)
            additions[member] = {'path': str(source_path.resolve()), 'sha256': source_hash, 'pkgbuildSha256': recipe_hash}
            notices = source_licenses(source_path, base)
            notice_dir = package_dir / 'source-notices-current'
            notice_dir.mkdir(exist_ok=True)
            for notice_name, data in notices.items():
                (notice_dir / safe_relative(notice_name).name).write_bytes(data)
        else:
            if member not in sums:
                raise ValueError('Source baseline omits a checksum: ' + member)
            source_hash = sums[member]
        # Include current installed custom-license files, when the package has them.
        db = args.msys_root / 'var/lib/pacman/local' / (name + '-' + version)
        db_files = (db / 'files').read_text(encoding='utf-8').splitlines()
        for installed_name in db_files:
            if '/share/licenses/' not in installed_name or installed_name.endswith('/'):
                continue
            installed = args.msys_root.joinpath(*safe_relative(installed_name).parts)
            if installed.is_file():
                target = package_dir / 'installed-notices' / Path(installed_name.split('/share/licenses/', 1)[1])
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(installed, target)
        license_files = [path for path in package_dir.rglob('*') if path.is_file() and path.name != 'source-build.json']
        if not license_files:
            raise ValueError('No distribution notices for package ' + name)
        provenance = {'schemaVersion': 1, 'package': name, 'version': version, 'sourcePackage': base,
                      'sourceMember': member, 'sourceUrl': 'https://repo.msys2.org/mingw/sources/' + PurePosixPath(member).name,
                      'sourceSha256': source_hash, 'licenseMetadata': metadata.get('LICENSE', ''),
                      'files': [{'relativePath': item['relativePath'], 'sha256': item['sha256']} for item in files]}
        (package_dir / 'source-build.json').write_text(json.dumps(provenance, indent=2) + '\n', encoding='utf-8')
        license_text = metadata.get('LICENSE', '').replace('\n', ', ').replace('|', '\\|')
        paths = ', '.join(sorted(item['relativePath'] for item in files))
        rows.append(f'| {name} | {version} | {license_text} | {paths} | [Source]({provenance["sourceUrl"]}) |')

    dependencies = json.loads((args.runtime_directory / 'security-dependencies-manifest.json').read_text(encoding='utf-8-sig'))
    if dependencies.get('schemaVersion') != 1 or sorted(item.get('name', '') for item in dependencies.get('components', [])) != ['libplist', 'openssl']:
        raise ValueError('Controlled components must identify libplist and OpenSSL exactly once.')
    controlled = []
    for component in dependencies['components']:
        expected_version = {'libplist': '2.8.0', 'openssl': '3.5.9'}[component['name']]
        allowed_files = {'libplist': {'libplist-2.0.dll'}, 'openssl': {'libcrypto-3-x64.dll', 'libssl-3-x64.dll'}}[component['name']]
        names = [file['relativePath'] for file in component.get('files', [])]
        if (component['version'] != expected_version or not names or len(names) != len(set(names)) or
                not set(names).issubset(allowed_files) or
                (component['name'] == 'openssl' and 'libcrypto-3-x64.dll' not in names)):
            raise ValueError('Invalid controlled version or runtime file set.')
        source_manifest = args.runtime_directory.parent / (component['name'] + '-security/manifest.json')
        manifest = json.loads(source_manifest.read_text(encoding='utf-8-sig'))
        for key in ['version', 'sourceUrl', 'sourceSha256']:
            if manifest[key].lower() != component[key].lower():
                raise ValueError('Controlled provenance mismatch: ' + component['name'])
        for file in component['files']:
            if file_sha(args.runtime_directory / file['relativePath']) != file['sha256'].lower():
                raise ValueError('Controlled binary mismatch.')
        manifest['files'] = component['files']
        manifest['originalBuildManifestSha256'] = file_sha(source_manifest)
        target = output / 'licenses' / (component['name'] + '-' + component['version'])
        target.mkdir(parents=True, exist_ok=True)
        (target / 'source-build.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
        controlled.append(component)
    parser_manifest = args.runtime_directory / 'gstreamer-security-manifest.json'
    shutil.copyfile(parser_manifest, output / 'licenses/gst-plugins-bad-1.28.9/source-build.json')

    intro = baseline.split('## Native runtime inventory')[0]
    intro = intro.replace('ClearCast 1.1.0', 'ClearCast ' + args.version).replace('/v1.1.0', '/v' + args.version)
    intro = intro.replace('under native/', 'under src/native/ and scripts/native/').replace('`native/Build-GStreamerSecurity.ps1`', '`scripts/native/Build-GStreamerSecurity.ps1`')
    inventory = intro + '## Native runtime inventory\n\nThe table describes the exact currently shipped MSYS2 package files, individually matched by SHA-256 to installed package files. Changed/new source recipes are matched to the signed binary packages\' BUILDINFO hashes. Controlled components use their exact source-build manifests.\n\n| MSYS2 package | Version | License metadata | Bundled files | Exact source archive |\n| --- | --- | --- | --- | --- |\n' + '\n'.join(rows) + '\n\n'
    inventory += '## Controlled components\n\n'
    for component in controlled:
        inventory += f'- {component["name"]} {component["version"]}: {component["sourceUrl"]}; exact source SHA-256 `{component["sourceSha256"]}` and shipped file hashes in `licenses/{component["name"]}-{component["version"]}/source-build.json`.\n'
    inventory += '\nAll native PE file identities are recorded in `runtime-components.json`. The ClearCast receiver is built from the pinned UxPlay source plus the application source patches, rather than an unmodified package binary.\n'
    (output / 'THIRD-PARTY-COMPONENTS.md').write_text(inventory, encoding='utf-8')
    (output / 'runtime-components.json').write_text(json.dumps({'schemaVersion': 1, 'version': args.version, 'files': records}, indent=2) + '\n', encoding='utf-8')
    result = {'noticeDirectory': str(output), 'msysPackageCount': len(grouped), 'fileCount': len(records),
              'peFileCount': sum(Path(item['relativePath']).suffix.lower() in {'.dll', '.exe'} for item in records),
              'baselineSha256': args.base_sha256.lower(), 'additionalSources': additions,
              'inventorySha256': file_sha(output / 'THIRD-PARTY-COMPONENTS.md')}
    (output / 'source-coverage.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
