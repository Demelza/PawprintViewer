using System.Text;
using LibreMetaverse;
using Radegast.Gtk;

internal static class ChatMemoryChecks
{
    public static Task Retention()
    {
        foreach (var conversation in new ChatConversation[] { new ImConversation(UUID.Random(), UUID.Random()), new GroupConversation(UUID.Random(), "Group") })
        {
            conversation.Draft = "unsent message";
            for (var i = 0; i < 10_000; i++) conversation.Append(new(DateTime.Now, $"Message {i}", false));
            Check(conversation.Messages.Count == ChatMemoryLimits.Messages && conversation.FirstMessageIndex == 8000 &&
                conversation.Messages[0].Text == "Message 8000" && conversation.Messages[^1].Text == "Message 9999" &&
                conversation.Draft == "unsent message", "Chat retention lost recent messages, its stream position or the draft");
            var text = new string('x', 2048);
            for (var i = 0; i < 2000; i++) conversation.Append(new(DateTime.Now, text, false));
            Check(conversation.Messages.Sum(message => message.Text.Length) <= ChatMemoryLimits.Characters &&
                conversation.Messages.Count == ChatMemoryLimits.Characters / text.Length,
                "Large messages exceeded the conversation character budget");
            conversation.Append(new(DateTime.Now, new string('x', ChatMemoryLimits.Characters - 2) + "😀more", false));
            var limited = conversation.Messages.Single().Text;
            Check(limited.Length <= ChatMemoryLimits.Characters && limited.EndsWith('…') &&
                limited.EnumerateRunes().All(rune => rune != Rune.ReplacementChar),
                "An oversized message bypassed the budget or split a Unicode character");
        }
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
