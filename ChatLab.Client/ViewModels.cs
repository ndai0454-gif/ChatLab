using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using ChatLab.Shared;

namespace ChatLab.Client;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum ItemKind { Text, System, Attachment }

/// <summary>One row in the chat window.</summary>
public sealed class ChatItem : Observable
{
    private static readonly Brush[] Palette =
    [
        Brushes.Crimson, Brushes.DarkOrange, Brushes.SeaGreen, Brushes.DodgerBlue,
        Brushes.MediumVioletRed, Brushes.DarkCyan, Brushes.SlateBlue, Brushes.OrangeRed, Brushes.Teal
    ];

    private ImageSource? _image;
    private string? _localPath;
    private string _imageNote = "";

    public ChatItem(ChatMessage message, bool isMine)
    {
        Message = message;
        IsMine = isMine;
        Kind = message.Type switch
        {
            MessageTypes.System => ItemKind.System,
            MessageTypes.File => ItemKind.Attachment,
            _ => ItemKind.Text
        };
        SenderBrush = Palette[(uint)StableHash(message.Sender) % Palette.Length];
    }

    public ChatMessage Message { get; }
    public bool IsMine { get; }
    public ItemKind Kind { get; }
    public Brush SenderBrush { get; }

    public string Sender => Message.Sender;
    public string Text => Message.Text;
    public string TimeText => Message.Time.ToString("HH:mm");
    public string FileName => Message.FileName;
    public string SizeText => FormatSize(Message.FileSize);

    public bool IsText => Kind == ItemKind.Text;
    public bool IsSystem => Kind == ItemKind.System;
    public bool IsAttachment => Kind == ItemKind.Attachment;
    public bool ShowSender => !IsMine && Kind != ItemKind.System;

    public HorizontalAlignment Align => Kind == ItemKind.System ? HorizontalAlignment.Center
        : IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public Brush BubbleBrush => IsMine
        ? new SolidColorBrush(Color.FromRgb(0xD6, 0xF5, 0xD0))
        : Brushes.White;

    public ImageSource? Image { get => _image; set { Set(ref _image, value); Raise(nameof(HasImage)); } }
    public bool HasImage => _image is not null;

    public string ImageNote { get => _imageNote; set => Set(ref _imageNote, value); }

    public string? LocalPath
    {
        get => _localPath;
        set { Set(ref _localPath, value); Raise(nameof(HasLocal)); }
    }
    public bool HasLocal => _localPath is not null;

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {units[i]}";
    }

    private static int StableHash(string s)
    {
        var h = 17;
        foreach (var c in s) h = h * 31 + c;
        return h;
    }
}

/// <summary>A running upload/download shown in the transfer panel, one mini bar per parallel stream.</summary>
public sealed class TransferVm : Observable
{
    private double _percent;
    private string _status = "";

    public TransferVm(string title, TransferProgress progress)
    {
        Title = title;
        Progress = progress;
        Streams = new ObservableCollection<StreamVm>(progress.Parts.Select((p, i) => new StreamVm(i + 1, p.Length)));
    }

    public string Title { get; }
    public TransferProgress Progress { get; }
    public ObservableCollection<StreamVm> Streams { get; }

    public double Percent { get => _percent; set => Set(ref _percent, value); }
    public string Status { get => _status; set => Set(ref _status, value); }

    private long _lastBytes;
    private DateTime _lastTick = DateTime.UtcNow;

    public void Refresh()
    {
        var total = Progress.TotalDone;
        for (var i = 0; i < Streams.Count; i++)
            Streams[i].Done = Progress.Done[i];

        Percent = Progress.Size == 0 ? 100 : total * 100.0 / Progress.Size;

        var now = DateTime.UtcNow;
        var dt = (now - _lastTick).TotalSeconds;
        var speed = dt > 0 ? (total - _lastBytes) / dt : 0;
        _lastBytes = total;
        _lastTick = now;

        Status = $"{Percent:0.0}%  -  {ChatItem.FormatSize(total)} / {ChatItem.FormatSize(Progress.Size)}  -  " +
                 $"{ChatItem.FormatSize((long)speed)}/s  -  {Streams.Count} parallel stream(s)";
    }
}

public sealed class StreamVm(int index, long length) : Observable
{
    private long _done;

    public string Label { get; } = $"#{index}";
    public double Maximum { get; } = Math.Max(1, length);
    public long Done { get => _done; set => Set(ref _done, value); }
}
