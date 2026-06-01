using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Interfaces.Services;

public interface IFfmpegWrapper
{
    Task<IReadOnlyList<SubtitleStreamInfo>> GetSubtitleStreamsAsync(string inputPath);
    Task ExtractSubtitleStreamAsync(string inputPath, string outputPath, int streamIndex);
}
