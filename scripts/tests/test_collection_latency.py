import importlib.util
import json
import pathlib
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("latency", pathlib.Path(__file__).parents[1] / "analyze-collection-latency.py")
latency = importlib.util.module_from_spec(spec)
spec.loader.exec_module(latency)


class CollectionReportTests(unittest.TestCase):
    def test_empty_or_naive_time_cannot_look_ready(self):
        with self.assertRaises(ValueError):
            latency.utc("2026-11-20T12:00:00")
        result = latency.analyze([], latency.utc("2026-11-20T12:00:00Z"), latency.utc("2026-11-20T13:00:00Z"))
        self.assertFalse(result["localLatencyThresholdsMet"])
        self.assertFalse(result["minimum60TickWindowsPresent"])

    def test_overlap_and_other_instrument_do_not_inflate_evidence(self):
        metrics = {"Version": "collection-timing-v2", "Events": 10, "FailedProcessing": 0,
                   "NegativeQueueClockDeltas": 0, "ReceiveAgeOverTwoSeconds": 0,
                   "Pipeline": {"P99UpperMs": 5}, "MaxPipelineMs": 6,
                   "QueueMonotonic": {"P99UpperMs": 8, "MaxMs": 10}}
        line = "Tick latency Instrument=EURUSD AsOfUtc=2026-11-20T12:01:00Z Metrics=" + json.dumps(metrics) + "\n"
        with tempfile.TemporaryDirectory() as folder:
            path = pathlib.Path(folder) / "log.txt"
            path.write_text(line + line + line.replace("EURUSD", "GBPUSD"), encoding="utf-8")
            result = latency.analyze([path, path], latency.utc("2026-11-20T12:00:00Z"), latency.utc("2026-11-20T13:00:00Z"), "EURUSD")
        self.assertEqual(1, result["tickWindows"])
        self.assertEqual(10, result["events"])
        self.assertEqual(5, result["worstWindowPipelineP99UpperMs"])
        self.assertFalse(result["localLatencyThresholdsMet"])  # No storage evidence.
        self.assertFalse(result["minimum60TickWindowsPresent"])


if __name__ == "__main__":
    unittest.main()
