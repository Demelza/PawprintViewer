using LibreMetaverse;

namespace Radegast.Gtk;

internal sealed record ChatMessage(DateTime Timestamp, string Text, bool Outgoing,
    UUID SenderId = default, string SenderName = "");

internal abstract class ChatConversation(UUID id)
{
    private readonly List<ChatMessage> _messages = new();
    public UUID Id { get; } = id;
    public IReadOnlyList<ChatMessage> Messages => _messages;
    public string Draft { get; set; } = string.Empty;
    public int UnreadCount { get; internal set; }
    internal void Append(ChatMessage message) => _messages.Add(message);

    internal static DateTime MessageTime(DateTime timestamp)
    {
        // LibreMetaverse 3.1.5 constructs Unix timestamps as DateTime ticks.
        if (timestamp.Year < 2000)
            return timestamp.Ticks is > 0 and <= uint.MaxValue
                ? DateTimeOffset.FromUnixTimeSeconds(timestamp.Ticks).LocalDateTime : DateTime.Now;
        return timestamp.Kind == DateTimeKind.Utc ? timestamp.ToLocalTime() : timestamp;
    }
}
