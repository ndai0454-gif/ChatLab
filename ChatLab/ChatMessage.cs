namespace ChatLab;

public sealed class ChatMessage
{
    public string Type { get; set; } = "chat";
    public string Sender { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;
    public Guid FileId { get; set; }
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public bool IsImage { get; set; }
}

public sealed record ChatIdentity(string Name, string Token);
