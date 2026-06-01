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

                string normalizedLang;
                try
                {
                    normalizedLang = LanguageCodeService.GetNormalizedCode(stream.Language);
                }
                catch (ArgumentException)
                {
                    _logger.LogWarning("Could not normalize language code '{Lang}' for stream {Index}, skipping", stream.Language, stream.Index);
                    continue;
                }

                if (!seenLanguages.Add(normalizedLang))
                {
                    _logger.LogDebug("Skipping stream {Index}: duplicate language {Lang}", stream.Index, normalizedLang);
                    continue;
                }

                var relativePath = mediaDir.TrimStart(Path.DirectorySeparatorChar);
                var outputPath = Path.Combine(_cacheRoot, relativePath, $"{mediaFileName}.{normalizedLang}{ext}");

                if (!File.Exists(outputPath))
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
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
