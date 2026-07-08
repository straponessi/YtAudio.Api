using Google.Apis.Services;
using Google.Apis.YouTube.v3;

namespace YtAudio.Api.Services
{
    public class YouTubeSearchService(IConfiguration config, ILogger<YouTubeSearchService> logger)
    {
        private readonly string _apiKey = config["Youtube:ApiKey"]
            ?? throw new InvalidOperationException("YouTube:ApiKey is missing.");

        public async Task<List<YouTubeSearchResult>> SearchAsync(string query, int maxResults = 8, CancellationToken ct = default)
        {
            var youtubeService = new YouTubeService(new BaseClientService.Initializer
            {
                ApiKey = _apiKey,
                ApplicationName = "YtAudio"
            });

            var request = youtubeService.Search.List("snippet");
            request.Q = query;
            request.MaxResults = maxResults;
            request.Type = "video";

            logger.LogInformation("Youtube search: {Query}", query);
            var response = await request.ExecuteAsync(ct);

            return response.Items
                .Where(i => i.Id.Kind == "youtube#video")
                .Select(i => new YouTubeSearchResult
                {
                    VideoId = i.Id.VideoId,
                    Title = i.Snippet.Title,
                    ChannelName = i.Snippet.ChannelTitle,
                    ThumbnailUrl = i.Snippet.Thumbnails.Default__.Url
                        ?? i.Snippet.Thumbnails.Default__?.Url
                        ?? string.Empty,
                    YoutubeUrl = $"https://www.youtube.com/watch?v={i.Id.VideoId}"
                })
                .ToList();
        }
    }

    public record YouTubeSearchResult
    {
        public string VideoId { get; init; } = default!;
        public string Title { get; init; } = default!;
        public string ChannelName { get; init; } = default!;
        public string ThumbnailUrl { get; init; } = default!;
        public string YoutubeUrl { get; init; } = default!;
    }
}
