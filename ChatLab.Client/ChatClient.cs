using System.IO;
using System.Net.Sockets;
using ChatLab.Shared;

namespace ChatLab.Client;

/// <summary>
/// Async TCP client. One long-lived "chat" connection for messages, plus short-lived connections
/// opened in parallel (one per byte range) to upload / download files.
/// </summary>
public sealed class ChatClient : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private SynchronizationContext? _ui;
    private string _host = "";
    private int _port;

    public string Name { get; private set; } = "";

    /// <summary>Raised on the thread that called <see cref="ConnectAsync"/> (the UI thread).</summary>
    public event Action<ChatMessage>? MessageReceived;
    public event Action? Disconnected;

    public async Task ConnectAsync(string host, int port, string name)
    {
        _ui = SynchronizationContext.Current;
        _host = host;
        _port = port;
        Name = name;

        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port, _cts.Token);
        Wire.Tune(_tcp);
        _stream = _tcp.GetStream();
        await Wire.WriteAsync(_stream, new Hello { Role = Roles.Chat, Sender = name }, _cts.Token);

        _ = ReceiveLoopAsync();
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (true)
            {
                var m = await Wire.ReadAsync<ChatMessage>(_stream!, _cts.Token);
                if (m is null) break;
                Post(() => MessageReceived?.Invoke(m));
            }
        }
        catch (Exception) { /* connection dropped or disposed */ }
        finally { Post(() => Disconnected?.Invoke()); }
    }

    private void Post(Action a)
    {
        if (_ui is null) a();
        else _ui.Post(_ => a(), null);
    }

    public async Task SendTextAsync(string text)
    {
        await _writeLock.WaitAsync(_cts.Token);
        try { await Wire.WriteAsync(_stream!, new ChatMessage { Type = MessageTypes.Chat, Text = text }, _cts.Token); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Uploads a file of any size: the file is cut into ranges and every range is sent on its own connection, in parallel.</summary>
    public async Task UploadAsync(Guid fileId, string path, bool isImage, TransferProgress progress, CancellationToken ct)
    {
        var name = Path.GetFileName(path);
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);

        await Parallel.ForEachAsync(Enumerable.Range(0, progress.Parts.Count),
            new ParallelOptions { MaxDegreeOfParallelism = progress.Parts.Count, CancellationToken = ct },
            async (i, token) =>
            {
                var (offset, length) = progress.Parts[i];
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(_host, _port, token);
                Wire.Tune(tcp);
                var s = tcp.GetStream();

                await Wire.WriteAsync(s, new Hello
                {
                    Role = Roles.Upload, Sender = Name, FileId = fileId, FileName = name,
                    FileSize = progress.Size, IsImage = isImage, Offset = offset, Length = length
                }, token);

                await Wire.FileToNetworkAsync(file, s, offset, length, n => progress.Add(i, n), token);

                var ack = new byte[1];
                if (!await Wire.TryReadExactAsync(s, ack, token))
                    throw new IOException("Server closed the connection before acknowledging the upload.");
            });
    }

    /// <summary>Downloads a file with the same parallel range scheme, writing every range straight into the destination file.</summary>
    public async Task DownloadAsync(ChatMessage msg, string destPath, TransferProgress progress, CancellationToken ct)
    {
        try
        {
            using var file = File.OpenHandle(destPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, FileOptions.Asynchronous);
            RandomAccess.SetLength(file, msg.FileSize);

            await Parallel.ForEachAsync(Enumerable.Range(0, progress.Parts.Count),
                new ParallelOptions { MaxDegreeOfParallelism = progress.Parts.Count, CancellationToken = ct },
                async (i, token) =>
                {
                    var (offset, length) = progress.Parts[i];
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync(_host, _port, token);
                    Wire.Tune(tcp);
                    var s = tcp.GetStream();

                    await Wire.WriteAsync(s, new Hello
                    {
                        Role = Roles.Download, Sender = Name, FileId = msg.FileId, Offset = offset, Length = length
                    }, token);

                    await Wire.NetworkToFileAsync(s, file, offset, length, n => progress.Add(i, n), token);
                });
        }
        catch
        {
            try { File.Delete(destPath); } catch (IOException) { }
            throw;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _tcp?.Dispose();
    }
}
