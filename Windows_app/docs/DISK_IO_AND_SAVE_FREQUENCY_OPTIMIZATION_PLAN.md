# Disk I/O and Save-Frequency Optimization Plan

## 1. Objective

Reduce disk, Windows Explorer, and OneDrive activity during long sessions and scientific tests without reducing the telemetry sampling rate and without silently losing samples.

This plan separates three concepts that must not be confused:

1. **Acquisition frequency:** how often telemetry is received and placed in memory. This remains unchanged (normally one frame every 2 seconds).
2. **File-write frequency:** how often buffered rows are handed to an open file stream.
3. **Durable-checkpoint frequency:** how often buffered data is flushed through the stream and made eligible for persistence by the operating system.

Every valid telemetry frame must still be captured. Optimization is achieved by grouping rows into fewer filesystem operations, not by discarding or downsampling measurements.

## 2. Current behavior and identified cause

The default device telemetry interval is `2000 ms`, approximately 0.5 frames per second (`AppSettings.Connection.DataDelayMs`).

During an active kLa test, every telemetry frame normally produces:

- one appended row in the current run's `dados-brutos.csv`;
- one appended row in the test's `serie-global.csv`.

Power tests have the same basic pattern. Tare and single-point captures also append accepted samples as they arrive.

The shared `BackgroundFileWriter` executes these operations on a background thread, but closes all open append writers whenever its queue becomes momentarily empty. Because frames normally arrive two seconds apart, the queue usually drains after every frame. The result is a repeated open/write/close cycle for each active CSV file. Inside a OneDrive directory, every close and metadata change can also wake OneDrive and Explorer.

The amount of data is small; the high cost is filesystem and synchronization churn.

Session logging is different. Its main TSV writer and servo sidecar stay open, rows are buffered, and both streams currently flush at most once per second. With telemetry every two seconds, this generally means one flush per received frame, but without the repeated open/close behavior of test files.

## 3. Frequency decisions

### 3.1 Active scientific tests: 10-second durable checkpoint

Use a **10-second maximum checkpoint interval** for kLa, power, tare, and single-point raw measurements.

This is the recommended balance because:

- at a two-second acquisition interval, a normal batch contains only about five frames;
- disk and OneDrive notifications fall substantially;
- the maximum uncommitted window during an abrupt process or power failure remains limited;
- run completion, transitions, faults, and shutdown force an immediate checkpoint, so the 10-second exposure applies only to an uncontrolled failure.

A blanket 30-second test interval is not recommended as the default. It would expose approximately 15 unique telemetry frames per stream at the normal acquisition rate. Saving at run completion does not protect an unfinished long run against a power failure, forced termination, disk disconnection, or machine crash.

Thirty seconds may be offered later as an explicit **low-disk-activity mode**, but it should not be the scientific-data default and the UI must state its maximum uncommitted-data window.

### 3.2 Session logs: 60-second durable checkpoint

Use a **60-second maximum checkpoint interval** for the normal session TSV and its servo sidecar.

This is acceptable because the session log is a secondary operational history, while active assay files are the authoritative scientific record. The session logger already keeps its streams open, so this change mainly reduces flush frequency rather than file-open churn.

The logger must still flush immediately when:

- logging is stopped;
- the connection is lost;
- a new session file is started;
- the app begins orderly shutdown;
- a write error is detected;
- the operator explicitly requests an export or backup involving the current session.

### 3.3 Event and state files: immediate, event-driven saves

Keep the following event-driven rather than timer-driven:

- test event journals;
- test manifests;
- condition tables;
- run results and analysis revisions;
- test result summaries;
- tare and torque calibration documents;
- settings changed by the operator.

These writes are infrequent and describe state transitions. Delaying them for 30 or 60 seconds would weaken recovery and can leave raw rows without the metadata needed to interpret them.

### 3.4 Application settings and logs

- Keep `settings.json` at its existing 750 ms debounce after the final change. Settings are not updated by telemetry and are not the disk-pressure source.
- Keep the application log behind its asynchronous sink and daily rolling policy.
- Crash reports remain immediate and failure-only.
- Backup ZIP generation remains manual or explicitly scheduled, never per telemetry frame.

## 4. Target frequency matrix

| File or data group | Acquisition/enqueue | Normal durable checkpoint | Mandatory immediate checkpoint |
|---|---:|---:|---|
| kLa run `dados-brutos.csv` | Every valid frame | 10 s or 64 KB | Run/phase transition, pause, stop, abort, fault, review, disconnect, shutdown |
| kLa `serie-global.csv` | Every active-test frame | 10 s or 64 KB | Same as run data |
| Power run `dados-brutos.csv` | Every valid frame | 10 s or 64 KB | Same as above |
| Power `serie-global.csv` | Every recorded frame | 10 s or 64 KB | Same as above |
| Tare raw CSV | Every accepted sample | 10 s or 64 KB | Point completion, tare completion/cancel/fault, disconnect, shutdown |
| Single-point raw CSV | Every accepted sample | 10 s or 64 KB | Capture completion/cancel/fault, disconnect, shutdown |
| Test `eventos.jsonl` | Every event | Immediate | Always |
| `teste.json` / `ensaio.json` | State changes only | Immediate atomic write | Always |
| `tabela-condicoes.json` | Condition/run status changes | Immediate atomic write | Always |
| `analise.json` and revision files | Analysis completion/revision | Immediate atomic write | Always |
| `resultado.csv` | Run completion/review | Immediate atomic write | Always |
| `resumo-resultados.csv` | Accepted/rejected result changes | Immediate atomic write | Always |
| `tara.json` / calibration JSON | Operator/calibration completion | Immediate atomic write | Always |
| Main session TSV | Every telemetry frame | 60 s | Start/stop, disconnect, export/backup, failure, shutdown |
| Servo session sidecar | Every telemetry frame | 60 s, aligned with main TSV | Same as main TSV |
| `settings.json` | Settings changes only | 750 ms after last change | Shutdown |
| Application `.log` | Log events | Existing async sink | Crash/shutdown sink close |
| Crash report | Unhandled failure only | Immediate | Always |

## 5. Required design changes

### 5.1 Redesign `BackgroundFileWriter` batching

Update `Services/Persistence/BackgroundFileWriter.cs` so it does not close every file as soon as the queue is empty.

Required behavior:

- Keep one append stream per active path.
- Buffer formatted lines by path.
- Drain buffered rows at least every 10 seconds.
- Drain earlier when the accumulated payload for a path reaches 64 KB.
- Flush and close a stream after 30 seconds of inactivity.
- Maintain a single ordered consumer so operations retain enqueue order.
- Preserve ordering between an append and a later atomic rewrite, delete, hash, read barrier, move, or export.
- Never acknowledge a forced checkpoint until all operations queued before it have completed.

The timer must be owned by the writer/consumer, not by the UI. No disk work may return to the dispatcher thread.

### 5.2 Introduce explicit checkpoint semantics

Extend the writer API to distinguish ordinary batching from a durability boundary. Suggested operations:

```csharp
void AppendLine(string path, string line, string? headerIfEmpty = null);
Task CheckpointAsync(string? pathPrefix = null, CancellationToken cancellationToken = default);
Task CloseAsync(string pathPrefix, CancellationToken cancellationToken = default);
```

`CheckpointAsync` must:

1. process every previously queued append in order;
2. flush each applicable `StreamWriter`;
3. flush its underlying `FileStream` using `Flush(flushToDisk: true)` where durable semantics are required;
4. complete only after those operations succeed or report an error.

Ordinary 10-second checkpoints may use normal stream flush initially if benchmarks show `flushToDisk: true` is too expensive on low-end hardware. Mandatory boundaries—run completion, abort, fault, and shutdown—should use durable flush.

### 5.3 Treat one telemetry frame as one logical batch

Add an API that enqueues the run-data row and global-series row together. The consumer must process both consecutively before unrelated work.

If either append fails:

- set the runner's storage-compromised state;
- preserve the rows still held in memory;
- show a persistent operator warning;
- retry the checkpoint with bounded backoff;
- pause or safely stop the test if durable storage cannot be restored;
- never silently continue while claiming the scientific record is complete.

This does not provide a filesystem-level transaction across two CSV files, but it prevents application-level reordering and silent partial enqueue.

### 5.4 Add checkpoints to test lifecycle boundaries

Update `KlaTestRunner` and `PowerTestRunner` to await a checkpoint before declaring these operations complete:

- run successfully completed;
- run sent to review;
- run accepted or rejected;
- test completed;
- test aborted or faulted;
- operator pause/cancel where control returns to the UI;
- connection-loss handling after the safe actuator command is dispatched;
- analysis begins and needs to read the full raw dataset.

The existing final full-file raw-data rewrite in the kLa workflow may remain temporarily as a verification/sealing operation, but it must run only after the append queue is checkpointed. A later phase may replace that redundant rewrite with streaming hash finalization.

### 5.5 Bound memory and apply backpressure

Replace the unbounded channel with a bounded queue sized by bytes and item count. Initial target:

- at least 5 minutes of frames at the configured telemetry rate;
- maximum 16 MB across pending formatted data;
- whichever bound is reached first triggers pressure handling.

Pressure policy:

1. request an immediate checkpoint;
2. display queue depth and oldest-uncommitted age in diagnostics;
3. stop accepting new test transitions while storage is unhealthy;
4. if the queue remains full, safely pause/abort the assay rather than dropping the oldest or newest samples.

Control and actuator safety must remain operational even when storage fails.

### 5.6 Update `SessionLogger`

Change `SessionLogger.FlushInterval` from 1 second to 60 seconds and retain the current open-stream design.

Also add explicit asynchronous checkpoint support so shutdown and disconnect can wait for both the primary and servo files. The two files must be flushed under the same lock and considered one logging checkpoint.

Do not reduce the number of rows written. Every telemetry frame continues to produce one main row and one aligned servo row.

### 5.7 Skip unchanged atomic rewrites

Before replacing a manifest, conditions table, or summary:

- serialize the new content;
- compare a cached content hash or byte-equivalent content with the last successfully saved version;
- skip the temporary-file creation and move when content is unchanged.

Do not use file timestamp alone for this decision.

### 5.8 Separate active acquisition from OneDrive

The preferred deployment architecture is:

1. Record active sessions and assays in a local non-OneDrive acquisition directory.
2. Complete and durably close the run/test.
3. Copy the completed directory to the configured OneDrive workspace using a staging directory.
4. Verify file sizes and SHA-256 hashes.
5. Atomically rename the staged directory into its final location when possible.
6. Retain the local copy until synchronization/export is confirmed or until a configurable retention period expires.

This provides the largest reduction in Explorer and OneDrive activity. Batching alone still causes OneDrive to inspect every live-file update.

The application must not delete the local authoritative copy automatically unless a verified second copy exists.

## 6. Recovery guarantees

### 6.1 What can be guaranteed

The design must guarantee no silent loss during:

- normal stop and app shutdown;
- run/test completion;
- controlled abort and fault handling;
- connection loss while the app remains operational;
- export, backup, analysis, and file reads;
- transient slow disk conditions within the bounded-queue capacity.

### 6.2 Sudden power loss limitation

No 10-, 30-, or 60-second in-memory buffer can guarantee zero loss during an abrupt power failure. With the selected defaults, the theoretical exposure is:

- tests: up to 10 seconds of the current batch;
- sessions: up to 60 seconds of session rows.

The practical exposure may also include operating-system and storage-device caches. A true zero-loss-on-power-failure guarantee would require a durable write for every frame, a transactional local journal/database with an appropriate synchronous mode, and hardware whose write cache is power-loss protected. That conflicts with the goal of minimizing disk activity.

For experiments where even 10 seconds is unacceptable, provide a **High Durability** mode with a 2-second test checkpoint. Do not lower the acquisition frequency.

## 7. Optional modes exposed to the operator

Avoid exposing arbitrary millisecond fields initially. Provide clear presets:

| Mode | Test checkpoint | Session checkpoint | Intended use |
|---|---:|---:|---|
| High Durability | 2 s | 10 s | Critical experiment or unstable PC/power |
| Balanced (default) | 10 s | 60 s | Normal operation |
| Low Disk Activity | 30 s | 60 s | Low-end PC with stable power and local buffering |

Every mode still checkpoints immediately at lifecycle boundaries. The UI should show `Dados de teste ainda não confirmados: N s / N linhas` whenever a test has pending rows.

Do not offer a 60-second test checkpoint as a standard preset. It exposes too much unique assay data for too little additional benefit over 30-second batching.

## 8. Files and code areas to modify

- `src/OpenTECHub/Services/Persistence/BackgroundFileWriter.cs`
  - timed per-path batching, bounded queue, checkpoints, idle close, diagnostics;
- `src/OpenTECHub/Services/KlaTesting/IKlaTestStore.cs`
  - asynchronous checkpoint and logical-frame append contract;
- `src/OpenTECHub/Services/KlaTesting/KlaTestStore.cs`
  - implement the new batching/checkpoint contract;
- `src/OpenTECHub/Services/KlaTesting/KlaTestRunner.cs`
  - lifecycle checkpoints and storage-failure policy;
- `src/OpenTECHub/Services/PowerTesting/IPowerTestStore.cs`
  - asynchronous checkpoint and logical-frame append contract;
- `src/OpenTECHub/Services/PowerTesting/PowerTestStore.cs`
  - implement the new batching/checkpoint contract for runs, tare, and single-point capture;
- `src/OpenTECHub/Services/PowerTesting/PowerTestRunner.cs`
  - lifecycle checkpoints and storage-failure policy;
- `src/OpenTECHub/Services/Telemetry/SessionLogger.cs`
  - 60-second default plus explicit checkpoint API;
- `src/OpenTECHub/Services/Persistence/AppSettings.cs`
  - durability preset and validated interval settings;
- settings UI/view model
  - preset selection, pending-data indication, and storage warning;
- application shutdown path in `App.xaml.cs`
  - await test writer and session logger checkpoints before service disposal;
- workspace/migration and backup services
  - local acquisition staging and verified transfer to OneDrive.

## 9. Implementation phases

### Phase 1: Instrument and establish a baseline

Add counters for:

- telemetry frames received;
- rows enqueued and committed;
- physical file opens/closes;
- batch count and batch byte size;
- normal and durable flush count;
- queue depth in rows and bytes;
- age of oldest uncommitted test and session row;
- write duration and failure count by path category.

Run a 30-minute simulator test using the current writer and record the baseline.

### Phase 2: Implement test batching and checkpoints

Implement the 10-second/64-KB batching behavior, bounded queue, logical-frame enqueue, and lifecycle checkpoints. Keep existing file formats unchanged.

### Phase 3: Adjust session flushing

Move session checkpointing to 60 seconds, add explicit disconnect/shutdown/export checkpoints, and verify main/servo row alignment.

### Phase 4: Add local acquisition staging

Make local non-OneDrive capture the recommended configuration, implement verified transfer, and preserve the local recovery copy.

### Phase 5: Add presets and diagnostics UI

Expose High Durability, Balanced, and Low Disk Activity modes. Show pending-row age and storage health without requiring the operator to understand filesystem internals.

## 10. Verification and acceptance criteria

### Functional integrity

- Every valid simulated telemetry frame appears exactly once in the appropriate raw/global files.
- Row order matches acquisition order.
- Main and servo session logs remain line-aligned.
- All file schemas, headers, decimal formatting, hashes, and existing analysis compatibility remain unchanged.
- Analysis and exports always see every row queued before they start.

### Failure and recovery tests

- Normal stop immediately commits all pending rows.
- Completion cannot be reported before its checkpoint succeeds.
- Disconnect commits pending rows and safely handles actuators.
- Forced process termination loses no already-checkpointed rows and recovery accepts all complete lines.
- A partial trailing line is detected, quarantined/truncated safely, and reported.
- Disk-full and access-denied conditions never silently discard samples.
- Queue saturation produces a visible warning and safe test pause/abort.
- Shutdown waits for writers with a bounded timeout and reports failure if durability cannot be confirmed.

### Performance targets

At the default two-second telemetry interval during a continuous test:

- reduce physical file open/close operations by at least 90%;
- reduce test-file write/flush cycles to approximately six per minute per active file in Balanced mode;
- limit the oldest uncommitted test row to 10 seconds under normal conditions;
- limit the oldest uncommitted session row to 60 seconds;
- keep the UI responsive while OneDrive or an antivirus scanner examines the workspace;
- demonstrate no missing or duplicated rows in an 8-hour simulator run.

## 11. Final recommendation

Adopt these defaults:

- retain every telemetry sample at the current acquisition cadence;
- checkpoint active scientific test data every **10 seconds** or **64 KB**, whichever comes first;
- checkpoint session logs every **60 seconds**;
- immediately checkpoint all files at run completion, phase/state boundaries, pause, abort, fault, disconnect, export/backup, and shutdown;
- keep manifests, events, results, analyses, calibrations, and settings event-driven and immediate;
- record active experiments locally outside OneDrive, then transfer completed and verified files into the synchronized workspace.

Thirty seconds is a valid optional low-disk mode for tests, but not the default. Sixty seconds is suitable for session logs, but too large for authoritative scientific test data.
