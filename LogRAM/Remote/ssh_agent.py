"""Read-only Linux log worker. Executed in memory through SSH; never copies logs."""
import array
import json
import os
import posixpath
import queue
import re
import signal
import stat
import sys
import threading
import time

MAX_RESULTS = 1000
MAX_REPLY_BYTES = 1024 * 1024
MAX_LINE_BYTES = 16 * 1024
CHUNK_BYTES = 4 * 1024 * 1024
WORKING_BYTES = 16 * 1024 * 1024
MEMORY_RESERVE_BYTES = 64 * 1024 * 1024
INDEX_ITEM_BYTES = array.array("Q").itemsize


class Cancelled(Exception):
    pass


def check_cancel(event):
    if event.is_set():
        raise Cancelled()


def full_path(path):
    if not isinstance(path, str) or not path or len(path) > 32768:
        raise ValueError("Invalid remote path")
    return os.path.abspath(os.path.expanduser(path))


def decode(raw, encoding, first=False):
    codec = "gbk" if encoding == "gbk" else ("utf-8-sig" if first else "utf-8")
    return raw.rstrip(b"\r\n").decode(codec, errors="replace")


def read_kib(path):
    values = {}
    with open(path) as source:
        for line in source:
            parts = line.split()
            if len(parts) == 3 and parts[2] == "kB":
                values[parts[0].rstrip(":")] = int(parts[1]) * 1024
    return values


def cgroup_available(available):
    try:
        with open("/proc/self/cgroup") as source:
            groups = source.readlines()
    except FileNotFoundError:
        return available
    for line in groups:
        _, controllers, group = line.strip().split(":", 2)
        if not controllers:
            root, limit_name, usage_name = "/sys/fs/cgroup", "memory.max", "memory.current"
        elif "memory" in controllers.split(","):
            root, limit_name, usage_name = "/sys/fs/cgroup/memory", "memory.limit_in_bytes", "memory.usage_in_bytes"
        else:
            continue
        directory = posixpath.normpath(root + "/" + group.lstrip("/"))
        if posixpath.commonpath([directory, root]) != root:
            directory = root
        while True:
            try:
                with open(posixpath.join(directory, limit_name)) as source:
                    limit = source.read().strip()
            except FileNotFoundError:
                limit = "max"
            if limit != "max":
                with open(posixpath.join(directory, usage_name)) as source:
                    used = int(source.read().strip())
                available = min(available, max(0, int(limit) - used))
            if directory == root:
                break
            directory = posixpath.dirname(directory)
    return available


def memory_status():
    if sys.platform == "win32":
        # Keep the same worker testable against the Windows client's local protocol fixtures.
        import ctypes
        from ctypes import wintypes

        class MemoryStatus(ctypes.Structure):
            _fields_ = [("length", wintypes.DWORD), ("load", wintypes.DWORD)] + [
                (name, ctypes.c_ulonglong) for name in ("total", "available", "page_total", "page_available", "virtual_total", "virtual_available", "extended")]

        class ProcessMemory(ctypes.Structure):
            _fields_ = [("length", wintypes.DWORD), ("faults", wintypes.DWORD)] + [
                (name, ctypes.c_size_t) for name in ("peak_rss", "rss", "peak_paged", "paged", "peak_nonpaged", "nonpaged", "pagefile", "peak_pagefile")]

        system, process = MemoryStatus(), ProcessMemory()
        system.length, process.length = ctypes.sizeof(system), ctypes.sizeof(process)
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        get_memory = ctypes.WinDLL("psapi", use_last_error=True).GetProcessMemoryInfo
        get_memory.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD]
        if not kernel.GlobalMemoryStatusEx(ctypes.byref(system)) or not get_memory(kernel.GetCurrentProcess(), ctypes.byref(process), process.length):
            raise ctypes.WinError(ctypes.get_last_error())
        return dict(availableBytes=system.available, processBytes=process.rss)
    available = read_kib("/proc/meminfo")["MemAvailable"]
    try:
        rss = read_kib("/proc/self/smaps_rollup")["Rss"]
    except (FileNotFoundError, PermissionError):
        rss = read_kib("/proc/self/status")["VmRSS"]
    return dict(availableBytes=cgroup_available(available), processBytes=rss)


def require_memory(required):
    try:
        available = memory_status()["availableBytes"]
    except (OSError, KeyError, ValueError) as error:
        raise OSError("Cannot determine remote free memory; operation refused: " + str(error)) from error
    budget = max(0, min(available * 4 // 5, available - MEMORY_RESERVE_BYTES))
    if required > budget:
        raise MemoryError("Remote memory insufficient: need at least %.1f MiB extra, %.1f MiB available "
                          "(reserve 20%% or 64 MiB, whichever is larger)" %
                          (required / 1048576, available / 1048576))


def build_index(source, size, event, previous=None, previous_longest=0):
    prefix = max(0, len(previous) - 1) if previous else 0
    start = previous[-1] if previous else 0
    require_memory(WORKING_BYTES)
    source.seek(start)
    offset, line_start, count, longest = start, start, int(size > start), previous_longest
    while offset < size:
        check_cancel(event)
        require_memory(WORKING_BYTES)
        chunk = source.read(min(CHUNK_BYTES, size - offset))
        if not chunk:
            raise OSError("The log was truncated while checking memory; reopen it")
        found = chunk.find(b"\n")
        while found >= 0:
            end = offset + found + 1
            longest = max(longest, end - line_start)
            line_start = end
            count += int(end < size)
            found = chunk.find(b"\n", found + 1)
        offset += len(chunk)
    longest = max(longest, size - line_start)
    count += prefix
    working = WORKING_BYTES + longest * 16
    require_memory(count * INDEX_ITEM_BYTES + working)
    starts = array.array("Q", [0]) * count
    if prefix:
        memoryview(starts)[:prefix] = memoryview(previous)[:prefix]
    position = prefix
    if size > start:
        starts[position] = start
        position += 1
    source.seek(start)
    offset = start
    while offset < size:
        check_cancel(event)
        require_memory(working)
        chunk = source.read(min(CHUNK_BYTES, size - offset))
        if not chunk:
            raise OSError("The log was truncated while indexing; reopen it")
        found = chunk.find(b"\n")
        while found >= 0:
            end = offset + found + 1
            if end < size:
                if position >= count:
                    raise OSError("The log changed while indexing; reopen it")
                starts[position] = end
                position += 1
            found = chunk.find(b"\n", found + 1)
        offset += len(chunk)
    if position != count:
        raise OSError("The log changed while indexing; reopen it")
    return starts, longest


class Agent:
    def __init__(self):
        self.file = None
        self.starts = array.array("Q")
        self.size = 0
        self.longest = 0

    def close(self):
        if self.file is not None:
            self.file.close()
            self.file = None

    def metadata(self):
        return dict(path=self.path, fileSize=self.size, lineCount=len(self.starts))

    def open(self, request, event):
        path = full_path(request["path"])
        descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_NONBLOCK", 0))
        new_file = os.fdopen(descriptor, "rb")
        info = os.fstat(new_file.fileno())
        if not stat.S_ISREG(info.st_mode):
            new_file.close()
            raise ValueError("Only regular log files can be opened")
        encoding = request.get("encoding", "utf8")
        if encoding not in ("utf8", "gbk"):
            new_file.close()
            raise ValueError("Encoding must be utf8 or gbk")
        try:
            starts, longest = build_index(new_file, info.st_size, event)
        except BaseException:
            new_file.close()
            raise
        self.close()
        self.file, self.path, self.encoding = new_file, path, encoding
        self.identity = (info.st_dev, info.st_ino)
        self.starts, self.longest, self.size = starts, longest, info.st_size
        return self.metadata()

    def list_directory(self, request, event):
        query = request.get("path", "~")
        path = full_path(query)
        prefix = ""
        if (request.get("filter") and not query.endswith(("/", os.sep)) and query != "~"
                and path != os.path.dirname(path)) or not os.path.isdir(path):
            path, prefix = os.path.dirname(path), os.path.basename(path)
        entries, limited = [], False
        with os.scandir(path) as directory:
            for entry in directory:
                check_cancel(event)
                if not entry.name.startswith(prefix):
                    continue
                try:
                    info = entry.stat()
                except OSError:
                    continue
                if stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode):
                    if len(entries) >= MAX_RESULTS:
                        limited = True
                        break
                    entries.append(dict(name=entry.name, path=entry.path,
                                        isDirectory=stat.S_ISDIR(info.st_mode), size=info.st_size))
        entries.sort(key=lambda entry: (not entry["isDirectory"], entry["name"].casefold()))
        return dict(path=path, parent=os.path.dirname(path), entries=entries,
                    limited=limited, prefix=prefix)

    def line(self, line_index):
        start = self.starts[line_index]
        end = self.starts[line_index + 1] if line_index + 1 < len(self.starts) else self.size
        self.file.seek(start)
        raw = self.file.read(min(end - start, MAX_LINE_BYTES))
        clipped = end - start > MAX_LINE_BYTES
        text = decode(raw, self.encoding, start == 0) + (" … [truncated]" if clipped else "")
        return dict(lineNumber=line_index + 1, text=text, startOffset=start, nextOffset=end)

    def read(self, request, event):
        count = request.get("maxLines", 100)
        if not isinstance(count, int) or not 1 <= count <= 500:
            raise ValueError("maxLines must be between 1 and 500")
        start = max(0, min(int(request.get("startLine", 1)) - 1, len(self.starts) - 1))
        lines, used = [], 0
        for index in range(start, min(len(self.starts), start + count)):
            check_cancel(event)
            line = self.line(index)
            cost = len(line["text"].encode("utf-8")) + 100
            if lines and used + cost > MAX_REPLY_BYTES:
                break
            lines.append(line)
            used += cost
        return dict(lines=lines, startOffset=lines[0]["startOffset"] if lines else 0,
                    nextOffset=lines[-1]["nextOffset"] if lines else 0)

    def refresh(self, request, event):
        info = os.stat(self.path)
        old_size, old_lines = self.size, len(self.starts)
        truncated = (info.st_dev, info.st_ino) != self.identity or info.st_size < old_size
        if not truncated and info.st_size > old_size:
            starts, longest = build_index(self.file, info.st_size, event, self.starts, self.longest)
            self.starts, self.longest, self.size = starts, longest, info.st_size
        return dict(oldFileSize=old_size, newFileSize=info.st_size, oldLineCount=old_lines,
                    newLineCount=len(self.starts), isTruncated=truncated)

    def search(self, request, event, progress):
        require_memory(WORKING_BYTES + self.longest * 16)
        pattern = request.get("pattern", "")
        includes, excludes = request.get("includes", []), request.get("excludes", [])
        if not pattern and not includes and not excludes:
            raise ValueError("A search condition is required")
        sensitive = request.get("caseSensitive", False)
        regex = re.compile(pattern, 0 if sensitive else re.IGNORECASE) if request.get("useRegex") else None
        if not sensitive and regex is None:
            pattern = pattern.casefold()
        if not sensitive:
            includes = [term.casefold() for term in includes]
            excludes = [term.casefold() for term in excludes]
        first = max(0, int(request.get("startLine", 1)) - 1)
        max_results = request.get("maxResults", MAX_RESULTS)
        if not isinstance(max_results, int) or not 0 <= max_results <= MAX_RESULTS:
            raise ValueError("maxResults must be between 0 and 1000")
        count, results, used, matches = 0, [], 0, False
        last_progress = time.monotonic()
        if first < len(self.starts):
            self.file.seek(self.starts[first])
        for index in range(first, len(self.starts)):
            check_cancel(event)
            start = self.file.tell()
            end = self.starts[index + 1] if index + 1 < len(self.starts) else self.size
            if end - start > self.longest:
                require_memory(WORKING_BYTES + (end - start) * 16)
            raw = self.file.read(end - start)
            if len(raw) != end - start:
                raise OSError("The log changed while searching; refresh it")
            text = decode(raw, self.encoding, start == 0)
            comparable = text if sensitive else text.casefold()
            if regex is not None:
                # Linux signals also interrupt a regex that would otherwise hold the GIL indefinitely.
                if hasattr(signal, "setitimer"):
                    signal.setitimer(signal.ITIMER_REAL, 2)
                try:
                    matches = regex.search(text) is not None
                finally:
                    if hasattr(signal, "setitimer"):
                        signal.setitimer(signal.ITIMER_REAL, 0)
            else:
                matches = not pattern or pattern in comparable
            matches = (matches and (not includes or any(term in comparable for term in includes))
                       and not any(term in comparable for term in excludes))
            if matches:
                count += 1
                # shortcut: return a bounded prefix of matches; add remote result paging if needed.
                if len(results) < max_results and used < MAX_REPLY_BYTES:
                    clipped = len(raw) > MAX_LINE_BYTES
                    displayed = decode(raw[:MAX_LINE_BYTES], self.encoding, start == 0)
                    if clipped:
                        displayed += " … [truncated]"
                    cost = len(displayed.encode("utf-8")) + 100
                    if used + cost <= MAX_REPLY_BYTES:
                        results.append(dict(lineNumber=index + 1, offset=start, text=displayed))
                        used += cost
                    else:
                        used = MAX_REPLY_BYTES
            if time.monotonic() - last_progress >= 0.25:
                require_memory(WORKING_BYTES + self.longest * 16)
                progress(dict(bytesRead=self.file.tell(), totalBytes=self.size, matchCount=count))
                last_progress = time.monotonic()
        progress(dict(bytesRead=self.size, totalBytes=self.size, matchCount=count))
        return dict(matchCount=count, results=results, lastLineMatched=matches)

    def dispatch(self, request, event, progress):
        operation = request.get("op")
        request = request.get("arguments", request)
        if operation == "memory":
            return {}
        if operation == "list":
            return self.list_directory(request, event)
        if operation == "open":
            return self.open(request, event)
        if self.file is None:
            raise ValueError("Open a log first")
        if operation == "read":
            return self.read(request, event)
        if operation == "refresh":
            return self.refresh(request, event)
        if operation == "search":
            return self.search(request, event, progress)
        raise ValueError("Unknown operation")


def serve():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    pending, events = queue.Queue(), {}

    def emit(value):
        print(json.dumps(value, ensure_ascii=False, separators=(",", ":")), flush=True)

    def receive():
        try:
            for raw in sys.stdin.buffer:
                request = json.loads(raw)
                request_id = request["id"]
                if request.get("op") == "cancel":
                    event = events.get(request_id)
                    if event is not None:
                        event.set()
                else:
                    event = threading.Event()
                    events[request_id] = event
                    pending.put((request, event))
        finally:
            for event in list(events.values()):
                event.set()
            pending.put(None)

    def regex_timeout(signum, frame):
        raise TimeoutError("Regex exceeded the 2 second per-line limit")

    if hasattr(signal, "SIGALRM"):
        signal.signal(signal.SIGALRM, regex_timeout)
    agent = Agent()
    emit(dict(ready=1, home=os.path.expanduser("~")))
    threading.Thread(target=receive, daemon=True).start()
    try:
        while True:
            item = pending.get()
            if item is None:
                break
            request, event = item
            request_id = request["id"]
            try:
                check_cancel(event)
                result = agent.dispatch(request, event,
                                        lambda value: emit(dict(id=request_id, progress=value, memory=memory_status())))
                emit(dict(id=request_id, result=result, memory=memory_status()))
            except Cancelled:
                emit(dict(id=request_id, cancelled=True))
            except Exception as error:
                emit(dict(id=request_id, error=str(error) or "Remote memory exhausted; operation refused"))
            finally:
                events.pop(request_id, None)
    except BrokenPipeError:
        pass
    finally:
        agent.close()


if __name__ == "__main__":
    serve()
