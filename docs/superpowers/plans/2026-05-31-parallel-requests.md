# Parallel Requests Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a globally configurable `max_concurrent_requests` setting that allows multiple subtitle translation HTTP requests to be in-flight simultaneously, reducing wall-clock translation time for both per-line and batch translation paths.

**Architecture:** A `SemaphoreSlim` in `SubtitleTranslationService` gates all outgoing translation calls. The per-line path uses a dispatch-then-drain pattern: all subtitle tasks are fired concurrently (up to the semaphore limit), then awaited in index order for in-order progress emission. The batch path mirrors this for chunk tasks. Default concurrency is `1` (serial — no behaviour change for existing users).

**Tech Stack:** C# / .NET 10, FluentMigrator, Vue 3 / TypeScript

---

## File Map

| File | Change |
|------|--------|
| `Lingarr.Core/Configuration/SettingKeys.cs` | Add `MaxConcurrentRequests` constant |
| `Lingarr.Migrations/Migrations/M0013_SeedMaxConcurrentRequests.cs` | New migration seeding default `"1"` |
| `Lingarr.Server/Jobs/TranslationJob.cs` | Read setting, pass to `SubtitleTranslationService` |
| `Lingarr.Server/Services/SubtitleTranslationService.cs` | Semaphore, `TranslationResult` record, dispatch-then-drain for both paths |
| `Lingarr.Server.Tests/Services/SubtitleTranslationServiceTests.cs` | Tests for parallel per-line and parallel batch paths |
| `Lingarr.Client/src/ts/setting.ts` | Add `MAX_CONCURRENT_REQUESTS` to `SETTINGS` and `ISettings` |
| `Lingarr.Client/src/components/features/settings/TranslationSettings.vue` | Add number input for `max_concurrent_requests` |

---

### Task 1: Add setting key constant

**Files:**
- Modify: `Lingarr.Core/Configuration/SettingKeys.cs`

- [ ] **Step 1: Add the constant**

In `SettingKeys.cs`, inside the `Translation` class, add after the `RetryDelayMultiplier` constant:

```csharp
public const string RetryDelayMultiplier = "retry_delay_multiplier";
public const string MaxConcurrentRequests = "max_concurrent_requests";
```

- [ ] **Step 2: Build to confirm no errors**

```bash
dotnet build Lingarr.Core/Lingarr.Core.csproj
```
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add Lingarr.Core/Configuration/SettingKeys.cs
git commit -m "feat: add MaxConcurrentRequests setting key"
```

---

### Task 2: Database migration

**Files:**
- Create: `Lingarr.Migrations/Migrations/M0013_SeedMaxConcurrentRequests.cs`

- [ ] **Step 1: Create the migration file**

```csharp
using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(13)]
public class M0013_SeedMaxConcurrentRequests : Migration
{
    public override void Up()
    {
        if (!Schema.Table("settings").Column("key").Exists() ||
            Execute.Scalar<int>("SELECT COUNT(*) FROM settings WHERE key = 'max_concurrent_requests'").Equals(0))
        {
            Insert.IntoTable("settings").Row(new { key = "max_concurrent_requests", value = "1" });
        }
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new { key = "max_concurrent_requests" });
    }
}
```

- [ ] **Step 2: Build migrations project**

```bash
dotnet build Lingarr.Migrations/Lingarr.Migrations.csproj
```
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add Lingarr.Migrations/Migrations/M0013_SeedMaxConcurrentRequests.cs
git commit -m "feat: seed max_concurrent_requests setting with default 1"
```

---

### Task 3: Refactor `SubtitleTranslationService` — semaphore, `TranslationResult`, per-line parallel path

**Files:**
- Modify: `Lingarr.Server/Services/SubtitleTranslationService.cs`

This is the largest task. Read the full current file before editing.

- [ ] **Step 1: Write the failing tests first**

Open `Lingarr.Server.Tests/Services/SubtitleTranslationServiceTests.cs`. The existing `CreatePerLineHarness` and `CreateBatchHarness` helpers do not accept a `maxConcurrentRequests` parameter — they will need updating after the constructor change. First add these two new test methods at the end of the `#region TranslateSubtitles Tests` block:

```csharp
[Fact]
public async Task TranslateSubtitles_ParallelConcurrency_TranslatesAllLines()
{
    // Arrange — concurrency 2, 4 subtitles; verifies all lines still get translated
    var translationServiceMock = new Mock<ITranslationService>();
    translationServiceMock
        .Setup(t => t.GetLanguagePair(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((string source, string target, CancellationToken _) =>
            new LanguagePair { Source = source, Target = target, Tier = MatchTier.Exact });
    translationServiceMock
        .Setup(t => t.TranslateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<List<string>?>(), It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((string text, string _, string _, List<string>? _, List<string>? _, CancellationToken _) =>
            text + "_translated");

    var progressServiceMock = new Mock<IProgressService>();
    progressServiceMock.Setup(p => p.Emit(It.IsAny<TranslationRequest>(), It.IsAny<int>())).Returns(Task.CompletedTask);
    progressServiceMock.Setup(p => p.EmitLine(
        It.IsAny<TranslationRequest>(), It.IsAny<int>(), It.IsAny<string>(),
        It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<LanguagePair?>())).Returns(Task.CompletedTask);

    var service = new SubtitleTranslationService(
        [new TranslationServiceEntry("test", translationServiceMock.Object, null)],
        NullLogger.Instance,
        progressServiceMock.Object,
        maxConcurrentRequests: 2);

    var subtitles = new List<SubtitleItem>
    {
        Subtitle(1, "a"), Subtitle(2, "b"), Subtitle(3, "c"), Subtitle(4, "d")
    };

    // Act
    await service.TranslateSubtitles(subtitles, NewRequest(),
        stripSubtitleFormatting: false, preserveLineBreaks: false,
        contextBefore: 0, contextAfter: 0, CancellationToken.None);

    // Assert
    Assert.Equal(["a_translated"], subtitles[0].TranslatedLines);
    Assert.Equal(["b_translated"], subtitles[1].TranslatedLines);
    Assert.Equal(["c_translated"], subtitles[2].TranslatedLines);
    Assert.Equal(["d_translated"], subtitles[3].TranslatedLines);
}

[Fact]
public async Task TranslateSubtitles_ParallelConcurrency_ResultsInSubtitleOrder()
{
    // Arrange — subtitle 0 takes longer; verify drain emits in order regardless
    var callOrder = new List<int>();
    var barrier = new TaskCompletionSource<bool>();

    var translationServiceMock = new Mock<ITranslationService>();
    translationServiceMock
        .Setup(t => t.GetLanguagePair(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((string source, string target, CancellationToken _) =>
            new LanguagePair { Source = source, Target = target, Tier = MatchTier.Exact });
    translationServiceMock
        .Setup(t => t.TranslateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<List<string>?>(), It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((string text, string _, string _, List<string>? _, List<string>? _, CancellationToken _) =>
            text + "_t");

    var emitOrder = new List<int>();
    var progressServiceMock = new Mock<IProgressService>();
    progressServiceMock.Setup(p => p.Emit(It.IsAny<TranslationRequest>(), It.IsAny<int>())).Returns(Task.CompletedTask);
    progressServiceMock
        .Setup(p => p.EmitLine(
            It.IsAny<TranslationRequest>(), It.IsAny<int>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<LanguagePair?>()))
        .Callback((TranslationRequest _, int pos, string _, string _, string? _, LanguagePair? _) =>
            emitOrder.Add(pos))
        .Returns(Task.CompletedTask);

    var service = new SubtitleTranslationService(
        [new TranslationServiceEntry("test", translationServiceMock.Object, null)],
        NullLogger.Instance,
        progressServiceMock.Object,
        maxConcurrentRequests: 3);

    var subtitles = new List<SubtitleItem>
    {
        Subtitle(1, "first"), Subtitle(2, "second"), Subtitle(3, "third")
    };

    // Act
    await service.TranslateSubtitles(subtitles, NewRequest(),
        stripSubtitleFormatting: false, preserveLineBreaks: false,
        contextBefore: 0, contextAfter: 0, CancellationToken.None);

    // Assert — EmitLine fired in subtitle position order
    Assert.Equal([1, 2, 3], emitOrder);
}
```

- [ ] **Step 2: Run new tests to confirm they fail (constructor doesn't accept the parameter yet)**

```bash
dotnet test Lingarr.Server.Tests/Lingarr.Server.Tests.csproj \
  --filter "TranslateSubtitles_ParallelConcurrency" -v n
```
Expected: compile error or `FAILED` — `SubtitleTranslationService` has no `maxConcurrentRequests` parameter.

- [ ] **Step 3: Add `TranslationResult` record and semaphore field to `SubtitleTranslationService`**

At the top of the class body (after `_loggedFallbacks` / `_candidatesByPair` fields), add:

```csharp
private readonly SemaphoreSlim _semaphore;

private record TranslationResult(List<string> Lines, string? Service, LanguagePair? Pair);
```

- [ ] **Step 4: Update the constructor signature and body**

Replace the existing constructor:

```csharp
public SubtitleTranslationService(
    IReadOnlyList<TranslationServiceEntry> services,
    ILogger logger,
    IProgressService? progressService = null)
{
    if (services.Count == 0)
    {
        throw new TranslationException("Subtitle translator could not be initialized, translation services list is empty.");
    }
    _services = services;
    _progressService = progressService;
    _logger = logger;
}
```

With:

```csharp
public SubtitleTranslationService(
    IReadOnlyList<TranslationServiceEntry> services,
    ILogger logger,
    IProgressService? progressService = null,
    int maxConcurrentRequests = 1)
{
    if (services.Count == 0)
    {
        throw new TranslationException("Subtitle translator could not be initialized, translation services list is empty.");
    }
    _services = services;
    _progressService = progressService;
    _logger = logger;
    _semaphore = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
}
```

- [ ] **Step 5: Replace `translationCache` with `ConcurrentDictionary` and refactor `TranslateSubtitles`**

Add `using System.Collections.Concurrent;` at the top of the file.

Replace the `TranslateSubtitles` method body entirely:

```csharp
public async Task<List<SubtitleItem>> TranslateSubtitles(
    List<SubtitleItem> subtitles,
    TranslationRequest translationRequest,
    bool stripSubtitleFormatting,
    bool preserveLineBreaks,
    int contextBefore,
    int contextAfter,
    CancellationToken cancellationToken)
{
    if (_progressService == null)
    {
        throw new TranslationException("Subtitle translator could not be initialized, progress service is null.");
    }

    var totalSubtitles = subtitles.Count;
    var translationCache = new ConcurrentDictionary<string, string>();

    // Dispatch phase — fire all tasks concurrently, semaphore-gated
    var tasks = new Task<TranslationResult>[totalSubtitles];
    for (var index = 0; index < totalSubtitles; index++)
    {
        var subtitle = subtitles[index];

        if (subtitle.TranslatedLines.Count > 0)
        {
            // Populate cache from already-translated entries
            var existingContentLines = stripSubtitleFormatting ? subtitle.PlaintextLines : subtitle.Lines;
            if (!(preserveLineBreaks && existingContentLines.Count > 1))
            {
                var sourceLine = string.Join(" ", existingContentLines);
                if (!string.IsNullOrWhiteSpace(sourceLine))
                {
                    var cacheKey = $"{subtitle.StartTime}|{subtitle.EndTime}|{sourceLine}";
                    translationCache.TryAdd(cacheKey, string.Join(" ", subtitle.TranslatedLines));
                }
            }
            tasks[index] = Task.FromResult(new TranslationResult(subtitle.TranslatedLines, null, null));
            continue;
        }

        var capturedIndex = index;
        var contextLinesBefore = BuildContext(subtitles, capturedIndex, contextBefore, stripSubtitleFormatting, true);
        var contextLinesAfter = BuildContext(subtitles, capturedIndex, contextAfter, stripSubtitleFormatting, false);

        tasks[index] = DispatchSubtitleTask(
            subtitle,
            translationRequest,
            stripSubtitleFormatting,
            preserveLineBreaks,
            contextLinesBefore,
            contextLinesAfter,
            translationCache,
            cancellationToken);
    }

    // Drain phase — await in order to preserve progress emission sequence
    var iteration = 0;
    for (var index = 0; index < totalSubtitles; index++)
    {
        var result = await tasks[index];
        var subtitle = subtitles[index];

        if (subtitle.TranslatedLines.Count == 0)
        {
            subtitle.TranslatedLines = result.Lines;
        }

        if (result.Service != null && result.Pair != null)
        {
            _translationByPosition[subtitle.Position] = (result.Service, result.Pair);
        }

        var contentLines = stripSubtitleFormatting ? subtitle.PlaintextLines : subtitle.Lines;
        var sourceText = string.Join(" ", contentLines);
        var translatedText = string.Join(" ", subtitle.TranslatedLines);

        await _progressService!.EmitLine(
            translationRequest,
            subtitle.Position,
            sourceText,
            translatedText,
            result.Service,
            result.Pair);

        iteration++;
        await EmitProgress(translationRequest, iteration, totalSubtitles);
    }

    _lastProgression = -1;
    return subtitles;
}
```

- [ ] **Step 6: Add `DispatchSubtitleTask` private method**

Add this method after `TranslateSubtitles`:

```csharp
private async Task<TranslationResult> DispatchSubtitleTask(
    SubtitleItem subtitle,
    TranslationRequest translationRequest,
    bool stripSubtitleFormatting,
    bool preserveLineBreaks,
    List<string> contextLinesBefore,
    List<string> contextLinesAfter,
    ConcurrentDictionary<string, string> translationCache,
    CancellationToken cancellationToken)
{
    await _semaphore.WaitAsync(cancellationToken);
    try
    {
        var contentLines = stripSubtitleFormatting ? subtitle.PlaintextLines : subtitle.Lines;
        var subtitleLines = preserveLineBreaks && contentLines.Count > 1
            ? contentLines
            : [string.Join(" ", contentLines)];

        var translatedLines = new List<string>(subtitleLines.Count);
        string? service = null;
        LanguagePair? pair = null;

        foreach (var subtitleLine in subtitleLines)
        {
            if (string.IsNullOrWhiteSpace(subtitleLine))
            {
                translatedLines.Add(subtitleLine);
                continue;
            }

            var cacheKey = $"{subtitle.StartTime}|{subtitle.EndTime}|{subtitleLine}";
            if (translationCache.TryGetValue(cacheKey, out var cachedTranslation))
            {
                translatedLines.Add(cachedTranslation);
                continue;
            }

            var result = await TranslateSubtitleLine(new TranslateAbleSubtitleLine
            {
                SubtitleLine = subtitleLine,
                SourceLanguage = translationRequest.SourceLanguage,
                TargetLanguage = translationRequest.TargetLanguage,
                ContextLinesBefore = contextLinesBefore.Count > 0 ? contextLinesBefore : null,
                ContextLinesAfter = contextLinesAfter.Count > 0 ? contextLinesAfter : null
            }, cancellationToken);

            translationCache[cacheKey] = result.Translation;
            translatedLines.Add(result.Translation);
            service ??= result.Service;
            pair ??= result.Pair;
        }

        var finalLines = translatedLines.Count > 1
            ? translatedLines
            : ToSubtitleLines(translatedLines[0], contentLines.Count, preserveLineBreaks, stripSubtitleFormatting, subtitle.Position);

        return new TranslationResult(finalLines, service, pair);
    }
    finally
    {
        _semaphore.Release();
    }
}
```

- [ ] **Step 7: Run the new parallel tests**

```bash
dotnet test Lingarr.Server.Tests/Lingarr.Server.Tests.csproj \
  --filter "TranslateSubtitles_ParallelConcurrency" -v n
```
Expected: both new tests `PASSED`.

- [ ] **Step 8: Run the full test suite to confirm no regressions**

```bash
dotnet test Lingarr.Server.Tests/Lingarr.Server.Tests.csproj -v n
```
Expected: all tests `PASSED`.

- [ ] **Step 9: Commit**

```bash
git add Lingarr.Server/Services/SubtitleTranslationService.cs \
        Lingarr.Server.Tests/Services/SubtitleTranslationServiceTests.cs
git commit -m "feat: add parallel per-line translation with semaphore dispatch-drain"
```

---

### Task 4: Batch path — parallel chunk dispatch

**Files:**
- Modify: `Lingarr.Server/Services/SubtitleTranslationService.cs`
- Modify: `Lingarr.Server.Tests/Services/SubtitleTranslationServiceTests.cs`

- [ ] **Step 1: Write the failing batch parallel test**

Add to `SubtitleTranslationServiceTests.cs` in the batch tests region:

```csharp
[Fact]
public async Task TranslateSubtitlesBatch_ParallelConcurrency_TranslatesAllChunks()
{
    // Arrange — batchSize=2, 4 subtitles → 2 chunks; concurrency=2 fires both chunks concurrently
    var translationServiceMock = new Mock<ITranslationService>();
    translationServiceMock
        .Setup(t => t.GetLanguagePair(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((string source, string target, CancellationToken _) =>
            new LanguagePair { Source = source, Target = target, Tier = MatchTier.Exact });

    var batchMock = new Mock<IBatchTranslationService>();
    batchMock
        .Setup(b => b.TranslateBatchAsync(
            It.IsAny<List<BatchSubtitleItem>>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((List<BatchSubtitleItem> items, string _, string _, CancellationToken _) =>
            items.ToDictionary(i => i.Position, i => i.Line + "_t"));

    var progressServiceMock = new Mock<IProgressService>();
    progressServiceMock.Setup(p => p.Emit(It.IsAny<TranslationRequest>(), It.IsAny<int>())).Returns(Task.CompletedTask);
    progressServiceMock.Setup(p => p.EmitLines(It.IsAny<TranslationRequest>(), It.IsAny<List<TranslatedLineData>>())).Returns(Task.CompletedTask);

    var service = new SubtitleTranslationService(
        [new TranslationServiceEntry("test", translationServiceMock.Object, batchMock.Object)],
        NullLogger.Instance,
        progressServiceMock.Object,
        maxConcurrentRequests: 2);

    var subtitles = new List<SubtitleItem>
    {
        Subtitle(1, "a"), Subtitle(2, "b"), Subtitle(3, "c"), Subtitle(4, "d")
    };

    // Act
    await service.TranslateSubtitlesBatch(subtitles, NewRequest(),
        stripSubtitleFormatting: false, preserveLineBreaks: false,
        batchSize: 2, CancellationToken.None);

    // Assert
    Assert.Equal(["a_t"], subtitles[0].TranslatedLines);
    Assert.Equal(["b_t"], subtitles[1].TranslatedLines);
    Assert.Equal(["c_t"], subtitles[2].TranslatedLines);
    Assert.Equal(["d_t"], subtitles[3].TranslatedLines);
    batchMock.Verify(b => b.TranslateBatchAsync(
        It.IsAny<List<BatchSubtitleItem>>(),
        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
        Times.Exactly(2));
}
```

- [ ] **Step 2: Run to confirm it fails**

```bash
dotnet test Lingarr.Server.Tests/Lingarr.Server.Tests.csproj \
  --filter "TranslateSubtitlesBatch_ParallelConcurrency" -v n
```
Expected: `FAILED` (constructor parameter not yet wired through to batch path, or the batch loop is still sequential with no issue — the test may actually pass if concurrency=1 is the default; but the `maxConcurrentRequests: 2` constructor argument confirms the parameter now exists so this depends on Task 3 being done first).

- [ ] **Step 3: Refactor `TranslateSubtitlesBatch` to dispatch-then-drain**

Replace the `TranslateSubtitlesBatch` method body entirely:

```csharp
public async Task<List<SubtitleItem>> TranslateSubtitlesBatch(
    List<SubtitleItem> subtitles,
    TranslationRequest translationRequest,
    bool stripSubtitleFormatting,
    bool preserveLineBreaks,
    int batchSize = 0,
    CancellationToken cancellationToken = default)
{
    if (_progressService == null)
    {
        throw new TranslationException("Subtitle translator could not be initialized, progress service is null.");
    }

    if (batchSize <= 0)
    {
        batchSize = subtitles.Count;
    }

    var totalBatches = (int)Math.Ceiling((double)subtitles.Count / batchSize);

    // Dispatch phase — fire all chunk tasks concurrently, semaphore-gated
    var chunkTasks = new Task<List<SubtitleItem>>[totalBatches];
    for (var batchIndex = 0; batchIndex < totalBatches; batchIndex++)
    {
        var currentBatch = subtitles
            .Skip(batchIndex * batchSize)
            .Take(batchSize)
            .ToList();
        chunkTasks[batchIndex] = DispatchBatchChunkTask(
            currentBatch,
            translationRequest.SourceLanguage,
            translationRequest.TargetLanguage,
            stripSubtitleFormatting,
            preserveLineBreaks,
            cancellationToken);
    }

    // Drain phase — await chunks in order, emit progress sequentially
    var processedSubtitles = 0;
    for (var batchIndex = 0; batchIndex < totalBatches; batchIndex++)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            _lastProgression = -1;
            break;
        }

        var newlyTranslated = await chunkTasks[batchIndex];

        if (newlyTranslated.Count > 0)
        {
            var lineData = newlyTranslated.Select(subtitle =>
            {
                _translationByPosition.TryGetValue(subtitle.Position, out var entry);
                return new TranslatedLineData
                {
                    Position = subtitle.Position,
                    Source = string.Join(" ", stripSubtitleFormatting ? subtitle.PlaintextLines : subtitle.Lines),
                    Target = string.Join(" ", subtitle.TranslatedLines),
                    Service = entry.Service,
                    Pair = entry.Pair
                };
            }).ToList();
            await _progressService!.EmitLines(translationRequest, lineData);
        }

        processedSubtitles += Math.Min(batchSize, subtitles.Count - batchIndex * batchSize);
        await EmitProgress(translationRequest, processedSubtitles, subtitles.Count);
    }

    _lastProgression = -1;
    return subtitles;
}
```

- [ ] **Step 4: Add `DispatchBatchChunkTask` private method**

Add after `DispatchSubtitleTask`:

```csharp
private async Task<List<SubtitleItem>> DispatchBatchChunkTask(
    List<SubtitleItem> currentBatch,
    string sourceLanguage,
    string targetLanguage,
    bool stripSubtitleFormatting,
    bool preserveLineBreaks,
    CancellationToken cancellationToken)
{
    await _semaphore.WaitAsync(cancellationToken);
    try
    {
        return await ProcessSubtitleBatch(
            currentBatch,
            sourceLanguage,
            targetLanguage,
            stripSubtitleFormatting,
            preserveLineBreaks,
            cancellationToken);
    }
    finally
    {
        _semaphore.Release();
    }
}
```

- [ ] **Step 5: Run the batch parallel test**

```bash
dotnet test Lingarr.Server.Tests/Lingarr.Server.Tests.csproj \
  --filter "TranslateSubtitlesBatch_ParallelConcurrency" -v n
```
Expected: `PASSED`.

- [ ] **Step 6: Run full test suite**

```bash
dotnet test Lingarr.Server.Tests/Lingarr.Server.Tests.csproj -v n
```
Expected: all tests `PASSED`.

- [ ] **Step 7: Commit**

```bash
git add Lingarr.Server/Services/SubtitleTranslationService.cs \
        Lingarr.Server.Tests/Services/SubtitleTranslationServiceTests.cs
git commit -m "feat: add parallel batch chunk dispatch with semaphore drain"
```

---

### Task 5: Wire setting through `TranslationJob`

**Files:**
- Modify: `Lingarr.Server/Jobs/TranslationJob.cs`

- [ ] **Step 1: Add `MaxConcurrentRequests` to the settings read**

In `TranslationJob.Execute`, add `SettingKeys.Translation.MaxConcurrentRequests` to the `GetSettings` call array:

```csharp
var settings = await _settings.GetSettings([
    SettingKeys.Translation.ServiceType,
    SettingKeys.Translation.FixOverlappingSubtitles,
    SettingKeys.Translation.StripSubtitleFormatting,
    SettingKeys.Translation.PreserveLineBreaks,
    SettingKeys.Translation.AddTranslatorInfo,

    SettingKeys.SubtitleValidation.ValidateSubtitles,
    SettingKeys.SubtitleValidation.MaxFileSizeBytes,
    SettingKeys.SubtitleValidation.MaxSubtitleLength,
    SettingKeys.SubtitleValidation.MinSubtitleLength,
    SettingKeys.SubtitleValidation.MinDurationMs,
    SettingKeys.SubtitleValidation.MaxDurationSecs,

    SettingKeys.Translation.AiContextPromptEnabled,
    SettingKeys.Translation.AiContextBefore,
    SettingKeys.Translation.AiContextAfter,
    SettingKeys.Translation.UseBatchTranslation,
    SettingKeys.Translation.MaxBatchSize,
    SettingKeys.Translation.RemoveLanguageTag,
    SettingKeys.Translation.UseSubtitleTagging,
    SettingKeys.Translation.SubtitleTag,
    SettingKeys.Translation.MaxConcurrentRequests
]);
```

- [ ] **Step 2: Parse the setting and pass it to `SubtitleTranslationService`**

After the existing variable declarations (e.g., after `contextAfter`), add:

```csharp
var maxConcurrentRequests = int.TryParse(settings[SettingKeys.Translation.MaxConcurrentRequests], out var concurrency)
    ? Math.Max(1, concurrency)
    : 1;
```

Then update the `SubtitleTranslationService` constructor call (currently `new SubtitleTranslationService(services, _logger, _progressService)`) to:

```csharp
var translator = new SubtitleTranslationService(services, _logger, _progressService, maxConcurrentRequests);
```

- [ ] **Step 3: Build**

```bash
dotnet build Lingarr.Server/Lingarr.Server.csproj
```
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add Lingarr.Server/Jobs/TranslationJob.cs
git commit -m "feat: read max_concurrent_requests and pass to SubtitleTranslationService"
```

---

### Task 6: Frontend — setting constant and type

**Files:**
- Modify: `Lingarr.Client/src/ts/setting.ts`

- [ ] **Step 1: Add `MAX_CONCURRENT_REQUESTS` to the `SETTINGS` object**

In `setting.ts`, add after `RETRY_DELAY_MULTIPLIER`:

```typescript
RETRY_DELAY_MULTIPLIER: 'retry_delay_multiplier',
MAX_CONCURRENT_REQUESTS: 'max_concurrent_requests',
```

- [ ] **Step 2: Add the property to `ISettings`**

In the `ISettings` interface, add after `retry_delay_multiplier`:

```typescript
retry_delay_multiplier: string
max_concurrent_requests: string
```

- [ ] **Step 3: Build / type-check**

```bash
cd Lingarr.Client && npx vue-tsc --noEmit
```
Expected: no errors.

- [ ] **Step 4: Commit**

```bash
git add Lingarr.Client/src/ts/setting.ts
git commit -m "feat: add MAX_CONCURRENT_REQUESTS to frontend settings constants"
```

---

### Task 7: Frontend — `TranslationSettings.vue` input

**Files:**
- Modify: `Lingarr.Client/src/components/features/settings/TranslationSettings.vue`

- [ ] **Step 1: Add `maxConcurrentRequests` to `isValid`**

In the `reactive` object:

```typescript
const isValid = reactive({
    maxBatchSize: true,
    requestTimeout: true,
    maxRetries: true,
    retryDelay: true,
    retryDelayMultiplier: true,
    maxConcurrentRequests: true
})
```

- [ ] **Step 2: Add the computed property**

After `retryDelayMultiplier`, add:

```typescript
const maxConcurrentRequests = computed({
    get: (): string => settingsStore.getSetting(SETTINGS.MAX_CONCURRENT_REQUESTS) as string,
    set: (newValue: string): void => {
        settingsStore.updateSetting(SETTINGS.MAX_CONCURRENT_REQUESTS, newValue, isValid.maxConcurrentRequests)
        saveNotification.value?.show()
    }
})
```

- [ ] **Step 3: Add the template block**

In the `<template #content>` block, after the `retryDelayMultiplier` input and before the closing `</template>`:

```html
<div class="flex flex-col space-x-2">
    <span class="font-semibold">Max concurrent requests:</span>
    Number of translation requests sent in parallel. Increase for faster
    translation on services that support concurrent load (e.g. local Ollama).
    Default is 1 (sequential).
</div>
<InputComponent
    v-model="maxConcurrentRequests"
    :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
    @update:validation="(val) => (isValid.maxConcurrentRequests = val)" />
```

- [ ] **Step 4: Build / type-check**

```bash
cd Lingarr.Client && npx vue-tsc --noEmit
```
Expected: no errors.

- [ ] **Step 5: Commit**

```bash
git add Lingarr.Client/src/components/features/settings/TranslationSettings.vue
git commit -m "feat: add max concurrent requests input to TranslationSettings"
```

---

### Task 8: Final integration check

- [ ] **Step 1: Run the full backend test suite one last time**

```bash
dotnet test Lingarr.Server.Tests/Lingarr.Server.Tests.csproj -v n
```
Expected: all tests `PASSED`.

- [ ] **Step 2: Build the full solution**

```bash
dotnet build
```
Expected: `Build succeeded.`

- [ ] **Step 3: Start the app and manually verify**

Start the app (follow your normal dev startup procedure). Navigate to **Settings → Translation Request**. Confirm:
- A "Max concurrent requests" number input appears after "Retry delay multiplier".
- Saving `2` persists correctly (check the value is retained on page reload).
- A translation job completes successfully with concurrency set to `2`.
- Setting it back to `1` produces identical sequential behaviour.
