using System.Collections.Concurrent;
using System.Text.Json;

namespace ChatLab;

public sealed class ChatStore
{
    public const long MaximumFileSize = 2L * 1024 * 1024 * 1024;
    private const int HistoryLimit = 300;
    private const int BufferSize = 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico"
        };

    private static readonly JsonSerializerOptions HistoryJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filesDirectory;
    private readonly string _historyPath;
    private readonly List<ChatMessage> _history = [];
    private readonly ConcurrentDictionary<Guid, ChatMessage> _files = new();
    private readonly object _historyLock = new();
    private readonly SemaphoreSlim _appendLock = new(1, 1);

    public ChatStore()
    {
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        _filesDirectory = Path.Combine(dataDirectory, "files");
        _historyPath = Path.Combine(dataDirectory, "history.jsonl");
        Directory.CreateDirectory(_filesDirectory);

        if (!File.Exists(_historyPath))
        {
            return;
        }

        foreach (var line in File.ReadLines(_historyPath))
        {
            try
            {
                var message = JsonSerializer.Deserialize<ChatMessage>(line, HistoryJsonOptions);
                if (message is null)
                {
                    continue;
                }

                _history.Add(message);
                if (message.Type == "file" && message.FileId != Guid.Empty)
                {
                    _files[message.FileId] = message;
                }
            }
            catch (JsonException)
            {
                // Ignore an incomplete or invalid history line and continue loading later entries.
            }
        }
    }

    public ChatMessage[] GetRecentHistory()
    {
        lock (_historyLock)
        {
            return _history.TakeLast(HistoryLimit).ToArray();
        }
    }

    public ChatMessage? GetFile(Guid id) =>
        _files.TryGetValue(id, out var message) ? message : null;

    public string GetFilePath(Guid id) =>
        Path.Combine(_filesDirectory, id.ToString("N") + ".bin");

    public string GetImageContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".tif" or ".tiff" => "image/tiff",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream"
        };

    public async Task SaveMessageAsync(ChatMessage message, CancellationToken cancellationToken)
    {
        await _appendLock.WaitAsync(cancellationToken);
        try
        {
            var json = JsonSerializer.Serialize(message);
            await File.AppendAllTextAsync(_historyPath, json + Environment.NewLine, cancellationToken);
            lock (_historyLock)
            {
                _history.Add(message);
            }
        }
        finally
        {
            _appendLock.Release();
        }
    }

    public async Task<ChatMessage> SaveFileAsync(
        Stream source,
        string requestedName,
        string sender,
        long? expectedLength,
        CancellationToken cancellationToken)
    {
        var fileName = SanitizeFileName(requestedName);
        if (expectedLength is < 0 or > MaximumFileSize)
        {
            throw new UploadTooLargeException();
        }

        var id = Guid.NewGuid();
        var temporaryPath = Path.Combine(_filesDirectory, id.ToString("N") + ".tmp");
        var finalPath = GetFilePath(id);
        long totalBytes = 0;

        try
        {
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[BufferSize];
                while (true)
                {
                    var bytesRead = await source.ReadAsync(buffer, cancellationToken);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    totalBytes += bytesRead;
                    if (totalBytes > MaximumFileSize)
                    {
                        throw new UploadTooLargeException();
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                }

                await destination.FlushAsync(cancellationToken);
            }

            if (expectedLength.HasValue && totalBytes != expectedLength.Value)
            {
                throw new InvalidDataException("The uploaded file size did not match the request length.");
            }

            File.Move(temporaryPath, finalPath);
            var message = new ChatMessage
            {
                Type = "file",
                Sender = sender,
                Time = DateTime.Now,
                FileId = id,
                FileName = fileName,
                FileSize = totalBytes,
                IsImage = ImageExtensions.Contains(Path.GetExtension(fileName))
            };

            try
            {
                await SaveMessageAsync(message, cancellationToken);
                _files[id] = message;
            }
            catch
            {
                File.Delete(finalPath);
                throw;
            }

            return message;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string SanitizeFileName(string requestedName)
    {
        var fileName = Path.GetFileName(requestedName.Replace('\\', '/')).Trim();
        if (fileName.Length is 0 or > 255 || fileName.Any(char.IsControl))
        {
            throw new ArgumentException("The file name must be between 1 and 255 characters.");
        }

        return fileName;
    }

    public sealed class UploadTooLargeException : Exception
    {
    }
}
