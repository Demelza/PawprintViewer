using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LibreMetaverse;

namespace Radegast.Gtk;

/// <summary>Bounded background writer; chat histories on screen remain bounded independently.</summary>
internal sealed class ImTranscriptStore : IDisposable
{
    private static readonly ConcurrentDictionary<Guid, ImTranscriptStore> Active = new();
    private static readonly SemaphoreSlim Files = new(1, 1);
    private readonly Guid _id = Guid.NewGuid();
    private readonly AccountStoragePaths _paths;
    private readonly Action<string> _error;
    private readonly Channel<Work> _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Dictionary<string, Dictionary<string, string>> _catalogs = new();
    private readonly Task _worker;
    private bool _disposed;
    private int _reportedError;

    public ImTranscriptStore(AccountStoragePaths paths, Action<string>? error = null)
    {
        _paths = paths;
        _error = error ?? (_ => { });
        Active[_id] = this;
        _worker = Task.Run(ProcessAsync);
    }

    public void Append(SavedLogin account, UUID peer, string? name, ChatMessage message)
    {
        var sender = message.Outgoing ? account.AccountName : string.IsNullOrWhiteSpace(name) ? peer.ToString() : name;
        Enqueue(new(_paths.ChatDirectory(account), peer, name,
            $"[{message.Timestamp:yyyy-MM-dd HH:mm:ss zzz}] {sender}: {ChatMemoryLimits.LimitMessage(message.Text)}\n", null));
    }

    public void ResolveName(SavedLogin account, UUID peer, string name) =>
        Enqueue(new(_paths.ChatDirectory(account), peer, name, null, null));

    private void Enqueue(Work work)
    {
        if (_disposed) return;
        if (!_queue.Writer.TryWrite(work)) Report("IM transcripts could not keep up with incoming messages.");
    }

    public async Task FlushAsync()
    {
        if (_disposed) { await _worker.ConfigureAwait(false); return; }
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await _queue.Writer.WriteAsync(new("", UUID.Zero, null, null, complete)).ConfigureAwait(false); }
        catch (ChannelClosedException) { await _worker.ConfigureAwait(false); return; }
        await complete.Task.ConfigureAwait(false);
    }

    public static Task FlushAllAsync() => Task.WhenAll(Active.Values.Select(store => store.FlushAsync()));

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync())
            {
                if (work.Barrier != null) { work.Barrier.TrySetResult(); continue; }
                try
                {
                    await Files.WaitAsync().ConfigureAwait(false);
                    try { await WriteAsync(work).ConfigureAwait(false); }
                    finally { Files.Release(); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                { Report("IM transcripts could not be saved: " + ex.Message); }
            }
        }
        finally { Active.TryRemove(_id, out _); }
    }

    private void Report(string message)
    {
        if (Interlocked.Exchange(ref _reportedError, 1) == 0) _error(message);
    }

    private async Task WriteAsync(Work work)
    {
        if (!_catalogs.TryGetValue(work.Directory, out var catalog))
        {
            var index = Path.Combine(work.Directory, ".conversations.json");
            catalog = new();
            if (File.Exists(index))
            {
                try
                {
                    foreach (var (id, savedFile) in JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(index).ConfigureAwait(false)) ?? new())
                        if (UUID.TryParse(id, out _) && SafeFileName(savedFile)) catalog[id] = savedFile;
                }
                catch (JsonException) { /* Reconstruct safely from transcript headers. */ }
            }
            _catalogs[work.Directory] = catalog;
        }
        var key = work.Peer.ToString();
        var knownName = !string.IsNullOrWhiteSpace(work.Name) && !UUID.TryParse(work.Name, out _);
        if (work.Text == null && !catalog.ContainsKey(key)) return;
        Directory.CreateDirectory(work.Directory);
        var previous = catalog.GetValueOrDefault(key);
        var desired = knownName ? AccountStoragePaths.ResidentFileName(work.Name!) + ".txt" : previous ?? key + ".txt";
        if (File.Exists(Path.Combine(work.Directory, desired)) && previous != desired && !OwnedBy(Path.Combine(work.Directory, desired), work.Peer))
            desired = Path.GetFileNameWithoutExtension(desired) + "." + key + ".txt";
        if (previous != null && previous != desired && File.Exists(Path.Combine(work.Directory, previous)))
        {
            // Names may arrive after an outgoing IM. Rename that UUID transcript
            // instead of starting another conversation file.
            if (!File.Exists(Path.Combine(work.Directory, desired)))
                File.Move(Path.Combine(work.Directory, previous), Path.Combine(work.Directory, desired));
            else desired = previous;
        }
        var file = Path.Combine(work.Directory, desired);
        if (work.Text != null)
        {
            var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var stream = new FileStream(file, options);
            await using var output = new StreamWriter(stream, new UTF8Encoding(false));
            if (stream.Length == 0) await output.WriteLineAsync($"# IM conversation {key}").ConfigureAwait(false);
            await output.WriteAsync(work.Text).ConfigureAwait(false);
        }
        if (previous != desired)
        {
            catalog[key] = desired;
            var index = Path.Combine(work.Directory, ".conversations.json");
            var temporary = index + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var output = new FileStream(temporary, options))
                    await JsonSerializer.SerializeAsync(output, catalog).ConfigureAwait(false);
                File.Move(temporary, index, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static bool SafeFileName(string value) => !string.IsNullOrWhiteSpace(value) && value == Path.GetFileName(value) &&
        !value.Contains('/') && !value.Contains('\\') && value.EndsWith(".txt", StringComparison.Ordinal);

    private static bool OwnedBy(string file, UUID peer)
    {
        using var input = new StreamReader(file);
        var header = new char[80];
        var length = input.ReadBlock(header, 0, header.Length);
        return new string(header, 0, length).Split('\n')[0].TrimEnd('\r') == $"# IM conversation {peer}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Writer.TryComplete();
    }

    private sealed record Work(string Directory, UUID Peer, string? Name, string? Text, TaskCompletionSource? Barrier);
}
