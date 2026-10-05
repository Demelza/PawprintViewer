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
    public HashSet<UUID> FailedFolders { get; } = new();
    public HashSet<UUID> MalformedFolders { get; } = new();

    public InventoryServer(GridClient client, Simulator simulator)
    {
        Snapshot(client.Inventory.Store!.RootFolder!);
        client.HttpCapsClient.Dispose();
        client.HttpCapsClient = new HttpCapsClient(this);
        simulator.Caps = (Caps)Activator.CreateInstance(typeof(Caps), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { simulator, new Uri("https://inventory.test/seed") }, null)!;
        var caps = (Dictionary<string, Uri>)typeof(Caps).GetField("_Caps", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(simulator.Caps)!;
        caps["FetchInventoryDescendents2"] = new Uri("https://inventory.test/folders");
        caps["FetchInventory2"] = new Uri("https://inventory.test/items");

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

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var payload = (OSDMap)OSDParser.Deserialize(await request.Content!.ReadAsByteArrayAsync(token));
        OSDMap reply;
        if (request.RequestUri!.AbsolutePath == "/folders")
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
            foreach (OSDMap entry in (OSDArray)payload["items"])
                if (_items.TryGetValue(entry["item_id"].AsUUID(), out var item)) items.Add(item);
            reply = new OSDMap { ["items"] = items };
        }
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
