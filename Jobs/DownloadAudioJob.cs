using Microsoft.EntityFrameworkCore;
using YtAudio.Api.Data;
using YtAudio.Api.Models;
using YtAudio.Api.Services;

namespace YtAudio.Api.Jobs
{
    public class DownloadAudioJob(
        AppDbContext db,
        YtDlpService ytDlp,
        FileStorageService storage,
        ILogger<DownloadAudioJob> logger)
    {

        public async Task ExecuteAsync(Guid taskId, CancellationToken ct = default)
        {
            var task = await db.DownloadTasks.FindAsync([taskId], ct)
            ?? throw new InvalidOperationException($"Task {taskId} not found.");

            task.Status = DownloadStatus.Processing;
            await db.SaveChangesAsync(ct);

            try
            {
                logger.LogInformation("[{Task}] Fetching metadata for {Url}", taskId, task.YoutubeUrl);
                var meta = await ytDlp.GetMetadataAsync(task.YoutubeUrl, ct);

                var existing = await db.Tracks.FirstOrDefaultAsync(t => t.YoutubeId == meta.YoutubeId, ct);
                if (existing is not null)
                {
                    logger.LogInformation("[{Task}] Track {Id} already exists, skipping download.", taskId, meta.YoutubeId);
                    task.Status = DownloadStatus.Completed;
                    task.TrackId = existing.Id;
                    task.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    return;
                }

                logger.LogInformation("[{Task}] Downloading audio for {Id}…", taskId, meta.YoutubeId);
                var tempDir = storage.CreateTempDirectory();
                var tempFile = await ytDlp.DownloadBestAudioAsync(task.YoutubeUrl, tempDir, ct);

                var finalPath = storage.MoveToStorage(tempFile, meta.YoutubeId, meta.Title, meta.Artist, meta.Album);
                var fileInfo = new FileInfo(finalPath);

                var track = new Track
                {
                    Id = Guid.NewGuid(),
                    YoutubeId = meta.YoutubeId,
                    Title = meta.Title,
                    Artist = meta.Artist,
                    Album = meta.Album,
                    ThumbnailUrl = meta.ThumbnailUrl,
                    DurationSeconds = meta.DurationSeconds,
                    FilePath = finalPath,
                    FileExtension = fileInfo.Extension.TrimStart('.'),
                    FileSizeBytes = fileInfo.Length,
                    CreatedAt = DateTime.UtcNow
                };

                db.Tracks.Add(track);
                task.Status = DownloadStatus.Completed;
                task.TrackId = track.Id;
                task.CompletedAt = DateTime.UtcNow;

                await db.SaveChangesAsync(ct);
                logger.LogInformation("[{Task}] ✓ Completed — {Title}", taskId, track.Title);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("[{Task}] Cancelled.", taskId);
                task.Status = DownloadStatus.Failed;
                task.ErrorMessage = "Cancelled";
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[{Task}] Failed.", taskId);
                task.Status = DownloadStatus.Failed;
                task.ErrorMessage = ex.Message;
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }
        }


    }
}
