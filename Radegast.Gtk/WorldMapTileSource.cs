using System.Net;

namespace Radegast.Gtk;

/// <summary>Grid-advertised map tiles, separate from the simulator's region lookup.</summary>
internal static class WorldMapTileSource
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private const int MaxImageBytes = 2 * 1024 * 1024;

    public static Uri? ServerUri(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    public static async Task<byte[]?> DownloadAsync(Uri server, MapTile tile, CancellationToken token)
    {
        using var response = await Http.GetAsync(new Uri(server, tile.Filename),
            HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxImageBytes)
            throw new IOException("Map tile is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var result = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (result.Length + count > MaxImageBytes) throw new IOException("Map tile is too large.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }
}
