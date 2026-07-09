using System.Text;

namespace YtAudio.Api.Utils
{
    public static class StringExtensions
    {
        private static readonly char[] InvalidNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];
        private const int MaxFileNameLength = 150;

        public static string BuildFileName(string youtubeId, string title)
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
}