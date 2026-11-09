import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest import mock

sys.dont_write_bytecode = True
AGENT_PATH = Path(__file__).resolve().parents[1] / "LogRAM" / "Remote" / "ssh_agent.py"
spec = importlib.util.spec_from_file_location("ssh_agent", AGENT_PATH)
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class AgentTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.path = Path(self.temp.name) / "日志 ' $test.log"
        self.agent = worker.Agent()
        self.cancel = threading.Event()

    def tearDown(self):
        self.agent.close()
        self.temp.cleanup()

    def open(self, content, encoding="utf8"):
        self.path.write_bytes(content)
        return self.agent.open(dict(path=str(self.path), encoding=encoding), self.cancel)

    def search(self, **arguments):
        return self.agent.search(arguments, self.cancel, lambda _: None)

    def test_utf8_bom_crlf_paging_and_empty_log(self):
        content = b"\xef\xbb\xbfINFO\r\n" + "中文 ERROR\nlast".encode()
        metadata = self.open(content)
        self.assertEqual(3, metadata["lineCount"])
        page = self.agent.read(dict(startLine=1, maxLines=2), self.cancel)
        self.assertEqual(["INFO", "中文 ERROR"], [line["text"] for line in page["lines"]])
        self.assertEqual(len(content) - 4, page["nextOffset"])
        self.assertEqual(content, self.path.read_bytes())
        self.agent.close()
        self.open(b"")
        self.assertEqual([], self.agent.read(dict(startLine=1), self.cancel)["lines"])

    def test_search_modes_gbk_and_range(self):
        self.open("ERROR 1\nerror retry\n中文错误\nwarning\n".encode("gbk"), "gbk")
        self.assertEqual(2, self.search(pattern="error")["matchCount"])
        self.assertEqual(1, self.search(pattern="error", caseSensitive=True)["matchCount"])
        self.assertEqual(1, self.search(pattern="错误")["matchCount"])
        self.assertEqual(2, self.search(pattern="^(ERROR|warning)", useRegex=True, caseSensitive=True)["matchCount"])
        filtered = self.search(includes=["ERROR", "warning"], excludes=["retry"])
        self.assertEqual([1, 4], [result["lineNumber"] for result in filtered["results"]])
        self.assertEqual(1, self.search(pattern="error", startLine=2)["matchCount"])
        with self.assertRaises(Exception):
            self.search(pattern="[", useRegex=True)

    def test_append_partial_line_newline_and_rotation(self):
        self.open(b"one\npart")
        with self.path.open("ab") as output:
            output.write(b"ial\nthree\n")
        refresh = self.agent.refresh({}, self.cancel)
        self.assertEqual((2, 3, False), (refresh["oldLineCount"], refresh["newLineCount"], refresh["isTruncated"]))
        self.assertEqual(["one", "partial", "three"], [line["text"] for line in self.agent.read(dict(startLine=1), self.cancel)["lines"]])
        with self.path.open("ab") as output:
            output.write(b"four\n")
        self.assertEqual(4, self.agent.refresh({}, self.cancel)["newLineCount"])
        self.agent.close()
        self.path.rename(self.path.with_suffix(".old"))
        self.path.write_bytes(b"replacement")
        self.agent.open(dict(path=str(self.path)), self.cancel)
        self.path.write_bytes(b"x")
        self.assertTrue(self.agent.refresh({}, self.cancel)["isTruncated"])

    def test_result_and_byte_limits(self):
        self.open(b"error\n" * 2005)
        response = self.search(pattern="error")
        self.assertEqual(2005, response["matchCount"])
        self.assertEqual(1000, len(response["results"]))
        self.assertEqual(0, len(self.search(pattern="error", maxResults=0)["results"]))
        self.agent.close()
        self.open((b"error " + b"x" * 20000 + b"\n") * 150)
        response = self.search(pattern="error")
        self.assertEqual(150, response["matchCount"])
        self.assertLess(len(json.dumps(response)), worker.MAX_REPLY_BYTES + 10000)
        self.assertIn("[truncated]", response["results"][0]["text"])
        page = self.agent.read(dict(startLine=1, maxLines=500), self.cancel)
        self.assertLess(len(json.dumps(page)), worker.MAX_REPLY_BYTES + 10000)

    def test_directory_and_cancellation_validation(self):
        self.open(b"error\n")
        directory = self.agent.list_directory(dict(path=self.temp.name), self.cancel)
        self.assertEqual(self.path.name, directory["entries"][0]["name"])
        with self.assertRaises(ValueError):
            self.agent.read(dict(maxLines=0), self.cancel)
        with self.assertRaises((ValueError, OSError)):
            self.agent.open(dict(path=self.temp.name), self.cancel)
        self.cancel.set()
        with self.assertRaises(worker.Cancelled):
            self.search(pattern="error")

    def test_directory_prefix_and_match_limit(self):
        root = Path(self.temp.name)
        (root / "b.log").touch()
        (root / "a").mkdir()
        for name in ("a.log", "alpha.log", "another.log"):
            (root / name).touch()
        response = self.agent.list_directory(dict(path=str(root / "a"), filter=True), self.cancel)
        self.assertEqual("a", response["prefix"])
        self.assertEqual(["a", "a.log", "alpha.log", "another.log"], [entry["name"] for entry in response["entries"]])
        self.assertEqual([], self.agent.list_directory(dict(path=str(root / "missing"), filter=True), self.cancel)["entries"])
        self.assertEqual(5, len(self.agent.list_directory(dict(path=str(root) + os.sep, filter=True), self.cancel)["entries"]))
        with mock.patch.object(worker, "MAX_RESULTS", 2):
            response = self.agent.list_directory(dict(path=str(root / "a"), filter=True), self.cancel)
        self.assertEqual(2, len(response["entries"]))
        self.assertTrue(response["limited"])
        self.assertTrue(all(entry["name"].startswith("a") for entry in response["entries"]))

    def test_memory_stats_are_measured(self):
        memory = worker.memory_status()
        self.assertGreaterEqual(memory["availableBytes"], 0)
        self.assertGreater(memory["processBytes"], 0)

    def test_container_limits_include_ancestors(self):
        files = {
            "/proc/self/cgroup": "0::/parent/child\n",
            "/sys/fs/cgroup/parent/child/memory.max": "900",
            "/sys/fs/cgroup/parent/child/memory.current": "100",
            "/sys/fs/cgroup/parent/memory.max": "700",
            "/sys/fs/cgroup/parent/memory.current": "200",
            "/sys/fs/cgroup/memory.max": "max",
        }

        def read(path, *args, **kwargs):
            if path not in files:
                raise FileNotFoundError(path)
            return io.StringIO(files[path])

        with mock.patch("builtins.open", side_effect=read):
            self.assertEqual(500, worker.cgroup_available(1000))
            self.assertEqual(300, worker.cgroup_available(300))
            files["/sys/fs/cgroup/parent/memory.current"] = "800"
            self.assertEqual(0, worker.cgroup_available(1000))
            files.clear()
            files.update({
                "/proc/self/cgroup": "2:memory:/worker\n",
                "/sys/fs/cgroup/memory/worker/memory.limit_in_bytes": "600",
                "/sys/fs/cgroup/memory/worker/memory.usage_in_bytes": "250",
            })
            self.assertEqual(350, worker.cgroup_available(1000))

    def test_memory_rejection_precedes_index_allocation_and_keeps_old_log(self):
        self.open(b"existing\n")
        original_file = self.agent.file
        other = Path(self.temp.name) / "large-index.log"
        for content in (b"\n" * 500, b"x" * 300):
            other.write_bytes(content)
            with mock.patch.object(worker, "memory_status", return_value=dict(availableBytes=4096)), \
                    mock.patch.object(worker, "WORKING_BYTES", 512), \
                    mock.patch.object(worker, "MEMORY_RESERVE_BYTES", 0), \
                    mock.patch.object(worker.array, "array", wraps=worker.array.array) as allocate:
                with self.assertRaisesRegex(MemoryError, "Remote memory insufficient"):
                    self.agent.open(dict(path=str(other)), self.cancel)
                allocate.assert_not_called()
            self.assertIs(original_file, self.agent.file)
            self.assertEqual("existing", self.agent.read(dict(startLine=1), self.cancel)["lines"][0]["text"])
        with mock.patch.object(worker, "memory_status", side_effect=OSError("unavailable")):
            with self.assertRaisesRegex(OSError, "operation refused"):
                self.agent.open(dict(path=str(other)), self.cancel)

    def test_append_memory_rejection_preserves_index(self):
        self.open(b"x\n" * 100)
        previous = self.agent.starts.tobytes()
        old_size = self.agent.size
        with self.path.open("ab") as output:
            output.write(b"x\n" * 300)
        with mock.patch.object(worker, "memory_status", return_value=dict(availableBytes=4096)), \
                mock.patch.object(worker, "WORKING_BYTES", 512), \
                mock.patch.object(worker, "MEMORY_RESERVE_BYTES", 0):
            with self.assertRaises(MemoryError):
                self.agent.refresh({}, self.cancel)
        self.assertEqual(old_size, self.agent.size)
        self.assertEqual(previous, self.agent.starts.tobytes())
        self.assertEqual(400, self.agent.refresh({}, self.cancel)["newLineCount"])

    def test_real_protocol_error_recovery_and_eof_cleanup(self):
        self.open(b"error\n")
        process = subprocess.Popen([sys.executable, "-u", str(AGENT_PATH)], stdin=subprocess.PIPE,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            self.assertEqual(1, json.loads(process.stdout.readline())["ready"])
            for request_id, path, success in [(1, str(self.path) + ".missing", False), (2, str(self.path), True)]:
                request = dict(id=request_id, op="open", arguments=dict(path=path))
                process.stdin.write((json.dumps(request) + "\n").encode())
                process.stdin.flush()
                response = json.loads(process.stdout.readline())
                self.assertEqual(request_id, response["id"])
                self.assertEqual(success, "result" in response)
                if success:
                    self.assertGreater(response["memory"]["processBytes"], 0)
            process.stdin.close()
            self.assertEqual(0, process.wait(timeout=3))
        finally:
            if process.poll() is None:
                process.kill()
                process.wait()
            process.stdout.close()
            process.stderr.close()


if __name__ == "__main__":
    unittest.main()
