using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using Gtk;
using LibreMetaverse;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using Radegast.Gtk;

/// <summary>Checks real capability parsing, reply races and GTK scrolling without grid access.</summary>
internal static class NativeProfileTextChecks
{
    public static int Run()
    {
        Application.Init();
        using var account = new AccountSession(GtkDispatch.Post);
        using var fixture = new Fixture(account);
        fixture.CapturePackets();
        Connected(account, true);
        using var parent = new Window("Profile text check");
        parent.ShowAll();
        using var server = new ProfileServer(account.Client, fixture.Simulator);
        var avatar = fixture.Friend("Long Profile").UUID;
        var modernFirst = fixture.Friend("Modern First").UUID;
        var failed = fixture.Friend("Fallback Profile").UUID;
        var mismatch = fixture.Friend("Wrong Reply").UUID;
        var cancelled = fixture.Friend("Closed Profile").UUID;
        var link = fixture.Friend("Alice Resident").UUID;
        var about = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"Paragraph {i}: café 日本語 😀 {new string('x', 70)}")) +
            $"\n{ChatLinkChecks.Url(link)}\nEND OF SECOND LIFE";
        var firstLife = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"First Life {i}: {new string('y', 80)}")) + "\nEND OF FIRST LIFE";
        var expectedAbout = about.Replace(ChatLinkChecks.Url(link), "Alice Resident", StringComparison.Ordinal);
        var windows = new List<AvatarProfileWindow>();
        var primary = Open(avatar);
        // Measure the previous window size under this desktop's font/theme.
        primary.Resize(440, 430);
        Properties(avatar, "Legacy truncated text", "Legacy First Life");
        var baselineHeight = 0;
        var stage = 0;
        var result = 0;
        AvatarProfileWindow? secondary = null, fallback = null, wrongReply = null, closing = null;
        GLib.Timeout.Add(250, () =>
        {
            try
            {
                switch (stage++)
                {
                    case 0:
                        Check(Text(primary, "_about").Buffer.Text == "Legacy truncated text", "The legacy fallback was not displayed while the full profile loaded");
                        Check(server.Requests.Contains(avatar), "The viewer did not request the full profile for its resident");
                        baselineHeight = Scroll(primary, "_about").AllocatedHeight;
                        primary.Resize(440, 520);
                        server.Reply(avatar, about, firstLife);
                        break;
                    case 1:
                        Check(Text(primary, "_about").Buffer.Text == expectedAbout && Text(primary, "_firstLife").Buffer.Text == firstLife,
                            "The full profile did not replace truncated UDP text, preserve Unicode or retain its final paragraphs");
                        var largerHeight = Scroll(primary, "_about").AllocatedHeight;
                        Check(largerHeight >= baselineHeight * 1.35 && largerHeight <= baselineHeight * 1.75,
                            $"The profile text area did not grow by about 50%: {baselineHeight} -> {largerHeight}");
                        Console.WriteLine($"Profile text area: {baselineHeight} -> {largerHeight} px; complete text: {about.Length}/{firstLife.Length} characters");
                        CheckScroll(primary, "_about");
                        Properties(avatar, "Late short UDP reply", "Late short First Life");
                        Tabs(primary).CurrentPage = 1;
                        secondary = Open(modernFirst);
                        server.Reply(modernFirst, "", "Modern short text");
                        break;
                    case 2:
                        Check(Text(primary, "_about").Buffer.Text == expectedAbout, "A late UDP reply overwrote the full profile");
                        CheckScroll(primary, "_firstLife");
                        Check(Text(secondary!, "_firstLife").Buffer.Text == "Modern short text", "A capability-first profile was not displayed");
                        // Complete replies can legitimately be shorter or empty.
                        Properties(modernFirst, "Longer outdated UDP text", "Outdated UDP First Life");
                        fallback = Open(failed);
                        Properties(failed, "Fallback when HTTP fails", "Fallback First Life");
                        server.Fail(failed);
                        break;
                    case 3:
                        Check(Text(secondary!, "_about").Buffer.Text == "" && Text(secondary!, "_firstLife").Buffer.Text == "Modern short text",
                            "A capability-first profile was downgraded to a later legacy reply");
                        Check(Text(fallback!, "_about").Buffer.Text == "Fallback when HTTP fails", "A failed capability discarded the legacy fallback");
                        wrongReply = Open(mismatch);
                        Properties(mismatch, "Correct resident fallback", "Correct First Life");
                        server.Reply(mismatch, "Another resident's profile", "Wrong First Life", avatar);
                        break;
                    case 4:
                        Check(Text(wrongReply!, "_about").Buffer.Text == "Correct resident fallback", "The capability populated the wrong resident's profile");
                        closing = Open(cancelled);
                        Check(server.Requests.Contains(cancelled), "The cancellation check did not start its capability request");
                        closing.CloseProfile();
                        server.Reply(cancelled, about, firstLife);
                        break;
                    case 5:
                        Check(closing!.Handle == IntPtr.Zero, "A late full profile reply reopened a dismissed window");
                        foreach (var window in windows) window.CloseProfile();
                        GC.Collect(); GC.WaitForPendingFinalizers();
                        break;
                    case 6:
                        Console.WriteLine("PASS native GTK full profile priority, UDP fallback, Unicode/link retention, taller text area, scrolling and late-reply cleanup");
                        Application.Quit();
                        return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                result = 1;
                Console.Error.WriteLine($"FAIL native GTK profile text (stage {stage - 1}): {ex}");
                Application.Quit();
                return false;
            }
        });
        try { Application.Run(); }
        finally
        {
            foreach (var window in windows) window.CloseProfile();
            Connected(account, false);
        }
        return result;

        AvatarProfileWindow Open(UUID id)
        {
            server.Prepare(id);
            var window = new AvatarProfileWindow(parent, account, id);
            windows.Add(window);
            window.ShowAll();
            return window;
        }

        void Properties(UUID id, string sl, string fl) =>
            typeof(AvatarManager).GetMethod("OnAvatarPropertiesReply", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(account.Client.Avatars, new object[] { new AvatarPropertiesReplyEventArgs(id,
                    new Avatar.AvatarProperties { AboutText = sl, FirstLifeText = fl, BornOn = "1/1/2020" }) });
    }

    private static void CheckScroll(AvatarProfileWindow window, string field)
    {
        var scroll = Scroll(window, field);
        var adjustment = scroll.Vadjustment;
        Check(scroll.VscrollbarPolicy == PolicyType.Automatic && adjustment.Upper > adjustment.PageSize && adjustment.Value < 1,
            "Long profile text did not start at the top with automatic vertical scrolling");
        adjustment.Value = adjustment.Upper - adjustment.PageSize;
        Check(adjustment.Value > 0 && Math.Abs(adjustment.Value + adjustment.PageSize - adjustment.Upper) < 1,
            "The profile could not scroll to the end of its text");
    }
    private static ChatHistoryView Text(AvatarProfileWindow window, string field) =>
        (ChatHistoryView)typeof(AvatarProfileWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static ScrolledWindow Scroll(AvatarProfileWindow window, string field) => (ScrolledWindow)Text(window, field).Parent;
    private static Notebook Tabs(AvatarProfileWindow window) => (Notebook)Scroll(window, "_about").Parent;
    private static void Connected(AccountSession account, bool connected) =>
        typeof(Radegast.NetCom).GetProperty(nameof(Radegast.NetCom.IsLoggedIn))!.SetValue(account.Net, connected);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class ProfileServer : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<UUID, TaskCompletionSource<HttpResponseMessage>> _replies = new();
        public ConcurrentBag<UUID> Requests { get; } = new();

        public ProfileServer(GridClient client, Simulator simulator)
        {
            client.HttpCapsClient.Dispose();
            client.HttpCapsClient = new HttpCapsClient(this);
            simulator.Caps = (Caps)Activator.CreateInstance(typeof(Caps), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { simulator, new Uri("https://profile.test/seed") }, null)!;
            var caps = (Dictionary<string, Uri>)typeof(Caps).GetField("_Caps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(simulator.Caps)!;
            caps["AgentProfile"] = new Uri("https://profile.test/cap/opaque-profile-id");
        }

        public void Prepare(UUID id) => _replies[id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reply(UUID requested, string sl, string fl, UUID? responseId = null)
        {
            var reply = new OSDMap
            {
                ["id"] = responseId ?? requested, ["sl_about_text"] = sl, ["fl_about_text"] = fl,
                ["member_since"] = OSD.FromDate(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            };
            _replies[requested].TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(OSDParser.SerializeLLSDXmlString(reply), Encoding.UTF8, "application/llsd+xml") });
        }
        public void Fail(UUID id) => _replies[id].TrySetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var id = UUID.Parse(request.RequestUri!.Segments.Last());
            Check(request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/cap/opaque-profile-id/" + id,
                "The full profile request lost its capability path or used the wrong method");
            Requests.Add(id);
            // Deliberately allow a late response even after window cancellation.
            return _replies[id].Task;
        }
    }
}
