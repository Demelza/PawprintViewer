using LibreMetaverse;
using LibreMetaverse.Packets;
using Radegast.Gtk;

internal static class ProfileLinkChecks
{
    internal static readonly UUID Payee = new("91592c0c-514e-476f-8bef-013c33d0227b");
    internal const string Wishlist = "https://marketplace.secondlife.com/wishlists/midori.pawprint";
    internal const string Store = "https://marketplace.secondlife.com/stores/255827";
    internal const string Location = "http://maps.secondlife.com/secondlife/Westbrook/104/87/21";
    internal static string Example => $"🌸 [secondlife:///app/agent/{Payee}/pay Spoil me ♥]\n" +
        $"🌸 [{Wishlist} Wishlist~]\n🌸 [{Store} Marketplace]\n🌸 [{Location} Mainstore]";
    internal const string Expected = "🌸 Spoil me ♥\n🌸 Wishlist~\n🌸 Marketplace\n🌸 Mainstore";

    public static Task Parsing()
    {
        var spans = ProfileTextLinks.Parse(Example);
        var links = spans.Where(span => span.Link != null).ToArray();
        Check(string.Concat(spans.Select(span => span.Text)) == Expected && links.Length == 4,
            "The supplied profile did not render all labels, emoji and line breaks");
        Check(links[0].Link!.Action == ProfileLinkAction.PayResident && links[0].Link!.AvatarId == Payee &&
            links.Skip(1).Select(span => span.Link!.Url).SequenceEqual(new[] { Wishlist, Store, Location }) &&
            links.Skip(1).All(span => span.Link!.Action == ProfileLinkAction.Web), "A label lost its exact URL or payment target");
        var about = ProfileTextLinks.Parse($"[{ChatLinkChecks.Url(Payee)} A custom ♥ name] {ChatLinkChecks.Url(Payee)}");
        Check(about[0].Text == "A custom ♥ name" && about[0].Link!.Action == ProfileLinkAction.AvatarProfile &&
            about.Last().AvatarId == Payee, "Custom profile labels or ordinary /about links were lost");
        var unicode = ProfileTextLinks.Parse("[HTTPS://example.org/path?q=one&two=%20 <Wishlist> & ♥ e\u0301 👩‍💻]");
        Check(unicode.Single().Text == "<Wishlist> & ♥ e\u0301 👩‍💻" &&
            unicode.Single().Link!.Url == "HTTPS://example.org/path?q=one&two=%20", "Label characters or URL query strings were changed");
        return Task.CompletedTask;
    }

    public static Task InvalidMarkup()
    {
        var text = "[https://example.org] [https://example.org   ] [https://example.org missing bracket\n" +
            "[not-a-url Label] [javascript:alert(1) Label] [file:///tmp/file Label] [data:text/html,abc Label] " +
            "[https:// Label] [secondlife:///app/agent/not-a-uuid/pay Pay] " +
            $"[secondlife:///app/agent/{UUID.Zero}/pay Pay] [secondlife:///app/agent/{Payee}/teleport Go]";
        var spans = ProfileTextLinks.Parse(text);
        Check(spans.All(span => span.Link == null && span.AvatarId == UUID.Zero) &&
            string.Concat(spans.Select(span => span.Text)) == text, "Malformed markup or unsupported actions became links or lost text");
        Check(ProfileTextLinks.Parse("").Count == 0, "An empty profile acquired text");
        return Task.CompletedTask;
    }

    public static async Task FormattingAndRestrictions()
    {
        using var account = new AccountSession(action => action());
        using var fixture = new Fixture(account);
        var packets = fixture.CapturePackets();
        Connected(account, true);
        try
        {
            fixture.Friend("Momoi Pawprint", id: Payee);
            foreach (var format in new Func<string, IReadOnlyList<ChatTextSpan>>[] { account.FormatProfileText, account.FormatChatText })
            {
                Connected(account, true);
                var text = Example + $"\n[{ChatLinkChecks.Url(Payee)} My partner ♥] {ChatLinkChecks.Url(Payee)}";
                var formatted = format(text);
                Check(Render(formatted) == Expected + "\nMy partner ♥ Momoi Pawprint" &&
                    formatted.Count(span => span.Link != null) == 5, "Formatting replaced a supplied label with a resident name");
                Check(!packets().OfType<MoneyTransferRequestPacket>().Any(), "Displaying a payment link sent money");
                await fixture.Command("@showloc=n");
                var hiddenLocation = format(text);
                Check(Render(hiddenLocation).Contains("[location hidden]") &&
                    hiddenLocation.Where(span => span.Link != null).All(span => !span.Link!.IsLocation) &&
                    !account.CanUseProfileLink(formatted.Single(span => span.Text == "Mainstore").Link!),
                    "A labeled map URL bypassed location hiding or stale-link permissions");
                await fixture.Command("@shownames=n");
                var hiddenNames = format(text);
                Check(!Render(hiddenNames).Contains("Momoi") && !Render(hiddenNames).Contains("My partner") &&
                    !Render(hiddenNames).Contains("Spoil") && hiddenNames.Where(span => span.Link != null)
                        .All(span => span.Link!.AvatarId == UUID.Zero) && hiddenNames.All(span => span.AvatarId == UUID.Zero) &&
                    !account.CanUseProfileLink(formatted.Single(span => span.Text == "Spoil me ♥").Link!),
                    "Avatar labels or stale payment links bypassed name hiding");
                await fixture.Command("@showloc=y,shownames=y");
                Check(Render(format(text)) == Expected + "\nMy partner ♥ Momoi Pawprint",
                    "Unlocking did not restore the original labels");
                Connected(account, false);
                var offline = format(Example);
                Check(Render(offline) == Expected && offline.Single(span => span.Text == "Spoil me ♥").Link == null &&
                    offline.Single(span => span.Text == "Wishlist~").Link != null, "Disconnected payment links remained active or web links disappeared");
            }
        }
        finally { Connected(account, false); }
    }

    private static string Render(IReadOnlyList<ChatTextSpan> spans) => string.Concat(spans.Select(span => span.Text));
    private static void Connected(AccountSession account, bool value) =>
        typeof(Radegast.NetCom).GetProperty(nameof(Radegast.NetCom.IsLoggedIn))!.SetValue(account.Net, value);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
