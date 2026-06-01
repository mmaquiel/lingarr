# MKV Subtitle Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract embedded subtitles from `.mkv` files via ffprobe/ffmpeg, cache them locally in the container, and merge them with filesystem sidecar results inside `SubtitleService.GetSubtitles`.

**Architecture:** A new `MkvSubtitleExtractor` service (behind `IMkvSubtitleExtractor`) encapsulates all ffprobe/ffmpeg concerns through a thin `IFfmpegWrapper` abstraction. `SubtitleService.GetSubtitles` calls it first (MKV results are priority), then falls back to the existing filesystem scan; the two lists are merged with MKV winning on same-language collision.

**Tech Stack:** C# / .NET 10, FFMpegCore 5.4.0, xUnit v3, Moq

---

## File Map

| Action | Path | Responsibility |
|--------|------|----------------|
| Create | `Lingarr.Server/Models/FileSystem/SubtitleStreamInfo.cs` | DTO record for a single subtitle stream from ffprobe |
| Create | `Lingarr.Server/Interfaces/Services/IFfmpegWrapper.cs` | Thin abstraction over static FFMpegCore calls (enables mocking) |
| Create | `Lingarr.Server/Services/FfmpegWrapper.cs` | Production implementation; calls `FFProbe.AnalyseAsync` + `FFMpegArguments` |
| Create | `Lingarr.Server/Interfaces/Services/IMkvSubtitleExtractor.cs` | Contract for MKV extraction business logic |
| Create | `Lingarr.Server/Services/MkvSubtitleExtractor.cs` | Probes, filters streams, checks cache, extracts, returns `List<Subtitles>` |
| Create | `Lingarr.Server.Tests/Services/MkvSubtitleExtractor/MkvSubtitleExtractorTestBase.cs` | Shared test setup (mocks + temp cache dir) |
| Create | `Lingarr.Server.Tests/Services/MkvSubtitleExtractor/StreamSelectionTests.cs` | Tests: codec filter, language filter, duplicate language dedup |
| Create | `Lingarr.Server.Tests/Services/MkvSubtitleExtractor/CacheTests.cs` | Tests: skip if cached, create dir, correct path |
| Create | `Lingarr.Server.Tests/Services/MkvSubtitleExtractor/ErrorHandlingTests.cs` | Tests: probe failure → empty list; single stream failure → continues |
| Create | `Lingarr.Server.Tests/Services/SubtitleService/MkvFallbackTests.cs` | Tests: MKV priority, filesystem supplement, no-MKV fallback |
| Modify | `Directory.Packages.props` | Add `FFMpegCore` version |
| Modify | `Lingarr.Server/Lingarr.Server.csproj` | Reference `FFMpegCore` |
| Modify | `Lingarr.Server/Dockerfile` | Install `ffmpeg` in final stage via `apt-get` |
| Modify | `Lingarr.Server/appsettings.json` | Add `SubtitleCache:RootPath` default |
| Modify | `Lingarr.Server/Extensions/ServiceCollectionExtensions.cs` | Register `IFfmpegWrapper` and `IMkvSubtitleExtractor` |
| Modify | `Lingarr.Server/Services/SubtitleService.cs` | Inject `IMkvSubtitleExtractor`; update `GetSubtitles` |

---

## Task 1: Infrastructure — FFMpegCore package + Dockerfile + config

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `Lingarr.Server/Lingarr.Server.csproj`
- Modify: `Lingarr.Server/Dockerfile`
- Modify: `Lingarr.Server/appsettings.json`

- [ ] **Step 1: Add FFMpegCore to central package manifest**

In `Directory.Packages.props`, add inside the `<ItemGroup>`:
```xml
<!-- FFMpeg -->
<PackageVersion Include="FFMpegCore" Version="5.4.0" />
```

- [ ] **Step 2: Reference the package in the server project**

In `Lingarr.Server/Lingarr.Server.csproj`, add inside the first `<ItemGroup>`:
```xml
<PackageReference Include="FFMpegCore" />
```

- [ ] **Step 3: Install ffmpeg in the Docker final stage**

In `Lingarr.Server/Dockerfile`, replace:
```dockerfile
# Step 5: Final image
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
```
With:
```dockerfile
# Step 5: Final image
FROM base AS final
WORKDIR /app
RUN apt-get update && apt-get install -y ffmpeg --no-install-recommends && rm -rf /var/lib/apt/lists/*
COPY --from=publish /app/publish .
```

- [ ] **Step 4: Add default cache path to appsettings.json**

In `Lingarr.Server/appsettings.json`, add the `SubtitleCache` section:
```json
{
  "Logging": { ... },
  "AllowedHosts": "*",
  "DbConnection": "mysql",
  "ConnectionStrings": {
    "MySqlConnection": "Server=localhost;Port=1433;Database=Lingarr;Uid=Lingarr;Pwd=Secret1234;Allow User Variables=True"
  },
  "SqliteDbPath": "/app/config/local.db",
  "SubtitleCache": {
    "RootPath": "/app/subtitle-cache"
  }
}
```

- [ ] **Step 5: Verify restore succeeds**

```bash
dotnet restore Lingarr.Server/Lingarr.Server.csproj
```
Expected: no errors, `FFMpegCore` appears in the restored packages list.

- [ ] **Step 6: Commit**

```bash
git add Directory.Packages.props Lingarr.Server/Lingarr.Server.csproj Lingarr.Server/Dockerfile Lingarr.Server/appsettings.json
git commit -m "feat: add FFMpegCore package, ffmpeg Dockerfile install, and subtitle cache config"
```

---

## Task 2: Create models and interfaces

**Files:**
- Create: `Lingarr.Server/Models/FileSystem/SubtitleStreamInfo.cs`
- Create: `Lingarr.Server/Interfaces/Services/IFfmpegWrapper.cs`
- Create: `Lingarr.Server/Interfaces/Services/IMkvSubtitleExtractor.cs`

- [ ] **Step 1: Create SubtitleStreamInfo record**

Create `Lingarr.Server/Models/FileSystem/SubtitleStreamInfo.cs`:
```csharp
namespace Lingarr.Server.Models.FileSystem;

public record SubtitleStreamInfo(int Index, string CodecName, string Language);
```

- [ ] **Step 2: Create IFfmpegWrapper interface**

Create `Lingarr.Server/Interfaces/Services/IFfmpegWrapper.cs`:
```csharp
using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Interfaces.Services;

public interface IFfmpegWrapper
{
    Task<IReadOnlyList<SubtitleStreamInfo>> GetSubtitleStreamsAsync(string inputPath);
    Task ExtractSubtitleStreamAsync(string inputPath, string outputPath, int streamIndex);
}
```

- [ ] **Step 3: Create IMkvSubtitleExtractor interface**

Create `Lingarr.Server/Interfaces/Services/IMkvSubtitleExtractor.cs`:
```csharp
using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Interfaces.Services;

public interface IMkvSubtitleExtractor
{
    Task<List<Subtitles>> ExtractSubtitles(string mkvPath, string mediaFileName);
}
```

- [ ] **Step 4: Verify project builds**

```bash
dotnet build Lingarr.Server/Lingarr.Server.csproj
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add Lingarr.Server/Models/FileSystem/SubtitleStreamInfo.cs \
        Lingarr.Server/Interfaces/Services/IFfmpegWrapper.cs \
        Lingarr.Server/Interfaces/Services/IMkvSubtitleExtractor.cs
git commit -m "feat: add SubtitleStreamInfo, IFfmpegWrapper, and IMkvSubtitleExtractor interfaces"
```

---

## Task 3: TDD — MkvSubtitleExtractor stream selection

**Files:**
- Create: `Lingarr.Server.Tests/Services/MkvSubtitleExtractor/MkvSubtitleExtractorTestBase.cs`
- Create: `Lingarr.Server.Tests/Services/MkvSubtitleExtractor/StreamSelectionTests.cs`
- Create: `Lingarr.Server/Services/MkvSubtitleExtractor.cs`
- Create: `Lingarr.Server/Services/FfmpegWrapper.cs`

- [ ] **Step 1: Create test base class**

Create `Lingarr.Server.Tests/Services/MkvSubtitleExtractor/MkvSubtitleExtractorTestBase.cs`:
```csharp
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Lingarr.Server.Tests.Services.MkvSubtitleExtractor;

public abstract class MkvSubtitleExtractorTestBase : IDisposable
{
    protected readonly Mock<IFfmpegWrapper> FfmpegWrapperMock;
    protected readonly Mock<ILogger<Lingarr.Server.Services.MkvSubtitleExtractor>> LoggerMock;
    protected readonly LanguageCodeService LanguageCodeService;
    protected readonly string TempCacheRoot;
    protected Lingarr.Server.Services.MkvSubtitleExtractor Extractor;

    protected MkvSubtitleExtractorTestBase()
    {
        FfmpegWrapperMock = new Mock<IFfmpegWrapper>();
        LoggerMock = new Mock<ILogger<Lingarr.Server.Services.MkvSubtitleExtractor>>();
        LanguageCodeService = new LanguageCodeService();
        TempCacheRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(TempCacheRoot);
        Extractor = CreateExtractor(TempCacheRoot);
    }

    protected Lingarr.Server.Services.MkvSubtitleExtractor CreateExtractor(string cacheRoot)
    {
        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["SubtitleCache:RootPath"]).Returns(cacheRoot);
        return new Lingarr.Server.Services.MkvSubtitleExtractor(
            LoggerMock.Object,
            FfmpegWrapperMock.Object,
            LanguageCodeService,
            configMock.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(TempCacheRoot))
            Directory.Delete(TempCacheRoot, recursive: true);
        GC.SuppressFinalize(this);
    }
}
```

- [ ] **Step 2: Write failing stream selection tests**

Create `Lingarr.Server.Tests/Services/MkvSubtitleExtractor/StreamSelectionTests.cs`:
```csharp
using Lingarr.Server.Models.FileSystem;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.MkvSubtitleExtractor;

public class StreamSelectionTests : MkvSubtitleExtractorTestBase
{
    [Fact]
    public async Task ExtractSubtitles_SkipsStreamWithUnrecognizedLanguage()
    {
        FfmpegWrapperMock.Setup(f => f.GetSubtitleStreamsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<SubtitleStreamInfo> { new(3, "subrip", "xyz") });

        var result = await Extractor.ExtractSubtitles("/media/Movie.mkv", "Movie");

        Assert.Empty(result);
        FfmpegWrapperMock.Verify(
            f => f.ExtractSubtitleStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task ExtractSubtitles_SkipsStreamWithUnsupportedCodec()
    {
        FfmpegWrapperMock.Setup(f => f.GetSubtitleStreamsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<SubtitleStreamInfo> { new(3, "dvd_subtitle", "eng") });

        var result = await Extractor.ExtractSubtitles("/media/Movie.mkv", "Movie");

        Assert.Empty(result);
        FfmpegWrapperMock.Verify(
            f => f.ExtractSubtitleStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task ExtractSubtitles_TakesFirstStreamForDuplicateLanguage()
    {
        FfmpegWrapperMock.Setup(f => f.GetSubtitleStreamsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<SubtitleStreamInfo>
            {
                new(3, "subrip", "ita"),
                new(4, "subrip", "ita")
            });
        FfmpegWrapperMock
            .Setup(f => f.ExtractSubtitleStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);

        var result = await Extractor.ExtractSubtitles("/media/Movie.mkv", "Movie");

        Assert.Single(result);
        FfmpegWrapperMock.Verify(
            f => f.ExtractSubtitleStreamAsync(It.IsAny<string>(), It.IsAny<string>(), 3), Times.Once);
        FfmpegWrapperMock.Verify(
            f => f.ExtractSubtitleStreamAsync(It.IsAny<string>(), It.IsAny<string>(), 4), Times.Never);
    }

    [Fact]
    public async Task ExtractSubtitles_ReturnsCorrectMetadataForSupportedStream()
    {
        FfmpegWrapperMock.Setup(f => f.GetSubtitleStreamsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<SubtitleStreamInfo> { new(5, "ass", "eng") });
        FfmpegWrapperMock
            .Setup(f => f.ExtractSubtitleStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);

        var result = await Extractor.ExtractSubtitles("/media/Movie.mkv", "Movie");

        Assert.Single(result);
        Assert.Equal("en", result[0].Language);
        Assert.Equal(".ass", result[0].Format);
        Assert.Equal("Movie.en", result[0].FileName);
    }
}
```

- [ ] **Step 3: Run tests to confirm they fail**

```bash
dotnet test Lingarr.Server.Tests/Lingarr.Server.Tests.csproj --filter "FullyQualifiedName~StreamSelectionTests" --no-build 2>&1 | tail -20
```
Expected: compilation error — `MkvSubtitleExtractor` does not exist yet.

- [ ] **Step 4: Create FfmpegWrapper (production implementation)**

Create `Lingarr.Server/Services/FfmpegWrapper.cs`:
```csharp
using FFMpegCore;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Services;

public class FfmpegWrapper : IFfmpegWrapper
{
    public async Task<IReadOnlyList<SubtitleStreamInfo>> GetSubtitleStreamsAsync(string inputPath)
    {
        var analysis = await FFProbe.AnalyseAsync(inputPath);
        return analysis.SubtitleStreams
            .Select(s => new SubtitleStreamInfo(
                s.Index,
                s.CodecName ?? string.Empty,
                s.Tags?.GetValueOrDefault("language") ?? string.Empty))
            .ToList();
    }

    public Task ExtractSubtitleStreamAsync(string inputPath, string outputPath, int streamIndex)
        => FFMpegArguments
            .FromFileInput(inputPath)
            .OutputToFile(outputPath, overwrite: true, options =>
                options.WithCustomArgument($"-map 0:{streamIndex}"))
            .ProcessAsynchronously();
}
```

- [ ] **Step 5: Create MkvSubtitleExtractor with stream selection logic**

Create `Lingarr.Server/Services/MkvSubtitleExtractor.cs`:
```csharp
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.FileSystem;
using Microsoft.Extensions.Configuration;

namespace Lingarr.Server.Services;

public class MkvSubtitleExtractor : IMkvSubtitleExtractor
{
    private readonly ILogger<MkvSubtitleExtractor> _logger;
    private readonly IFfmpegWrapper _ffmpegWrapper;
    private readonly LanguageCodeService _languageCodeService;
    private readonly string _cacheRoot;

    private static readonly Dictionary<string, string> SupportedCodecs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["subrip"] = ".srt",
            ["ass"]    = ".ass",
            ["ssa"]    = ".ssa"
        };

    public MkvSubtitleExtractor(
        ILogger<MkvSubtitleExtractor> logger,
        IFfmpegWrapper ffmpegWrapper,
        LanguageCodeService languageCodeService,
        IConfiguration configuration)
    {
        _logger = logger;
        _ffmpegWrapper = ffmpegWrapper;
        _languageCodeService = languageCodeService;
        _cacheRoot = configuration["SubtitleCache:RootPath"] ?? "/app/subtitle-cache";
    }

    public async Task<List<Subtitles>> ExtractSubtitles(string mkvPath, string mediaFileName)
    {
        try
        {
            var streams = await _ffmpegWrapper.GetSubtitleStreamsAsync(mkvPath);
            var result = new List<Subtitles>();
            var seenLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mediaDir = Path.GetDirectoryName(mkvPath) ?? string.Empty;

            foreach (var stream in streams)
            {
                if (!SupportedCodecs.TryGetValue(stream.CodecName, out var ext))
                {
                    _logger.LogDebug("Skipping stream {Index}: unsupported codec {Codec}", stream.Index, stream.CodecName);
                    continue;
                }

                if (!_languageCodeService.Validate(stream.Language))
                {
                    _logger.LogDebug("Skipping stream {Index}: unrecognized language '{Lang}'", stream.Index, stream.Language);
                    continue;
                }

                var normalizedLang = LanguageCodeService.GetNormalizedCode(stream.Language);
                if (!seenLanguages.Add(normalizedLang))
                {
                    _logger.LogDebug("Skipping stream {Index}: duplicate language {Lang}", stream.Index, normalizedLang);
                    continue;
                }

                var relativePath = mediaDir.TrimStart(Path.DirectorySeparatorChar);
                var outputPath = Path.Combine(_cacheRoot, relativePath, $"{mediaFileName}.{normalizedLang}{ext}");

                if (!File.Exists(outputPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    try
                    {
                        await _ffmpegWrapper.ExtractSubtitleStreamAsync(mkvPath, outputPath, stream.Index);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to extract stream {Index} from {MkvPath}", stream.Index, mkvPath);
                        continue;
                    }
                }

                result.Add(new Subtitles
                {
                    Path = outputPath,
                    FileName = $"{mediaFileName}.{normalizedLang}",
                    Language = normalizedLang,
                    Caption = string.Empty,
                    Format = ext
                });
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe subtitle streams from {MkvPath}", mkvPath);
            return [];
        }
    }
}
```

- [ ] **Step 6: Run stream selection tests to confirm they pass**

```bash
dotnet test Lingarr.Server.Tests/Lingarr.Server.Tests.csproj --filter "FullyQualifiedName~StreamSelectionTests" 2>&1 | tail -20
```
Expected: 4 tests passing, 0 failing.

- [ ] **Step 7: Commit**

```bash
git add Lingarr.Server/Services/FfmpegWrapper.cs \
        Lingarr.Server/Services/MkvSubtitleExtractor.cs \
        Lingarr.Server.Tests/Services/MkvSubtitleExtractor/MkvSubtitleExtractorTestBase.cs \
        Lingarr.Server.Tests/Services/MkvSubtitleExtractor/StreamSelectionTests.cs
git commit -m "feat: implement MkvSubtitleExtractor stream selection with TDD"
```

---

## Task 4: TDD — MkvSubtitleExtractor cache behavior

**Files:**
- Create: `Lingarr.Server.Tests/Services/MkvSubtitleExtractor/CacheTests.cs`
- Modify: `Lingarr.Server/Services/MkvSubtitleExtractor.cs` (cache logic already present from Task 3 — tests verify it)

- [ ] **Step 1: Write failing cache tests**

Create `Lingarr.Server.Tests/Services/MkvSubtitleExtractor/CacheTests.cs`:
```csharp
using Lingarr.Server.Models.FileSystem;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.MkvSubtitleExtractor;

public class CacheTests : MkvSubtitleExtractorTestBase
{
    [Fact]
    public async Task ExtractSubtitles_SkipsExtractionWhenCacheFileExists()
    {
        const string mkvPath = "/media/movies/Movie/Movie.mkv";
        FfmpegWrapperMock.Setup(f => f.GetSubtitleStreamsAsync(mkvPath))
            .ReturnsAsync(new List<SubtitleStreamInfo> { new(3, "subrip", "eng") });

        // Pre-create cache file
        var cachedPath = Path.Combine(TempCacheRoot, "media", "movies", "Movie", "Movie.en.srt");
        Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);
        await File.WriteAllTextAsync(cachedPath, "1\n00:00:01,000 --> 00:00:02,000\nHello\n");

        var result = await Extractor.ExtractSubtitles(mkvPath, "Movie");

        Assert.Single(result);
        Assert.Equal(cachedPath, result[0].Path);
        FfmpegWrapperMock.Verify(
            f => f.ExtractSubtitleStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task ExtractSubtitles_CreatesOutputDirectoryIfMissing()
    {
        const string mkvPath = "/media/movies/NewMovie/NewMovie.mkv";
        FfmpegWrapperMock.Setup(f => f.GetSubtitleStreamsAsync(mkvPath))
            .ReturnsAsync(new List<SubtitleStreamInfo> { new(3, "subrip", "eng") });
        FfmpegWrapperMock
            .Setup(f => f.ExtractSubtitleStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);

        var expectedDir = Path.Combine(TempCacheRoot, "media", "movies", "NewMovie");
        Assert.False(Directory.Exists(expectedDir));

        await Extractor.ExtractSubtitles(mkvPath, "NewMovie");

        Assert.True(Directory.Exists(expectedDir));
    }

    [Fact]
    public async Task ExtractSubtitles_ExtractsToCorrectCachePath()
    {
        const string mkvPath = "/media/movies/Movie (2023)/Movie.mkv";
        FfmpegWrapperMock.Setup(f => f.GetSubtitleStreamsAsync(mkvPath))
            .ReturnsAsync(new List<SubtitleStreamInfo> { new(3, "subrip", "eng") });

        string? capturedOutputPath = null;
        FfmpegWrapperMock
            .Setup(f => f.ExtractSubtitleStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .Callback<string, string, int>((_, output, _) => capturedOutputPath = output)
            .Returns(Task.CompletedTask);

        await Extractor.ExtractSubtitles(mkvPath, "Movie");

        var expectedPath = Path.Combine(TempCacheRoot, "media", "movies", "Movie (2023)", "Movie.en.srt");
        Assert.Equal(expectedPath, capturedOutputPath);
    }
}
```

- [ ] **Step 2: Run cache tests**

```bash
dotnet test Lingarr.Server.Tests/Lingarr.Server.Tests.csproj --filter "FullyQualifiedName~CacheTests" 2>&1 | tail -20
```
Expected: 3 tests passing, 0 failing.

- [ ] **Step 3: Commit**

```bash
git add Lingarr.Server.Tests/Services/MkvSubtitleExtractor/CacheTests.cs
git commit -m "test: add MkvSubtitleExtractor cache behavior tests"
```

---

## Task 5: TDD — MkvSubtitleExtractor error handling

**Files:**
- Create: `Lingarr.Server.Tests/Services/MkvSubtitleExtractor/ErrorHandlingTests.cs`

- [ ] **Step 1: Write failing error handling tests**

Create `Lingarr.Server.Tests/Services/MkvSubtitleExtractor/ErrorHandlingTests.cs`:
```csharp
using Lingarr.Server.Models.FileSystem;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.MkvSubtitleExtractor;

public class ErrorHandlingTests : MkvSubtitleExtractorTestBase
{
    [Fact]
    public async Task ExtractSubtitles_ReturnsEmptyListWhenProbeThrows()
    {
        FfmpegWrapperMock.Setup(f => f.GetSubtitleStreamsAsync(It.IsAny<string>()))
            .ThrowsAsync(new Exception("ffprobe process failed"));

        var result = await Extractor.ExtractSubtitles("/media/Movie.mkv", "Movie");

        Assert.Empty(result);
    }

    [Fact]
    public async Task ExtractSubtitles_ContinuesToNextStreamWhenExtractionFails()
    {
        FfmpegWrapperMock.Setup(f => f.GetSubtitleStreamsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<SubtitleStreamInfo>
            {
                new(3, "subrip", "eng"),
                new(4, "subrip", "ita")
            });
        FfmpegWrapperMock
            .Setup(f => f.ExtractSubtitleStreamAsync(It.IsAny<string>(), It.IsAny<string>(), 3))
            .ThrowsAsync(new Exception("stream 3 extraction failed"));
        FfmpegWrapperMock
            .Setup(f => f.ExtractSubtitleStreamAsync(It.IsAny<string>(), It.IsAny<string>(), 4))
            .Returns(Task.CompletedTask);

        var result = await Extractor.ExtractSubtitles("/media/Movie.mkv", "Movie");

        Assert.Single(result);
        Assert.Equal("it", result[0].Language);
    }
}
```

- [ ] **Step 2: Run error handling tests**

```bash
dotnet test Lingarr.Server.Tests/Lingarr.Server.Tests.csproj --filter "FullyQualifiedName~ErrorHandlingTests" 2>&1 | tail -20
```
Expected: 2 tests passing, 0 failing.

- [ ] **Step 3: Run all MkvSubtitleExtractor tests to confirm nothing regressed**

```bash
dotnet test Lingarr.Server.Tests/Lingarr.Server.Tests.csproj --filter "FullyQualifiedName~MkvSubtitleExtractor" 2>&1 | tail -20
```
Expected: 9 tests passing, 0 failing.

- [ ] **Step 4: Commit**

```bash
git add Lingarr.Server.Tests/Services/MkvSubtitleExtractor/ErrorHandlingTests.cs
git commit -m "test: add MkvSubtitleExtractor error handling tests"
```

---

## Task 6: Register new services in DI

**Files:**
- Modify: `Lingarr.Server/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Register IFfmpegWrapper and IMkvSubtitleExtractor**

In `Lingarr.Server/Extensions/ServiceCollectionExtensions.cs`, add both usings at the top:
```csharp
using Lingarr.Server.Interfaces.Services;
```
(This namespace is already imported — no change needed.)

In the `ConfigureServices` method, after the line:
```csharp
builder.Services.AddScoped<ISubtitleService, SubtitleService>();
```
Add:
```csharp
builder.Services.AddScoped<IFfmpegWrapper, FfmpegWrapper>();
builder.Services.AddScoped<IMkvSubtitleExtractor, MkvSubtitleExtractor>();
```

- [ ] **Step 2: Verify project builds**

```bash
dotnet build Lingarr.Server/Lingarr.Server.csproj 2>&1 | tail -10
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Lingarr.Server/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat: register IFfmpegWrapper and IMkvSubtitleExtractor in DI"
```

---

## Task 7: TDD — SubtitleService.GetSubtitles merge behavior

**Files:**
- Create: `Lingarr.Server.Tests/Services/SubtitleService/MkvFallbackTests.cs`
- Modify: `Lingarr.Server/Services/SubtitleService.cs`

- [ ] **Step 1: Write failing SubtitleService merge tests**

Create `Lingarr.Server.Tests/Services/SubtitleService/MkvFallbackTests.cs`:
```csharp
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.SubtitleService;

public class MkvFallbackTests : IDisposable
{
    private readonly Mock<IMkvSubtitleExtractor> _mkvExtractorMock;
    private readonly Lingarr.Server.Services.SubtitleService _service;
    private readonly string _tempDir;

    public MkvFallbackTests()
    {
        _mkvExtractorMock = new Mock<IMkvSubtitleExtractor>();
        var loggerMock = new Mock<ILogger<Lingarr.Server.Services.SubtitleService>>();
        var languageCodeService = new LanguageCodeService();
        _service = new Lingarr.Server.Services.SubtitleService(
            loggerMock.Object, languageCodeService, _mkvExtractorMock.Object);
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task GetSubtitles_MkvResultsTakePriorityOverFilesystemForSameLanguage()
    {
        // Create dummy mkv so File.Exists passes, and an en sidecar on the filesystem
        var mkvPath = Path.Combine(_tempDir, "Movie.mkv");
        await File.WriteAllTextAsync(mkvPath, string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "Movie.en.srt"),
            "1\n00:00:01,000 --> 00:00:02,000\nFilesystem\n");

        _mkvExtractorMock
            .Setup(e => e.ExtractSubtitles(mkvPath, "Movie"))
            .ReturnsAsync(new List<Subtitles>
            {
                new() { Path = "/cache/Movie.en.srt", FileName = "Movie.en", Language = "en", Format = ".srt", Caption = "" }
            });

        var result = await _service.GetSubtitles(_tempDir, "Movie");

        Assert.Single(result);
        Assert.Equal("/cache/Movie.en.srt", result[0].Path);
    }

    [Fact]
    public async Task GetSubtitles_FilesystemFillsLanguagesAbsentFromMkv()
    {
        var mkvPath = Path.Combine(_tempDir, "Movie.mkv");
        await File.WriteAllTextAsync(mkvPath, string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "Movie.en.srt"),
            "1\n00:00:01,000 --> 00:00:02,000\nEnglish\n");

        _mkvExtractorMock
            .Setup(e => e.ExtractSubtitles(mkvPath, "Movie"))
            .ReturnsAsync(new List<Subtitles>
            {
                new() { Path = "/cache/Movie.it.srt", FileName = "Movie.it", Language = "it", Format = ".srt", Caption = "" }
            });

        var result = await _service.GetSubtitles(_tempDir, "Movie");

        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => s.Language == "it" && s.Path == "/cache/Movie.it.srt");
        Assert.Contains(result, s => s.Language == "en");
    }

    [Fact]
    public async Task GetSubtitles_ReturnsFilesystemOnlyWhenNoMkvExists()
    {
        // No mkv file — only a sidecar
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "Movie.en.srt"),
            "1\n00:00:01,000 --> 00:00:02,000\nHello\n");

        var result = await _service.GetSubtitles(_tempDir, "Movie");

        Assert.Single(result);
        Assert.Equal("en", result[0].Language);
        _mkvExtractorMock.Verify(
            e => e.ExtractSubtitles(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```bash
dotnet test Lingarr.Server.Tests/Lingarr.Server.Tests.csproj --filter "FullyQualifiedName~MkvFallbackTests" 2>&1 | tail -20
```
Expected: compilation error — `SubtitleService` constructor does not yet accept `IMkvSubtitleExtractor`.

- [ ] **Step 3: Modify SubtitleService to inject IMkvSubtitleExtractor and update GetSubtitles**

In `Lingarr.Server/Services/SubtitleService.cs`:

Replace the constructor and field declarations (lines 20-29):
```csharp
private readonly ILogger<SubtitleService> _logger;
private readonly LanguageCodeService _languageCodeService;

public SubtitleService(
    ILogger<SubtitleService> logger,
    LanguageCodeService languageCodeService)
{
    _logger = logger;
    _languageCodeService = languageCodeService;
}
```
With:
```csharp
private readonly ILogger<SubtitleService> _logger;
private readonly LanguageCodeService _languageCodeService;
private readonly IMkvSubtitleExtractor _mkvSubtitleExtractor;

public SubtitleService(
    ILogger<SubtitleService> logger,
    LanguageCodeService languageCodeService,
    IMkvSubtitleExtractor mkvSubtitleExtractor)
{
    _logger = logger;
    _languageCodeService = languageCodeService;
    _mkvSubtitleExtractor = mkvSubtitleExtractor;
}
```

Also add the using at the top of the file if not present:
```csharp
using Lingarr.Server.Interfaces.Services;
```

Replace the `GetSubtitles` method (lines 537-543):
```csharp
/// <inheritdoc />
public async Task<List<Subtitles>> GetSubtitles(string path, string fileName)
{
    var allSubtitles = await GetAllSubtitles(path);
    return allSubtitles
        .Where(s => s.FileName.StartsWith(fileName + ".") || s.FileName == fileName)
        .ToList();
}
```
With:
```csharp
/// <inheritdoc />
public async Task<List<Subtitles>> GetSubtitles(string path, string fileName)
{
    var mkvPath = Path.Combine(path, fileName + ".mkv");
    var mkvSubtitles = new List<Subtitles>();

    if (File.Exists(mkvPath))
    {
        mkvSubtitles = await _mkvSubtitleExtractor.ExtractSubtitles(mkvPath, fileName);
    }

    var allSubtitles = await GetAllSubtitles(path);
    var fsSubtitles = allSubtitles
        .Where(s => s.FileName.StartsWith(fileName + ".") || s.FileName == fileName)
        .ToList();

    var mkvLanguages = mkvSubtitles
        .Select(s => s.Language)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    return mkvSubtitles
        .Concat(fsSubtitles.Where(s => !mkvLanguages.Contains(s.Language)))
        .ToList();
}
```

- [ ] **Step 4: Run MkvFallbackTests to confirm they pass**

```bash
dotnet test Lingarr.Server.Tests/Lingarr.Server.Tests.csproj --filter "FullyQualifiedName~MkvFallbackTests" 2>&1 | tail -20
```
Expected: 3 tests passing, 0 failing.

- [ ] **Step 5: Run the full test suite to confirm no regressions**

```bash
dotnet test Lingarr.Server.Tests/Lingarr.Server.Tests.csproj 2>&1 | tail -20
```
Expected: All tests passing, 0 failing.

- [ ] **Step 6: Commit**

```bash
git add Lingarr.Server/Services/SubtitleService.cs \
        Lingarr.Server.Tests/Services/SubtitleService/MkvFallbackTests.cs
git commit -m "feat: integrate MKV subtitle extraction into SubtitleService.GetSubtitles"
```
