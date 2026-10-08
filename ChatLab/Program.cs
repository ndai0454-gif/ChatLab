using ChatLab;

var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 9000;
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var server = new ChatServer(port, dataDir);
await server.RunAsync(cts.Token);
