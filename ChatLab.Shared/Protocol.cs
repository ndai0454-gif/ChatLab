using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;

namespace ChatLab.Shared;

public static class Roles
{
    public const string Chat = "chat";
    public const string Upload = "upload";
    public const string Download = "download";
}

public static class MessageTypes
{
    public const string Chat = "chat";
    public const string File = "file";
    public const string Users = "users";
    public const string System = "system";
}

/// <summary>First frame of every TCP connection. Tells the server what the connection is for.</summary>
public class Hello
{
    public string Role { get; set; } = Roles.Chat;
    public string Sender { get; set; } = "";

    // Upload / download: one connection moves one byte range [Offset, Offset + Length) of a file.
    public Guid FileId { get; set; }
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public bool IsImage { get; set; }
    public long Offset { get; set; }
    public long Length { get; set; }
}

/// <summary>Everything that travels on a chat connection (and what is persisted as history).</summary>
public class ChatMessage
{
    public string Type { get; set; } = MessageTypes.Chat;
    public string Sender { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;

    public Guid FileId { get; set; }
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public bool IsImage { get; set; }

    public List<string> Users { get; set; } = [];
}

public static class Wire
{
    public const int MaxFrame = 1024 * 1024;
    public const int BufferSize = 1024 * 1024;

    /// <summary>Length-prefixed JSON frame: [int32 length][utf8 json].</summary>
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value);
        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, json.Length);
        json.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct = default)
    {
        var head = new byte[4];
        if (!await TryReadExactAsync(stream, head, ct)) return default;
        var len = BinaryPrimitives.ReadInt32BigEndian(head);
        if (len <= 0 || len > MaxFrame) throw new InvalidDataException($"Bad frame length {len}");
        var body = new byte[len];
        if (!await TryReadExactAsync(stream, body, ct)) return default;
        return JsonSerializer.Deserialize<T>(body);
    }

    public static async Task<bool> TryReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct = default)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes from the network into a file at <paramref name="offset"/>.
    /// Uses positional I/O (RandomAccess) so many connections can write one file at the same time.
    /// </summary>
    public static async Task NetworkToFileAsync(Stream net, Microsoft.Win32.SafeHandles.SafeFileHandle file,
        long offset, long length, Action<int>? onBytes, CancellationToken ct)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long done = 0;
            while (done < length)
            {
                var want = (int)Math.Min(buffer.Length, length - done);
                var n = await net.ReadAsync(buffer.AsMemory(0, want), ct);
                if (n == 0) throw new EndOfStreamException("Connection closed mid-transfer.");
                await RandomAccess.WriteAsync(file, buffer.AsMemory(0, n), offset + done, ct);
                done += n;
                onBytes?.Invoke(n);
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static async Task FileToNetworkAsync(Microsoft.Win32.SafeHandles.SafeFileHandle file, Stream net,
        long offset, long length, Action<int>? onBytes, CancellationToken ct)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long done = 0;
            while (done < length)
            {
                var want = (int)Math.Min(buffer.Length, length - done);
                var n = await RandomAccess.ReadAsync(file, buffer.AsMemory(0, want), offset + done, ct);
                if (n == 0) throw new EndOfStreamException("File ended early.");
                await net.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                onBytes?.Invoke(n);
            }
            await net.FlushAsync(ct);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static void Tune(TcpClient client)
    {
        client.NoDelay = true;
        client.ReceiveBufferSize = 1 << 20;
        client.SendBufferSize = 1 << 20;
    }

    /// <summary>Splits [0,size) into at most <paramref name="maxParts"/> contiguous ranges (min 8 MB each).</summary>
    public static List<(long Offset, long Length)> Split(long size, int maxParts)
    {
        const long minPart = 8L * 1024 * 1024;
        var parts = (int)Math.Clamp(size / minPart, 1, maxParts);
        var chunk = (size + parts - 1) / parts;
        var list = new List<(long, long)>();
        for (long off = 0; off < size; off += chunk)
            list.Add((off, Math.Min(chunk, size - off)));
        if (list.Count == 0) list.Add((0, 0));
        return list;
    }
}
