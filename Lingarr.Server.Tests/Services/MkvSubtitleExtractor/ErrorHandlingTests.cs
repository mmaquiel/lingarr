using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
