using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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
