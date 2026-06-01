using System.Collections.Generic;
using System.Threading.Tasks;
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
