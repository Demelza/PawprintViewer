using System.Reflection;
using System.Text;
using Gtk;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Radegast.Gtk;

internal static class NativeProfileLinkChecks
{
    public static int Run()
    {
        Application.Init();
        using var account = new AccountSession(GtkDispatch.Post);
        using var fixture = new Fixture(account);
        var packets = fixture.CapturePackets();
        Connected(account, true);
        var owner = fixture.Friend("Profile Owner").UUID;
        fixture.Friend("Momoi Pawprint", id: ProfileLinkChecks.Payee);
        using var parent = new Window("Profile link check");
        parent.ShowAll();
        var browser = new List<string>();
        var profiles = new List<UUID>();
        account.AvatarProfileRequested += (_, id) => profiles.Add(id);
        using var profile = new AvatarProfileWindow(parent, account, owner, browser.Add);
        profile.ShowAll();
        Properties(account, owner, ProfileLinkChecks.Example + " plain text", $"[{ProfileLinkChecks.Wishlist} First life wishlist ♥]\n" +
            $"[{ChatLinkChecks.Url(ProfileLinkChecks.Payee)} My partner ♥]");
        var about = Field<ChatHistoryView>(profile, "_about");
        var firstLife = Field<ChatHistoryView>(profile, "_firstLife");
        var stage = 0;
        var result = 0;
        // GTK's multi-press gesture uses a real timeout, not event timestamps.
        // Separate intentional single clicks using this desktop's setting.
        GLib.Timeout.Add((uint)Math.Max(300, global::Gtk.Settings.Default.DoubleClickTime + 100), () =>
        {
            try
            {
                switch (stage++)
                {
                    case 0:
                        Check(about.Buffer.Text == ProfileLinkChecks.Expected + " plain text" &&
                            firstLife.Buffer.Text == "First life wishlist ♥\nMy partner ♥", "The profile did not hide URL/brackets or preserve its labels");
                        foreach (var label in new[] { "Spoil me ♥", "Wishlist~", "Marketplace", "Mainstore" })
                        {
                            var tag = about.Buffer.GetIterAtOffset(Offset(about.Buffer.Text, label)).Tags.Single();
                            Check(tag.Underline == Pango.Underline.Single, "A profile label was not styled as a link");
                        }
                        Check(!about.Buffer.StartIter.Tags.Any() &&
                            !about.Buffer.GetIterAtOffset(Offset(about.Buffer.Text, "plain text")).Tags.Any(), "Link tags leaked into surrounding text");
                        NativeChatLinkChecks.Click(about, "Wishlist~");
                        NativeChatLinkChecks.Click(about, "Marketplace");
                        Check(browser.SequenceEqual(new[] { ProfileLinkChecks.Wishlist, ProfileLinkChecks.Store }), "Web label clicks opened the wrong URLs");
                        break;
                    case 1:
                        NativeChatLinkChecks.Click(about, "Mainstore");
                        Check(browser.Last() == ProfileLinkChecks.Location, "The map label lost the original location URL");
                        packets();
                        NativeChatLinkChecks.Click(about, "Spoil me ♥");
                        var payment = Field<ResidentPaymentWindow>(profile, "_payment");
                        Check(Field<UUID>(payment, "_recipient") == ProfileLinkChecks.Payee &&
                            Field<Label>(payment, "_recipientLabel").Text == "Pay Momoi Pawprint" &&
                            !packets().OfType<MoneyTransferRequestPacket>().Any(), "The payment link paid immediately or used the profile owner");
                        Field<Entry>(payment, "_amount").Text = "23";
                        Field<Button>(payment, "_pay").Click();
                        var paid = packets().OfType<MoneyTransferRequestPacket>().Single();
                        Check(paid.MoneyData.DestID == ProfileLinkChecks.Payee && paid.MoneyData.Amount == 23 &&
                            paid.AgentData.AgentID == fixture.Owner, "Payment confirmation used another recipient, amount or account");
                        break;
                    case 2:
                        Field<Button>(profile, "_pay").Click();
                        var ownerPayment = Field<ResidentPaymentWindow>(profile, "_payment");
                        Check(Field<UUID>(ownerPayment, "_recipient") == owner, "The existing Pay button stopped targeting the profile owner");
                        NativeChatLinkChecks.Click(about, "Spoil me ♥");
                        var linkedPayment = Field<ResidentPaymentWindow>(profile, "_payment");
                        Check(ownerPayment.Handle == IntPtr.Zero && Field<UUID>(linkedPayment, "_recipient") == ProfileLinkChecks.Payee,
                            "Switching between Pay and a labeled payment retained the wrong recipient");
                        break;
                    case 3:
                        var previousPayment = Field<ResidentPaymentWindow>(profile, "_payment");
                        NativeChatLinkChecks.Click(about, "Spoil me ♥");
                        Check(ReferenceEquals(previousPayment, Field<ResidentPaymentWindow>(profile, "_payment")), "Repeated clicks duplicated the payment window");
                        previousPayment.ClosePayment();
                        Tabs(profile).CurrentPage = 1;
                        break;
                    case 4:
                        NativeChatLinkChecks.Click(firstLife, "First life wishlist ♥");
                        NativeChatLinkChecks.Click(firstLife, "My partner ♥");
                        Check(browser.Last() == ProfileLinkChecks.Wishlist && profiles.SequenceEqual(new[] { ProfileLinkChecks.Payee }),
                            "A First Life label did not open its exact web/profile target");
                        NativeChatLinkChecks.Click(firstLife, "First life wishlist ♥", drag: true);
                        Check(browser.Count == 4, "Selecting profile link text launched the browser");
                        firstLife.SetText($"[{ChatLinkChecks.Url(ProfileLinkChecks.Payee)} Custom ♥ label] {ChatLinkChecks.Url(ProfileLinkChecks.Payee)}");
                        Check(firstLife.Buffer.Text == "Custom ♥ label Momoi Pawprint", "Rendering custom/raw links together changed the supplied label");
                        break;
                    case 5:
                        NativeChatLinkChecks.Click(firstLife, "Custom ♥ label");
                        Check(profiles.Count == 2 && profiles.Last() == ProfileLinkChecks.Payee,
                            "A custom /about label did not open its avatar's profile");
                        for (var i = 0; i < 10; i++) firstLife.SetText(ProfileLinkChecks.Example);
                        GC.Collect(); GC.WaitForPendingFinalizers();
                        break;
                    case 6:
                        NativeChatLinkChecks.Click(firstLife, "Spoil me ♥");
                        var closingPayment = Field<ResidentPaymentWindow>(profile, "_payment");
                        Properties(account, owner, "Late profile reply", "Late First Life reply");
                        profile.CloseProfile(); profile.CloseProfile();
                        Check(closingPayment.Handle == IntPtr.Zero && profile.Handle == IntPtr.Zero, "Dismissal retained a profile or linked payment prompt");
                        GC.Collect(); GC.WaitForPendingFinalizers();
                        Console.WriteLine("PASS native GTK labeled profile web/pay/about links, both text tabs, selection, recipient switching and window cleanup");
                        Connected(account, false);
                        parent.Dispose();
                        Application.Quit();
                        return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL native GTK profile links (stage {stage - 1}): {ex}");
                result = 1;
                Connected(account, false);
                profile.CloseProfile();
                parent.Dispose();
                Application.Quit();
                return false;
            }
        });
        Application.Run();
        Connected(account, false);
        return result;
    }

    private static int Offset(string text, string label) => text[..text.LastIndexOf(label, StringComparison.Ordinal)].EnumerateRunes().Count();
    private static Notebook Tabs(Window window) => (Notebook)((Box)window.Child).Children.Single(child => child is Notebook);
    private static void Properties(AccountSession account, UUID id, string about, string firstLife) =>
        account.Client.Avatars.GetType().GetMethod("OnAvatarPropertiesReply", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(account.Client.Avatars, new object[] { new AvatarPropertiesReplyEventArgs(id,
                new Avatar.AvatarProperties { AboutText = about, FirstLifeText = firstLife }) });
    private static T Field<T>(object target, string field) =>
        (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Connected(AccountSession account, bool value) =>
        typeof(Radegast.NetCom).GetProperty(nameof(Radegast.NetCom.IsLoggedIn))!.SetValue(account.Net, value);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
