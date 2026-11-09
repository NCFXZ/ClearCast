"""Load controlled native dependencies before Python's hashlib/ssl imports.

Windows resolves dependent DLLs by module name. Conda's hashlib can otherwise
preload a different libcrypto and make a protocol test exercise that DLL.
"""
import ctypes as c
from ctypes import wintypes as w
import os
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
_kernel = c.WinDLL('kernel32', use_last_error=True)
_kernel.GetModuleHandleW.argtypes = [w.LPCWSTR]
_kernel.GetModuleHandleW.restype = w.HMODULE
_kernel.GetModuleFileNameW.argtypes = [w.HMODULE, w.LPWSTR, w.DWORD]
_kernel.LoadLibraryExW.argtypes = [w.LPCWSTR, w.HANDLE, w.DWORD]
_kernel.LoadLibraryExW.restype = w.HMODULE
_handles = {}


def module_path(handle):
    value = c.create_unicode_buffer(32768)
    if not _kernel.GetModuleFileNameW(handle, value, len(value)):
        raise c.WinError(c.get_last_error())
    return pathlib.Path(value.value).resolve()


def ensure_controlled_dependencies(runtime=None, names=('libcrypto-3-x64.dll', 'libplist-2.0.dll')):
    runtime = pathlib.Path(runtime or os.environ.get('CLEARCAST_PROBE_RUNTIME', ROOT / '.runtime/receiver')).resolve()
    for name in names:
        expected = (runtime / name).resolve()
        loaded = _kernel.GetModuleHandleW(name)
        if loaded and module_path(loaded) != expected:
            raise RuntimeError(f'{name} was already loaded from {module_path(loaded)}; '
                'use a fresh Python host that loads the controlled runtime before hashlib/ssl.')
        if not loaded:
            loaded = _kernel.LoadLibraryExW(str(expected), None, 0x100 | 0x800)
            if not loaded:
                raise c.WinError(c.get_last_error())
        if module_path(loaded) != expected:
            raise RuntimeError(f'{name} did not load from the controlled runtime')
        _handles[name] = loaded
    return runtime


def loaded_dependency_evidence(runtime=None, names=('libcrypto-3-x64.dll', 'libplist-2.0.dll')):
    runtime = ensure_controlled_dependencies(runtime, names)
    import hashlib
    evidence = []
    for name in names:
        handle = _kernel.GetModuleHandleW(name)
        actual = module_path(handle)
        library = c.CDLL(str(actual))
        if name == 'libcrypto-3-x64.dll':
            library.OpenSSL_version.argtypes = [c.c_int]
            library.OpenSSL_version.restype = c.c_char_p
            version = library.OpenSSL_version(0).decode()
        else:
            library.libplist_version.argtypes = []
            library.libplist_version.restype = c.c_char_p
            version = library.libplist_version().decode()
        evidence.append({'module': name, 'actualPath': str(actual),
            'sha256': hashlib.sha256(actual.read_bytes()).hexdigest(), 'version': version})
    return evidence
