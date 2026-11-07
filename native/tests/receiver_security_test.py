"""Security, bounded adversarial load and lifecycle checks against the real DLL.

Uses disposable state and localhost only. Close the desktop receiver first.
SPDX-License-Identifier: GPL-3.0-or-later
"""
from receiver_test_bootstrap import ensure_controlled_dependencies, loaded_dependency_evidence
ensure_controlled_dependencies()

import base64
import ctypes as c
from ctypes import wintypes as w
import hashlib
import json
import os
import pathlib
import random
import socket
import threading
import time

from receiver_test_support import (ROOT, Receiver, connect, plist_request,
    raw_public, request, verify_pairing, ed25519, plistlib)

results = {'started_utc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
           'cases': [], 'limitations': ['Local protocol tests do not replace real-device audiovisual testing.',
           'Bounded deterministic mutations are not exhaustive fuzzing or a formal security proof.']}
results['loadedDependencies'] = loaded_dependency_evidence()
receiver = Receiver()
results['state'] = str(receiver.state)
started = time.monotonic()


def run(name, action):
    begin = time.monotonic()
    try:
        detail = action()
        results['cases'].append({'name': name, 'passed': True,
            'seconds': round(time.monotonic()-begin, 3), 'detail': detail})
        print('PASS:', name, flush=True)
    except Exception as error:
        results['cases'].append({'name': name, 'passed': False,
            'seconds': round(time.monotonic()-begin, 3), 'error': repr(error)})
        raise


def health():
    with connect() as sock:
        request(sock, '*', method='OPTIONS')


def invalid_abi():
    base = [str(receiver.state).encode(), b'1920x1080', 30, 1, 0, b'0001', receiver.hwnd, 0]
    cases = [(0, None), (0, b'relative'), (1, b'0x1080'), (1, b'65536x1'),
             (1, b'1x65536'), (1, b'1x2junk'), (2, 0), (2, 121), (3, -1),
             (3, 3), (4, 2), (5, b'0000'), (5, b'abcd'), (6, 0)]
    for index, value in cases:
        args = base.copy()
        args[index] = value
        assert receiver.dll.clearcast_receiver_start_v2(*args) == 87, (index, value)
    return {'invalid_argument_cases': len(cases)}


private = None
server_ed = None


def pairing_and_metadata():
    global private, server_ed
    with connect() as sock:
        private, server_ed = receiver.pair(sock, lowercase=True)
        verify_pairing(sock, private, server_ed)
        metadata = plistlib.dumps({'deviceID':'AA:BB:CC:DD:EE:FF', 'model':'iPhone',
            'name':'QA 设备, Sender\nInjected\rLine', 'eiv':b'', 'ekey':b''}, fmt=plistlib.FMT_BINARY)
        request(sock, '/metadata-test', metadata, method='SETUP',
                content_type='application/x-apple-binary-plist', expected=400)
        before = (receiver.state/'clients.txt').stat().st_size
        begin = time.monotonic()
        for _ in range(250):
            request(sock, '/metadata-test', metadata, method='SETUP',
                    content_type='application/x-apple-binary-plist', expected=400)
        after = (receiver.state/'clients.txt').stat().st_size
        assert after == before, (before, after)
        rows = (receiver.state/'clients.txt').read_text(encoding='utf-8').splitlines()
        assert len(rows) == 2 and 'QA 设备, Sender Injected Line' in rows[-1], rows
        return {'duplicate_setups':250,'file_growth_bytes':after-before,
                'seconds':round(time.monotonic()-begin,3),'unique_client_keys':1}


def malformed_verification():
    checked = 0
    rng = random.Random(0x43415354)
    bodies = [b'',b'\2\0\0\0',b'\1\0\0\0x',b'\0\0\0\0x']
    bodies += [rng.randbytes(rng.randrange(0,100)) for _ in range(256)]
    for body in bodies:
        with connect() as sock:
            status, _, _ = request(sock, '/pair-verify', body, expected=None)
            assert status in (400,470), (len(body),status)
        checked += 1
    health()
    return {'malformed_payloads':checked}


def media_requires_auth():
    methods = ['SETUP','RECORD','FLUSH','TEARDOWN','SET_PARAMETER','GET_PARAMETER']
    for method in methods:
        with connect() as sock:
            request(sock, '/unauthorized', method=method, expected=401)
    with connect() as sock:
        verify_pairing(sock, private, server_ed, corrupt=True)
    health()
    return {'unauthenticated_media_methods_rejected':len(methods), 'tampered_signature_rejected':True}


def keepalive_io():
    with connect() as sock:
        request(sock,'/feedback')
        before = (receiver.state/'receiver.log').stat().st_size
        for _ in range(100):
            request(sock,'/feedback')
        after = (receiver.state/'receiver.log').stat().st_size
        assert before == after, (before,after)
    return {'successful_keepalives':100,'routine_log_growth_bytes':after-before}


def private_diagnostics():
    secret = 'QA_DO_NOT_LOG_CREDENTIAL_68251'
    with connect() as sock:
        request(sock, 'RTSP://qa_user:'+secret+'@127.0.0.1/info?token='+secret,
                method='GET', expected=200)
        request(sock, '/pair-verify', secret.encode(), expected=400)
    log = (receiver.state/'receiver.log').read_text()
    assert secret not in log and 'qa_user' not in log, 'Sensitive URI/body data was logged'
    assert 'path=/info' in log
    return {'uri_authority_query_and_body_omitted':True}


def rotation():
    log = receiver.state/'receiver.log'
    log.write_bytes(b'x'*(256*1024-16))
    health()
    files = [log, receiver.state/'receiver.previous.log']
    assert all(path.is_file() and path.stat().st_size <= 256*1024 for path in files)
    return {'max_bytes_per_log':256*1024,'files':len(files)}


def bad_network_frames():
    frames = [b'\0'*32, b'not a request\r\n\r\n',
        b'POST /x RTSP/1.0\r\nContent-Length: 1048577\r\n\r\n',
        b'POST /x RTSP/1.0\r\nContent-Length: -1\r\n\r\n',
        b'GET /'+b'a'*8193+b' RTSP/1.0\r\nCSeq: 1\r\n\r\n',
        b'OPTIONS * RTSP/1.0\r\nCSeq: 1\r\nX: '+b'a'*1025+b'\r\n\r\n',
        b'OPTIONS * RTSP/1.0\r\nCSeq: 1\r\n'+b'X: a\r\n'*22+b'\r\n']
    for frame in frames:
        with connect(timeout=3) as sock:
            sock.sendall(frame)
            try:
                reply = sock.recv(4096)
                assert not reply or b'431 ' in reply, reply[:80]
            except ConnectionResetError:
                pass
        health()
    return {'malformed_frames':len(frames),'health_checks':len(frames)}


def slow_partial_requests():
    sockets = [connect() for _ in range(12)]
    try:
        for sock in sockets:
            sock.sendall(b'OPTIONS * RTSP/1.0\r\nCSeq: 1\r\nX-Slow: ')
        closed = set()
        begin = time.monotonic()
        while time.monotonic()-begin < 17.5:
            for index,sock in enumerate(sockets):
                if index in closed:
                    continue
                try:
                    sock.sendall(b'a')
                except OSError:
                    closed.add(index)
            time.sleep(.5)
        for index,sock in enumerate(sockets):
            try:
                sock.settimeout(.1)
                if not sock.recv(1):
                    closed.add(index)
            except (ConnectionResetError,ConnectionAbortedError):
                closed.add(index)
            except socket.timeout:
                pass
        assert len(closed) == 12, ('Slow requests still reserve slots',len(closed))
        health()
        return {'slow_connections_closed':len(closed),'deadline_seconds':15}
    finally:
        for sock in sockets:
            sock.close()


def blocked_reader():
    sock = connect()
    sock.setsockopt(socket.SOL_SOCKET,socket.SO_RCVBUF,1024)
    sock.setsockopt(socket.IPPROTO_TCP,socket.TCP_NODELAY,1)
    sock.settimeout(.1)
    sent = [0]
    def writer():
        frame = b'GET /info RTSP/1.0\r\nCSeq: 1\r\n\r\n'
        for _ in range(1500):
            try:
                sock.sendall(frame)
                sent[0] += 1
            except OSError:
                break
            time.sleep(.005)
    thread = threading.Thread(target=writer,daemon=True)
    thread.start()
    time.sleep(3)
    before = time.monotonic()
    health()
    elapsed = time.monotonic()-before
    sock.close()
    thread.join(2)
    assert elapsed < 3.5, elapsed
    return {'unread_response_requests_sent':sent[0],'other_client_response_seconds':round(elapsed,3)}


def expired_pin_and_idle_cpu():
    pin = receiver.challenge()
    before_cpu, before_wall = time.process_time(),time.monotonic()
    time.sleep(61)
    cpu, wall = time.process_time()-before_cpu,time.monotonic()-before_wall
    with connect() as sock:
        plist_request(sock,{'method':'pin','user':'AA:BB:CC:DD:EE:FF'},expected=470)
    return {'expired_pin_rejected':True,'idle_seconds':round(wall,2),
            'process_cpu_percent_of_one_core':round(cpu/wall*100,3)}


def pin_cooldown():
    # A successful fresh PIN handshake resets the failed-proof counter.
    with connect() as sock:
        receiver.pair(sock,private=private)
    for _ in range(5):
        receiver.challenge()
        with connect() as sock:
            plist_request(sock,{'method':'pin','user':'AA:BB:CC:DD:EE:FF'})
            plist_request(sock,{'pk':b'\1'*256,'proof':b'\0'*20},expected=470)
    with connect() as sock:
        request(sock,'/pair-pin-start',expected=470)
    with connect() as sock:
        verify_pairing(sock,private,server_ed)
    time.sleep(61)
    receiver.challenge()
    return {'bad_proofs':5,'cooldown_rejects_new_pin':True,
            'paired_client_allowed_during_cooldown':True,'new_pin_recovers_after_cooldown':True}


def handles():
    kernel = c.WinDLL('kernel32')
    kernel.GetCurrentProcess.restype = w.HANDLE
    kernel.GetProcessHandleCount.argtypes = [w.HANDLE,c.POINTER(w.DWORD)]
    count = w.DWORD()
    assert kernel.GetProcessHandleCount(kernel.GetCurrentProcess(),c.byref(count))
    return count.value


def lifecycle():
    receiver.stop()
    initial = handles()
    durations=[]
    for _ in range(40):
        receiver.start()
        with connect() as sock:
            verify_pairing(sock,private,server_ed)
            # Leave a partial request in flight during shutdown.
            sock.sendall(b'POST /partial RTSP/1.0\r\nCSeq: 1\r\nContent-Length: 1024\r\n\r\nx')
            begin=time.monotonic()
            receiver.stop()
            durations.append(time.monotonic()-begin)
    final = handles()
    assert max(durations)<4 and final-initial<=8,(durations,initial,final)
    receiver.start()
    return {'restart_cycles':40,'worst_stop_seconds':round(max(durations),3),
            'handle_delta':final-initial,'saved_pairing_survives_all_cycles':True}


def rapid_start_stop():
    receiver.stop()
    initial = handles()
    durations=[]
    for _ in range(30):
        begin=time.monotonic()
        result=receiver.dll.clearcast_receiver_start_v2(str(receiver.state).encode(),
            b'1920x1080',30,1,0,b'0001',receiver.hwnd,0)
        assert result==0,result
        # Stop before startup has reported Ready, then repeat an idle Stop.
        receiver.stop()
        receiver.stop()
        durations.append(time.monotonic()-begin)
    final=handles()
    assert max(durations)<4 and final-initial<=8,(durations,initial,final)
    receiver.start()
    assert receiver.dll.clearcast_receiver_start_v2(str(receiver.state).encode(),
        b'1920x1080',30,1,0,b'0001',receiver.hwnd,0)==170
    errors=[]
    def stop_once():
        try:
            receiver.stop()
        except Exception as error:
            errors.append(repr(error))
    callers=[threading.Thread(target=stop_once) for _ in range(4)]
    for caller in callers:
        caller.start()
    deadline=time.monotonic()+5
    message=w.MSG()
    while any(caller.is_alive() for caller in callers) and time.monotonic()<deadline:
        # Deliver synchronous HWND messages if teardown targets this thread.
        receiver.user32.PeekMessageW(c.byref(message),None,0,0,0)
        time.sleep(.005)
    assert not errors and not any(caller.is_alive() for caller in callers),errors
    for caller in callers:
        caller.join()
    receiver.start()
    health()
    return {'immediate_stop_cycles':30,'repeated_idle_stop_calls':30,
            'concurrent_stop_callers':4,'active_start_rejected_as_busy':True,
            'worst_cycle_seconds':round(max(durations),3),'handle_delta':final-initial}


def read_error_preserves_identity():
    receiver.stop()
    before = hashlib.sha256((receiver.state/'receiver.pem').read_bytes()).hexdigest()
    kernel = c.WinDLL('kernel32',use_last_error=True)
    kernel.CreateFileW.argtypes = [w.LPCWSTR,w.DWORD,w.DWORD,w.LPVOID,w.DWORD,w.DWORD,w.HANDLE]
    kernel.CreateFileW.restype = w.HANDLE
    kernel.CloseHandle.argtypes=[w.HANDLE]
    handle = kernel.CreateFileW(str(receiver.state/'clients.txt'),0x80000000,0,None,3,0,None)
    assert handle != w.HANDLE(-1).value
    try:
        receiver.start(expect_error=30)
        receiver.stop()
        assert hashlib.sha256((receiver.state/'receiver.pem').read_bytes()).hexdigest()==before
    finally:
        kernel.CloseHandle(handle)
    receiver.start()
    health()
    return {'locked_pairing_file_fails_without_identity_replacement':True}


try:
    run('invalid_native_arguments',invalid_abi)
    receiver.start()
    run('full_pairing_case_insensitive_headers_and_metadata_dedup',pairing_and_metadata)
    run('malformed_verification',malformed_verification)
    run('media_authorization_and_tampered_signature',media_requires_auth)
    run('routine_keepalive_disk_io',keepalive_io)
    run('diagnostic_privacy',private_diagnostics)
    run('bounded_log_rotation',rotation)
    run('malformed_network_frames',bad_network_frames)
    run('slow_partial_request_deadline',slow_partial_requests)
    run('nonreading_client_does_not_block_other_clients',blocked_reader)
    run('pin_expiry_and_idle_cpu',expired_pin_and_idle_cpu)
    run('pin_failure_cooldown',pin_cooldown)
    run('restart_and_pending_request_shutdown',lifecycle)
    run('rapid_start_stop_and_idempotent_stop',rapid_start_stop)
    run('pairing_read_error_preserves_identity',read_error_preserves_identity)
finally:
    receiver.close()
    results['seconds']=round(time.monotonic()-started,3)
    results['passed']=bool(results['cases']) and all(case['passed'] for case in results['cases'])
    destination=pathlib.Path(os.environ.get('CLEARCAST_QA_REPORT',ROOT/'.runtime/qa-native-security-results.json'))
    destination.write_text(json.dumps(results,indent=2),encoding='utf-8')
    print('Report:',destination,flush=True)
