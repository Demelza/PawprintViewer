using System.Net;
using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.StructuredData;

// Exercise the real capability deserializer and inventory store without a grid or sockets.
sealed class InventoryServer : HttpMessageHandler
{
    private readonly Dictionary<UUID, OSDMap> _folders = new();
    private readonly Dictionary<UUID, OSDMap> _items = new();
    public List<UUID> FolderRequests { get; } = new();
    public List<UUID> ItemRequests { get; } = new();
    public List<int> ItemBatchSizes { get; } = new();
    public TaskCompletionSource? Gate { get; set; }
    public HashSet<UUID> FailedFolders { get; } = new();
    public HashSet<UUID> MalformedFolders { get; } = new();
    private readonly GridClient _client;
    private readonly Dictionary<string, Uri> _caps;
    public List<int> BakeVersions { get; } = new();
    public List<string> InterestRequests { get; } = new();
    public TaskCompletionSource? InterestGate { get; set; }
    public HashSet<UUID> FailedDeletes { get; } = new();
    public bool FailCreates { get; set; }
    public TaskCompletionSource? BakeGate { get; set; }
    public TaskCompletionSource? CreateGate { get; set; }
    public bool IgnoreBakeCancellation { get; set; }
    public int CreatedLinks { get; private set; }

    public InventoryServer(GridClient client, Simulator simulator)
    {
        _client = client;
        Snapshot(client.Inventory.Store!.RootFolder!);
        client.HttpCapsClient.Dispose();
        client.HttpCapsClient = new HttpCapsClient(this);
        simulator.Caps = (Caps)Activator.CreateInstance(typeof(Caps), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { simulator, new Uri("https://inventory.test/seed") }, null)!;
        var caps = (Dictionary<string, Uri>)typeof(Caps).GetField("_Caps", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(simulator.Caps)!;
        caps["FetchInventoryDescendents2"] = new Uri("https://inventory.test/folders");
        caps["FetchInventory2"] = new Uri("https://inventory.test/items");
        caps["InventoryAPIv3"] = new Uri("https://inventory.test/ais");
        _caps = caps;

        void Snapshot(InventoryFolder folder)
        {
            var children = client.Inventory.Store.GetContents(folder.UUID);
            _folders[folder.UUID] = new OSDMap
            {
                ["folder_id"] = folder.UUID, ["owner_id"] = client.Self.AgentID,
                ["version"] = folder.Version, ["descendents"] = children.Count,
                ["categories"] = new OSDArray(children.OfType<InventoryFolder>().Select(f => (OSD)new OSDMap
                {
                    ["category_id"] = f.UUID, ["parent_id"] = f.ParentUUID, ["agent_id"] = f.OwnerID,
                    ["name"] = f.Name, ["version"] = f.Version, ["type_default"] = (int)f.PreferredType
                }).ToList()),
                ["items"] = new OSDArray(children.OfType<InventoryItem>().Select(i => (OSD)Item(i)).ToList())
            };
            foreach (var item in children.OfType<InventoryItem>()) _items[item.UUID] = Item(item);
            foreach (var child in children.OfType<InventoryFolder>()) Snapshot(child);
        }
    }

    public void EnableAppearance() => _caps["UpdateAvatarAppearance"] = new Uri("https://inventory.test/bake");
    public void EnableInterest() => _caps["InterestList"] = new Uri("https://inventory.test/interest");
    public void SetVersion(UUID folder, int version) => _folders[folder]["version"] = version;
    public HashSet<UUID> LinkedItems(UUID folder) => ((OSDArray)_folders[folder]["items"]).Cast<OSDMap>()
        .Where(i => i["type"].AsInteger() == (int)AssetType.Link).Select(i => i["asset_id"].AsUUID()).ToHashSet();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var payload = request.Content == null ? new OSDMap() : (OSDMap)OSDParser.Deserialize(await request.Content.ReadAsByteArrayAsync(token));
        OSDMap reply;
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/interest")
        {
            InterestRequests.Add(request.Method == HttpMethod.Delete ? "reset" : payload["mode"].AsString());
            if (InterestGate != null) await InterestGate.Task.WaitAsync(token);
            reply = new() { ["success"] = true };
        }
        else if (path == "/bake")
        {
            var version = payload["cof_version"].AsInteger();
            BakeVersions.Add(version);
            if (BakeGate != null) await BakeGate.Task.WaitAsync(IgnoreBakeCancellation ? CancellationToken.None : token);
            var cof = _client.Inventory.Store!.GetContents(_client.Inventory.Store.RootFolder!.UUID).OfType<InventoryFolder>()
                .Single(f => f.PreferredType == FolderType.CurrentOutfit);
            var expected = _folders[cof.UUID]["version"].AsInteger();
            reply = version == expected ? new() { ["success"] = true, ["cof_version"] = version }
                : new() { ["success"] = false, ["expected"] = expected };
        }
        else if (path.StartsWith("/ais/item/"))
        {
            var id = new UUID(path.Split('/').Last());
            if (FailedDeletes.Contains(id)) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("failure") };
            var item = _items[id];
            var parent = item["parent_id"].AsUUID();
            var folder = _folders[parent];
            if (request.Method == HttpMethod.Patch)
            {
                item["desc"] = payload["desc"];
                foreach (OSDMap child in (OSDArray)folder["items"])
                    if (child["item_id"].AsUUID() == id) child["desc"] = payload["desc"];
                var updatedVersion = folder["version"].AsInteger() + 1;
                folder["version"] = updatedVersion;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OSDParser.SerializeLLSDXmlString(
                    new OSDMap { ["_updated_category_versions"] = new OSDMap { [parent.ToString()] = updatedVersion } })) };
            }
            folder["items"] = new OSDArray(((OSDArray)folder["items"]).Where(i => ((OSDMap)i)["item_id"].AsUUID() != id).ToList());
            _items.Remove(id);
            folder["descendents"] = ((OSDArray)folder["items"]).Count;
            var version = folder["version"].AsInteger() + 1;
            folder["version"] = version;
            reply = new() { ["_removed_items"] = new OSDArray { id },
                ["_updated_category_versions"] = new OSDMap { [parent.ToString()] = version } };
        }
        else if (path.StartsWith("/ais/category/"))
        {
            CreatedLinks++;
            if (CreateGate != null) await CreateGate.Task.WaitAsync(token);
            if (FailCreates) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("failure") };
            var parent = new UUID(path.Split('/').Last());
            var created = new OSDMap();
            foreach (OSDMap source in (OSDArray)payload["links"])
            {
                var link = new InventoryItem(UUID.Random()) { Name = source["name"], Description = source["desc"],
                    OwnerID = _client.Self.AgentID, ParentUUID = parent, AssetUUID = source["linked_id"],
                    AssetType = AssetType.Link, InventoryType = (InventoryType)source["inv_type"].AsInteger() };
                var data = Item(link);
                data["linked_id"] = link.AssetUUID;
                data["agent_id"] = link.OwnerID;
                created[link.UUID.ToString()] = data;
                _items[link.UUID] = data;
                ((OSDArray)_folders[parent]["items"]).Add(data);
            }
            var folder = _folders[parent];
            folder["descendents"] = ((OSDArray)folder["items"]).Count;
            var version = folder["version"].AsInteger() + 1;
            folder["version"] = version;
            reply = new() { ["_embedded"] = new OSDMap { ["links"] = created },
                ["_updated_category_versions"] = new OSDMap { [parent.ToString()] = version } };
        }
        else if (path == "/folders")
        {
            var folders = new OSDArray();
            foreach (OSDMap entry in (OSDArray)payload["folders"])
            {
                var id = entry["folder_id"].AsUUID();
                FolderRequests.Add(id);
                if (!FailedFolders.Contains(id) && _folders.TryGetValue(id, out var folder))
                {
                    if (MalformedFolders.Contains(id))
                    {
                        var malformed = new OSDMap();
                        foreach (KeyValuePair<string, OSD> field in folder) malformed[field.Key] = field.Value;
                        malformed["items"] = "invalid items response";
                        folders.Add(malformed);
                    }
                    else folders.Add(folder);
                }
            }
            reply = new OSDMap { ["folders"] = folders };
        }
        else
        {
            var items = new OSDArray();
            ItemBatchSizes.Add(((OSDArray)payload["items"]).Count);
            foreach (OSDMap entry in (OSDArray)payload["items"])
            {
                ItemRequests.Add(entry["item_id"].AsUUID());
                if (_items.TryGetValue(entry["item_id"].AsUUID(), out var item)) items.Add(item);
            }
            reply = new OSDMap { ["items"] = items };
        }
        if (Gate != null) await Gate.Task.WaitAsync(token);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(OSDParser.SerializeLLSDXmlString(reply), System.Text.Encoding.UTF8, "application/llsd+xml")
        };
    }

    private static OSDMap Item(InventoryItem item) => new()
    {
        ["item_id"] = item.UUID, ["parent_id"] = item.ParentUUID, ["name"] = item.Name,
        ["desc"] = item.Description, ["asset_id"] = item.AssetUUID, ["type"] = (int)item.AssetType,
        ["inv_type"] = (int)item.InventoryType, ["flags"] = item.Flags, ["created_at"] = 0,
        ["permissions"] = new OSDMap
        {
            ["owner_id"] = item.OwnerID, ["creator_id"] = item.OwnerID, ["last_owner_id"] = item.OwnerID,
            ["group_id"] = UUID.Zero, ["is_owner_group"] = false, ["base_mask"] = int.MaxValue,
            ["owner_mask"] = int.MaxValue, ["everyone_mask"] = 0, ["group_mask"] = 0, ["next_owner_mask"] = int.MaxValue
        },
        ["sale_info"] = new OSDMap { ["sale_price"] = 0, ["sale_type"] = 0 }
    };
}
