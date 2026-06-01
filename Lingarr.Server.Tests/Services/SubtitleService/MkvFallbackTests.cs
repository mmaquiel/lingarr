using System;
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
