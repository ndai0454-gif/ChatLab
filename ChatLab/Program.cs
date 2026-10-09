using ChatLab;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = ChatStore.MaximumFileSize;
});

builder.Services.AddRazorPages();
builder.Services.AddSingleton<ChatStore>();
builder.Services.AddSingleton<ChatRoom>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseRouting();
app.UseWebSockets();
app.MapRazorPages();

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await context.RequestServices.GetRequiredService<ChatRoom>()
        .HandleClientAsync(socket, context.RequestAborted);
});

app.MapPost("/api/files", async (HttpContext context, ChatStore store, ChatRoom room) =>
{
    var token = context.Request.Headers["X-Chat-Session"].ToString();
    if (!room.TryGetIdentity(token, out var identity))
    {
        return Results.Unauthorized();
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
        var message = await store.SaveFileAsync(
            context.Request.Body,
            fileName,
            identity.Name,
            context.Request.ContentLength,
            context.RequestAborted);
        await room.BroadcastMessageAsync(message, CancellationToken.None);
        return Results.Ok(message);
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
});

app.MapGet("/files/{id:guid}", (Guid id, ChatStore store) =>
{
    var file = store.GetFile(id);
    if (file is null || !file.IsImage)
    {
        return Results.NotFound();
    }

    return Results.File(store.GetFilePath(id), store.GetImageContentType(file.FileName));
});

app.MapGet("/files/{id:guid}/download", (Guid id, ChatStore store) =>
{
    var file = store.GetFile(id);
    return file is null
        ? Results.NotFound()
        : Results.File(store.GetFilePath(id), "application/octet-stream", file.FileName, enableRangeProcessing: true);
});

app.Run();
