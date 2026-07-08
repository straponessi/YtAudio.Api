using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace YtAudio.Api.Services
{
    public class YtDlpService(IConfiguration config, ILogger<YtDlpService> logger)
    {
        private readonly string _ytDlpPath = config["YtDlp:ExecutablePath"] ?? "yt-dlp";
        private readonly string _ffmpegPath = config["YtDlp:FfmpegPath"] ?? "ffmpeg";

        public async Task<YtDlpMetadata> GetMetadataAsync(string url, CancellationToken ct = default)
        {
            var json = await RunAsync($"--dump-json --no-download \"{url}\"", ct);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new YtDlpMetadata
            {
                YoutubeId = root.GetProperty("id").GetString()!,
                Title = root.GetProperty("title").GetString()!,
                Artist = root.TryGetProperty("uploader", out var uploader) ? uploader.GetString() : null,
                Album = root.TryGetProperty("album", out var album) ? album.GetString() : null,
                ThumbnailUrl = root.TryGetProperty("thumbnail", out var thumb) ? thumb.GetString() : null,
                DurationSeconds = root.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number
                ? (long)dur.GetDouble()
                : 0
            };
        }

        public async Task<string> DownloadBestAudioAsync(string url, string outputDir, CancellationToken ct = default)
        {
            var args = string.Join(" ",
                "-x",
                "-f bestaudio",
                "--audio-format", "m4a",
                "--embed-thumbnail",
                "--add-metadata",
                "--convert-thumbnails jpg",
                "--no-playlist",
                $"--ffmpeg-location \"{_ffmpegPath}\"",
                $"-o \"{outputDir}/%(id)s.%(ext)s\"",
                $"\"{url}\""
            );

            await RunAsync(args, ct);

            var file = Directory
                .EnumerateFiles(outputDir)
                .Where(f => !f.EndsWith(".part"))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            return file ?? throw new InvalidOperationException("yt-dlp finished but output file was not found.");
        }

        private async Task<string> RunAsync(string arguments, CancellationToken ct)
        {
            logger.LogDebug("yt-dlp {args}", arguments);

            var psi = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                logger.LogError("yt-dlp exited {Code}. stderr: {Err}", process.ExitCode, stderr);
                throw new InvalidOperationException($"yt-dlp exited with code {process.ExitCode}.\n{stderr}");
            }

            return stdout.ToString();
        }
    }

    public record YtDlpMetadata
    {
        public string YoutubeId { get; init; } = default!;
        public string Title { get; init; } = default!;
        public string? Artist { get; init; }
        public string? Album { get; init; }
        public string? ThumbnailUrl { get; init; }
        public long DurationSeconds { get; init; }
    }
}
