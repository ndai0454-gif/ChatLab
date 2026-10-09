using ChatLab;
using ChatLab.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = ChatStore.MaximumFileSize;
});

var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDirectory);
builder.Services.AddDbContext<ChatDbContext>(options =>
    options.UseSqlite($"Data Source={Path.Combine(dataDirectory, "chatlab-users.db")}"));
builder.Services.AddIdentity<IdentityUser, IdentityRole>(options =>
{
    options.User.RequireUniqueEmail = false;
    options.Password.RequiredLength = 12;
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
})
    .AddEntityFrameworkStores<ChatDbContext>()
    .AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "ChatLab.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.LoginPath = "/Account/Login";
});
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/Register");
    options.Conventions.AllowAnonymousToPage("/Error");
});
builder.Services.AddSingleton<ChatStore>();
builder.Services.AddScoped<ChatRoom>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
    await database.Database.EnsureCreatedAsync();
    await database.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "ChatConversations" (
            "Id" TEXT NOT NULL PRIMARY KEY,
            "Name" TEXT NOT NULL,
            "IsGroup" INTEGER NOT NULL,
            "DirectKey" TEXT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ChatConversations_DirectKey"
            ON "ChatConversations" ("DirectKey");
        CREATE TABLE IF NOT EXISTS "ChatConversationMembers" (
            "ConversationId" TEXT NOT NULL,
            "UserId" TEXT NOT NULL,
            PRIMARY KEY ("ConversationId", "UserId"),
            FOREIGN KEY ("ConversationId") REFERENCES "ChatConversations" ("Id") ON DELETE CASCADE,
            FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_ChatConversationMembers_UserId"
            ON "ChatConversationMembers" ("UserId");
        CREATE TABLE IF NOT EXISTS "ChatMessages" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "ConversationId" TEXT NOT NULL,
            "SenderId" TEXT NOT NULL,
            "Sender" TEXT NOT NULL,
            "Type" TEXT NOT NULL,
            "Text" TEXT NOT NULL,
            "Time" TEXT NOT NULL,
            "FileId" TEXT NOT NULL,
            "FileName" TEXT NOT NULL,
            "FileSize" INTEGER NOT NULL,
            "IsImage" INTEGER NOT NULL,
            FOREIGN KEY ("ConversationId") REFERENCES "ChatConversations" ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_ChatMessages_ConversationId_Id"
            ON "ChatMessages" ("ConversationId", "Id");
        """);
}

app.UseStaticFiles();
app.UseRouting();
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapRazorPages();

app.Map("/ws", async context =>
{
    if (context.User.Identity?.IsAuthenticated != true)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await context.RequestServices.GetRequiredService<ChatRoom>()
        .HandleClientAsync(
            socket,
            context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value,
            context.User.Identity.Name!,
            context.RequestAborted);
});

app.MapPost("/api/files", async (HttpContext context, ChatStore store, ChatRoom room) =>
{
    try
    {
        await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest(new { error = "Invalid or missing anti-forgery token." });
    }

    if (!Guid.TryParse(context.Request.Query["conversationId"], out var conversationId))
    {
        return Results.BadRequest(new { error = "A valid conversation is required." });
    }

    var fileName = context.Request.Query["name"].ToString();
    if (context.Request.ContentLength is null)
    {
        return Results.StatusCode(StatusCodes.Status411LengthRequired);
    }

    if (context.Request.ContentLength > ChatStore.MaximumFileSize)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    try
    {
        var message = await room.SaveFileAsync(
            context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value,
            context.User.Identity!.Name!,
            conversationId.ToString("N"),
            context.Request.Body,
            fileName,
            context.Request.ContentLength,
            context.RequestAborted);
        return Results.Ok(message);
    }
    catch (ChatRoom.ConversationAccessException)
    {
        return Results.Forbid();
    }
    catch (ChatStore.UploadTooLargeException)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidDataException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

app.MapGet("/files/{id:guid}", async (Guid id, HttpContext context, ChatStore store) =>
{
    var db = context.RequestServices.GetRequiredService<ChatDbContext>();
    var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value;
    var file = await db.Messages.AsNoTracking()
        .FirstOrDefaultAsync(x => x.FileId == id && x.IsImage &&
            db.ConversationMembers.Any(member => member.ConversationId == x.ConversationId && member.UserId == userId));
    if (file is null)
    {
        return Results.NotFound();
    }

    return Results.File(store.GetFilePath(id), store.GetImageContentType(file.FileName));
}).RequireAuthorization();

app.MapGet("/files/{id:guid}/download", async (Guid id, HttpContext context, ChatStore store) =>
{
    var db = context.RequestServices.GetRequiredService<ChatDbContext>();
    var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value;
    var file = await db.Messages.AsNoTracking()
        .FirstOrDefaultAsync(x => x.FileId == id &&
            db.ConversationMembers.Any(member => member.ConversationId == x.ConversationId && member.UserId == userId));
    return file is null
        ? Results.NotFound()
        : Results.File(store.GetFilePath(id), "application/octet-stream", file.FileName, enableRangeProcessing: true);
}).RequireAuthorization();

app.Run();
