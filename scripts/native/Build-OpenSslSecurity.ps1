[CmdletBinding()]
param(
    [string] $ArchivePath,
    [string] $Ucrt64Directory,
    [string] $BuildDirectory,
    [string] $OutputDirectory,
    [string] $BuildToolsDirectory,
    [string] $RuntimeDirectory,
    [ValidateRange(1, 32)] [int] $Parallelism = 8
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$runtime = Join-Path $repositoryRoot '.runtime'
$version = '3.5.9'
$sourceHash = '603F5602E2EEF00D77FBD429D34DCD5822BB301757A1BC9CDB24C670F1EB859A'
$sourceUrl = "https://github.com/openssl/openssl/releases/download/openssl-$version/openssl-$version.tar.gz"
if (-not $ArchivePath) { $ArchivePath = Join-Path $runtime "openssl-$version.tar.gz" }
if (-not $Ucrt64Directory) { $Ucrt64Directory = Join-Path $runtime 'msys64\ucrt64' }
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $runtime "openssl-security-build\$version" }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $runtime 'openssl-security' }
if (-not $BuildToolsDirectory) { $BuildToolsDirectory = Join-Path $runtime 'build-tools\openssl-tools' }
if (-not $RuntimeDirectory) { $RuntimeDirectory = Join-Path $runtime 'receiver' }
$archive = [IO.Path]::GetFullPath($ArchivePath)
$ucrt = (Resolve-Path -LiteralPath $Ucrt64Directory).Path
$msys = Split-Path -Parent $ucrt
$build = [IO.Path]::GetFullPath($BuildDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$tools = [IO.Path]::GetFullPath($BuildToolsDirectory)
foreach ($directory in @($build, $output, $tools)) {
    if ($directory -eq $msys -or $directory.StartsWith($msys.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Security builds and their tools must not overwrite the MSYS installation.'
    }
    [void][IO.Directory]::CreateDirectory($directory)
}
if (-not (Test-Path -LiteralPath $archive)) {
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $archive))
    Invoke-WebRequest $sourceUrl -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $sourceHash) {
    throw 'The OpenSSL source does not match the pinned official SHA-256.'
}

# Build-time utilities are isolated as well. These hashes are from the MSYS2
# repository database; none of these build tools is part of the app payload.
$toolPackages = @(
    @{ Name = 'make-4.4.1-3-x86_64.pkg.tar.zst'; Sha256 = 'AF0BDBA17F06FE037F0194069ADAA31A8FE45F1A11381501896AEA1FAE37BD5D'; Executable = 'make.exe' },
    @{ Name = 'perl-5.42.3-3-x86_64.pkg.tar.zst'; Sha256 = '6A4DBC38CDCC53D16239904B5C87D2277CFFC49ED92C6A7796F8BF6B49517D5B'; Executable = 'perl.exe' }
)
$toolCache = Join-Path $runtime 'openssl-security-inputs'
[void][IO.Directory]::CreateDirectory($toolCache)
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
foreach ($package in $toolPackages) {
    if (-not (Test-Path -LiteralPath (Join-Path $tools ('usr\bin\' + $package.Executable)))) {
        $packagePath = Join-Path $toolCache $package.Name
        if (-not (Test-Path -LiteralPath $packagePath)) {
            Invoke-WebRequest ('https://repo.msys2.org/msys/x86_64/' + $package.Name) -OutFile $packagePath
        }
        if ((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -ne $package.Sha256) {
            throw "The isolated build tool checksum does not match: $($package.Name)"
        }
        & $tar -xf $packagePath -C $tools
        if ($LASTEXITCODE -ne 0) { throw 'Could not extract the isolated build tools.' }
    }
}
$source = Join-Path $build "openssl-$version"
if (-not (Test-Path -LiteralPath (Join-Path $source 'Configure'))) {
    & $tar -xf $archive -C $build
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the verified OpenSSL source.' }
}
$prefix = Join-Path $build 'prefix'
function ConvertTo-MsysPath([string] $Path) {
    $absolute = [IO.Path]::GetFullPath($Path).Replace('\', '/')
    if ($absolute -notmatch '^[A-Za-z]:/') { throw 'The build requires an absolute Windows drive path.' }
    return '/' + $absolute.Substring(0, 1).ToLowerInvariant() + $absolute.Substring(2)
}
function Quote-Shell([string] $Text) {
    if ($Text.Contains("'")) { throw 'Apostrophes in the build path are unsupported.' }
    return "'$Text'"
}
$sourcePosix = ConvertTo-MsysPath $source
$prefixPosix = ConvertTo-MsysPath $prefix
$toolsPosix = ConvertTo-MsysPath $tools
$ucrtPosix = ConvertTo-MsysPath $ucrt
$configuration = @('mingw64', 'shared', 'no-tests', 'no-docs', '--release', '--libdir=lib',
    "--prefix=$prefixPosix", "--openssldir=$prefixPosix/ssl", '-O2', '-fstack-protector-strong', '-D_FORTIFY_SOURCE=2')
$configurationFile = Join-Path $build 'configuration.json'
$configurationJson = $configuration | ConvertTo-Json -Compress
if (Test-Path -LiteralPath (Join-Path $source 'Makefile')) {
    if (-not (Test-Path -LiteralPath $configurationFile) -or
        [IO.File]::ReadAllText($configurationFile).Trim() -ne $configurationJson) {
        throw 'The existing OpenSSL build has another configuration. Use a fresh -BuildDirectory.'
    }
}
$shellLines = @(
    'set -euo pipefail',
    ('export PATH=' + (Quote-Shell "$toolsPosix/usr/bin:$ucrtPosix/bin:/usr/bin") + ':$PATH'),
    ('export PERL5LIB=' + (Quote-Shell "$toolsPosix/usr/lib/perl5/core_perl:$toolsPosix/usr/share/perl5/core_perl:$toolsPosix/usr/lib/perl5/site_perl:$toolsPosix/usr/share/perl5/site_perl")),
    ('cd ' + (Quote-Shell $sourcePosix)),
    ('if [ ! -f Makefile ]; then perl Configure ' + (($configuration | ForEach-Object { Quote-Shell $_ }) -join ' ') + '; fi'),
    "make -j$Parallelism build_sw",
    'make install_sw'
)
$shellFile = Join-Path $build 'build-openssl.sh'
[IO.File]::WriteAllText($shellFile, ($shellLines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($configurationFile, $configurationJson, [Text.UTF8Encoding]::new($false))
& (Join-Path $msys 'usr\bin\bash.exe') --noprofile --norc (ConvertTo-MsysPath $shellFile)
if ($LASTEXITCODE -ne 0) { throw 'The isolated OpenSSL build or install failed.' }

$inventory = foreach ($name in 'libcrypto-3-x64.dll', 'libssl-3-x64.dll') {
    $destination = Join-Path $output $name
    Copy-Item -LiteralPath (Join-Path $prefix ('bin\' + $name)) -Destination $destination -Force
    [ordered]@{ relativePath = $name; sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash }
}
Copy-Item -LiteralPath (Join-Path $source 'LICENSE.txt') -Destination (Join-Path $output 'LICENSE.txt') -Force
$developmentPrefix = Join-Path $output 'prefix'
foreach ($directory in 'include', 'lib', 'lib\pkgconfig', 'bin') {
    [void][IO.Directory]::CreateDirectory((Join-Path $developmentPrefix $directory))
}
Copy-Item -LiteralPath (Join-Path $prefix 'include\openssl') -Destination (Join-Path $developmentPrefix 'include') -Recurse -Force
foreach ($name in 'libcrypto.dll.a', 'libssl.dll.a') {
    Copy-Item -LiteralPath (Join-Path $prefix ('lib\' + $name)) -Destination (Join-Path $developmentPrefix 'lib') -Force
}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $prefix 'lib\pkgconfig') -Filter '*.pc' -File) {
    $content = [IO.File]::ReadAllText($file.FullName) -replace '(?m)^prefix=.*$', 'prefix=$${pcfiledir}/../..'
    $content = $content -replace '(?m)^libdir=.*$', 'libdir=$${prefix}/lib'
    $content = $content -replace '(?m)^includedir=.*$', 'includedir=$${prefix}/include'
    [IO.File]::WriteAllText((Join-Path $developmentPrefix ('lib\pkgconfig\' + $file.Name)), $content, [Text.UTF8Encoding]::new($false))
}
foreach ($name in 'libcrypto-3-x64.dll', 'libssl-3-x64.dll') {
    Copy-Item -LiteralPath (Join-Path $output $name) -Destination (Join-Path $developmentPrefix 'bin') -Force
}
$buildFiles = @(foreach ($file in Get-ChildItem -LiteralPath $developmentPrefix -Recurse -File) {
    [ordered]@{ relativePath = [IO.Path]::GetRelativePath($output, $file.FullName).Replace('\', '/');
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
})

# Inspect PE import/export tables directly. Do not load arbitrary dependency DLLs
# into the build process merely to inspect their compatibility.
$verifyScript = @'
import _sha2 as hashlib
import ctypes, json, pathlib, struct, sys

kernel32=ctypes.WinDLL('kernel32',use_last_error=True)
kernel32.GetModuleHandleW.argtypes=[ctypes.c_wchar_p]
kernel32.GetModuleHandleW.restype=ctypes.c_void_p
kernel32.GetModuleFileNameW.argtypes=[ctypes.c_void_p,ctypes.c_wchar_p,ctypes.c_uint]
kernel32.GetModuleFileNameW.restype=ctypes.c_uint
loaded_ssl=[]
for library in ['libcrypto-3-x64.dll','libssl-3-x64.dll']:
    module=kernel32.GetModuleHandleW(library)
    if module:
        name=ctypes.create_unicode_buffer(32768)
        if not kernel32.GetModuleFileNameW(module,name,len(name)): raise ctypes.WinError(ctypes.get_last_error())
        path=pathlib.Path(name.value)
        loaded_ssl.append({'library':library,'path':str(path),'sha256':hashlib.sha256(path.read_bytes()).hexdigest()})
if loaded_ssl or '_hashlib' in sys.modules:
    raise AssertionError(f'The PE-only verifier unexpectedly loaded OpenSSL: {loaded_ssl}')

class PE:
    def __init__(self, path):
        self.path = path
        self.data = path.read_bytes()
        d = self.data
        if d[:2] != b'MZ': raise ValueError(f'Not a PE file: {path}')
        p = struct.unpack_from('<I', d, 0x3c)[0]
        if d[p:p+4] != b'PE\0\0': raise ValueError(f'Invalid PE signature: {path}')
        self.machine, count = struct.unpack_from('<HH', d, p+4)
        optsize = struct.unpack_from('<H', d, p+20)[0]
        opt = p+24
        magic = struct.unpack_from('<H', d, opt)[0]
        self.wide = magic == 0x20b
        if magic not in (0x10b,0x20b): raise ValueError('Unknown PE optional header')
        self.imagebase = struct.unpack_from('<Q' if self.wide else '<I', d, opt+(24 if self.wide else 28))[0]
        dd = opt + (112 if self.wide else 96)
        self.exports_rva = struct.unpack_from('<I', d, dd)[0]
        self.imports_rva = struct.unpack_from('<I', d, dd+8)[0]
        self.delay_rva = struct.unpack_from('<I', d, dd+13*8)[0]
        self.sections = []
        for i in range(count):
            h = opt+optsize+i*40
            vsize, rva, size, offset = struct.unpack_from('<IIII', d, h+8)
            self.sections.append((rva,max(vsize,size),offset))
    def offset(self, rva):
        for base,size,offset in self.sections:
            if base <= rva < base+size: return offset+rva-base
        if rva < min(s[0] for s in self.sections): return rva
        raise ValueError(f'Unmapped PE RVA {rva:x}: {self.path}')
    def string(self, rva):
        off=self.offset(rva)
        return self.data[off:self.data.index(b'\0',off)].decode('ascii')
    def exports(self):
        if not self.exports_rva: return set(),set()
        off=self.offset(self.exports_rva)
        base,nfunctions,nnames,functions,names,ordinals=struct.unpack_from('<IIIIII',self.data,off+16)
        symbols={self.string(struct.unpack_from('<I',self.data,self.offset(names)+4*i)[0]) for i in range(nnames)}
        numbers={base+i for i in range(nfunctions) if struct.unpack_from('<I',self.data,self.offset(functions)+4*i)[0]}
        return symbols,numbers
    def imports(self):
        result=[]
        def symbols_at(rva):
            thunk=self.offset(rva)
            symbols=[]
            width=8 if self.wide else 4
            fmt='<Q' if self.wide else '<I'
            flag=1<<(width*8-1)
            while True:
                value=struct.unpack_from(fmt,self.data,thunk)[0]
                if value==0: break
                symbols.append(value&0xffff if value&flag else self.string(value+2))
                thunk+=width
            return symbols
        if self.imports_rva:
            off=self.offset(self.imports_rva)
            while True:
                ilt,timestamp,chain,name,iat=struct.unpack_from('<IIIII',self.data,off)
                if not any((ilt,timestamp,chain,name,iat)): break
                result.append((self.string(name).lower(),symbols_at(ilt or iat)))
                off+=20
        if self.delay_rva:
            off=self.offset(self.delay_rva)
            while True:
                attrs,name,module,iat,ilt,bound,unload,timestamp=struct.unpack_from('<IIIIIIII',self.data,off)
                if not any((attrs,name,module,iat,ilt,bound,unload,timestamp)): break
                as_rva=lambda value: value if attrs&1 else value-self.imagebase
                result.append((self.string(as_rva(name)).lower(),symbols_at(as_rva(ilt or iat))))
                off+=32
        return result

runtime=pathlib.Path(sys.argv[1]).resolve()
output=pathlib.Path(sys.argv[2]).resolve()
candidates={name:PE(output/name) for name in ['libcrypto-3-x64.dll','libssl-3-x64.dll']}
exports={name:pe.exports() for name,pe in candidates.items()}
for name,pe in candidates.items():
    if pe.machine!=0x8664: raise AssertionError(f'Candidate is not Windows x64: {name}')
    if any(dll in ('msys-2.0.dll','cygwin1.dll') for dll,_ in pe.imports()):
        raise AssertionError(f'Candidate depends on a POSIX runtime: {name}')
files=sorted([p for p in runtime.rglob('*') if p.is_file() and p.suffix.lower() in ('.dll','.exe')])
if not files: raise AssertionError('The input receiver runtime has no PE files')
files += [output/name for name in candidates]
consumers=[]
checked=0
for path in files:
    pe=PE(path)
    for dll,symbols in pe.imports():
        if dll not in exports: continue
        names,numbers=exports[dll]
        missing=[s for s in symbols if s not in (numbers if isinstance(s,int) else names)]
        if missing: raise AssertionError(f'{path.name} imports missing symbols from {dll}: {missing}')
        checked+=len(symbols)
        consumers.append({'file':str(path),'sha256':hashlib.sha256(pe.data).hexdigest(),
            'library':dll,'importCount':len(symbols),'imports':symbols})
report={'status':'pass','method':'Normal and delayed PE import/export inspection; no dependency DLL execution',
    'hashBackend':'CPython builtin _sha2; isolated interpreter without site initialization',
    'loadedOpenSslModules':loaded_ssl,
    'runtimeDirectory':str(runtime),'inspectedPeFiles':len(files),'checkedImportSymbols':checked,
    'candidateExports':{name:len(names) for name,(names,_) in exports.items()},'consumers':consumers}
report_path=pathlib.Path(sys.argv[3]).resolve() if len(sys.argv)>3 else output/'compatibility.json'
report_path.parent.mkdir(parents=True,exist_ok=True)
report_path.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
print(f'PASS: {len(files)} PE files; {len(consumers)} OpenSSL consumers; {checked} imported symbols')
'@
$verifyFile = Join-Path $output 'verify-imports.py'
[IO.File]::WriteAllText($verifyFile, $verifyScript, [Text.UTF8Encoding]::new($false))
& (Join-Path $ucrt 'bin\python.exe') -I -S $verifyFile (Resolve-Path -LiteralPath $RuntimeDirectory).Path $output
if ($LASTEXITCODE -ne 0) { throw 'The new OpenSSL DLLs do not satisfy the receiver runtime imports.' }

$knownAnswer = Join-Path $build 'sha256-known-answer.bin'
[IO.File]::WriteAllBytes($knownAnswer, [byte[]]@(0x61,0x62,0x63))
$versionInfo = & (Join-Path $prefix 'bin\openssl.exe') version -a
if ($LASTEXITCODE -ne 0 -or -not ($versionInfo[0] -match '^OpenSSL 3\.5\.9 ')) { throw 'The built OpenSSL CLI version is incorrect.' }
$digest = & (Join-Path $prefix 'bin\openssl.exe') dgst -sha256 $knownAnswer
if ($LASTEXITCODE -ne 0 -or $digest -notmatch 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad') {
    throw 'The built OpenSSL SHA-256 known-answer check failed.'
}
$cryptoTestSource = @'
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <openssl/crypto.h>
#include <openssl/err.h>
#include <openssl/evp.h>
#include <openssl/rand.h>

#define CHECK(value, label) do { if (!(value)) { fprintf(stderr, "FAIL: %s\n", label); ERR_print_errors_fp(stderr); return 1; } checks++; } while (0)

static int hex(const char *text, unsigned char *bytes, size_t length) {
    if (strlen(text) != length * 2) return 0;
    for (size_t i = 0; i < length; i++) {
        unsigned int value;
        if (sscanf(text + i * 2, "%2x", &value) != 1) return 0;
        bytes[i] = (unsigned char)value;
    }
    return 1;
}

int main(void) {
    int checks = 0, size = 0, final_size = 0;
    HMODULE module = GetModuleHandleW(L"libcrypto-3-x64.dll");
    wchar_t wide_path[32768];
    char module_path[131072];
    CHECK(module && GetModuleFileNameW(module, wide_path, 32768), "locate the loaded libcrypto module");
    CHECK(WideCharToMultiByte(CP_UTF8, 0, wide_path, -1, module_path, sizeof(module_path), NULL, NULL), "encode the loaded module path");
    for (char *p = module_path; *p; p++) if (*p == '\\') *p = '/';
    CHECK(strncmp(OpenSSL_version(OPENSSL_VERSION), "OpenSSL 3.5.9 ", 14) == 0, "runtime OpenSSL version is 3.5.9");
    CHECK(GetModuleHandleW(L"libssl-3-x64.dll") == NULL, "crypto checks do not load an unused SSL library");

    /* NIST GCM vector from the pinned upstream evpciph_aes_common.txt. */
    unsigned char key[16] = {0}, iv[12] = {0}, plain[16] = {0}, cipher[32], tag[16], expected[16], expected_tag[16], recovered[32];
    CHECK(hex("0388dace60b6a392f328c2b971b2fe78", expected, 16) &&
          hex("ab6e47d42cec13bdf53a67b21257bddf", expected_tag, 16), "decode AES-GCM known answers");
    EVP_CIPHER_CTX *aes = EVP_CIPHER_CTX_new();
    CHECK(aes && EVP_EncryptInit_ex(aes, EVP_aes_128_gcm(), NULL, key, iv) == 1 &&
          EVP_EncryptUpdate(aes, cipher, &size, plain, 16) == 1 &&
          EVP_EncryptFinal_ex(aes, cipher + size, &final_size) == 1 &&
          EVP_CIPHER_CTX_ctrl(aes, EVP_CTRL_GCM_GET_TAG, 16, tag) == 1, "AES-GCM encryption executes");
    CHECK(size == 16 && CRYPTO_memcmp(cipher, expected, 16) == 0 &&
          CRYPTO_memcmp(tag, expected_tag, 16) == 0, "AES-GCM ciphertext and authentication tag match");
    CHECK(EVP_DecryptInit_ex(aes, EVP_aes_128_gcm(), NULL, key, iv) == 1 &&
          EVP_DecryptUpdate(aes, recovered, &size, cipher, 16) == 1 &&
          EVP_CIPHER_CTX_ctrl(aes, EVP_CTRL_GCM_SET_TAG, 16, tag) == 1 &&
          EVP_DecryptFinal_ex(aes, recovered + size, &final_size) == 1 &&
          CRYPTO_memcmp(recovered, plain, 16) == 0, "AES-GCM verifies and decrypts the valid tag");
    tag[0] ^= 1;
    CHECK(EVP_DecryptInit_ex(aes, EVP_aes_128_gcm(), NULL, key, iv) == 1 &&
          EVP_DecryptUpdate(aes, recovered, &size, cipher, 16) == 1 &&
          EVP_CIPHER_CTX_ctrl(aes, EVP_CTRL_GCM_SET_TAG, 16, tag) == 1 &&
          EVP_DecryptFinal_ex(aes, recovered + size, &final_size) <= 0, "AES-GCM rejects a tampered tag");
    EVP_CIPHER_CTX_free(aes);
    ERR_clear_error();

    /* RFC 8032 Ed25519 empty-message vector, also in pinned evppkey_ecx.txt. */
    unsigned char seed[32], expected_public[32], public_key[32], signature[64], expected_signature[64];
    size_t public_size = sizeof(public_key), signature_size = sizeof(signature);
    CHECK(hex("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60", seed, 32) &&
          hex("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", expected_public, 32) &&
          hex("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b", expected_signature, 64), "decode Ed25519 known answers");
    EVP_PKEY *ed = EVP_PKEY_new_raw_private_key(EVP_PKEY_ED25519, NULL, seed, 32);
    CHECK(ed && EVP_PKEY_get_raw_public_key(ed, public_key, &public_size) == 1 && public_size == 32 &&
          CRYPTO_memcmp(public_key, expected_public, 32) == 0, "Ed25519 public key matches RFC 8032");
    EVP_MD_CTX *sign = EVP_MD_CTX_new();
    CHECK(sign && EVP_DigestSignInit(sign, NULL, NULL, NULL, ed) == 1 &&
          EVP_DigestSign(sign, signature, &signature_size, (const unsigned char *)"", 0) == 1 && signature_size == 64 &&
          CRYPTO_memcmp(signature, expected_signature, 64) == 0, "Ed25519 signature matches RFC 8032");
    EVP_PKEY *ed_public = EVP_PKEY_new_raw_public_key(EVP_PKEY_ED25519, NULL, expected_public, 32);
    EVP_MD_CTX *verify = EVP_MD_CTX_new();
    CHECK(ed_public && verify && EVP_DigestVerifyInit(verify, NULL, NULL, NULL, ed_public) == 1 &&
          EVP_DigestVerify(verify, signature, 64, (const unsigned char *)"", 0) == 1, "Ed25519 verifies a valid signature");
    signature[0] ^= 1;
    CHECK(EVP_DigestVerify(verify, signature, 64, (const unsigned char *)"", 0) == 0, "Ed25519 rejects a tampered signature");
    EVP_MD_CTX_free(sign); EVP_MD_CTX_free(verify); EVP_PKEY_free(ed_public); EVP_PKEY_free(ed);
    ERR_clear_error();

    /* RFC 7748 X25519 Alice/Bob vector from pinned evppkey_ecx.txt. */
    unsigned char alice_private[32], alice_public[32], bob_public[32], shared[32], expected_shared[32], zero[32] = {0};
    CHECK(hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a", alice_private, 32) &&
          hex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a", expected_public, 32) &&
          hex("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f", bob_public, 32) &&
          hex("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742", expected_shared, 32), "decode X25519 known answers");
    EVP_PKEY *alice = EVP_PKEY_new_raw_private_key(EVP_PKEY_X25519, NULL, alice_private, 32);
    EVP_PKEY *bob = EVP_PKEY_new_raw_public_key(EVP_PKEY_X25519, NULL, bob_public, 32);
    public_size = sizeof(alice_public);
    CHECK(alice && bob && EVP_PKEY_get_raw_public_key(alice, alice_public, &public_size) == 1 && public_size == 32 &&
          CRYPTO_memcmp(alice_public, expected_public, 32) == 0, "X25519 public key matches RFC 7748");
    EVP_PKEY_CTX *derive = EVP_PKEY_CTX_new(alice, NULL);
    size_t shared_size = sizeof(shared);
    CHECK(derive && EVP_PKEY_derive_init(derive) == 1 && EVP_PKEY_derive_set_peer(derive, bob) == 1 &&
          EVP_PKEY_derive(derive, shared, &shared_size) == 1 && shared_size == 32 &&
          CRYPTO_memcmp(shared, expected_shared, 32) == 0, "X25519 shared secret matches RFC 7748");
    EVP_PKEY *invalid_peer = EVP_PKEY_new_raw_public_key(EVP_PKEY_X25519, NULL, zero, 32);
    shared_size = sizeof(shared);
    CHECK(invalid_peer && EVP_PKEY_derive_set_peer(derive, invalid_peer) == 1 &&
          EVP_PKEY_derive(derive, shared, &shared_size) <= 0, "X25519 rejects an all-zero peer secret");
    EVP_PKEY_CTX_free(derive); EVP_PKEY_free(invalid_peer); EVP_PKEY_free(alice); EVP_PKEY_free(bob);
    ERR_clear_error();

    unsigned char random_a[32], random_b[32];
    CHECK(RAND_status() == 1 && RAND_bytes(random_a, sizeof(random_a)) == 1 &&
          RAND_priv_bytes(random_b, sizeof(random_b)) == 1, "public and private random generators execute successfully");
    CHECK(CRYPTO_memcmp(random_a, random_b, 32) != 0 && CRYPTO_memcmp(random_a, zero, 32) != 0 &&
          CRYPTO_memcmp(random_b, zero, 32) != 0, "random samples are distinct and nonzero");
    printf("{\"status\":\"pass\",\"checkCount\":%d,\"loadedCryptoModule\":\"%s\",\"runtimeVersion\":\"%s\",\"algorithms\":[\"AES-128-GCM\",\"Ed25519\",\"X25519\",\"RAND_bytes/RAND_priv_bytes\"]}\n", checks, module_path, OpenSSL_version(OPENSSL_VERSION));
    return 0;
}
'@
$cryptoSourceFile = Join-Path $build 'crypto-known-answers.c'
$cryptoExecutable = Join-Path $prefix 'bin\openssl-security-check.exe'
[IO.File]::WriteAllText($cryptoSourceFile, $cryptoTestSource, [Text.UTF8Encoding]::new($false))
$compilerPath = $env:PATH
try {
    $env:PATH = (Join-Path $ucrt 'bin') + ';' + $compilerPath
    & (Join-Path $ucrt 'bin\gcc.exe') -O2 -Wall -Wextra ("-I" + (Join-Path $prefix 'include')) $cryptoSourceFile ("-L" + (Join-Path $prefix 'lib')) -lcrypto -o $cryptoExecutable
    $cryptoCompilerExitCode = $LASTEXITCODE
} finally {
    $env:PATH = $compilerPath
}
if ($cryptoCompilerExitCode -ne 0) { throw 'Could not build the actual-library crypto checks.' }
$cryptoResultJson = & $cryptoExecutable
if ($LASTEXITCODE -ne 0) { throw 'The new OpenSSL crypto known-answer checks failed.' }
$cryptoResult = $cryptoResultJson | ConvertFrom-Json
$expectedLoadedPath = [IO.Path]::GetFullPath((Join-Path $prefix 'bin\libcrypto-3-x64.dll'))
if (-not [IO.Path]::GetFullPath($cryptoResult.loadedCryptoModule).Equals($expectedLoadedPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The crypto checks loaded another OpenSSL DLL instead of the newly built library.'
}
$loadedCryptoSha256 = (Get-FileHash -LiteralPath $cryptoResult.loadedCryptoModule -Algorithm SHA256).Hash
if ($loadedCryptoSha256 -ne (@($inventory | Where-Object relativePath -eq 'libcrypto-3-x64.dll')[0].sha256)) {
    throw 'The loaded crypto-check module does not match the component output hash.'
}
$cryptoResult | Add-Member -NotePropertyName loadedCryptoModuleSha256 -NotePropertyValue $loadedCryptoSha256
$cryptoResult | Add-Member -NotePropertyName upstreamTestScope -NotePropertyValue 'The upstream complete test suite was not run (no-tests build); these are targeted actual-library crypto checks.'
$cryptoResult | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'crypto-checks.json') -Encoding utf8NoBOM
[ordered]@{
    schemaVersion = 1
    component = 'openssl'
    version = $version
    supportedUntil = '2030-04-08'
    sourceUrl = $sourceUrl
    sourceSha256 = $sourceHash
    license = 'Apache-2.0'
    configuration = $configuration
    prefixRelativePath = 'prefix'
    compiler = (& (Join-Path $ucrt 'bin\gcc.exe') --version | Select-Object -First 1)
    compilerTarget = (& (Join-Path $ucrt 'bin\gcc.exe') -dumpmachine)
    buildTools = $toolPackages
    buildFiles = $buildFiles
    build = [ordered]@{
        script = 'scripts/native/Build-OpenSslSecurity.ps1'
        target = 'mingw64'
        configuration = $configuration
        compiler = (& (Join-Path $ucrt 'bin\gcc.exe') --version | Select-Object -First 1)
        compilerTarget = (& (Join-Path $ucrt 'bin\gcc.exe') -dumpmachine)
        prefixRelativePath = 'prefix'
        completeUpstreamTestSuiteRun = $false
        targetedCryptoCheckCount = $cryptoResult.checkCount
        cryptoChecksSha256 = (Get-FileHash -LiteralPath (Join-Path $output 'crypto-checks.json') -Algorithm SHA256).Hash
        compatibilityReportSha256 = (Get-FileHash -LiteralPath (Join-Path $output 'compatibility.json') -Algorithm SHA256).Hash
    }
    versionInfo = $versionInfo
    knownAnswerSha256 = 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'
    cryptoChecksSha256 = (Get-FileHash -LiteralPath (Join-Path $output 'crypto-checks.json') -Algorithm SHA256).Hash
    compatibilityReportSha256 = (Get-FileHash -LiteralPath (Join-Path $output 'compatibility.json') -Algorithm SHA256).Hash
    files = @($inventory)
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding utf8NoBOM
Get-Item -LiteralPath (Join-Path $output 'manifest.json')
