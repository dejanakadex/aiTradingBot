"""Read-only N3 report. Python standard library; minute quantiles are NOT merged into a global quantile."""
import argparse
import datetime as dt
import json
import pathlib
import re


def utc(value):
    result = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    if result.tzinfo is None:
        raise ValueError("An explicit UTC offset is required")
    return result.astimezone(dt.timezone.utc)


def analyze(paths, start, end, instrument="FX-CASH-EUR-USD-IDEALPRO"):
    ticks, storage, resources, slow_sql = [], [], [], []
    seen = set()
    for path in paths:
        with path.open(encoding="utf-8-sig") as stream:
            for line in stream:
                if not any(x in line for x in ("Tick latency ", "Dataset storage latency ", "Collection resources ", "Slow database command ", "Slow database transaction ")):
                    continue
                if ("Tick latency " in line or "Collection resources " in line) and f"Instrument={instrument} " not in line:
                    continue
                stamp = re.search(r"AsOfUtc=(\S+)", line)
                if not stamp or not start <= utc(stamp[1]) < end:
                    continue
                # A copied/archive-overlap log line must not double count a minute.
                if line in seen:
                    continue
                seen.add(line)
                if "Slow database command " in line or "Slow database transaction " in line:
                    slow_sql.append(line.split("Slow database ", 1)[1].strip())
                    continue
                metrics = json.loads(line.split("Metrics=", 1)[1])
                entry = {"atUtc": stamp[1], "metrics": metrics}
                if "Tick latency " in line:
                    if metrics.get("Version") == "collection-timing-v2":
                        ticks.append(entry)
                elif "Dataset storage latency " in line:
                    storage.append(entry)
                else:
                    resources.append(entry)
    def maximum(items, *keys):
        values = []
        for item in items:
            value = item["metrics"]
            for key in keys:
                value = (value or {}).get(key)
            if isinstance(value, (int, float)):
                values.append(value)
        return max(values) if values else None
    report = {
        "version": "n3-report-v1", "instrumentId": instrument, "fromUtc": start.isoformat(), "toUtc": end.isoformat(),
        "scope": "Offline log report; window quantiles are approximate upper bounds, not global percentiles. Does not approve N3 or trading.",
        "tickWindows": len(ticks), "events": sum(x["metrics"]["Events"] for x in ticks),
        "failedProcessing": sum(x["metrics"]["FailedProcessing"] for x in ticks),
        "negativeQueueClockDeltas": sum(x["metrics"]["NegativeQueueClockDeltas"] for x in ticks),
        "sourceAgeOverTwoSeconds": sum(x["metrics"]["ReceiveAgeOverTwoSeconds"] for x in ticks),
        "worstWindowPipelineP99UpperMs": maximum(ticks, "Pipeline", "P99UpperMs"),
        "maxPipelineMs": maximum(ticks, "MaxPipelineMs"),
        "worstWindowQueueP99UpperMs": maximum(ticks, "QueueMonotonic", "P99UpperMs"),
        "maxQueueMonotonicMs": maximum(ticks, "QueueMonotonic", "MaxMs"),
        "maxQualityReadMs": maximum(ticks, "Phases", "QualityRead", "MaxMs"),
        "maxQualitySaveMs": maximum(ticks, "Phases", "QualitySave", "MaxMs"),
        "storageWindows": len(storage), "maxFlushMs": maximum(storage, "Flush", "MaxMs"),
        "maxEnqueueToDurableMs": maximum(storage, "EnqueueToDurable", "MaxMs"),
        "storageFailures": sum(x["metrics"]["Failures"] for x in storage),
        "maxProcessCpuPercent": maximum(resources, "CpuPercentOfMachine"),
        "maxWorkingSetBytes": maximum(resources, "WorkingSetBytes"),
        "worstPipelineEvents": [x["metrics"]["WorstPipelineEvent"] for x in ticks if x["metrics"].get("WorstPipelineEvent")],
        "slowDatabaseCommands": slow_sql,
    }
    thresholds = {"worstWindowPipelineP99UpperMs": 50, "maxPipelineMs": 250,
                  "worstWindowQueueP99UpperMs": 100, "maxQueueMonotonicMs": 500,
                  "maxFlushMs": 2000, "maxEnqueueToDurableMs": 7500}
    report["thresholdsMs"] = thresholds
    report["localLatencyThresholdsMet"] = bool(ticks and storage) and all(
        report[k] is not None and report[k] <= limit for k, limit in thresholds.items()
    ) and report["failedProcessing"] == 0 and report["storageFailures"] == 0
    report["minimum60TickWindowsPresent"] = sum(x["metrics"]["Events"] > 0 for x in ticks) >= 60
    report["processIds"] = sorted({x["metrics"]["ProcessId"] for x in ticks if "ProcessId" in x["metrics"]})
    report["remainingChecks"] = ["Gap/drop/reconnect evidence from collection status and SQLite",
                                 "Explain outliers and compare equivalent market/load sessions",
                                 "Source timestamp resolution and Windows clock synchronization",
                                 "Feature/signal parity and repeat after restart"]
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--log", type=pathlib.Path, action="append", required=True)
    parser.add_argument("--from", dest="start", type=utc, required=True)
    parser.add_argument("--to", dest="end", type=utc, required=True)
    parser.add_argument("--instrument-id", default="FX-CASH-EUR-USD-IDEALPRO")
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    if args.start >= args.end:
        parser.error("Range must be positive")
    result = analyze(args.log, args.start, args.end, args.instrument_id)
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(result, output, ensure_ascii=False, indent=2)
        output.write("\n")
    print(json.dumps({k: result[k] for k in ("tickWindows", "events", "maxPipelineMs", "localLatencyThresholdsMet")}))
