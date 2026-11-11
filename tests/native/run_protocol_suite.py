"""Run isolated receiver protocols serially, or the separate no-port parser phase.

The report binds results to the complete runtime and Python fixture sources.
Only this runner's child process tree is stopped after a timeout/interruption.
SPDX-License-Identifier: GPL-3.0-or-later
"""
from pathlib import Path
import argparse
import contextlib
import hashlib
import json
import os
import socket
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
TESTS = ROOT / "tests/native"


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def cases(phase, runtime):
    # The parser fixture never opens a receiver; keep it out of the port phase.
    if phase == "parser":
        return [("libplist", "libplist_security_test.py", ["--library", str(runtime / "libplist-2.0.dll")],
                 "--output", None)]
    return [
        ("native-security", "receiver_security_test.py", [], None, "CLEARCAST_QA_REPORT"),
        ("pairing-reconnect", "pairing_reconnect_test.py", [], None, None),
        ("pairing-feedback", "pairing_feedback_test.py", [], None, "CLEARCAST_PAIRING_FEEDBACK_REPORT"),
        ("first-pin", "first_pin_retry_test.py", [], None, "CLEARCAST_FIRST_PIN_REPORT"),
        ("plugin-scanner", "plugin_scanner_lifecycle_test.py", [], None, "CLEARCAST_SCANNER_REPORT"),
        ("media-metadata", "media_metadata_protocol_test.py", [], None, "CLEARCAST_MEDIA_PROTOCOL_REPORT"),
        ("direct-video", "direct_video_protocol_test.py", ["--runtime", str(runtime)], "--report", None),
        ("discovery", "mdns_discovery_test.py", [], "--report", None),
        ("network-scope", "network_scope_protocol_test.py", [], "--report", None),
    ]


def fingerprint(runtime):
    files = [{"relativePath": path.relative_to(runtime).as_posix(), "sha256": sha(path)}
             for path in sorted(runtime.rglob("*")) if path.is_file()]
    scripts = [{"relativePath": path.relative_to(ROOT).as_posix(), "sha256": sha(path)}
               for path in sorted(TESTS.glob("*.py"))]
    value = {"runtime": str(runtime), "files": files, "pythonFixtures": scripts}
    value["sha256"] = hashlib.sha256(json.dumps(value, sort_keys=True).encode()).hexdigest()
    return value


def check_ports():
    for port in (47000, 47001, 47002):
        with socket.socket() as probe:
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
            try:
                probe.bind(("0.0.0.0", port))
            except OSError as error:
                raise RuntimeError(f"TCP {port} is occupied; stop the other receiver before running this suite.") from error


@contextlib.contextmanager
def receiver_slot(enabled):
    if not enabled:
        yield
        return
    import msvcrt
    with (ROOT / ".runtime/native-protocol-runner.lock").open("a+b") as lock:
        lock.seek(0)
        if not lock.read(1):
            lock.write(b"0")
            lock.flush()
        lock.seek(0)
        try:
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as error:
            raise RuntimeError("Another receiver protocol suite owns the test slot.") from error
        try:
            yield
        finally:
            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)


def stop_child(process):
    if process.poll() is None:
        taskkill = Path(os.environ["SystemRoot"]) / "System32/taskkill.exe"
        subprocess.run([str(taskkill), "/PID", str(process.pid), "/T", "/F"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                       timeout=30, creationflags=subprocess.CREATE_NO_WINDOW, check=False)
        if process.poll() is None:
            process.kill()
        process.wait(timeout=30)


def report_passed(path):
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"))
        return value.get("passed") is True or value.get("status") == "pass"
    except (OSError, ValueError, AttributeError):
        return False


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", type=Path, default=ROOT / ".runtime/receiver")
    parser.add_argument("--report-directory", required=True, type=Path)
    parser.add_argument("--phase", choices=("protocol", "parser"), default="protocol")
    parser.add_argument("--resume", action="store_true", help="Reuse only matching, intact successful evidence.")
    parser.add_argument("--timeout", type=int, default=900, help="Seconds allowed for each child script.")
    parser.add_argument("--list", action="store_true", help="Show the plan without locking or opening receiver ports.")
    args = parser.parse_args()
    runtime = args.runtime.resolve(strict=True)
    output = args.report_directory.resolve()
    if not output.is_relative_to((ROOT / ".runtime").resolve()) or output == (ROOT / ".runtime").resolve():
        parser.error("Use a dedicated report directory inside the repository's .runtime directory.")
    if args.timeout <= 0:
        parser.error("--timeout must be positive.")
    plan = cases(args.phase, runtime)
    if args.list:
        print(json.dumps({"phase": args.phase, "runtime": str(runtime), "cases": [row[0] for row in plan]}, indent=2))
        return 0
    if os.name != "nt":
        parser.error("The real receiver protocol suite requires Windows.")
    required = ["libplist-2.0.dll"] + (["clearcast_receiver.dll", "libcrypto-3-x64.dll"] if args.phase == "protocol" else [])
    if any(not (runtime / name).is_file() for name in required):
        parser.error("The requested controlled runtime is incomplete.")
    identity = fingerprint(runtime)
    summary = output / ("native-" + args.phase + "-suite.json")
    state = {"schemaVersion": 1, "phase": args.phase, "identity": identity,
             "expectedCases": [row[0] for row in plan], "cases": [], "passed": False, "complete": False}
    if args.resume:
        if not summary.is_file():
            parser.error("--resume requires an existing suite summary.")
        state = json.loads(summary.read_text(encoding="utf-8-sig"))
        if (state.get("schemaVersion") != 1 or state.get("phase") != args.phase or
                state.get("identity") != identity or state.get("expectedCases") != [row[0] for row in plan]):
            parser.error("Runtime/fixtures/plan changed; use a new report directory for a complete rerun.")
    elif output.exists() and any(output.iterdir()):
        parser.error("Use an empty report directory or --resume; prior evidence is never overwritten.")
    output.mkdir(parents=True, exist_ok=True)

    def intact(item):
        evidence = [item["log"]] + ([item["report"]] if item.get("report") else [])
        return all((output / record["path"]).is_file() and sha(output / record["path"]) == record["sha256"]
                   for record in evidence)

    def save():
        state["complete"] = len(state["cases"]) == len(plan)
        state["passed"] = state["complete"] and state.get("integrityVerified", False) and all(row["passed"] for row in state["cases"])
        summary.write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8")

    environment = dict(os.environ, CLEARCAST_PROBE_RUNTIME=str(runtime), PYTHONUTF8="1")
    environment.pop("PYTHONOPTIMIZE", None)  # Protocol assertions must execute.
    state["integrityVerified"] = False
    save()
    with receiver_slot(args.phase == "protocol"):
        for name, script, arguments, report_option, report_env in plan:
            previous = next((row for row in state["cases"] if row["name"] == name), None)
            if previous and previous["passed"]:
                if not intact(previous):
                    raise RuntimeError(f"Saved evidence changed: {name}. Use a new report directory.")
                print(json.dumps({"name": name, "passed": True, "reused": True}), flush=True)
                continue
            if args.phase == "protocol":
                check_ports()
            # A killed runner may have created a log before saving its result.
            # Preserve that orphaned evidence and choose a new attempt filename.
            attempt = max((int(path.stem.rsplit("-", 1)[1]) for path in output.glob(f"{name}-attempt-*.log")), default=0) + 1
            log_path = output / f"{name}-attempt-{attempt}.log"
            report_path = output / f"{name}-attempt-{attempt}.json" if report_option or report_env else None
            command = [sys.executable, "-X", "utf8", str(TESTS / script)] + arguments
            child_environment = dict(environment)
            if report_option:
                command += [report_option, str(report_path)]
            if report_env:
                child_environment[report_env] = str(report_path)
            started = time.monotonic()
            failure = None
            code = None
            process = None
            with log_path.open("x", encoding="utf-8") as log:
                try:
                    process = subprocess.Popen(command, cwd=ROOT, env=child_environment, stdout=log,
                                               stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
                    code = process.wait(timeout=args.timeout)
                except (subprocess.TimeoutExpired, KeyboardInterrupt) as error:
                    if process is not None:
                        stop_child(process)
                        code = process.returncode
                    failure = type(error).__name__
                except OSError as error:
                    failure = f"{type(error).__name__}: {error}"
            passed = failure is None and code == 0 and (report_passed(report_path) if report_path else
                "PASS: identity renewal;" in log_path.read_text(encoding="utf-8"))
            item = {"name": name, "passed": passed, "exitCode": code, "seconds": round(time.monotonic() - started, 3),
                    "log": {"path": log_path.name, "sha256": sha(log_path)}, "error": failure}
            if report_path and report_path.is_file():
                item["report"] = {"path": report_path.name, "sha256": sha(report_path)}
            item["attempts"] = (previous.get("attempts", []) if previous else []) + [{k: v for k, v in item.items() if k != "attempts"}]
            state["cases"] = [row for row in state["cases"] if row["name"] != name] + [item]
            save()
            print(json.dumps({k: item[k] for k in ("name", "passed", "exitCode", "seconds", "error")}), flush=True)
            if not passed:
                return 1
    state["integrityVerified"] = fingerprint(runtime) == identity and all(intact(item) for item in state["cases"])
    save()
    return 0 if state["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
