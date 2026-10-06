using System.Net;
using System.Reflection;
using System.Threading.Channels;
using Gtk;
using LibreMetaverse;
using Radegast.Gtk;

/// <summary>Exercises real GTK window lifetimes without grid access or a desktop keyring.</summary>
internal static class NativeLoginChecks
{
    public static int Run()
    {
        Application.Init();
        var directory = Path.Combine(Path.GetTempPath(), "pawprint-login-check-" + Guid.NewGuid().ToString("N"));
        var vault = new PasswordStore();
        var store = new SavedLoginStore(Path.Combine(directory, "accounts.json"), vault);
        using var main = new MainWindow(new GlobalSettings(Path.Combine(directory, "settings.json")), new Notices());
        main.ShowAll();
        var result = 0;
        var cycles = 0;
        var connected = 0;
        var warnings = 0;
        var pendingClosures = 0;
        GLib.Timeout.Add(200, () =>
        {
            try
            {
                if (result != 0) return false;
                if (cycles == 30)
                {
                    if (pendingClosures != 0) return true;
                    Check(connected == 20 && vault.Saves.Count == 20, "Successful login sessions or credential saves were lost");
                    Check(warnings == 10, "Save errors were lost after closing the login window");
                    Check(store.ForGrid("https://login.example.org/login.cgi").Count == 20, "Successful accounts were not remembered");
                    Console.WriteLine("PASS native GTK login success, asynchronous saves, cancel/window close and garbage collection");
                    Application.Quit();
                    return false;
                }

                var cycle = cycles++;
                var session = new AccountSession(GtkDispatch.Post);
                var login = new LoginWindow(main, store);
                var destroyed = 0;
                login.Destroyed += (_, _) => destroyed++;
                login.LoginSucceeded += added =>
                {
                    connected++;
                    Invoke(main, "AddSession", added);
                };
                login.CredentialsWarning += (_, _) => warnings++;
                login.ShowAll();
                SetField(login, "_pending", session);
                if (cycle < 20)
                {
                    // Fake the completed network login; all outgoing packets stay in memory.
                    SetProperty(session, nameof(AccountSession.Name), $"Test{cycle} Resident");
                    SetProperty(session.Client.Self, nameof(AgentManager.AgentID), UUID.Random());
                    SetProperty(session.Net, nameof(Radegast.NetCom.IsLoggedIn), true);
                    var sim = new Simulator(session.Client, new IPEndPoint(IPAddress.Loopback, 13000),
                        Utils.UIntsToLong(256000, 256512)) { Name = "Test Region" };
                    session.Client.Network.Simulators.Add(sim);
                    session.Client.Network.CurrentSim = sim;
                    SetField(session.Client.Network, "_packetOutbox", Channel.CreateUnbounded<NetworkManager.OutgoingPacket>());
                    SetField(login, "_submittedCredentials",
                        ($"Test{cycle} Resident", "https://login.example.org/login.cgi", "synthetic-test-password"));
                    Invoke(login, "OnLoginProgress", session, LoginStatus.Success, "Connected", "");
                    Check(destroyed == 1 && login.Handle == IntPtr.Zero, "Login popup retained a destroyed native window");
                    // Saving finishes after the popup closes, including recoverable keyring failures.
                    if (cycle % 2 == 0) vault.Saves.Last().SetResult();
                    else vault.Saves.Last().SetException(new InvalidOperationException("Synthetic keyring failure"));
                    pendingClosures++;
                    GLib.Timeout.Add(70, () =>
                    {
                        Guard(() =>
                        {
                            SetProperty(session.Net, nameof(Radegast.NetCom.IsLoggedIn), false);
                            Invoke(main, "RemoveSession", session);
                            sim.Dispose();
                            Collect();
                            pendingClosures--;
                        });
                        return false;
                    });
                }
                else
                {
                    if (cycle % 2 == 0) FindButton(login, "Cancel")!.Click();
                    else login.Close(); // Emit the same delete-event as the window manager's close button.
                    pendingClosures++;
                    GLib.Timeout.Add(70, () =>
                    {
                        Guard(() =>
                        {
                            Check(destroyed == 1 && login.Handle == IntPtr.Zero, $"Cancelling retained a destroyed native window in cycle {cycle}");
                            Invoke(login, "OnLoginProgress", session, LoginStatus.Success, "Late callback", "");
                            Check(connected == 20, "A closed popup accepted a late login callback");
                            Collect();
                            pendingClosures--;
                        });
                        return false;
                    });
                }
                return true;
            }
            catch (Exception ex)
            {
                Fail(ex);
                return false;
            }
        });
        try { Application.Run(); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        return result;

        void Guard(System.Action action)
        {
            try { action(); }
            catch (Exception ex) { Fail(ex); }
        }
        void Fail(Exception ex)
        {
            result = 1;
            Console.Error.WriteLine($"FAIL native GTK login lifecycle: {ex}");
            Application.Quit();
        }
    }

    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Invoke(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void SetField(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void SetProperty(object target, string property, object value) =>
        target.GetType().GetProperty(property)!.SetValue(target, value);
    private static Button? FindButton(Widget widget, string label)
    {
        if (widget is Button button && button.Label == label) return button;
        if (widget is Container container)
            foreach (var child in container.Children)
                if (FindButton(child, label) is { } found) return found;
        return null;
    }

    private sealed class PasswordStore : ILoginPasswordStore
    {
        public List<TaskCompletionSource> Saves { get; } = new();
        public Task<string?> LookupAsync(string key, CancellationToken token) => Task.FromResult<string?>(null);
        public Task StoreAsync(string key, string label, string password, CancellationToken token)
        {
            var save = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Saves.Add(save);
            return save.Task;
        }
    }

    private sealed class Notices : INotificationOutput
    {
        public string? Error => null;
        public void Show(string key, string accountId, NotificationCategory category, string title, string body) { }
        public void Clear(string? accountId = null, NotificationCategory? category = null) { }
        public void Dispose() { }
    }
}
