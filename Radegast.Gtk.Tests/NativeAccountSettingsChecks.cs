using System.Reflection;
using Gtk;
using LibreMetaverse;
using Radegast.Gtk;

internal static class NativeAccountSettingsChecks
{
    public static int Run()
    {
        Application.Init();
        using var h = new ReconnectHarness(post: GtkDispatch.Post);
        h.Connect();
        var globalSettings = new GlobalSettings(Path.Combine(h.DirectoryPath, "global.json"));
        using var main = new MainWindow(globalSettings, new Notices());
        Invoke(main, "AddSession", h.Account);
        main.ShowAll();
        var sessions = Field<Dictionary<AccountSession, SessionWidgets>>(main, "_sessions");
        var widgets = sessions[h.Account];
        var panel = Field<AccountSettingsPanel>(widgets, "_settingsPanel");
        var reconnect = Field<CheckButton>(panel, "_reconnect");
        var delay = Field<SpinButton>(panel, "_delay");
        var autoSit = Field<CheckButton>(panel, "_autoSit");
        var autoSitRestart = Field<CheckButton>(panel, "_autoSitRestart");
        var furniture = Field<Entry>(panel, "_autoSitObject");
        var restart = Field<CheckButton>(panel, "_restartTeleport");
        var restartRegion = Field<Entry>(panel, "_restartRegion");
        var restartX = Field<SpinButton>(panel, "_restartX");
        var restartY = Field<SpinButton>(panel, "_restartY");
        var restartZ = Field<SpinButton>(panel, "_restartZ");
        var returnDelay = Field<SpinButton>(panel, "_returnDelay");
        var inventory = Field<InventoryPanel>(widgets, "_inventoryPanel");
        var staleFolder = UUID.Random();
        LibreMetaverse.Inventory? replacement = null;
        var stage = 0;
        var result = 0;
        GLib.Timeout.Add(350, () =>
        {
            try
            {
                switch (stage++)
                {
                    case 0:
                        var globalWindow = new GlobalSettingsWindow(main, globalSettings, Field<NotificationController>(main, "_notifications"));
                        globalWindow.ShowAll();
                        Check(!Descendants(globalWindow).OfType<Button>().Any(button => button.Label == "Test notification"),
                            "Global Settings retained the test notification button");
                        var restartNotices = Descendants(globalWindow).OfType<CheckButton>().Single(toggle => toggle.Label == "Sim Restarts");
                        Check(restartNotices.Active, "Sim restart notifications were not enabled by default");
                        restartNotices.Active = false;
                        Check(!globalSettings.Value.SimRestarts && !new GlobalSettings(globalSettings.FilePath).Value.SimRestarts,
                            "The sim restart checkbox did not apply and save its value");
                        restartNotices.Active = true;
                        globalWindow.CloseSettings();
                        Check(!reconnect.Active && delay.ValueAsInt == 30 && !delay.Sensitive &&
                            !Descendants(panel).Any(child => child is TreeView), "Account Settings retained the restriction list or had incorrect reconnect defaults");
                        Check(!Descendants(panel).OfType<Button>().Any(button => button.Label == "Disconnect to test reconnect"),
                            "Account Settings retained the test reconnect button");
                        Check(((Label)widgets.Tabs.GetTabLabel(panel)).Text == "Account Settings", "The settings tab was renamed or moved");
                        Check(!restart.Active && !restartRegion.Sensitive && !returnDelay.Sensitive && returnDelay.ValueAsInt == 5,
                            "Restart protection was enabled by default or had incorrect control defaults");
                        Check(!autoSit.Active && !autoSitRestart.Active && !furniture.Sensitive && furniture.Text == "", "Auto Sit controls had unsafe defaults");
                        var content = ((Box)autoSit.Parent).Children;
                        Check(Array.IndexOf(content, Field<CheckButton>(panel, "_debug")) < Array.IndexOf(content, autoSit) &&
                            Array.IndexOf(content, autoSit) < Array.IndexOf(content, reconnect), "Auto Sit was not directly below the RLV settings");
                        inventory.StartLoading();
                        reconnect.Active = true;
                        delay.Value = 2;
                        Check(delay.Sensitive && h.Account.Settings == new AccountSettings { AutoReconnect = true, ReconnectDelaySeconds = 2 } &&
                            h.Store.Load(new SavedLogin(h.Name, h.Grid.LoginURI), out _) == h.Account.Settings,
                            "Settings controls did not apply and persist the account's delay");
                        var rlv = Field<CheckButton>(panel, "_rlvEnabled");
                        rlv.Active = false;
                        Check(!h.Account.Rlv.Enabled, "The RLV enable control stopped working");
                        rlv.Active = true;
                        autoSit.Active = true;
                        furniture.Text = "incomplete";
                        Check(furniture.Sensitive && Field<Label>(panel, "_autoSitStatus").Text.Contains("valid furniture UUID") &&
                            h.Clock.PendingTimers == 0, "Incomplete UUID entry crashed, lost feedback or scheduled Auto Sit");
                        furniture.Text = UUID.Random().ToString();
                        Check(h.Account.Settings.AutoSit && h.Account.Settings.AutoSitObjectId == furniture.Text && h.Clock.PendingTimers == 1 &&
                            h.Store.Load(new SavedLogin(h.Name, h.Grid.LoginURI), out _) == h.Account.Settings,
                            "The Auto Sit checkbox/UUID did not apply, schedule or persist");
                        autoSit.Active = false;
                        autoSitRestart.Active = true;
                        Check(furniture.Sensitive && !h.Account.Settings.AutoSit && h.Account.Settings.AutoSitOnRestartReturn &&
                            h.Clock.PendingTimers == 0 && h.Store.Load(new SavedLogin(h.Name, h.Grid.LoginURI), out _) == h.Account.Settings,
                            "Restart return Auto Sit could not be enabled/saved independently with the shared UUID");
                        autoSit.Active = true;
                        restart.Active = true;
                        restartRegion.Text = "Safe Region";
                        restartX.Value = 45.5;
                        restartY.Value = 67;
                        restartZ.Value = 901;
                        returnDelay.Value = 12;
                        Check(restartRegion.Sensitive && restartX.Sensitive && returnDelay.Sensitive &&
                            h.Account.Settings.TeleportOnRegionRestart && h.Account.Settings.RestartDestinationRegion == "Safe Region" &&
                            h.Account.Settings.RestartDestinationX == 45.5f && h.Account.Settings.RestartDestinationY == 67 &&
                            h.Account.Settings.RestartDestinationZ == 901 && h.Account.Settings.ReturnDelayMinutes == 12 &&
                            h.Store.Load(new SavedLogin(h.Name, h.Grid.LoginURI), out _) == h.Account.Settings &&
                            h.Account.RestartTeleportStatus.StartsWith("Waiting"),
                            "Restart controls did not apply/persist their settings or update their status");
                        restart.Active = false;
                        Check(!restartRegion.Sensitive && !h.Account.Settings.TeleportOnRegionRestart,
                            "Turning restart protection off left its controls active");
                        restart.Active = true;
                        h.Disconnect();
                        break;
                    case 1:
                        Check(!h.Account.IsConnected && h.Account.Status.Contains("reconnect scheduled"), "The disconnected account was removed or lost its retry status");
                        h.Clock.Advance(1);
                        Check(h.Attempts == 1, "The GTK session reconnected before its configured delay");
                        break;
                    case 2:
                        h.Clock.Advance(1);
                        break;
                    case 3:
                        Check(h.Attempts == 2 && sessions.Count == 1 && ReferenceEquals(sessions[h.Account], widgets) &&
                            h.Account.Status == "Reconnecting…", "Reconnect duplicated the account or replaced its UI");
                        Field<HashSet<UUID>>(inventory, "_fetched").Add(staleFolder);
                        replacement = new LibreMetaverse.Inventory(h.Account.Client, h.Fixture.Owner);
                        replacement.RootFolder = new InventoryFolder(UUID.Random())
                            { Name = "Reloaded inventory", OwnerID = h.Fixture.Owner, PreferredType = FolderType.Root };
                        replacement.UpdateNodeFor(new InventoryFolder(UUID.Random())
                            { Name = "Fresh folder", ParentUUID = replacement.RootFolder.UUID, OwnerID = h.Fixture.Owner });
                        typeof(InventoryManager).GetField("_Store", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(h.Account.Client.Inventory, replacement);
                        h.Connect();
                        break;
                    case 4:
                        Check(h.Account.IsConnected && Field<LibreMetaverse.Inventory>(inventory, "_subscribedStore") == replacement &&
                            !Field<HashSet<UUID>>(inventory, "_fetched").Contains(staleFolder),
                            "Inventory did not refresh after reconnect");
                        h.Disconnect();
                        break;
                    case 5:
                        widgets.LogoutButton.Click();
                        h.Clock.Advance(10);
                        Check(sessions.Count == 0 && h.Clock.PendingTimers == 0 && Field<bool>(panel, "_stopped"),
                            "Logging out retained account widgets, settings subscriptions or a reconnect timer");
                        break;
                    case 6:
                        GC.Collect(); GC.WaitForPendingFinalizers();
                        Check(h.Attempts == 2, "A removed account reconnected through a queued GTK callback");
                        Console.WriteLine("PASS native GTK independent login/return Auto Sit controls, shared UUID validation and saving, removed test buttons, restart/RLV controls, reconnect and logout cleanup");
                        Application.Quit();
                        return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL native GTK account settings (stage {stage - 1}): {ex}");
                result = 1;
                h.LoggedOut();
                if (sessions.ContainsKey(h.Account)) Invoke(main, "RemoveSession", h.Account);
                Application.Quit();
                return false;
            }
        });
        Application.Run();
        return result;
    }

    private static IEnumerable<Widget> Descendants(Container parent) => parent.Children.SelectMany(child =>
        child is Container container ? new[] { child }.Concat(Descendants(container)) : new[] { child });
    private static void Invoke(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static T Field<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Notices : INotificationOutput
    {
        public string? Error => null;
        public void Show(string key, string accountId, NotificationCategory category, string title, string body) { }
        public void Clear(string? accountId = null, NotificationCategory? category = null) { }
        public void Dispose() { }
    }
}
