using LibreMetaverse;

namespace Radegast.Gtk;

internal sealed record ChatMessage(DateTime Timestamp, string Text, bool Outgoing,
    UUID SenderId = default, string SenderName = "");

internal abstract class ChatConversation(UUID id)
{
    private readonly List<ChatMessage> _messages = new();
    private int _characters;
    public UUID Id { get; } = id;
    public IReadOnlyList<ChatMessage> Messages => _messages;
    public long FirstMessageIndex { get; private set; }
    public string Draft { get; set; } = string.Empty;
    public int UnreadCount { get; internal set; }
    internal void Append(ChatMessage message)
    {
        message = message with { Text = ChatMemoryLimits.LimitMessage(message.Text) };
        _messages.Add(message);
        _characters += message.Text.Length;
        var removed = 0;
        while (_messages.Count - removed > ChatMemoryLimits.Messages || _characters > ChatMemoryLimits.Characters)
            _characters -= _messages[removed++].Text.Length;
        if (removed == 0) return;
        _messages.RemoveRange(0, removed);
        FirstMessageIndex += removed;
    }

    internal static DateTime MessageTime(DateTime timestamp)
    {
        // LibreMetaverse 3.1.5 constructs Unix timestamps as DateTime ticks.
        if (timestamp.Year < 2000)
            return timestamp.Ticks is > 0 and <= uint.MaxValue
                ? DateTimeOffset.FromUnixTimeSeconds(timestamp.Ticks).LocalDateTime : DateTime.Now;
        return timestamp.Kind == DateTimeKind.Utc ? timestamp.ToLocalTime() : timestamp;
    }
}
