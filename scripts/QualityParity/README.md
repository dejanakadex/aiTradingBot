# Offline quality checkpoint parity

Compares durable per-event quality state with coalesced collection checkpoints using the
same verified, bounded quote snapshot and receive clock. Uses two SQLite databases in
memory; does not start the application host, open a broker connection, read an operational
database, approve a profile or send orders.

```powershell
dotnet run --project scripts/QualityParity/QualityParity.csproj -c Release -- C:/repo/TradingBot.Web/appsettings.ForexCollection.json C:/research/frozen-snapshot 2026-11-23T11:45:15Z 2026-11-23T12:55:00Z C:/research/new-parity.json
```

Paths must be absolute, output must not exist, dates must use UTC `Z`, range is at most
one day and selected manifest rows at most 200,000. Freeze the dataset with `fx-snapshot`
first. The tool verifies hashes before and after processing and rejects duplicate event IDs.
Input must have exactly one enabled FX instrument, disabled trading, AnalysisOnly mode,
and an enabled research signal profile with no approval versions. It intentionally refuses
approved profiles: its recording sink does not simulate durable accepted-signal cooldown.

Every quote is compared for quality assessment and checked against its recorded quality
status. Every newly completed aggregate, feature result and signal decision is compared;
the full signal-decision sequence is hashed independently on both sides. Only random
`PipelineContext.CorrelationId` is excluded. Deterministic signal identity, versions,
pattern candidates, scores, rejection reasons, prices and timestamps remain compared.
New features use each bar's availability time, as in the signal service, including when
several bars complete at one quote. Final durable quality states must also match.

The checkpoint clock is held fixed and checkpoints are explicitly flushed once per
reconstructed second. This tests semantic parity, not storage latency or failure recovery.
Separate checkpoint unit tests cover cancellation, write failures, lag bounds and restarts.
`ForexSignalTests.QualityCheckpointsPreserveAcceptedSignalAndDurableCooldownAcrossTriggerRestart`
covers an accepted synthetic signal and persisted cooldown with real isolated SQLite files.

This is receive-ordered offline reconstruction, not observed live timer/model/order parity.
Nonzero warm feature and pattern evaluation counts are required. Zero accepted signals
are expected for the unchanged, unapproved collection profile. A mismatch throws and no
successful report is created. The tool's build is checked in CI; real snapshot results
are recorded separately in `docs/evidence` with their input and output hashes.
