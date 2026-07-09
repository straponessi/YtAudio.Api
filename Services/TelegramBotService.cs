using Hangfire;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using YtAudio.Api.Data;
using YtAudio.Api.Jobs;
using YtAudio.Api.Models;

namespace YtAudio.Api.Services;

public class TelegramBotService(
    IConfiguration config,
    IServiceScopeFactory scopeFactory,
    YouTubeSearchService youTubeSearch,
    IBackgroundJobClient jobs,
    ILogger<TelegramBotService> logger) : BackgroundService
{
    private TelegramBotClient? _bot;

    private readonly HashSet<long> _allowedUserIds = config
        .GetSection("Telegram:AllowedUserIds")
        .Get<string[]>()?
        .Select(long.Parse)
        .ToHashSet() ?? [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var token = config["Telegram:BotToken"]
            ?? throw new InvalidOperationException("Telegram:BotToken is missing.");

        _bot = new TelegramBotClient(token);

        var me = await _bot.GetMe(ct);
        logger.LogInformation("Telegram bot started: @{Username}", me.Username);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.InlineQuery, UpdateType.ChosenInlineResult, UpdateType.Message]
        };

        await _bot.ReceiveAsync(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: ct);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        logger.LogInformation("Telegram update: {Type}", update.Type);
        try
        {
            switch (update.Type)
            {
                case UpdateType.InlineQuery when update.InlineQuery is not null:
                    await HandleInlineQueryAsync(bot, update.InlineQuery, ct);
                    break;

                case UpdateType.ChosenInlineResult when update.ChosenInlineResult is not null:
                    await HandleChosenResultAsync(bot, update.ChosenInlineResult, ct);
                    break;

                case UpdateType.Message when update.Message is not null:
                    await HandleMessageAsync(bot, update.Message, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling update {UpdateId}", update.Id);
        }
    }

    private async Task HandleInlineQueryAsync(ITelegramBotClient bot, InlineQuery query, CancellationToken ct)
    {
        if (!IsAllowed(query.From.Id))
        {
            await bot.AnswerInlineQuery(query.Id, [], cancellationToken: ct);
            return;
        }

        var searchQuery = query.Query.Trim();
        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            await bot.AnswerInlineQuery(query.Id, [], cancellationToken: ct);
            return;
        }

        logger.LogInformation("Inline query from {User}: {Query}", query.From.Username, searchQuery);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var ytResults = await youTubeSearch.SearchAsync(searchQuery, maxResults: 8, ct);

        var videoIds = ytResults.Select(r => r.VideoId).ToList();
        var cachedTracks = await db.Tracks
            .Where(t => videoIds.Contains(t.YoutubeId) && t.TelegramFileId != null)
            .ToDictionaryAsync(t => t.YoutubeId, t => t.TelegramFileId!, ct);

        var inlineResults = new List<InlineQueryResult>();

        foreach (var r in ytResults)
        {
            if (cachedTracks.TryGetValue(r.VideoId, out var fileId))
            {
                inlineResults.Add(new InlineQueryResultCachedAudio(
                    id: r.VideoId,
                    audioFileId: fileId
                ));
            }
            else
            {
                inlineResults.Add(new InlineQueryResultArticle(
                    id: r.VideoId,
                    title: r.Title,
                    inputMessageContent: new InputTextMessageContent(
                        $"⏳ Скачиваю: *{r.Title}*\nАудио придёт в личных сообщениях бота.")
                    { ParseMode = ParseMode.Markdown }
                )
                {
                    Description = $"🎵 {r.ChannelName} · Нажми чтобы скачать",
                    ThumbnailUrl = r.ThumbnailUrl,
                });
            }
        }

        await bot.AnswerInlineQuery(
            query.Id,
            inlineResults,
            cacheTime: 30,
            cancellationToken: ct);
    }

    private async Task HandleChosenResultAsync(ITelegramBotClient bot, ChosenInlineResult chosen, CancellationToken ct)
    {
        if (!IsAllowed(chosen.From.Id)) return;

        var videoId = chosen.ResultId;
        var chatId = chosen.From.Id;

        logger.LogInformation("ChosenInlineResult: {VideoId} from {User}", videoId, chosen.From.Username);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await db.Tracks.FirstOrDefaultAsync(t => t.YoutubeId == videoId, ct);
        if (existing?.TelegramFileId is not null)
        {
            logger.LogInformation("Track {Id} already cached, inline audio was sent.", videoId);
            return;
        }

        var youtubeUrl = $"https://www.youtube.com/watch?v={videoId}";
        logger.LogInformation("Queuing download for {VideoId} → chat {ChatId}", videoId, chatId);

        var task = new DownloadTask
        {
            Id = Guid.NewGuid(),
            YoutubeUrl = youtubeUrl,
            CreatedAt = DateTime.UtcNow
        };
        db.DownloadTasks.Add(task);
        await db.SaveChangesAsync(ct);

        jobs.Enqueue<TelegramDownloadAndSendJob>(j =>
            j.ExecuteAsync(task.Id, chatId, CancellationToken.None));
    }

    private async Task HandleMessageAsync(ITelegramBotClient bot, Message message, CancellationToken ct)
    {
        if (message.From is null) return;
        if (!IsAllowed(message.From.Id))
        {
            await bot.SendMessage(message.Chat.Id, "⛔ Доступ запрещён.", cancellationToken: ct);
            return;
        }

        var text = message.Text ?? string.Empty;

        if (text == "/start")
        {
            await bot.SendMessage(message.Chat.Id,
                "🎵 *YtAudio Bot*\n\n" +
                "Используй меня inline в любом чате:\n" +
                "`@botusername название трека`\n\n" +
                "Уже скачанные треки появятся прямо как аудио.\n" +
                "Новые — скачаются и придут сюда в личку.\n\n" +
                "/status — статус последних задач",
                parseMode: ParseMode.Markdown,
                cancellationToken: ct);
            return;
        }

        if (text.Contains("youtube.com/watch") || text.Contains("youtu.be/"))
        {
            logger.LogInformation("Direct YouTube URL from {User}: {Url}", message.From.Username, text);

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var task = new DownloadTask
            {
                Id = Guid.NewGuid(),
                YoutubeUrl = text.Trim(),
                CreatedAt = DateTime.UtcNow
            };
            db.DownloadTasks.Add(task);
            await db.SaveChangesAsync(ct);

            await bot.SendMessage(message.Chat.Id,
                "⏳ Принято! Скачиваю аудио, жди...",
                cancellationToken: ct);

            jobs.Enqueue<TelegramDownloadAndSendJob>(j =>
                j.ExecuteAsync(task.Id, message.Chat.Id, CancellationToken.None));
        }
    }

    public static async Task<string> SendAudioFromStorageAsync(
        ITelegramBotClient bot, long chatId, Models.Track track, CancellationToken ct)
    {
        await using var stream = File.OpenRead(track.FilePath);

        InputFile? thumbnail = null;
        MemoryStream? thumbStream = null;

        if (!string.IsNullOrEmpty(track.ThumbnailUrl))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var bytes = await http.GetByteArrayAsync(track.ThumbnailUrl, ct);
                thumbStream = new MemoryStream(bytes);
                thumbnail = InputFile.FromStream(thumbStream, "thumb.jpg");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Thumbnail] Failed to load: {ex.Message}");
            }
        }

        var fileName = track.Title + track.FileExtension;
        var msg = await bot.SendAudio(
            chatId,
            InputFile.FromStream(stream, fileName),
            title: track.Title,
            performer: track.Artist,
            thumbnail: thumbnail,
            cancellationToken: ct);

        thumbStream?.Dispose();

        return msg.Audio?.FileId ?? string.Empty;
    }

    private bool IsAllowed(long userId) => _allowedUserIds.Contains(userId);

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        logger.LogError(ex, "Telegram polling error");
        return Task.CompletedTask;
    }
}