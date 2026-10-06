using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Radegast.Gtk;

internal static class MapChecks
{
    public static async Task ClickedRegions()
    {
        using var account = new AccountSession(action => action());
        using var fixture = new Fixture(account);
        var packets = fixture.CapturePackets();
        Connected(account, true);
        fixture.Simulator.Name = "Home";
        fixture.Simulator.Handle = Utils.UIntsToLong(256000, 256512);
        try
        {
            var home = await account.FindMapRegionAtAsync(256255.99, 256513);
            Check(home.Name == "Home" && packets().Count == 0, "Clicking home required a lookup or rounded into the next region");
            var lookup = account.FindMapRegionAtAsync(256256, 256600);
            var request = packets().OfType<MapBlockRequestPacket>().Single();
            Check(request.PositionData.MinX == 1001 && request.PositionData.MaxX == 1001 &&
                request.PositionData.MinY == 1002 && request.PositionData.MaxY == 1002,
                "A map click queried the wrong region or requested more than the clicked cell");
            fixture.Receive(RegionReply("Next Region", 1001, 1002));
            var region = await lookup.WaitAsync(TimeSpan.FromSeconds(3));
            Check(region.Name == "Next Region" && region.RegionHandle == Utils.UIntsToLong(256256, 256512), "Click did not resolve the region name");
            var water = account.FindMapRegionAtAsync(257000, 256600);
            _ = packets();
            fixture.Receive(RegionReply("", 1003, 1002, SimAccess.NonExistent));
            await Rejected(water);
            foreach (var (x, y) in new[] { (-1d, 256600d), (double.NaN, 256600d), (256000d, double.PositiveInfinity), (65536d * 256, 256600d) })
                await Rejected(account.FindMapRegionAtAsync(x, y));
            Check(packets().Count == 0, "An invalid map point emitted a lookup");
            using var cancel = new CancellationTokenSource();
            var pending = account.FindMapRegionAtAsync(258000, 256600, cancel.Token);
            _ = packets(); cancel.Cancel(); await Canceled(pending);
            fixture.Simulator.SizeX = 512;
            Check((await account.FindMapRegionAtAsync(256300, 256600)).RegionHandle == fixture.Simulator.Handle && packets().Count == 0,
                "A click inside a large current region used a neighbouring region name");
            await fixture.Command("@showworldmap=n");
            await Rejected(account.FindMapRegionAtAsync(256000, 256600));
        }
        finally { Connected(account, false); }
        await Rejected(account.FindMapRegionAtAsync(256000, 256600));
    }

    public static async Task AvatarPositions()
    {
        using var account = new AccountSession(action => action());
        using var other = new AccountSession(action => action());
        using var fixture = new Fixture(account);
        using var otherFixture = new Fixture(other);
        fixture.CapturePackets(); otherFixture.CapturePackets();
        Connected(account, true); Connected(other, true);
        fixture.Simulator.Handle = Utils.UIntsToLong(256000, 256512);
        using var neighbour = new Simulator(account.Client, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 13001),
            Utils.UIntsToLong(256256, 256512));
        account.Client.Network.Simulators.Add(neighbour);
        typeof(Simulator).GetField("connected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(neighbour, true);
        var person = UUID.Random();
        var second = UUID.Random();
        try
        {
            fixture.Receive(Coarse((fixture.Owner, 10, 20), (person, 45, 55)));
            await WaitUntil(() => account.GetMapAvatarLocations().SelectMany(region => region.People).Count() == 1);
            var marker = account.GetMapAvatarLocations().SelectMany(region => region.People).Single();
            Check(marker == new MapAvatarMarker(256045, 256567), "Nearby avatar coordinates were not converted to world meters");
            Check(other.GetMapAvatarLocations().Count == 0, "Avatar markers leaked between accounts");
            fixture.Receive(Coarse((person, 1, 2), (second, 30, 40)), neighbour);
            await WaitUntil(() => account.GetMapAvatarLocations().SelectMany(region => region.People).Count() == 2);
            Check(account.GetMapAvatarLocations().SelectMany(region => region.People).Contains(new MapAvatarMarker(256286, 256552)),
                "Connected neighbouring avatars were omitted or the same resident was drawn twice");
            typeof(Simulator).GetField("connected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(neighbour, false);
            Check(account.GetMapAvatarLocations().SelectMany(region => region.People).Count() == 1, "Disconnected neighbours retained markers");
            await fixture.Command("@shownearby=n");
            Check(!account.CanViewMapPeople && account.GetMapAvatarLocations().Count == 0, "Nearby restrictions revealed other avatars");
            await fixture.Command("@clear");
            fixture.Receive(Coarse((fixture.Owner, 10, 20)));
            await WaitUntil(() => !account.GetMapAvatarLocations().SelectMany(region => region.People).Any());
        }
        finally { Connected(account, false); Connected(other, false); }
        Check(account.GetMapAvatarLocations().Count == 0, "Disconnected account retained visible markers");
    }

    public static Task PopulationBounds()
    {
        var view = new WorldMapViewport();
        view.Center(256128, 256640);
        var regions = view.VisibleRegions(682, 682);
        Check(regions.Count == 9 && regions.Contains(((ushort)1000, (ushort)1002)), "Nearby population queries omitted visible cells");
        view.Zoom(-8, 341, 341, 682, 682);
        Check(view.VisibleRegions(682, 682).Count == 0, "Zooming out attempted to query hundreds of regions");
        view.Center(0, 0);
        view.Zoom(8, 341, 341, 682, 682);
        Check(view.VisibleRegions(682, 682).All(region => region.X <= 1 && region.Y <= 1), "Population queries wrapped around the grid edge");
        return Task.CompletedTask;
    }

    private static CoarseLocationUpdatePacket Coarse(params (UUID Id, byte X, byte Y)[] people) => new()
    {
        AgentData = people.Select(person => new CoarseLocationUpdatePacket.AgentDataBlock { AgentID = person.Id }).ToArray(),
        Location = people.Select(person => new CoarseLocationUpdatePacket.LocationBlock { X = person.X, Y = person.Y, Z = 10 }).ToArray()
    };

    public static Task Viewport()
    {
        var map = new WorldMapViewport();
        map.Center(256128, 257064);
        var center = map.ToScreen(256128, 257064, 640, 640);
        Check(center == (320d, 320d), "Avatar is not centered");
        var north = map.ToScreen(256128, 257164, 640, 640);
        Check(north.Y < center.Y, "North is not up");
        var point = map.ToWorld(100, 160, 640, 640);
        map.Zoom(-2, 100, 160, 640, 640);
        var zoomed = map.ToWorld(100, 160, 640, 640);
        Check(Math.Abs(point.X - zoomed.X) < 0.001 && Math.Abs(point.Y - zoomed.Y) < 0.001,
            "Zoom moved the world point under the cursor");
        var before = map.ToScreen(point.X, point.Y, 640, 640);
        map.Pan(80, -25);
        var after = map.ToScreen(point.X, point.Y, 640, 640);
        Check(Math.Abs(after.X - before.X - 80) < 0.001 && Math.Abs(after.Y - before.Y + 25) < 0.001,
            "Drag moved the map in the wrong direction");
        for (var i = 0; i < 50; i++) map.Zoom(-8, 320, 320, 640, 640);
        Check(map.Scale == 1d / 128 && map.TileLevel == 8, "Zoom out was not bounded");
        var tiles = map.VisibleTiles(640, 640);
        Check(tiles.Count is > 0 and < 30 && tiles.All(tile => tile.X % tile.Regions == 0 && tile.Y % tile.Regions == 0),
            "Zoomed tiles are not aligned to the grid or requests are unbounded");
        var corner = map.ToWorld(0, 640, 640, 640);
        Check(tiles.Any(tile => corner.X >= tile.X * 256d && corner.X < tile.X * 256d + tile.Meters &&
            corner.Y >= tile.Y * 256d && corner.Y < tile.Y * 256d + tile.Meters), "Viewport corner is missing its tile");
        for (var i = 0; i < 50; i++) map.Zoom(8, 320, 320, 640, 640);
        Check(map.Scale == 4 && map.TileLevel == 1, "Zoom in was not bounded");
        map.Center(-100, -100);
        Check(map.VisibleTiles(640, 640).All(tile => tile.X >= 0 && tile.Y >= 0), "Negative tile coordinates requested");
        return Task.CompletedTask;
    }

    public static async Task RegionLookup()
    {
        using var account = new AccountSession(action => action());
        using var other = new AccountSession(action => action());
        using var fixture = new Fixture(account);
        using var otherFixture = new Fixture(other);
        var packets = fixture.CapturePackets();
        var otherPackets = otherFixture.CapturePackets();
        Connected(account, true); Connected(other, true);
        fixture.Simulator.Name = "Home";
        try
        {
            Check((await account.FindMapRegionAsync(" home ")).RegionHandle == fixture.Simulator.Handle && packets().Count == 0,
                "The current region was unnecessarily fetched or its name was not normalized");
            var lookup = account.FindMapRegionAsync("Destination");
            var request = packets().OfType<MapNameRequestPacket>().Single();
            Check(Utils.BytesToString(request.NameData.Name) == "destination" && request.AgentData.AgentID == fixture.Owner,
                "Region lookup did not use the account or exact input name");
            fixture.Receive(RegionReply("Destination", 1100, 1200));
            var destination = await lookup.WaitAsync(TimeSpan.FromSeconds(3));
            Check(destination.RegionHandle == Utils.UIntsToLong(1100 * 256, 1200 * 256), "Region origin was not converted to world meters");
            Check((await account.FindMapRegionAsync("DESTINATION")).RegionHandle == destination.RegionHandle && packets().Count == 0,
                "Exact cached lookup failed");
            Check(otherPackets().Count == 0 && !other.Client.Grid.RegionsReadOnly.ContainsKey("destination"),
                "Region lookup leaked into another account");
            var missing = account.FindMapRegionAsync("Missing");
            _ = packets();
            fixture.Receive(RegionReply("Missing", 1101, 1200, SimAccess.NonExistent));
            await Rejected(missing);
            using var cancel = new CancellationTokenSource();
            var pending = account.FindMapRegionAsync("Pending", cancel.Token);
            _ = packets(); cancel.Cancel();
            await Canceled(pending);
            fixture.Receive(RegionReply("Pending", 1102, 1200));
            Check(account.CanViewWorldMap, "Canceled lookup changed map permissions");
        }
        finally { Connected(account, false); Connected(other, false); }
    }

    public static async Task Permissions()
    {
        using var account = new AccountSession(action => action());
        using var fixture = new Fixture(account);
        var packets = fixture.CapturePackets();
        fixture.Simulator.Name = "Home";
        Connected(account, true);
        var region = await account.FindMapRegionAsync("Home");
        try
        {
            foreach (var position in new[] { new Vector3(-1, 10, 20), new Vector3(256, 10, 20),
                new Vector3(10, 256, 20), new Vector3(10, 10, -1), new Vector3(float.NaN, 10, 20),
                new Vector3(10, 10, float.PositiveInfinity) })
                await Rejected(account.TeleportMapAsync(region, position));
            Check(packets().Count == 0, "Invalid coordinates emitted a teleport");
            await fixture.Command("@showworldmap=n");
            Check(!account.CanViewWorldMap, "World map restriction was ignored or blacklisted");
            await Rejected(account.FindMapRegionAsync("Home"));
            await Rejected(account.TeleportMapAsync(region, new(10, 10, 20)));
            await fixture.Command("@clear,showloc=n");
            Check(!account.CanViewWorldMap, "The map revealed a hidden location");
            await fixture.Command("@clear,tploc=n");
            Check(account.CanViewWorldMap && account.MapTeleportError(region, new(10, 10, 20)) != null,
                "Location teleport restriction was ignored");
            await Rejected(account.TeleportMapAsync(region, new(10, 10, 20)));
            await fixture.Command("@clear,tplocal:2=n");
            Check(account.MapTeleportError(region, new(0, 0, 1)) == null && account.MapTeleportError(region, new(10, 10, 20)) != null,
                "Local teleport distance restriction was ignored");
            await fixture.Command("@clear,unsit=n");
            typeof(AgentManager).GetField("sittingOn", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(account.Client.Self, 123u);
            await Rejected(account.TeleportMapAsync(region, new(10, 10, 20)));
            Check(packets().Count == 0, "Restricted actions emitted teleport or lookup packets");
        }
        finally { Connected(account, false); }
        await Rejected(account.FindMapRegionAsync("Home"));
        await Rejected(account.TeleportMapAsync(region, new(10, 10, 20)));
    }

    public static async Task Teleport()
    {
        using var account = new AccountSession(action => action());
        using var fixture = new Fixture(account);
        var packets = fixture.CapturePackets();
        fixture.Simulator.Name = "Home";
        Connected(account, true);
        var region = await account.FindMapRegionAsync("Home");
        try
        {
            var teleport = account.TeleportMapAsync(region, new(25, 35, 45));
            await Rejected(account.TeleportMapAsync(region, new(50, 60, 70)));
            typeof(NetworkManager).GetMethod("OnEventQueueRunning", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(account.Client.Network, new object[] { new EventQueueRunningEventArgs(fixture.Simulator) });
            TeleportLocationRequestPacket? sent = null;
            await WaitUntil(() => (sent ??= packets().OfType<TeleportLocationRequestPacket>().SingleOrDefault()) != null);
            Check(sent!.AgentData.AgentID == fixture.Owner && sent.Info.RegionHandle == region.RegionHandle &&
                sent.Info.Position == new Vector3(25, 35, 45), "Teleport used the wrong account, region or coordinates");
            Check(!teleport.IsCompleted, "Teleport was reported complete without server confirmation");
            fixture.Receive(new TeleportLocalPacket
            {
                Info = { AgentID = fixture.Owner, Position = new(25, 35, 45), LookAt = new(0, 1, 0) }
            });
            await teleport.WaitAsync(TimeSpan.FromSeconds(3));
            Check(account.Client.Self.SimPosition == new Vector3(25, 35, 45), "Confirmed teleport did not update the avatar position");
            var pending = account.TeleportMapAsync(region, new(30, 40, 50));
            await fixture.Command("@tploc=n");
            await Canceled(pending);
            Check(!packets().OfType<TeleportLocationRequestPacket>().Any(), "A new RLV lock did not cancel a waiting request");
        }
        finally { Connected(account, false); }
    }

    public static Task TileServer()
    {
        using var account = new AccountSession(action => action());
        using var other = new AccountSession(action => action());
        var reply = typeof(AccountSession).GetMethod("OnLoginResponse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        reply.Invoke(account, new object?[] { true, false, "", "", new LoginResponseData { MapServerUrl = "https://example.org/map" } });
        reply.Invoke(other, new object?[] { true, false, "", "", new LoginResponseData { MapServerUrl = "https://other.example/map/" } });
        Check(new Uri(account.MapTileServer!, new MapTile(3, 1000, 1004).Filename).AbsoluteUri ==
            "https://example.org/map/map-3-1000-1004-objects.jpg", "Login tile endpoint or zoom tile filename was ignored");
        Check(other.MapTileServer!.Host == "other.example", "Accounts shared their map tile server");
        Check(WorldMapTileSource.ServerUri("file:///tmp/map") == null && WorldMapTileSource.ServerUri("") == null,
            "An invalid map service was accepted");
        return Task.CompletedTask;
    }

    private static MapBlockReplyPacket RegionReply(string name, ushort x, ushort y, SimAccess access = SimAccess.PG) => new()
    {
        Data = new[] { new MapBlockReplyPacket.DataBlock { Name = Utils.StringToBytes(name), X = x, Y = y, Access = (byte)access } }
    };

    private static void Connected(AccountSession account, bool connected) =>
        typeof(Radegast.NetCom).GetProperty(nameof(Radegast.NetCom.IsLoggedIn))!.SetValue(account.Net, connected);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Rejected(Task action)
    {
        try { await action; }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Invalid map action was accepted");
    }
    private static async Task Canceled(Task action)
    {
        try { await action.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Pending map action was not canceled");
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
