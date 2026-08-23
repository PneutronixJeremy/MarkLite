# Barn Sensor Sync — Offline-First Telemetry Uploader

Sample planning document. Fictional project — exists to exercise Markdown
rendering: task-list checkboxes, status lines, tables, fenced code, blockquotes.

## For Future Agents
Execute **one phase per turn — never more**. Mark checkboxes `- [x]` as items
complete; when a phase is done, set its status to `Complete` and write its
**Phase Summary**. Run the phase's **Verification Plan** and record the result.
Then stop and wait for review before continuing.

> **Note:** this is a rendering fixture. A viewer that renders it well shows
> task lists as real checkboxes, status lines as plain text, and the table
> below as a grid — not a wall of pipes.

### Baseline measurements

| Component        | Idle RAM | Sync RAM | Cold start |
|------------------|---------:|---------:|-----------:|
| Sensor daemon    |    18 MB |    24 MB |     120 ms |
| Upload queue     |     9 MB |    41 MB |      80 ms |
| Conflict resolver|    12 MB |    55 MB |     210 ms |

## Phase 1: Local capture queue
Status: Complete

- [x] Persist sensor readings to SQLite ring buffer (`readings.db`, 7-day cap).
- [x] Add `--drain` CLI flag that flushes the buffer and exits.
- [x] Handle clock skew: reject readings stamped more than 5 minutes in the future.

### Verification Plan
- `sensord --selftest` exits 0 and prints `queue: ok`.
- Kill the daemon mid-write; restart; `PRAGMA integrity_check` returns `ok`.

### Phase Summary
Ring buffer landed with WAL mode enabled. Clock-skew rejection logs to
`skew.log` instead of dropping silently — decided after finding two barns with
drifting RTCs.

## Phase 2: Upload with retry
Status: In progress

- [x] Batch readings into 500-row chunks, gzip, POST to `/api/v2/ingest`.
- [ ] Exponential backoff with jitter (base 2 s, cap 5 min) on 5xx and timeouts.
- [ ] Dead-letter file for chunks rejected with 4xx; never retry those.

Retry loop sketch:

```csharp
while (queue.TryPeek(out Chunk chunk))
{
    HttpResponseMessage response = await client.PostAsync(ingestUri, Compress(chunk));

    if (response.IsSuccessStatusCode)
    {
        queue.Dequeue();
        delay = TimeSpan.FromSeconds(2);
        continue;
    }

    if ((int)response.StatusCode is >= 400 and < 500)
    {
        deadLetter.Write(chunk);
        queue.Dequeue();
        continue;
    }

    await Task.Delay(Jitter(delay));
    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks));
}
```

### Verification Plan
- Point uploader at mock server returning `503` three times then `200`:

  ```powershell
  .\mock-ingest.ps1 -FailCount 3; .\sensord.exe --drain --verbose
  ```

  Expect three `backoff` log lines with increasing delays, then `chunk accepted`.

### Phase Summary
_(write when phase completes)_

## Phase 3: Conflict resolution
Status: Not started

- [ ] Last-writer-wins for scalar readings; log both values when timestamps tie.
- [ ] Merge strategy for cumulative counters (feed dispensed, water flow): sum deltas, never overwrite.
- [ ] Nightly reconciliation report emailed via existing `reportd` — see [reportd docs](https://example.invalid/reportd).

Ordered rollout:

1. Shadow mode — resolve but only log
   1. Barn 3 first (lowest traffic)
   2. Barns 1–2 after one clean week
2. Enforce mode
3. Remove legacy `sync.py`

### Verification Plan
- Replay fixture `conflicts.jsonl` through resolver; output matches `expected.jsonl` byte-for-byte (`fc.exe /b`).

### Phase Summary
_(write when phase completes)_

## Final Recap
_(write when all phases complete)_
