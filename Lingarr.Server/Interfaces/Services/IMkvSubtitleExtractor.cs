using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Interfaces.Services;

public interface IMkvSubtitleExtractor
{
    Task<List<Subtitles>> ExtractSubtitles(string mkvPath, string mediaFileName);
}
