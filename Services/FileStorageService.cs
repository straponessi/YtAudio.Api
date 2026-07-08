using System.Text;

namespace YtAudio.Api.Services
{
    public class FileStorageService
    {
        private readonly string _storageRoot;
        private readonly string _tempRoot;
        private readonly ILogger<FileStorageService> _logger;

        private static readonly char[] InvalidNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];
        private const int MaxFileNameLength = 150;

        public FileStorageService(IConfiguration config, ILogger<FileStorageService> logger)
        {
            _logger = logger;
            _storageRoot = config["Storage:Root"]
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "YtAudio", "tracks");

            _tempRoot = Path.Combine(_storageRoot, ".tmp");

            Directory.CreateDirectory(_storageRoot);
            Directory.CreateDirectory(_tempRoot);

            logger.LogInformation("Storage root: {Root}", _storageRoot);
        }

        public string CreateTempDirectory()
        {
            var dir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public string MoveToStorage(string tempFilePath, string youtubeId, string title, string? artist, string? album)
        {
            var ext = Path.GetExtension(tempFilePath);
            var fileName = BuildFileName(youtubeId, title) + ext;

            var destination = MakeUnique(Path.Combine(_storageRoot, fileName));

            File.Move(tempFilePath, destination, overwrite: false);
            _logger.LogInformation("Stored track {Id} → {Path}", youtubeId, destination);

            var tempDir = Path.GetDirectoryName(tempFilePath);
            if (tempDir is not null && tempDir != _tempRoot && Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not clean temp dir {Dir}", tempDir); }
            }

            return destination;
        }

        public bool Exists(string filePath) => File.Exists(filePath);

        public void Delete(string filePath)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogInformation("Deleted file {Path}", filePath);
            }
        }

        public StorageStats GetStats()
        {
            var files = Directory.GetFiles(_storageRoot, "*", SearchOption.TopDirectoryOnly);
            return new StorageStats
            {
                TrackCount = files.Length,
                TotalBytes = files.Sum(f => new FileInfo(f).Length)
            };
        }

        private static string BuildFileName(string youtubeId, string title)
        {
            var parts = new List<string>();

            var titlePart = !string.IsNullOrWhiteSpace(title) ? SanitizeComponent(title) : youtubeId;
            parts.Add(titlePart);

            var name = string.Join(" - ", parts);

            return name.Length > MaxFileNameLength
                ? name[..MaxFileNameLength].TrimEnd()
                : name;
        }

        private static string SanitizeComponent(string value)
        {
            var sb = new StringBuilder(value.Length);

            foreach (var c in value)
                sb.Append(InvalidNameChars.Contains(c) || char.IsControl(c) ? '-' : c);

            var cleaned = sb.ToString();

            while (cleaned.Contains("  "))
                cleaned = cleaned.Replace("  ", " ");
            while (cleaned.Contains("--"))
                cleaned = cleaned.Replace("--", "-");

            cleaned = cleaned.Trim(' ', '-', '.');

            return cleaned.Length > 0 ? cleaned : "untitled";
        }

        private static string MakeUnique(string destination)
        {
            if (!File.Exists(destination)) return destination;

            var dir = Path.GetDirectoryName(destination)!;
            var name = Path.GetFileNameWithoutExtension(destination);
            var ext = Path.GetExtension(destination);

            for (var i = 2; ; i++)
            {
                var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }
        }
    }

    public record StorageStats
    {
        public int TrackCount { get; init; }
        public long TotalBytes { get; init; }
        public string TotalHuman => TotalBytes switch
        {
            < 1024 => $"{TotalBytes} B",
            < 1024 * 1024 => $"{TotalBytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{TotalBytes / (1024.0 * 1024):F1} MB",
            _ => $"{TotalBytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }
}