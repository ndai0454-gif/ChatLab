using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ChatLab.Data;

public sealed class ChatDbContext(DbContextOptions<ChatDbContext> options)
    : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<ChatConversation> Conversations => Set<ChatConversation>();
    public DbSet<ChatConversationMember> ConversationMembers => Set<ChatConversationMember>();
    public DbSet<ChatMessageRecord> Messages => Set<ChatMessageRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ChatConversation>().ToTable("ChatConversations");
        builder.Entity<ChatConversationMember>().ToTable("ChatConversationMembers");
        builder.Entity<ChatMessageRecord>().ToTable("ChatMessages");
        builder.Entity<ChatConversation>().HasIndex(x => x.DirectKey).IsUnique();
        builder.Entity<ChatConversationMember>()
            .HasKey(x => new { x.ConversationId, x.UserId });
        builder.Entity<ChatConversationMember>()
            .HasOne<IdentityUser>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ChatConversationMember>()
            .HasOne(x => x.Conversation)
            .WithMany(x => x.Members)
            .HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ChatMessageRecord>()
            .HasOne(x => x.Conversation)
            .WithMany()
            .HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ChatConversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool IsGroup { get; set; }
    public string? DirectKey { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<ChatConversationMember> Members { get; set; } = [];
}

public sealed class ChatConversationMember
{
    public string ConversationId { get; set; } = "";
    public ChatConversation Conversation { get; set; } = null!;
    public string UserId { get; set; } = "";
}

public sealed class ChatMessageRecord
{
    public long Id { get; set; }
    public string ConversationId { get; set; } = "";
    public ChatConversation Conversation { get; set; } = null!;
    public string SenderId { get; set; } = "";
    public string Sender { get; set; } = "";
    public string Type { get; set; } = "chat";
    public string Text { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.UtcNow;
    public Guid FileId { get; set; }
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public bool IsImage { get; set; }

    public ChatMessage ToMessage() => new()
    {
        Id = Id,
        ConversationId = ConversationId,
        Type = Type,
        Sender = Sender,
        Text = Text,
        Time = Time,
        FileId = FileId,
        FileName = FileName,
        FileSize = FileSize,
        IsImage = IsImage
    };
}
