using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChatLab;

public sealed class ChatRoom(ChatStore store, ILogger<ChatRoom> logger)
{
    private const int MaximumCommandSize = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, ChatConnection> _connections = new();
    private readonly ConcurrentDictionary<string, ChatIdentity> _sessions = new();

    public bool TryGetIdentity(string token, out ChatIdentity identity) =>
        _sessions.TryGetValue(token, out identity!);

    public async Task HandleClientAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var connection = new ChatConnection(socket);
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var payload = await ReceiveTextAsync(socket, cancellationToken);
                if (payload is null)
                {
                    break;
                }

                ClientCommand? command;
                try
                {
                    command = JsonSerializer.Deserialize<ClientCommand>(payload, JsonOptions);
                }
                catch (JsonException)
                {
                    await SendSafeAsync(connection, new { type = "error", text = "Invalid message format." }, cancellationToken);
                    continue;
                }

                if (command is null)
                {
                    continue;
                }

                if (connection.Identity is null)
                {
                    if (!string.Equals(command.Type, "join", StringComparison.Ordinal))
                    {
                        await SendSafeAsync(connection, new { type = "error", text = "Join the chat before sending messages." }, cancellationToken);
                        continue;
                    }

                    await JoinAsync(connection, command.Name ?? "", cancellationToken);
                    continue;
                }

                if (string.Equals(command.Type, "send", StringComparison.Ordinal))
                {
                    await SendChatMessageAsync(connection, command.Text ?? "", cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException ex)
        {
            logger.LogDebug(ex, "A ChatLab websocket disconnected unexpectedly.");
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "A ChatLab websocket connection ended with an I/O error.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while handling a ChatLab websocket.");
            if (socket.State == WebSocketState.Open)
            {
                await CloseSafelyAsync(socket, WebSocketCloseStatus.InternalServerError, "Chat connection failed.");
            }
        }
        finally
        {
            if (connection.Identity is not null)
            {
                _connections.TryRemove(connection.ConnectionId, out _);
                _sessions.TryRemove(connection.Identity.Token, out _);
                await BroadcastAsync(new { type = "system", text = $"{connection.Identity.Name} left the chat" }, CancellationToken.None);
                await BroadcastPresenceAsync(CancellationToken.None);
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await CloseSafelyAsync(socket, WebSocketCloseStatus.NormalClosure, "Disconnected.");
            }
        }
    }

    public async Task BroadcastMessageAsync(ChatMessage message, CancellationToken cancellationToken) =>
        await BroadcastAsync(new { type = "message", message }, cancellationToken);

    private async Task JoinAsync(ChatConnection connection, string requestedName, CancellationToken cancellationToken)
    {
        var name = requestedName.Trim();
        if (name.Length is 0 or > 32 || name.Any(char.IsControl))
        {
            await SendSafeAsync(connection, new { type = "error", text = "Choose a name between 1 and 32 characters." }, cancellationToken);
            return;
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var identity = new ChatIdentity(name, token);
        connection.Identity = identity;
        _connections[connection.ConnectionId] = connection;
        _sessions[token] = identity;

        await SendSafeAsync(connection, new
        {
            type = "joined",
            sessionToken = token,
            history = store.GetRecentHistory()
        }, cancellationToken);

        await BroadcastAsync(new { type = "system", text = $"{name} joined the chat" }, cancellationToken);
        await BroadcastPresenceAsync(cancellationToken);
    }

    private async Task SendChatMessageAsync(
        ChatConnection connection,
        string requestedText,
        CancellationToken cancellationToken)
    {
        var text = requestedText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (text.Length > 10_000)
        {
            await SendSafeAsync(connection, new { type = "error", text = "Messages cannot exceed 10,000 characters." }, cancellationToken);
            return;
        }

        var message = new ChatMessage
        {
            Type = "chat",
            Sender = connection.Identity!.Name,
            Text = text,
            Time = DateTime.Now
        };

        await store.SaveMessageAsync(message, cancellationToken);
        await BroadcastMessageAsync(message, cancellationToken);
    }

    private Task BroadcastPresenceAsync(CancellationToken cancellationToken)
    {
        var users = _connections.Values
            .Select(connection => connection.Identity!.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return BroadcastAsync(new { type = "presence", users }, cancellationToken);
    }

    private async Task BroadcastAsync(object message, CancellationToken cancellationToken)
    {
        var connections = _connections.Values.ToArray();
        await Task.WhenAll(connections.Select(connection =>
            SendSafeAsync(connection, message, cancellationToken)));
    }

    private async Task SendSafeAsync(
        ChatConnection connection,
        object message,
        CancellationToken cancellationToken)
    {
        try
        {
            await connection.SendAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            logger.LogDebug(ex, "Could not send a message to ChatLab connection {ConnectionId}.", connection.ConnectionId);
        }
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                await CloseSafelyAsync(socket, WebSocketCloseStatus.InvalidMessageType, "Only text messages are supported.");
                return null;
            }

            if (message.Length + result.Count > MaximumCommandSize)
            {
                await CloseSafelyAsync(socket, WebSocketCloseStatus.MessageTooBig, "Message is too large.");
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
    }

    private static async Task CloseSafelyAsync(
        WebSocket socket,
        WebSocketCloseStatus status,
        string description)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(status, description, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
        }
    }

    private sealed class ChatConnection(WebSocket socket)
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public ChatIdentity? Identity { get; set; }

        public async Task SendAsync(object message, CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }

    private sealed class ClientCommand
    {
        public string Type { get; set; } = "";
        public string? Name { get; set; }
        public string? Text { get; set; }
    }
}
