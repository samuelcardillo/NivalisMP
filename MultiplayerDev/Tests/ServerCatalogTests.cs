using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using NivalisTogether;
using NivalisTogether.Dedicated;

internal static class ServerCatalogTests
{
    private static int assertions;
    private static readonly string Instance = "11111111111111111111111111111111";
    private static readonly string OtherInstance = "22222222222222222222222222222222";
    private static readonly string World = "33333333333333333333333333333333";
    private static string testRoot;
    private static void Check(bool condition, string name)
    { assertions++; if (!condition) throw new Exception("FAIL: " + name); }
    private static ServerCatalog Catalog(string name, params ServerBookmark[] bookmarks)
    {
        string path = Path.Combine(testRoot, name + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(bookmarks));
        return new ServerCatalog(path);
    }
    private static ServerBookmark Bookmark(string host, string name = "My server") => new ServerBookmark { Host = host, Name = name, Port = 47778 };
    private static string Key(string host) => (string)typeof(ServerCatalog).GetMethod("Key", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { host, 47778 });
    private static ServerStatus Status(string instance = null) => new ServerStatus
    {
        ServerInstanceId = instance ?? Instance, Name = "Same server name", WorldId = World,
        Port = 47778, MaxPlayers = 4, Players = 1, Ready = true, State = "Unity simulation ready"
    };
    private static void Confirm(ServerCatalog catalog, string host, string remote, string instance = null, int generation = 0)
    {
        typeof(ServerCatalog).GetMethod("RecordStatus", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(catalog, new object[] { Key(host), generation, Status(instance), new IPEndPoint(IPAddress.Parse(remote), 47778), 15 });
    }
    private static MultiplayerServerEntry[] Online(ServerCatalog catalog) => catalog.Entries().Where(e => e.Online).ToArray();
    public static int Main(string[] args)
    {
        testRoot = args[0]; Directory.CreateDirectory(testRoot);
        using (var c = Catalog("aliases", Bookmark("localhost", "Saved DNS"), Bookmark("192.168.1.50", "Saved LAN")))
        {
            Confirm(c, "127.0.0.1", "127.0.0.1"); Confirm(c, "localhost", "127.0.0.1"); Confirm(c, "192.168.1.50", "192.168.1.50");
            var rows = Online(c);
            Check(rows.Length == 1, "loopback, LAN and DNS collapse by TCP instance identity");
            Check(rows[0].AliasIds.Length == 3, "display projection retains all aliases");
            Check(rows[0].Saved, "reachable saved alias is representative");
            Check(rows[0].Host != "127.0.0.1", "unsaved automatic alias does not replace bookmark address");
            Check(rows[0].Capacity == 4 && rows[0].Players == 1, "projection preserves status");
            rows[0].AliasIds[0] = "corrupt";
            Check(!Online(c)[0].AliasIds.Contains("corrupt"), "returned alias arrays are detached");
            c.Remove(Key("192.168.1.50"));
            var disk = JsonSerializer.Deserialize<ServerBookmark[]>(File.ReadAllText(Path.Combine(testRoot, "aliases.json")));
            Check(disk.Length == 1 && disk[0].Host == "localhost", "removing one alias preserves other saved hostname");
            Check(disk[0].Name == "Saved DNS", "probe name never overwrites persisted user bookmark name");
            Check(Online(c).Length == 1 && Online(c)[0].AliasIds.Length == 2, "other aliases remain after one removal");
            Confirm(c, "192.168.1.50", "192.168.1.50");
            Check(Online(c)[0].AliasIds.Length == 2, "late probe does not resurrect removed endpoint");
        }
        using (var c = Catalog("distinct", Bookmark("192.168.1.50")))
        {
            Confirm(c, "127.0.0.1", "127.0.0.1"); Confirm(c, "192.168.1.50", "192.168.1.50", OtherInstance);
            Check(Online(c).Length == 2, "same name and world with distinct service instances stays separate");
            Confirm(c, "192.168.1.50", "127.0.0.1", OtherInstance);
            Check(Online(c).Length == 2, "distinct current instance IDs override even matching remote endpoint");
        }
        using (var c = Catalog("legacy", Bookmark("localhost"), Bookmark("192.168.1.50")))
        {
            Confirm(c, "127.0.0.1", "127.0.0.1", ""); Confirm(c, "localhost", "127.0.0.1", ""); Confirm(c, "192.168.1.50", "192.168.1.50", "");
            Check(Online(c).Length == 2, "legacy fallback combines only exact TCP remote endpoints");
            Check(Online(c).Single(r => r.Host == "localhost").AliasIds.Length == 2, "legacy DNS/IP alias combined");
        }
        using (var c = Catalog("unverified", Bookmark("localhost")))
        {
            Confirm(c, "127.0.0.1", "127.0.0.1");
            Check(c.Entries().Count == 2, "unverified bookmark is not merged by matching name");
            Check(c.Entries()[0].Online && c.Entries()[0].Host == "127.0.0.1", "unreachable saved alias does not hide working endpoint");
            Confirm(c, "localhost", "127.0.0.1");
            Check(c.Entries().Count == 1 && c.Entries()[0].Host == "localhost", "verified saved alias becomes stable representative");
            Confirm(c, "localhost", "127.0.0.1", OtherInstance);
            Check(c.Entries().Count == 2, "new instance replaces prior proof instead of retaining stale group");
            typeof(ServerCatalog).GetField("generation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(c, 1);
            Confirm(c, "localhost", "127.0.0.1", Instance, 0);
            Check(c.Entries().Count == 2, "cancelled generation cannot overwrite newer identity");
            c.Dispose(); Confirm(c, "localhost", "127.0.0.1", Instance, 1);
            Check(c.Entries().Count == 2, "disposed catalog rejects completion");
        }
        using (var c = Catalog("raw-advertisements"))
        {
            var raw = new MultiplayerServerEntry { Host = "192.168.1.50", Name = "Same server name", Online = true, AliasIds = new[] { Key("127.0.0.1") } };
            typeof(ServerCatalog).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(c, new object[] { raw });
            Confirm(c, "127.0.0.1", "127.0.0.1");
            Check(c.Entries().Count == 2, "raw endpoint/advertisement DTO cannot establish shared identity");
            Check(c.Entries().Single(e => e.Host == raw.Host).AliasIds.Length == 1, "untrusted DTO alias claims discarded");
        }
        Check(Key("::ffff:127.0.0.1") == Key("127.0.0.1"), "IPv4 mapped form canonicalized");
        Check(Key("2001:0DB8::1") == Key("2001:db8:0:0:0:0:0:1"), "IPv6 forms canonicalized");
        Check(Key("LOCALHOST") == Key("localhost"), "hostname case normalized");
        var validation = typeof(ServerCatalog).GetMethod("ValidStatus", BindingFlags.Static | BindingFlags.NonPublic);
        var malformed = Status("not-a-guid");
        Check(!(bool)validation.Invoke(null, new object[] { malformed }), "malformed nonempty identity rejected");
        Check((bool)validation.Invoke(null, new object[] { Status("") }), "older missing identity remains queryable");
        Console.WriteLine("PASS: " + assertions + " catalog identity/bookmark/generation assertions; production catalog and DTO, no game or service launched.");
        return 0;
    }
}
