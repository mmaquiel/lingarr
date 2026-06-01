# MKV Subtitle Extraction Design

**Date:** 2026-06-01
**Status:** Approved

## Summary

Add a new subtitle source strategy that extracts embedded subtitles from `.mkv` files using ffprobe and ffmpeg (via the `FFMpegCore` NuGet package). MKV-embedded subtitles are preferred over filesystem sidecars because they are provided by the media distributor. Filesystem sidecar files remain as a supplement for languages not present in the MKV.

## Architecture

A dedicated `MkvSubtitleExtractor` service (behind `IMkvSubtitleExtractor`) encapsulates all ffprobe/ffmpeg concerns. `SubtitleService.GetSubtitles` gains awareness of this service: it probes the MKV, extracts to a container-local cache directory, then merges with the filesystem scan.

```
MediaSubtitleProcessor.ProcessMedia(media)
  └─ SubtitleService.GetSubtitles(path, fileName)
       ├─ MkvSubtitleExtractor.ExtractSubtitles(mkvPath, fileName)   ← NEW (priority)
       │    ├─ FFProbe: discover subtitle streams + language tags
       │    ├─ For each stream: check cache → skip or extract via FFMpeg
       │    └─ Return List<Subtitles> (paths pointing to cache dir)
       ├─ GetAllSubtitles(path)                                       ← existing (supplement)
       └─ Merge: MKV results take priority; filesystem fills in missing languages
```

### Cache Directory

Extracted subtitles are stored in a container-local directory (not the shared media volume) to avoid polluting the media library. The path mirrors the media directory structure to guarantee uniqueness:

- MKV at `/media/movies/Movie (2023)/Movie.mkv`
- Cache at `/app/subtitle-cache/movies/Movie (2023)/Movie.eng.srt`

Default root: `/app/subtitle-cache`, configurable via `appsettings.json` key `SubtitleCache:RootPath`.

## Components

### New Files

**`Lingarr.Server/Interfaces/Services/IMkvSubtitleExtractor.cs`**
```csharp
public interface IMkvSubtitleExtractor
{
    Task<List<Subtitles>> ExtractSubtitles(string mkvPath, string mediaFileName);
}
```

**`Lingarr.Server/Services/MkvSubtitleExtractor.cs`**

Responsibilities:
- Reads `SubtitleCache:RootPath` from `IConfiguration` (default: `/app/subtitle-cache`)
- Calls `FFProbe.AnalyseAsync(mkvPath)` to discover subtitle streams
- For each stream with a recognized language tag and supported codec:
  - Builds output path: `{cacheRoot}/{relative-media-dir}/{mediaFileName}.{lang}.{ext}`
  - Skips extraction if cache file already exists
  - Extracts via `FFMpegArguments.FromFileInput(mkvPath).OutputToFile(outputPath).WithSelectStream(streamIndex)`
- Returns `List<Subtitles>` with paths pointing into the cache directory
- When multiple streams share the same language, only the first is used

Supported codecs and their output extensions:
| FFProbe codec | Output extension |
|---|---|
| `subrip` | `.srt` |
| `ass` | `.ass` |
| `ssa` | `.ssa` |

### Modified Files

**`SubtitleService.cs` — `GetSubtitles(path, fileName)`**

New flow:
1. Look for `{path}/{fileName}.mkv`
2. If found, call `_mkvSubtitleExtractor.ExtractSubtitles(mkvPath, fileName)` → `mkvSubtitles`
3. Call `GetAllSubtitles(path)` → `fsSubtitles`, filtered to `fileName`
4. Merge: start with `mkvSubtitles`, append `fsSubtitles` entries whose language is not already covered
5. Return merged list

**`Program.cs`** — register `IMkvSubtitleExtractor` → `MkvSubtitleExtractor` as scoped

**`Lingarr.Server.csproj`** — add `FFMpegCore` NuGet package reference

**`Dockerfile`** — install ffmpeg in the final image stage:
```dockerfile
FROM base AS final
WORKDIR /app
RUN apt-get update && apt-get install -y ffmpeg --no-install-recommends && rm -rf /var/lib/apt/lists/*
COPY --from=publish /app/publish .
```

The base image `mcr.microsoft.com/dotnet/aspnet:10.0` defaults to Ubuntu Noble, which supports `apt-get`.

## Data Flow

```
GetSubtitles(path, fileName)
  │
  ├─ 1. Find MKV: look for {path}/{fileName}.mkv
  │       └─ Not found → skip extraction, return filesystem-only results
  │
  ├─ 2. MkvSubtitleExtractor.ExtractSubtitles(mkvPath, mediaFileName)
  │       ├─ FFProbe.AnalyseAsync(mkvPath) → SubtitleStreams[]
  │       ├─ For each stream:
  │       │    ├─ language tag unrecognized → skip
  │       │    ├─ codec unsupported → skip
  │       │    ├─ same language already seen in this run → skip (take first)
  │       │    ├─ cache file exists → include as-is
  │       │    └─ extract → write to cache, include
  │       └─ Return List<Subtitles> (paths in cache dir)
  │
  ├─ 3. GetAllSubtitles(path) → filesystem List<Subtitles>
  │
  └─ 4. Merge
          ├─ Start with MKV results
          ├─ Append filesystem entries whose language is NOT already covered by MKV results
          └─ Filter: keep only entries whose FileName matches mediaFileName
```

### Cache Path Construction

The relative media directory is derived by stripping the leading `/` from the absolute `path` argument, then appending to the cache root:

```
path  = /media/movies/Movie (2023)/
cache = {cacheRoot}/media/movies/Movie (2023)/Movie.eng.srt
```

Concretely: `Path.Combine(cacheRoot, path.TrimStart(Path.DirectorySeparatorChar), $"{mediaFileName}.{lang}{ext}")`

This requires no additional configuration and is fully deterministic.

## Error Handling

| Scenario | Behaviour |
|---|---|
| MKV file not found | Skip extraction silently; return filesystem-only results |
| FFProbe fails (corrupt file, unsupported container) | Log warning; return empty list from extractor; filesystem scan still runs |
| FFMpeg extraction fails for a single stream | Log warning; continue to next stream; partial results returned |
| Unrecognized language tag | Stream skipped silently |
| Unsupported codec (e.g. `dvd_subtitle`, `hdmv_pgs_subtitle`) | Stream skipped silently |
| Cache directory does not exist | `MkvSubtitleExtractor` creates it via `Directory.CreateDirectory` on first use |

## Testing

### New test folder: `Lingarr.Server.Tests/Services/MkvSubtitleExtractor/`

- **`StreamSelectionTests.cs`** — skips streams with unrecognized language, unsupported codec, or duplicate language (first stream wins per language)
- **`CacheTests.cs`** — skips extraction when cache file already exists; creates cache directory if missing
- **`ErrorHandlingTests.cs`** — FFProbe failure returns empty list; single-stream extraction failure continues to next stream

### New test file in existing folder

- **`Lingarr.Server.Tests/Services/SubtitleService/MkvFallbackTests.cs`** — MKV results take priority over filesystem for same language; filesystem fills in languages absent from MKV; no MKV file found returns filesystem-only results

`IMkvSubtitleExtractor` is mocked in all `SubtitleService` tests via the existing DI pattern.

## Prerequisites

- `ffmpeg` and `ffprobe` must be available in the container (installed via `apt-get` in the Dockerfile)
- `FFMpegCore` NuGet package added to `Lingarr.Server.csproj`
