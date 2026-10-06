namespace Radegast.Gtk;

internal readonly record struct MapTile(int Level, int X, int Y)
{
    public int Regions => 1 << (Level - 1);
    public double Meters => Regions * 256d;
    public string Filename => $"map-{Level}-{X}-{Y}-objects.jpg";
}

/// <summary>North is up; world coordinates are meters, tile coordinates are regions.</summary>
internal sealed class WorldMapViewport
{
    public double CenterX { get; private set; }
    public double CenterY { get; private set; }
    public double Scale { get; private set; } = 1;
    public int TileLevel => Math.Clamp(1 + (int)Math.Floor(Math.Log2(1 / Scale)), 1, 8);

    public void Center(double x, double y)
    {
        CenterX = Math.Clamp(x, 0, 65536d * 256);
        CenterY = Math.Clamp(y, 0, 65536d * 256);
    }

    public (double X, double Y) ToScreen(double x, double y, double width, double height) =>
        (width / 2 + (x - CenterX) * Scale, height / 2 - (y - CenterY) * Scale);

    public (double X, double Y) ToWorld(double x, double y, double width, double height) =>
        (CenterX + (x - width / 2) / Scale, CenterY - (y - height / 2) / Scale);

    public void Pan(double dx, double dy) => Center(CenterX - dx / Scale, CenterY + dy / Scale);

    public void Zoom(double steps, double x, double y, double width, double height)
    {
        var anchor = ToWorld(x, y, width, height);
        Scale = Math.Clamp(Scale * Math.Pow(1.35, Math.Clamp(steps, -8, 8)), 1d / 128, 4);
        Center(anchor.X - (x - width / 2) / Scale, anchor.Y + (y - height / 2) / Scale);
    }

    /// <summary>Limit population queries to a close view; never scan thousands of regions at world zoom.</summary>
    public IReadOnlyList<(ushort X, ushort Y)> VisibleRegions(double width, double height)
    {
        var bottomLeft = ToWorld(0, height, width, height);
        var topRight = ToWorld(width, 0, width, height);
        var minX = Math.Max(0, (int)Math.Floor(bottomLeft.X / 256));
        var minY = Math.Max(0, (int)Math.Floor(bottomLeft.Y / 256));
        var maxX = Math.Min(65535, (int)Math.Floor(topRight.X / 256));
        var maxY = Math.Min(65535, (int)Math.Floor(topRight.Y / 256));
        if ((long)(maxX - minX + 1) * (maxY - minY + 1) > 64) return Array.Empty<(ushort, ushort)>();
        var regions = new List<(ushort X, ushort Y)>();
        for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++) regions.Add(((ushort)x, (ushort)y));
        return regions.OrderBy(region => Math.Abs(region.X * 256 + 128 - CenterX) +
            Math.Abs(region.Y * 256 + 128 - CenterY)).ToArray();
    }

    public IReadOnlyList<MapTile> VisibleTiles(double width, double height)
    {
        var level = TileLevel;
        var stride = 1 << (level - 1);
        var meters = stride * 256d;
        var bottomLeft = ToWorld(0, height, width, height);
        var topRight = ToWorld(width, 0, width, height);
        var minX = Math.Max(0, (int)Math.Floor(bottomLeft.X / meters));
        var minY = Math.Max(0, (int)Math.Floor(bottomLeft.Y / meters));
        var maxX = Math.Min(65536 / stride - 1, (int)Math.Floor(topRight.X / meters));
        var maxY = Math.Min(65536 / stride - 1, (int)Math.Floor(topRight.Y / meters));
        var tiles = new List<MapTile>();
        for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++) tiles.Add(new(level, x * stride, y * stride));
        return tiles.OrderBy(tile => Math.Abs(tile.X * 256 + meters / 2 - CenterX) +
            Math.Abs(tile.Y * 256 + meters / 2 - CenterY)).ToArray();
    }
}
