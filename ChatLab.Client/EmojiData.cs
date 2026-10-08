namespace ChatLab.Client;

/// <summary>Emoji for the picker, built from Unicode code point ranges (rendered in colour via the Segoe UI Emoji font).</summary>
public static class EmojiData
{
    public static readonly IReadOnlyList<(string Title, string[] Items)> Categories =
    [
        ("😀", Range(0x1F600, 0x1F64F)),
        ("👍", Range(0x1F44A, 0x1F450).Concat(["👍", "👎", "👏", "🙏", "💪", "🤝", "✌️", "🤞", "🤟", "🤘", "👌", "🤙"]).ToArray()),
        ("❤️", Range(0x1F493, 0x1F49F).Concat(["❤️", "🧡", "💛", "💚", "💙", "💜", "🖤", "🤍", "🤎", "💔", "💋", "🔥", "✨", "⭐", "🌈"]).ToArray()),
        ("🐶", Range(0x1F400, 0x1F43E)),
        ("🍔", Range(0x1F345, 0x1F37F)),
        ("⚽", Range(0x1F3A0, 0x1F3CA)),
        ("🚀", Range(0x1F680, 0x1F6A4)),
    ];

    private static string[] Range(int from, int to) =>
        Enumerable.Range(from, to - from + 1).Select(char.ConvertFromUtf32).ToArray();
}
