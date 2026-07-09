using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using YtAudio.Api.Data;
using YtAudio.Api.Models;
using YtAudio.Api.Services;
using YtAudio.Api.Utils;

namespace YtAudio.Api.Jobs;

public class TelegramDownloadAndSendJob(
    AppDbContext db,
    YtDlpService ytDlp,
    FileStorageService storage,
    IConfiguration config,
    ILogger<TelegramDownloadAndSendJob> logger)
{
    public async Task ExecuteAsync(Guid taskId, long chatId, CancellationToken ct = default)
    {
        var token = config["Telegram:BotToken"]
            ?? throw new InvalidOperationException("Telegram:BotToken is missing.");
        var bot = new TelegramBotClient(token);

        var task = await db.DownloadTasks.FindAsync([taskId], ct)
            ?? throw new InvalidOperationException($"Task {taskId} not found.");

        task.Status = DownloadStatus.Processing;
        await db.SaveChangesAsync(ct);

        try
        {
            var meta = await ytDlp.GetMetadataAsync(task.YoutubeUrl, ct);

            var existing = await db.Tracks.FirstOrDefaultAsync(t => t.YoutubeId == meta.YoutubeId, ct);
            if (existing is not null)
            {
                logger.LogInformation("Track {Id} already exists, sending from storage.", meta.YoutubeId);

                var fileId = await TelegramBotService.SendAudioFromStorageAsync(bot, chatId, existing, ct);
                if (!string.IsNullOrEmpty(fileId) && existing.TelegramFileId is null)
                {
                    existing.TelegramFileId = fileId;
                }

                task.Status = DownloadStatus.Completed;
                task.TrackId = existing.Id;
                task.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            logger.LogInformation("Downloading {Url} for chat {ChatId}", task.YoutubeUrl, chatId);
            var tempDir = storage.CreateTempDirectory();
            var tempFile = await ytDlp.DownloadBestAudioAsync(task.YoutubeUrl, tempDir, ct);
            var finalPath = storage.MoveToStorage(tempFile, meta.YoutubeId, meta.Title, meta.Artist, meta.Album);
            var fileInfo = new FileInfo(finalPath);
            var fileName = StringExtensions.BuildFileName(meta.YoutubeId ,meta.Title);

            var track = new Track
            {
                Id = Guid.NewGuid(),
                YoutubeId = meta.YoutubeId,
                Title = fileName,
                Artist = meta.Artist,
                Album = meta.Album,
                ThumbnailUrl = meta.ThumbnailUrl,
                DurationSeconds = meta.DurationSeconds,
                FilePath = finalPath,
                FileExtension = fileInfo.Extension.TrimStart('.'),
                FileSizeBytes = fileInfo.Length,
                CreatedAt = DateTime.UtcNow
            };

            logger.LogInformation("Sending audio {Title} to chat {ChatId}", track.Title, chatId);
            var telegramFileId = await TelegramBotService.SendAudioFromStorageAsync(bot, chatId, track, ct);

            if (!string.IsNullOrEmpty(telegramFileId))
            {
                track.TelegramFileId = telegramFileId;
                logger.LogInformation("Cached Telegram file_id for {YoutubeId}", track.YoutubeId);
            }

            db.Tracks.Add(track);
            task.Status = DownloadStatus.Completed;
            task.TrackId = track.Id;
            task.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TelegramDownloadAndSendJob failed for task {TaskId}", taskId);
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);

            try
            {
                await bot.SendMessage(chatId,
                    $"❌ Не удалось скачать трек.\n`{ex.Message}`",
                    parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                    cancellationToken: CancellationToken.None);
            }
            catch { }

            throw;
        }
    }
}