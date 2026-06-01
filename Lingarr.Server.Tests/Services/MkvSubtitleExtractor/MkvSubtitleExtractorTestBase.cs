using System;
using System.IO;
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
