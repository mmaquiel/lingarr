# Parallel Requests for OpenAI-Compatible API (Custom) Translation Service

**Date:** 2026-05-31  
**Status:** Approved

## Overview

Today, subtitle lines are translated strictly sequentially — one HTTP request at a time. This design adds configurable parallelism so that multiple translation requests can be in-flight simultaneously, reducing wall-clock translation time for users running local or remote OpenAI-compatible endpoints that can handle concurrent load.

Parallelism is implemented as a network-layer concern and applies to **all AI translation services** across both the per-line and batch translation paths.

---

## Settings & Configuration

**New setting key** added to `SettingKeys.Translation`:

```csharp
public const string MaxConcurrentRequests = "max_concurrent_requests";
```

- **Default:** `1` (fully serial — identical to current behavior, no opt-in required)
- **Valid range:** 1–10 (enforced by frontend validation)

**Database migration** — a new migration in `Lingarr.Migrations` seeds `max_concurrent_requests` with default value `"1"` alongside existing translation settings.

**Backend** — `TranslationJob` reads `MaxConcurrentRequests` with the rest of the translation settings and passes the parsed integer to `SubtitleTranslationService` as a constructor parameter.

**Frontend** — a "Max concurrent requests" number input is added to `TranslationSettings.vue`, placed near the existing `MaxRetries` / `RetryDelay` fields. Validated as a positive integer in range 1–10.

---

## Core Parallelism — `SubtitleTranslationService`

**Constructor change** — accepts a new `int maxConcurrentRequests` parameter (default `1`). Internally creates:

```csharp
private readonly SemaphoreSlim _semaphore = new(maxConcurrentRequests, maxConcurrentRequests);
```

`initialCount` and `maxCount` are both set to the configured value: start with N slots available, never allow more than N. This prevents a runaway `Release()` from exceeding the intended ceiling.

### Per-Line Path (`TranslateSubtitles`)

The current sequential `for` loop is replaced with a two-phase dispatch-then-drain pattern:

**Dispatch phase** — fire a `Task` for every subtitle that needs translation. Each task is semaphore-gated:

```csharp
await _semaphore.WaitAsync(cancellationToken);
try
{
    return await TranslateSubtitleLine(..., cancellationToken);
}
finally
{
    _semaphore.Release();
}
```

Tasks are stored in a `Dictionary<int, Task<TranslationResult>>` keyed by subtitle index.

**Drain phase** — iterate indices in order, await each task, write `TranslatedLines`, then emit progress:

```csharp
for (var index = 0; index < totalSubtitles; index++)
{
    var result = await tasks[index];      // awaiting an already-completed task returns immediately
    subtitle[index].TranslatedLines = result.Lines;
    await EmitLine(...);
    await EmitProgress(...);
}
```

Because the drain awaits each position in sequence, progress only advances when the next in-order line is ready. Tasks that finished early sit in completed `Task<T>` state and are awaited synchronously with no suspension.

### Batch Path (`TranslateSubtitlesBatch`)

The same `_semaphore` gates each chunk dispatch. All chunk tasks are fired in a dispatch phase (semaphore-gated), then a drain loop awaits them in index order before emitting batch progress. Multiple chunks can be in-flight concurrently up to the configured limit.

### `TranslationResult`

The existing inline tuple `(string Translation, string Service, LanguagePair Pair)` returned by `TranslateSubtitleLine` is promoted to a named private record for use across both dispatch and drain phases:

```csharp
private record TranslationResult(List<string> Lines, string Service, LanguagePair Pair);
```

---

## Retry / Pause Behavior

The semaphore slot is **not released** while a retry backoff is in progress. The slot is released only in the `finally` block after the final attempt completes (success or throw). This means:

- A task in backoff holds its slot, blocking new dispatches until it recovers.
- The window effectively pauses when any slot is in retry backoff.
- This avoids hammering a struggling endpoint with additional requests during a rate-limit event.

---

## Error Handling & Cancellation

**Task failures** — if a task throws after exhausting all retries, the exception surfaces when `await tasks[index]` is reached in the drain loop. The existing `try/catch` in `TranslationJob.Execute` catches this and marks the request as `Failed`. In-flight sibling tasks run to natural completion; their results are never emitted because the drain loop already threw.

**Cancellation** — the `CancellationToken` is passed through to every dispatched task. When the token fires, each in-flight call throws `OperationCanceledException`. The `finally` block in each task always releases the semaphore slot, preventing a deadlock where the drain loop is waiting for a slot that was never returned.

**`maxConcurrentRequests = 1`** — both paths degenerate to fully sequential behavior, identical to today. No behavioral change for users who do not opt in.

---

## Files Affected

| File | Change |
|------|--------|
| `Lingarr.Core/Configuration/SettingKeys.cs` | Add `MaxConcurrentRequests` constant |
| `Lingarr.Migrations/Migrations/M00XX_...cs` | New migration seeding default value `"1"` |
| `Lingarr.Server/Jobs/TranslationJob.cs` | Read setting, pass to `SubtitleTranslationService` |
| `Lingarr.Server/Services/SubtitleTranslationService.cs` | Semaphore field, dispatch-then-drain refactor for both paths |
| `Lingarr.Client/src/components/features/settings/TranslationSettings.vue` | New number input for `max_concurrent_requests` |

---

## Out of Scope

- Per-service concurrency limits (all services share the global setting)
- Parallelism across multiple subtitle files / translation jobs (Hangfire job-level concurrency is unchanged)
- Dynamic backpressure or auto-tuning of the concurrency limit
