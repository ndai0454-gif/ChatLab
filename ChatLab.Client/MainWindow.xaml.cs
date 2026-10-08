using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChatLab.Shared;
using Microsoft.Win32;

namespace ChatLab.Client;

public partial class MainWindow : Window
{
    private const long AutoPreviewLimit = 30L * 1024 * 1024; // images above this are not downloaded automatically
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico"];

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatLab", "cache");

    private readonly ObservableCollection<ChatItem> _items = [];
    private readonly ObservableCollection<TransferVm> _transfers = [];
    private readonly Dictionary<Guid, string> _localPaths = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private ChatClient? _client;
    private string? _pendingPath;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(CacheDir);

        NameBox.Text = Environment.UserName;
        MessageList.ItemsSource = _items;
        TransferList.ItemsSource = _transfers;
        SetConnected(false);
        BuildEmojiPicker();

        _timer.Tick += (_, _) => { foreach (var t in _transfers) t.Refresh(); };
        _timer.Start();
    }

    // ---------------------------------------------------------------- connection

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not null)
        {
            _client.Dispose();
            return;
        }

        var parts = HostBox.Text.Trim().Split(':');
        var host = parts[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 9000;
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { MessageBox.Show("Please enter a name."); return; }

        ConnectButton.IsEnabled = false;
        StateText.Text = "Connecting...";
        var client = new ChatClient();
        client.MessageReceived += OnMessage;
        client.Disconnected += () =>
        {
            if (!ReferenceEquals(_client, client)) return;
            _client = null;
            SetConnected(false);
        };

        try
        {
            await client.ConnectAsync(host, port, name);
            _client = client;
            _items.Clear();
            SetConnected(true);
        }
        catch (Exception ex)
        {
            client.Dispose();
            StateText.Text = "Disconnected";
            ConnectButton.IsEnabled = true;
            MessageBox.Show($"Cannot connect to {host}:{port}\n{ex.Message}");
        }
    }

    private void SetConnected(bool connected)
    {
        ConnectButton.IsEnabled = true;
        ConnectButton.Content = connected ? "Disconnect" : "Connect";
        StateText.Text = connected ? $"Connected as {NameBox.Text.Trim()}" : "Disconnected";
        HostBox.IsEnabled = NameBox.IsEnabled = !connected;
        InputBox.IsEnabled = SendButton.IsEnabled = EmojiButton.IsEnabled = AttachButton.IsEnabled = connected;
        if (!connected) UserList.ItemsSource = null;
    }

    private void Window_Closed(object? sender, EventArgs e) => _client?.Dispose();

    // ---------------------------------------------------------------- receiving

    private void OnMessage(ChatMessage m)
    {
        if (m.Type == MessageTypes.Users)
        {
            UserList.ItemsSource = m.Users;
            return;
        }

        var item = new ChatItem(m, isMine: m.Sender == _client?.Name && m.Type != MessageTypes.System);
        _items.Add(item);
        MessageList.ScrollIntoView(item);

        if (item.IsAttachment) _ = PrepareAttachmentAsync(item);
    }

    /// <summary>Finds (or downloads, for reasonably small images) the file so images can be previewed inline.</summary>
    private async Task PrepareAttachmentAsync(ChatItem item)
    {
        var m = item.Message;
        try
        {
            var cached = CachePath(m);
            if (_localPaths.TryGetValue(m.FileId, out var own) && File.Exists(own)) item.LocalPath = own;
            else if (File.Exists(cached)) item.LocalPath = cached;

            if (!m.IsImage) return;

            if (item.LocalPath is null)
            {
                if (m.FileSize > AutoPreviewLimit)
                {
                    item.ImageNote = $"Image is large ({item.SizeText}) - use Save as... to download it.";
                    return;
                }

                item.ImageNote = "Loading preview...";
                await DownloadAsync(m, cached, showTransfer: false);
                item.LocalPath = cached;
            }

            item.Image = await LoadThumbnailAsync(item.LocalPath);
            item.ImageNote = item.Image is null ? "Preview not available." : "";
        }
        catch (Exception ex)
        {
            item.ImageNote = $"Preview failed: {ex.Message}";
        }
    }

    private static string CachePath(ChatMessage m) =>
        Path.Combine(CacheDir, m.FileId.ToString("N") + Path.GetExtension(m.FileName));

    private static Task<BitmapSource?> LoadThumbnailAsync(string path) => Task.Run(() =>
    {
        try
        {
            using var fs = File.OpenRead(path);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // read everything now so the file is not locked
            bmp.DecodePixelWidth = 640;                 // keeps memory small even for huge images
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();                               // lets the UI thread use an image built on a worker
            return (BitmapSource?)bmp;
        }
        catch (Exception) { return null; }
    });

    /// <summary>Parallel ranged download into <paramref name="dest"/>; written to a .part file first so partial files are never used.</summary>
    private async Task DownloadAsync(ChatMessage m, string dest, bool showTransfer)
    {
        var client = _client ?? throw new InvalidOperationException("Not connected.");
        var progress = new TransferProgress(m.FileSize);
        var vm = new TransferVm($"⬇ Downloading {m.FileName}", progress);
        if (showTransfer) _transfers.Add(vm);

        var part = dest + ".part";
        try
        {
            await client.DownloadAsync(m, part, progress, CancellationToken.None);
            File.Move(part, dest, overwrite: true);
        }
        finally { _transfers.Remove(vm); }
    }

    // ---------------------------------------------------------------- sending

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; Send_Click(sender, e); }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var client = _client;
        if (client is null) return;

        var text = InputBox.Text.Trim();
        var path = _pendingPath;
        if (text.Length == 0 && path is null) return;

        InputBox.Clear();
        ClearAttachment();

        try
        {
            if (text.Length > 0) await client.SendTextAsync(text);
            if (path is not null) await UploadFileAsync(client, path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Send failed: {ex.Message}");
        }
    }

    private async Task UploadFileAsync(ChatClient client, string path)
    {
        var fileId = Guid.NewGuid();
        _localPaths[fileId] = path; // our own message can show the original file without downloading it back
        var isImage = ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        var progress = new TransferProgress(new FileInfo(path).Length);
        var vm = new TransferVm($"⬆ Uploading {Path.GetFileName(path)}", progress);
        _transfers.Add(vm);
        try
        {
            await client.UploadAsync(fileId, path, isImage, progress, CancellationToken.None);
        }
        finally { _transfers.Remove(vm); }
    }

    // ---------------------------------------------------------------- attachment preview

    private async void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Choose an image or file", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return;

        _pendingPath = dlg.FileName;
        AttachName.Text = Path.GetFileName(dlg.FileName);
        AttachSize.Text = ChatItem.FormatSize(new FileInfo(dlg.FileName).Length);
        AttachImage.Source = null;
        AttachPanel.Visibility = Visibility.Visible;

        if (ImageExtensions.Contains(Path.GetExtension(dlg.FileName).ToLowerInvariant()))
        {
            var path = dlg.FileName;
            var thumb = await LoadThumbnailAsync(path);
            if (_pendingPath == path) AttachImage.Source = thumb;
        }
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e) => ClearAttachment();

    private void ClearAttachment()
    {
        _pendingPath = null;
        AttachImage.Source = null;
        AttachPanel.Visibility = Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- open / save

    private static ChatItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ChatItem;

    private void Image_Click(object sender, MouseButtonEventArgs e) => OpenItem(ItemOf(sender));

    private void Open_Click(object sender, RoutedEventArgs e) => OpenItem(ItemOf(sender));

    private static void OpenItem(ChatItem? item)
    {
        if (item?.LocalPath is null || !File.Exists(item.LocalPath)) return;
        Process.Start(new ProcessStartInfo(item.LocalPath) { UseShellExecute = true });
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (item is null) return;

        var dlg = new SaveFileDialog { FileName = item.FileName, OverwritePrompt = true };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            if (item.LocalPath is not null && File.Exists(item.LocalPath))
                File.Copy(item.LocalPath, dlg.FileName, overwrite: true);
            else
            {
                await DownloadAsync(item.Message, dlg.FileName, showTransfer: true);
                item.LocalPath = dlg.FileName;
                if (item.Message.IsImage) item.Image = await LoadThumbnailAsync(dlg.FileName);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- emoji picker

    private void BuildEmojiPicker()
    {
        var tabs = new TabControl { Width = 420, Height = 260, FontFamily = new FontFamily("Segoe UI Emoji") };
        foreach (var (title, items) in EmojiData.Categories)
        {
            var wrap = new WrapPanel();
            foreach (var emoji in items)
            {
                var b = new Button
                {
                    Content = emoji, FontSize = 24, Width = 44, Height = 40,
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand
                };
                b.Click += (_, _) => InsertEmoji(emoji);
                wrap.Children.Add(b);
            }
            tabs.Items.Add(new TabItem
            {
                Header = new TextBlock { Text = title, FontSize = 18 },
                Content = new ScrollViewer { Content = wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
            });
        }

        EmojiPopup.Child = new Border
        {
            Background = Brushes.White, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(4), Child = tabs
        };
    }

    private void Emoji_Click(object sender, RoutedEventArgs e) => EmojiPopup.IsOpen = !EmojiPopup.IsOpen;

    private void InsertEmoji(string emoji)
    {
        var caret = InputBox.CaretIndex;
        InputBox.Text = InputBox.Text.Insert(caret, emoji);
        InputBox.CaretIndex = caret + emoji.Length;
        InputBox.Focus();
    }
}
