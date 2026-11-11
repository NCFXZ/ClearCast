"""Cold registry startup and cancellation checks for the bundled scanner.

Each case runs in a new process with disposable state, so GStreamer's in-process
initialization cache cannot turn a cold scan into a warm one.
SPDX-License-Identifier: GPL-3.0-or-later
"""
import ctypes as c
from ctypes import wintypes as w
import json
import os
import pathlib
import socket
import subprocess
import sys
import time

from receiver_test_support import ROOT, Receiver, connect, request
from receiver_test_bootstrap import loaded_dependency_evidence

kernel = c.WinDLL('kernel32', use_last_error=True)
user = c.WinDLL('user32')


class ProcessEntry(c.Structure):
    _fields_ = [('size', w.DWORD), ('usage', w.DWORD), ('pid', w.DWORD),
        ('heap', c.c_size_t), ('module', w.DWORD), ('threads', w.DWORD),
        ('parent', w.DWORD), ('priority', w.LONG), ('flags', w.DWORD),
        ('name', w.WCHAR * 260)]


kernel.CreateToolhelp32Snapshot.argtypes = [w.DWORD, w.DWORD]
kernel.CreateToolhelp32Snapshot.restype = w.HANDLE
kernel.Process32FirstW.argtypes = [w.HANDLE, c.POINTER(ProcessEntry)]
kernel.Process32NextW.argtypes = kernel.Process32FirstW.argtypes
kernel.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
kernel.OpenProcess.restype = w.HANDLE
kernel.CloseHandle.argtypes = [w.HANDLE]
kernel.WaitForSingleObject.argtypes = [w.HANDLE, w.DWORD]
kernel.QueryFullProcessImageNameW.argtypes = [w.HANDLE, w.DWORD, w.LPWSTR, c.POINTER(w.DWORD)]
user.GetWindowThreadProcessId.argtypes = [w.HWND, c.POINTER(w.DWORD)]
user.IsWindowVisible.argtypes = [w.HWND]


def scanner_children():
    snapshot = kernel.CreateToolhelp32Snapshot(2, 0)
    item = ProcessEntry()
    item.size = c.sizeof(item)
    found = []
    try:
        more = kernel.Process32FirstW(snapshot, c.byref(item))
        while more:
            if item.parent == os.getpid() and item.name.lower() == 'gst-plugin-scanner.exe':
                found.append(item.pid)
            more = kernel.Process32NextW(snapshot, c.byref(item))
    finally:
        kernel.CloseHandle(snapshot)
    return found


def visible_windows(pid):
    found = []

    @c.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    def visit(window, unused):
        owner = w.DWORD()
        user.GetWindowThreadProcessId(window, c.byref(owner))
        if owner.value == pid and user.IsWindowVisible(window):
            found.append(int(window))
        return True

    user.EnumWindows(visit, 0)
    return found


def process_path(process):
    path = c.create_unicode_buffer(32768)
    size = w.DWORD(len(path))
    assert kernel.QueryFullProcessImageNameW(process, 0, path, c.byref(size))
    return pathlib.Path(path.value).resolve()


def run_case(mode):
    receiver = Receiver()
    helper = None
    detail = {'mode': mode, 'state': str(receiver.state), 'loadedDependencies': loaded_dependency_evidence()}
    try:
        assert not (receiver.state / 'gstreamer-registry.bin').exists()
        assert receiver.dll.clearcast_receiver_start_v2(str(receiver.state).encode(),
            b'1920x1080', 30, 1, 0, b'0001', receiver.hwnd,
            int.from_bytes(socket.inet_aton('127.0.0.1'), 'little')) == 0
        receiver.running = True
        if mode != 'stop_immediately':
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline:
                children = scanner_children()
                if children:
                    helper = kernel.OpenProcess(0x100000 | 0x1000, False, children[0])
                    if helper:
                        break
                time.sleep(.005)
            assert helper, 'Cold startup did not launch the bundled scanner'
            detail['helperActiveWhenObserved'] = kernel.WaitForSingleObject(helper, 0) == 258
            assert detail['helperActiveWhenObserved']
            helper_path = process_path(helper)
            detail['helperPath'] = str(helper_path)
            assert helper_path == (receiver.runtime / 'gst-plugin-scanner.exe').resolve()
            detail['helperVisibleWindowCount'] = len(visible_windows(children[0]))
            assert detail['helperVisibleWindowCount'] == 0

        if mode == 'ready_then_stop':
            deadline = time.monotonic() + 15
            ready = False
            while time.monotonic() < deadline and not ready:
                for event in receiver.events():
                    assert event[0] != 5, ('receiver failed', event[4])
                    ready |= event[0] == 1
                if not ready:
                    time.sleep(.01)
            assert ready, 'Cold receiver did not become ready'
            detail['registryWritten'] = (receiver.state / 'gstreamer-registry.bin').stat().st_size > 0
            assert detail['registryWritten']
            with connect() as sock:
                request(sock, '*', method='OPTIONS')
        elif mode == 'stop_during_scan':
            assert not any(event[0] == 1 for event in receiver.events()), 'Cancellation missed the scan'

        started = time.monotonic()
        receiver.stop()
        detail['stopSeconds'] = round(time.monotonic() - started, 4)
        assert detail['stopSeconds'] < 10, 'Cold scan cancellation did not finish promptly'
        if helper:
            detail['helperExitedAfterStop'] = kernel.WaitForSingleObject(helper, 3000) == 0
            assert detail['helperExitedAfterStop'], 'Scanner survived receiver shutdown'
        detail['remainingHelperChildren'] = scanner_children()
        assert not detail['remainingHelperChildren'], 'Scanner child was orphaned'
        detail['passed'] = True
        return detail
    finally:
        receiver.close()
        if helper:
            kernel.CloseHandle(helper)


if len(sys.argv) == 3 and sys.argv[1] == '--case':
    print(json.dumps(run_case(sys.argv[2])))
else:
    report = {'passed': False, 'cases': []}
    try:
        for mode in ('ready_then_stop', 'stop_during_scan', 'stop_immediately'):
            result = subprocess.run([sys.executable, __file__, '--case', mode],
                capture_output=True, text=True, timeout=45)
            assert result.returncode == 0, result.stderr or result.stdout
            report['cases'].append(json.loads(result.stdout))
            print('PASS:', mode, flush=True)
        report['passed'] = True
    finally:
        output = pathlib.Path(os.environ.get('CLEARCAST_SCANNER_REPORT',
            ROOT / '.runtime/qa-plugin-scanner-results.json'))
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(report, indent=2), encoding='utf-8')
