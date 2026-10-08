using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ChatLab.Shared;

namespace ChatLab;

/// <summary>
/// One listening port, three kinds of connection (see <see cref="Roles"/>):
/// chat (long lived, JSON frames), upload and download (short lived, one byte range each).
/// Every connection is served by its own async task, so many clients and many file ranges run in parallel.
/// </summary>
public class ChatServer
{
    private sealed class ChatClientConn(TcpClient tcp, string name)
    {
        public TcpClient Tcp { get; } = tcp;
        public string Name { get; } = name;
        public NetworkStream Stream { get; } = tcp.GetStream();
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }

    private sealed class Upload
    {
        public required Hello Info { get; init; }
        public required string Path { get; init; }
        public long Received;
        public int Completed; // guards "announce once"
        public object InitLock { get; } = new();
    }

    private readonly int _port;
    private readonly string _filesDir;
    private readonly string _historyPath;
    private readonly ConcurrentDictionary<Guid, ChatClientConn> _clients = new();
    private readonly ConcurrentDictionary<Guid, Upload> _uploads = new();
    private readonly ConcurrentDictionary<Guid, ChatMessage> _files = new();
    private readonly List<ChatMessage> _history = [];
    private readonly object _historyLock = new();

    public ChatServer(int port, string dataDir)
    {
        _port = port;
        _filesDir = Path.Combine(dataDir, "files");
        _historyPath = Path.Combine(dataDir, "history.jsonl");
        Directory.CreateDirectory(_filesDir);
        LoadHistory();
    }

    private void LoadHistory()
    {
        if (!File.Exists(_historyPath)) return;
        foreach (var line in File.ReadLines(_historyPath))
        {
            try
            {
                var m = JsonSerializer.Deserialize<ChatMessage>(line);
                if (m is null) continue;
                _history.Add(m);
                if (m.Type == MessageTypes.File) _files[m.FileId] = m;
            }
            catch (JsonException) { }
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start(512);
        Console.WriteLine($"ChatLab server listening on port {_port}  (Ctrl+C to stop)");
        Console.WriteLine($"Files + history are kept in {Path.GetDirectoryName(_historyPath)}");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(ct);
                Wire.Tune(tcp);
                _ = HandleAsync(tcp, ct); // fire and forget: each connection is its own task
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    private async Task HandleAsync(TcpClient tcp, CancellationToken ct)
    {
        try
        {
            using (tcp)
            {
                var stream = tcp.GetStream();
                var hello = await Wire.ReadAsync<Hello>(stream, ct);
                if (hello is null) return;

                switch (hello.Role)
                {
                    case Roles.Chat: await HandleChatAsync(tcp, hello, ct); break;
                    case Roles.Upload: await HandleUploadAsync(stream, hello, ct); break;
                    case Roles.Download: await HandleDownloadAsync(stream, hello, ct); break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or EndOfStreamException or InvalidDataException)
        {
            // client went away / bad peer - nothing to do
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Connection error: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- chat

    private async Task HandleChatAsync(TcpClient tcp, Hello hello, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(hello.Sender) ? "Guest" : hello.Sender.Trim();
        var id = Guid.NewGuid();
        var conn = new ChatClientConn(tcp, name);
        _clients[id] = conn;
        Console.WriteLine($"+ {name} joined ({_clients.Count} online)");

        try
        {
            ChatMessage[] snapshot;
            lock (_historyLock) snapshot = _history.TakeLast(300).ToArray();
            foreach (var m in snapshot) await SendAsync(conn, m, ct);

            await BroadcastAsync(new ChatMessage { Type = MessageTypes.System, Text = $"{name} joined the chat" }, persist: false, ct);
            await BroadcastUsersAsync(ct);

            while (true)
            {
                var msg = await Wire.ReadAsync<ChatMessage>(conn.Stream, ct);
                if (msg is null) break;
                if (msg.Type != MessageTypes.Chat || string.IsNullOrWhiteSpace(msg.Text)) continue;

                msg.Sender = name; // never trust the sender name claimed by the client
                msg.Time = DateTime.Now;
                await BroadcastAsync(msg, persist: true, ct);
            }
        }
        finally
        {
            _clients.TryRemove(id, out _);
            Console.WriteLine($"- {name} left ({_clients.Count} online)");
            await BroadcastAsync(new ChatMessage { Type = MessageTypes.System, Text = $"{name} left the chat" }, persist: false, CancellationToken.None);
            await BroadcastUsersAsync(CancellationToken.None);
        }
    }

    private static async Task SendAsync(ChatClientConn c, ChatMessage m, CancellationToken ct)
    {
        await c.WriteLock.WaitAsync(ct);
        try { await Wire.WriteAsync(c.Stream, m, ct); }
        finally { c.WriteLock.Release(); }
    }

    /// <summary>Delivers to every client in parallel so one slow receiver cannot delay the others.</summary>
    private async Task BroadcastAsync(ChatMessage m, bool persist, CancellationToken ct)
    {
        if (persist)
        {
            lock (_historyLock)
            {
                _history.Add(m);
                File.AppendAllText(_historyPath, JsonSerializer.Serialize(m) + Environment.NewLine);
            }
        }

        await Parallel.ForEachAsync(_clients.Values.ToArray(), new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 16 },
            async (c, token) =>
            {
                try { await SendAsync(c, m, token); }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
            });
    }

    private Task BroadcastUsersAsync(CancellationToken ct) =>
        BroadcastAsync(new ChatMessage
        {
            Type = MessageTypes.Users,
            Users = _clients.Values.Select(c => c.Name).Distinct().Order().ToList()
        }, persist: false, ct);

    // ---------------------------------------------------------------- upload

    private async Task HandleUploadAsync(NetworkStream stream, Hello h, CancellationToken ct)
    {
        if (h.FileSize < 0 || h.Offset < 0 || h.Length < 0 || h.Offset + h.Length > h.FileSize) return;

        var up = _uploads.GetOrAdd(h.FileId, _ => new Upload
        {
            Info = h,
            Path = System.IO.Path.Combine(_filesDir, h.FileId.ToString("N") + ".bin")
        });

        // Every range opens the same file with its own handle and writes at its own offset.
        using var handle = File.OpenHandle(up.Path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, FileOptions.Asynchronous);
        lock (up.InitLock)
        {
            if (RandomAccess.GetLength(handle) < h.FileSize) RandomAccess.SetLength(handle, h.FileSize);
        }

        await Wire.NetworkToFileAsync(stream, handle, h.Offset, h.Length,
            n => Interlocked.Add(ref up.Received, n), ct);

        await stream.WriteAsync(new byte[] { 1 }, ct); // ack for this range

        if (Interlocked.Read(ref up.Received) >= h.FileSize && Interlocked.Exchange(ref up.Completed, 1) == 0)
        {
            _uploads.TryRemove(h.FileId, out _);
            var msg = new ChatMessage
            {
                Type = MessageTypes.File,
                Sender = up.Info.Sender,
                Time = DateTime.Now,
                FileId = h.FileId,
                FileName = System.IO.Path.GetFileName(up.Info.FileName),
                FileSize = h.FileSize,
                IsImage = up.Info.IsImage
            };
            _files[msg.FileId] = msg;
            Console.WriteLine($"* {msg.Sender} sent {(msg.IsImage ? "image" : "file")} {msg.FileName} ({h.FileSize / 1048576.0:F1} MB)");
            await BroadcastAsync(msg, persist: true, CancellationToken.None);
        }
    }

    // ---------------------------------------------------------------- download

    private async Task HandleDownloadAsync(NetworkStream stream, Hello h, CancellationToken ct)
    {
        if (!_files.TryGetValue(h.FileId, out var meta)) return;
        if (h.Offset < 0 || h.Length < 0 || h.Offset + h.Length > meta.FileSize) return;

        var path = System.IO.Path.Combine(_filesDir, h.FileId.ToString("N") + ".bin");
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.Asynchronous);
        await Wire.FileToNetworkAsync(handle, stream, h.Offset, h.Length, null, ct);
    }
}
