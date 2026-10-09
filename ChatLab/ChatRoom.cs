using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ChatLab.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ChatLab;

public sealed class ChatRoom(
    ChatDbContext db,
    UserManager<IdentityUser> users,
    ChatStore store,
    ILogger<ChatRoom> logger)
{
    private const int MaximumCommandSize = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, ChatConnection> _connections = new();
    private readonly SemaphoreSlim _directLock = new(1, 1);

    public async Task HandleClientAsync(WebSocket socket, string userId, string authenticatedName, CancellationToken cancellationToken)
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
                        await SendSafeAsync(connection, new { type = "error", text = "Join before using chat." }, cancellationToken);
                        continue;
                    }

                    await JoinAsync(connection, userId, authenticatedName, cancellationToken);
                    continue;
                }

                switch (command.Type)
                {
                    case "openDirect":
                        await OpenDirectAsync(connection, command.UserId ?? "", cancellationToken);
                        break;
                    case "select":
                        await SelectConversationAsync(connection, command.ConversationId ?? "", cancellationToken);
                        break;
                    case "createGroup":
                        await CreateGroupAsync(connection, command.Name ?? "", command.MemberIds ?? [], cancellationToken);
                        break;
                    case "send":
                        await SendChatMessageAsync(connection, command.ConversationId ?? "", command.Text ?? "", cancellationToken);
                        break;
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
                await BroadcastDirectoryAsync(CancellationToken.None);
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await CloseSafelyAsync(socket, WebSocketCloseStatus.NormalClosure, "Disconnected.");
            }
        }
    }

    public async Task<ChatMessage> SaveFileAsync(
        string userId,
        string userName,
        string conversationId,
        Stream source,
        string fileName,
        long? expectedLength,
        CancellationToken cancellationToken)
    {
        await EnsureMemberAsync(conversationId, userId, cancellationToken);
        var message = await store.SaveFileAsync(
            source, fileName, userName, conversationId, expectedLength, cancellationToken);
        await SaveMessageAsync(message, userId, cancellationToken);
        await BroadcastConversationMessageAsync(message, cancellationToken);
        return message;
    }

    private async Task JoinAsync(ChatConnection connection, string userId, string name, CancellationToken cancellationToken)
    {
        connection.Identity = new ChatIdentity(userId, name);
        _connections[connection.ConnectionId] = connection;
        await SendSafeAsync(connection, new
        {
            type = "joined",
            users = await GetDirectoryAsync(),
            conversations = await GetConversationsAsync(userId)
        }, cancellationToken);
        await BroadcastDirectoryAsync(cancellationToken);
    }

    private async Task OpenDirectAsync(ChatConnection connection, string targetUserId, CancellationToken cancellationToken)
    {
        var currentUserId = connection.Identity!.Id;
        if (targetUserId == currentUserId || await users.FindByIdAsync(targetUserId) is null)
        {
            await SendErrorAsync(connection, "That user is not available.", cancellationToken);
            return;
        }

        var directKey = string.CompareOrdinal(currentUserId, targetUserId) < 0
            ? $"{currentUserId}:{targetUserId}"
            : $"{targetUserId}:{currentUserId}";

        await _directLock.WaitAsync(cancellationToken);
        try
        {
            var conversation = await db.Conversations.FirstOrDefaultAsync(
                item => item.DirectKey == directKey, cancellationToken);
            if (conversation is null)
            {
                var otherUser = await users.FindByIdAsync(targetUserId);
                conversation = new ChatConversation
                {
                    Name = otherUser!.UserName ?? "Direct message",
                    DirectKey = directKey,
                    CreatedBy = currentUserId
                };
                db.Conversations.Add(conversation);
                db.ConversationMembers.AddRange(
                    new ChatConversationMember { Conversation = conversation, UserId = currentUserId },
                    new ChatConversationMember { Conversation = conversation, UserId = targetUserId });
                await db.SaveChangesAsync(cancellationToken);
            }

            await PublishConversationListsAsync([currentUserId, targetUserId], cancellationToken);
            await SelectConversationAsync(connection, conversation.Id, cancellationToken);
        }
        finally
        {
            _directLock.Release();
        }
    }

    private async Task CreateGroupAsync(
        ChatConnection connection,
        string requestedName,
        IEnumerable<string> requestedMemberIds,
        CancellationToken cancellationToken)
    {
        var name = requestedName.Trim();
        var memberIds = requestedMemberIds
            .Append(connection.Identity!.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (name.Length is 0 or > 60 || memberIds.Length > 50)
        {
            await SendErrorAsync(connection, "Enter a group name (up to 60 characters) and select at most 49 other members.", cancellationToken);
            return;
        }

        var validMembers = await users.Users
            .Where(user => memberIds.Contains(user.Id))
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);
        if (validMembers.Count != memberIds.Length)
        {
            await SendErrorAsync(connection, "One or more selected users no longer exist.", cancellationToken);
            return;
        }

        var conversation = new ChatConversation
        {
            Name = name,
            IsGroup = true,
            CreatedBy = connection.Identity.Id
        };
        db.Conversations.Add(conversation);
        foreach (var memberId in memberIds)
        {
            db.ConversationMembers.Add(new ChatConversationMember
            {
                Conversation = conversation,
                UserId = memberId
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await PublishConversationListsAsync(memberIds, cancellationToken);
        await SelectConversationAsync(connection, conversation.Id, cancellationToken);
    }

    private async Task SelectConversationAsync(
        ChatConnection connection,
        string conversationId,
        CancellationToken cancellationToken)
    {
        if (!await IsMemberAsync(conversationId, connection.Identity!.Id, cancellationToken))
        {
            await SendErrorAsync(connection, "You are not a member of that conversation.", cancellationToken);
            return;
        }

        connection.ActiveConversationId = conversationId;
        var historyRecords = await db.Messages.AsNoTracking()
            .Where(message => message.ConversationId == conversationId)
            .OrderByDescending(message => message.Id)
            .Take(300)
            .OrderBy(message => message.Id)
            .ToListAsync(cancellationToken);
        await SendSafeAsync(connection, new
        {
            type = "history",
            conversationId,
            messages = historyRecords.Select(message => message.ToMessage()).ToArray()
        }, cancellationToken);
    }

    private async Task SendChatMessageAsync(
        ChatConnection connection,
        string conversationId,
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
            await SendErrorAsync(connection, "Messages cannot exceed 10,000 characters.", cancellationToken);
            return;
        }

        await EnsureMemberAsync(conversationId, connection.Identity!.Id, cancellationToken);
        var message = new ChatMessage
        {
            ConversationId = conversationId,
            Type = "chat",
            Sender = connection.Identity.Name,
            Text = text,
            Time = DateTime.UtcNow
        };
        await SaveMessageAsync(message, connection.Identity.Id, cancellationToken);
        await BroadcastConversationMessageAsync(message, cancellationToken);
    }

    private async Task SaveMessageAsync(ChatMessage message, string senderId, CancellationToken cancellationToken)
    {
        var record = new ChatMessageRecord
        {
            ConversationId = message.ConversationId,
            SenderId = senderId,
            Sender = message.Sender,
            Type = message.Type,
            Text = message.Text,
            Time = message.Time,
            FileId = message.FileId,
            FileName = message.FileName,
            FileSize = message.FileSize,
            IsImage = message.IsImage
        };
        db.Messages.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        message.Id = record.Id;
    }

    private async Task BroadcastConversationMessageAsync(ChatMessage message, CancellationToken cancellationToken)
    {
        var memberIds = await db.ConversationMembers.AsNoTracking()
            .Where(member => member.ConversationId == message.ConversationId)
            .Select(member => member.UserId)
            .ToListAsync(cancellationToken);
        var recipients = _connections.Values
            .Where(connection => memberIds.Contains(connection.Identity!.Id))
            .ToArray();
        await Task.WhenAll(recipients.Select(connection =>
            SendSafeAsync(connection, new { type = "message", message }, cancellationToken)));
        await PublishConversationListsAsync(memberIds, cancellationToken);
    }

    private async Task PublishConversationListsAsync(IEnumerable<string> userIds, CancellationToken cancellationToken)
    {
        foreach (var userId in userIds.Distinct(StringComparer.Ordinal))
        {
            var connections = _connections.Values
                .Where(connection => connection.Identity!.Id == userId)
                .ToArray();
            if (connections.Length == 0)
            {
                continue;
            }

            var conversations = await GetConversationsAsync(userId);
            await Task.WhenAll(connections.Select(connection =>
                SendSafeAsync(connection, new { type = "conversations", conversations }, cancellationToken)));
        }
    }

    private async Task<object[]> GetConversationsAsync(string userId)
    {
        var conversations = await db.Conversations.AsNoTracking()
            .Where(conversation => conversation.Members.Any(member => member.UserId == userId))
            .OrderByDescending(conversation => conversation.CreatedAt)
            .Select(conversation => new
            {
                conversation.Id,
                conversation.Name,
                conversation.IsGroup,
                LastMessage = db.Messages.Where(message => message.ConversationId == conversation.Id)
                    .OrderByDescending(message => message.Id)
                    .Select(message => message.Text.Length > 0 ? message.Text : message.FileName)
                    .FirstOrDefault()
            })
            .ToListAsync();

        var conversationIds = conversations.Select(conversation => conversation.Id).ToArray();
        var memberIdsByConversation = await db.ConversationMembers.AsNoTracking()
            .Where(member => conversationIds.Contains(member.ConversationId))
            .GroupBy(member => member.ConversationId)
            .ToDictionaryAsync(
                group => group.Key,
                group => group.Select(member => member.UserId).ToList());
        var usersById = await users.Users.AsNoTracking()
            .ToDictionaryAsync(user => user.Id, user => user.UserName ?? "User");
        return conversations.Select(conversation => (object)new
        {
            conversation.Id,
            Name = conversation.IsGroup
                ? conversation.Name
                : memberIdsByConversation.GetValueOrDefault(conversation.Id, [])
                    .Where(id => id != userId)
                    .Select(id => usersById.GetValueOrDefault(id, "User"))
                    .FirstOrDefault() ?? "Direct message",
            conversation.IsGroup,
            conversation.LastMessage
        }).ToArray();
    }

    private async Task<object[]> GetDirectoryAsync()
    {
        var onlineIds = _connections.Values.Select(connection => connection.Identity!.Id).ToHashSet(StringComparer.Ordinal);
        var directory = await users.Users.AsNoTracking()
            .OrderBy(user => user.UserName)
            .Select(user => new UserDirectoryEntry(user.Id, user.UserName ?? "User"))
            .ToListAsync();
        return directory.Select(user => (object)new
        {
            user.Id,
            user.Name,
            IsOnline = onlineIds.Contains(user.Id)
        }).ToArray();
    }

    private async Task BroadcastDirectoryAsync(CancellationToken cancellationToken)
    {
        var directory = await GetDirectoryAsync();
        await BroadcastAsync(new { type = "directory", users = directory }, cancellationToken);
    }

    private Task BroadcastAsync(object message, CancellationToken cancellationToken)
    {
        var connections = _connections.Values.ToArray();
        return Task.WhenAll(connections.Select(connection =>
            SendSafeAsync(connection, message, cancellationToken)));
    }

    private async Task<bool> IsMemberAsync(string conversationId, string userId, CancellationToken cancellationToken) =>
        await db.ConversationMembers.AsNoTracking()
            .AnyAsync(member => member.ConversationId == conversationId && member.UserId == userId, cancellationToken);

    private async Task EnsureMemberAsync(string conversationId, string userId, CancellationToken cancellationToken)
    {
        if (!await IsMemberAsync(conversationId, userId, cancellationToken))
        {
            throw new ConversationAccessException();
        }
    }

    private Task SendErrorAsync(ChatConnection connection, string text, CancellationToken cancellationToken) =>
        SendSafeAsync(connection, new { type = "error", text }, cancellationToken);

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

    private static async Task CloseSafelyAsync(WebSocket socket, WebSocketCloseStatus status, string description)
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
        public string? ActiveConversationId { get; set; }

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
        public string? UserId { get; set; }
        public string? ConversationId { get; set; }
        public string[]? MemberIds { get; set; }
    }

    private sealed record UserDirectoryEntry(string Id, string Name);

    public sealed class ConversationAccessException : Exception
    {
    }
}
