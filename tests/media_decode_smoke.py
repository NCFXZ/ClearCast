"""CPU-only decode smoke for a supplied Windows GStreamer runtime.

SPDX-License-Identifier: GPL-3.0-or-later
Generated fixtures and registry are temporary; no receiver/device sinks are used.
"""
import argparse, ctypes as c, hashlib, json, os, pathlib, shutil, subprocess, tempfile, time

def run_smoke(RUNTIME, FFMPEG, OUT):
    gst = None
    cookie = None
    try:
        # Suppress discovery of every other installed plugin, notably d3d11/device sinks.
        os.environ['GST_PLUGIN_SYSTEM_PATH_1_0'] = ''
        os.environ['GST_PLUGIN_PATH_1_0'] = ''
        os.environ['GST_REGISTRY'] = str(OUT / 'registry.bin')
        c.WinDLL('kernel32').SetDefaultDllDirectories(0x1000)
        cookie = os.add_dll_directory(str(RUNTIME))
        gst = c.CDLL(str(RUNTIME / 'libgstreamer-1.0-0.dll'))
        gobj = c.CDLL(str(RUNTIME / 'libgobject-2.0-0.dll'))
        glib = c.CDLL(str(RUNTIME / 'libglib-2.0-0.dll'))
        P = c.c_void_p

        def bind(lib, name, args, result):
            fn = getattr(lib, name); fn.argtypes = args; fn.restype = result; return fn

        class GError(c.Structure):
            _fields_ = [('domain', c.c_uint), ('code', c.c_int), ('message', c.c_char_p)]
        class MiniObject(c.Structure):
            # ABI from the installed GStreamer 1.28.9 gstminiobject.h.
            _fields_ = [('type', c.c_size_t), ('refcount', c.c_int), ('lockstate', c.c_int),
                        ('flags', c.c_uint), ('copy', P), ('dispose', P), ('free', P),
                        ('priv_uint', c.c_uint), ('priv_pointer', P)]
        class MessageHeader(c.Structure):
            _fields_ = [('mini', MiniObject), ('type', c.c_uint)]
        assert c.sizeof(MiniObject) == 64

        version = bind(gst, 'gst_version_string', [], c.c_char_p)
        plugin_version = bind(gst, 'gst_plugin_get_version', [P], c.c_char_p)
        bind(gst, 'gst_registry_fork_set_enabled', [c.c_int], None)(0)
        if not bind(gst, 'gst_init_check', [P, P, c.POINTER(P)], c.c_int)(None, None, None):
            raise RuntimeError('GStreamer initialization failed')
        load_plugin = bind(gst, 'gst_plugin_load_file', [c.c_char_p, c.POINTER(P)], P)
        parse = bind(gst, 'gst_parse_launch', [c.c_char_p, c.POINTER(P)], P)
        state = bind(gst, 'gst_element_set_state', [P, c.c_int], c.c_int)
        get_bus = bind(gst, 'gst_element_get_bus', [P], P)
        pop = bind(gst, 'gst_bus_timed_pop_filtered', [P, c.c_uint64, c.c_uint], P)
        get_sink = bind(gst, 'gst_bin_get_by_name', [P, c.c_char_p], P)
        caps_get = bind(gst, 'gst_pad_get_current_caps', [P], P)
        caps_text = bind(gst, 'gst_caps_to_string', [P], P)
        buffer_size = bind(gst, 'gst_buffer_get_size', [P], c.c_size_t)
        buffer_extract = bind(gst, 'gst_buffer_extract', [P,c.c_size_t,P,c.c_size_t],c.c_size_t)
        parse_error = bind(gst, 'gst_message_parse_error', [P, c.POINTER(P), c.POINTER(P)], None)
        mini_unref = bind(gst, 'gst_mini_object_unref', [P], None)
        obj_unref = bind(gobj, 'g_object_unref', [P], None)
        connect = bind(gobj, 'g_signal_connect_data', [P,c.c_char_p,P,P,P,c.c_int], c.c_ulong)
        free = bind(glib, 'g_free', [P], None)
        error_free = bind(glib, 'g_error_free', [P], None)

        def error_text(pointer):
            if not pointer: return ''
            error = c.cast(pointer, c.POINTER(GError)).contents
            result = error.message.decode('utf-8', 'replace')
            error_free(pointer)
            return result

        plugins = []
        for name in ('coreelements', 'typefindfunctions', 'videoparsersbad', 'libav'):
            path = RUNTIME / 'lib/gstreamer-1.0' / f'libgst{name}.dll'
            error = P()
            plugin = load_plugin(str(path).encode(), c.byref(error))
            if not plugin: raise RuntimeError(error_text(error))
            plugins.append({'path':str(path), 'sha256':hashlib.sha256(path.read_bytes()).hexdigest(), 'version':plugin_version(plugin).decode()})
            obj_unref(plugin)

        def make_clip(codec):
            path = OUT / ('synthetic.' + ('h264' if codec == 'h264' else 'hevc'))
            cmd = [str(FFMPEG), '-hide_banner', '-loglevel', 'error', '-y', '-f','lavfi',
                   '-i','testsrc2=size=320x180:rate=30', '-frames:v','12','-pix_fmt','yuv420p',
                   '-c:v', 'libx264' if codec == 'h264' else 'libx265', '-preset','ultrafast']
            if codec == 'h264': cmd += ['-tune','zerolatency']
            else: cmd += ['-x265-params','log-level=error:pools=1:frame-threads=1']
            cmd += ['-f',codec if codec == 'h264' else 'hevc',str(path)]
            subprocess.run(cmd,check=True,timeout=30,capture_output=True)
            return path

        def decode(codec):
            path = make_clip(codec)
            parser = 'h264parse' if codec == 'h264' else 'h265parse'
            decoder = 'avdec_h264' if codec == 'h264' else 'avdec_h265'
            # An explicit software decoder and fakesink prevent hardware or audio output.
            pipeline_text = f'filesrc location="{path.as_posix()}" ! {parser} ! {decoder} ! fakesink name=qa_sink sync=false signal-handoffs=true'
            error = P(); pipeline = parse(pipeline_text.encode(),c.byref(error))
            if error: raise RuntimeError(error_text(error))
            if not pipeline: raise RuntimeError('No pipeline')
            sink = get_sink(pipeline,b'qa_sink'); bus = get_bus(pipeline)
            samples = []; callback_errors = []
            @c.CFUNCTYPE(None,P,P,P,P)
            def handoff(element, buffer, pad, data):
                try:
                    caps = caps_get(pad); value = caps_text(caps)
                    try: text = c.string_at(value).decode()
                    finally: free(value); mini_unref(caps)
                    size = buffer_size(buffer)
                    raw = c.create_string_buffer(size)
                    copied = buffer_extract(buffer,0,raw,size)
                    if copied != size: raise RuntimeError('Incomplete decoded buffer extraction')
                    samples.append({'bytes':size,'caps':text,'sha256':hashlib.sha256(raw.raw).hexdigest()})
                except Exception as ex: callback_errors.append(str(ex))
            connect(sink,b'handoff',c.cast(handoff,P),None,None,0)
            started = time.monotonic()
            try:
                if state(pipeline,4) == 0: raise RuntimeError('PLAYING state failed')
                message = pop(bus,10_000_000_000,1|2)
                if not message: raise RuntimeError('No EOS or ERROR within 10 seconds')
                try:
                    message_type = c.cast(message,c.POINTER(MessageHeader)).contents.type
                    if message_type == 2:
                        error = P(); debug = P(); parse_error(message,c.byref(error),c.byref(debug))
                        text = error_text(error)
                        if debug: free(debug)
                        raise RuntimeError(text)
                    if message_type != 1: raise RuntimeError(f'Unexpected message type {message_type}')
                finally: mini_unref(message)
                if callback_errors: raise RuntimeError(callback_errors)
                assert len(samples) == 12, f'{codec}: decoded {len(samples)} frames, expected 12'
                assert all('width=(int)320' in s['caps'] and 'height=(int)180' in s['caps'] for s in samples)
                assert all(s['bytes'] == 320*180*3//2 for s in samples)
                unique_frames = len({s['sha256'] for s in samples})
                assert unique_frames > 1, 'Decoded synthetic video has no changing pixel data'
                return {'codec':codec,'status':'pass','frames':len(samples),'width':320,'height':180,
                        'rawBytesPerFrame':samples[0]['bytes'],'caps':samples[0]['caps'],'uniqueDecodedFrames':unique_frames,
                        'elapsedMilliseconds':round((time.monotonic()-started)*1000,2),
                        'encodedBytes':path.stat().st_size,'inputSha256':hashlib.sha256(path.read_bytes()).hexdigest(),
                        'pipeline':pipeline_text}
            finally:
                state(pipeline,1)
                obj_unref(bus);obj_unref(sink);obj_unref(pipeline)

        report = {'runtime':str(RUNTIME),'gstreamerVersion':version().decode(),'plugins':plugins,
                  'scope':'Synthetic CPU software parser/libav decode to fakesink; no receiver ports, hardware decoder, device video sink or audio output.'}
        try:
            report['cases'] = []
            for codec in ('h264','h265'): report['cases'].append(decode(codec))
            report['status'] = 'pass'
        except Exception as ex:
            report['status'] = 'fail';report['failure'] = str(ex)
        return report
    finally:
        if gst is not None:
            try:
                gst.gst_deinit.argtypes = []
                gst.gst_deinit.restype = None
                gst.gst_deinit()
            except Exception:
                pass
        if cookie is not None: cookie.close()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', required=True, type=pathlib.Path, help='Complete portable receiver runtime directory.')
    parser.add_argument('--ffmpeg', default='ffmpeg', help='FFmpeg executable with libx264 and libx265 encoders.')
    parser.add_argument('--output', type=pathlib.Path, help='Optional persistent JSON report.')
    args = parser.parse_args()
    if os.name != 'nt': parser.error('This smoke test requires Windows.')
    runtime = args.runtime.resolve()
    ffmpeg = shutil.which(args.ffmpeg) or (str(pathlib.Path(args.ffmpeg).resolve()) if pathlib.Path(args.ffmpeg).is_file() else None)
    if not ffmpeg: parser.error('FFmpeg was not found.')
    try:
        with tempfile.TemporaryDirectory(prefix='ClearCast-media-smoke-') as temporary:
            report = run_smoke(runtime, pathlib.Path(ffmpeg), pathlib.Path(temporary))
        report['temporaryFixturesRemoved'] = True
    except Exception as error:
        report = {'status':'fail','failure':str(error),'runtime':str(runtime)}
    value = json.dumps(report,ensure_ascii=False,indent=2)
    if args.output:
        args.output.parent.mkdir(parents=True,exist_ok=True)
        args.output.write_text(value,encoding='utf-8')
    print(value)
    return 0 if report['status'] == 'pass' else 1

if __name__ == '__main__': raise SystemExit(main())
