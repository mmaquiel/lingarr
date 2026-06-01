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
