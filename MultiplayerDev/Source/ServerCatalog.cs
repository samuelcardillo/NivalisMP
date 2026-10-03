using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using NivalisTogether.Dedicated;

namespace NivalisTogether
{
    // Only endpoint bookmarks are persisted. Passwords never enter this file.
    public sealed class ServerBookmark
    {
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; } = 47778;
    }

    public sealed class ServerCatalog : IDisposable
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, MultiplayerServerEntry> entries = new Dictionary<string, MultiplayerServerEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ServerBookmark> bookmarks = new Dictionary<string, ServerBookmark>(StringComparer.OrdinalIgnoreCase);
        // Only TCP status responses populate this map. UDP advertisements and UI
        // DTOs never establish identity, and bookmarks retain every original alias.
        private readonly Dictionary<string, string> confirmedIdentity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly string path;
        private int generation;
        private bool disposed;
        private CancellationTokenSource refreshCancellation;
        private readonly HashSet<string> discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim probeSlots = new SemaphoreSlim(8, 8);
        public ServerCatalog(string path)
        {
            this.path = path;
            Add(new MultiplayerServerEntry { Host = "127.0.0.1", Port = 47778, Name = "This PC", Description = "Dedicated server on this computer" });
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.Length > 128 * 1024) throw new InvalidDataException("Server bookmark file exceeds size limit.");
                foreach (var item in JsonSerializer.Deserialize<List<ServerBookmark>>(File.ReadAllText(path)) ?? new List<ServerBookmark>())
                    if (entries.Count < 100 && item != null && Valid(item.Host, item.Port) && DedicatedProtocol.Text(item.Name, 80, true))
                    {
                        Add(new MultiplayerServerEntry { Host = item.Host, Port = item.Port, Name = item.Name.Length == 0 ? item.Host : item.Name, Saved = true });
                        bookmarks[Key(item.Host, item.Port)] = item;
                    }
            }
        }
        private static bool Valid(string host, int port) => DedicatedProtocol.Text(host, 253) && port > 0 && port <= 65535 && !host.Any(char.IsWhiteSpace);
        private static string Key(string host, int port)
        {
            string name = host.Trim();
            if (IPAddress.TryParse(name, out var ip))
            { if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4(); name = ip.ToString(); }
            return name.ToLowerInvariant() + ":" + port;
        }
        private void Add(MultiplayerServerEntry entry)
        { entry.Id = Key(entry.Host, entry.Port); entries[entry.Id] = entry; }
        public IReadOnlyList<MultiplayerServerEntry> Entries()
        {
            lock (gate)
            {
                return entries.Values.GroupBy(e => e.Online && confirmedIdentity.TryGetValue(e.Id, out var identity)
                        ? identity : "bookmark:" + e.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                    {
                        // Prefer a saved, reachable address. Do not replace a saved
                        // hostname with a LAN address merely because discovery won a race.
                        var row = Clone(group.OrderByDescending(e => e.Saved).ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase).First());
                        row.AliasIds = group.Select(e => e.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
                        return row;
                    }).OrderByDescending(e => e.Online).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
        private static MultiplayerServerEntry Clone(MultiplayerServerEntry e) => new MultiplayerServerEntry
        {
            Id = e.Id, Name = e.Name, Host = e.Host, Port = e.Port, Players = e.Players, Capacity = e.Capacity,
            PingMilliseconds = e.PingMilliseconds, Online = e.Online, Saved = e.Saved, PasswordRequired = e.PasswordRequired, Description = e.Description,
            AliasIds = e.AliasIds == null ? Array.Empty<string>() : (string[])e.AliasIds.Clone()
        };
        public void Save(MultiplayerServerEntry entry)
        {
            if (!Valid(entry.Host, entry.Port)) throw new ArgumentException("Enter a valid server address and port.");
            lock (gate)
            {
                string key = Key(entry.Host, entry.Port);
                if (!entries.ContainsKey(key) && entries.Count >= 100) throw new InvalidOperationException("The server list is limited to 100 entries.");
                var saved = Clone(entry); saved.Host = saved.Host.Trim(); saved.Saved = true;
                saved.Online = false; saved.AliasIds = Array.Empty<string>();
                confirmedIdentity.Remove(key); Add(saved);
                bookmarks[key] = new ServerBookmark { Name = saved.Name, Host = saved.Host, Port = saved.Port };
                Persist();
            }
            Refresh();
        }
        public void Remove(string id)
        { lock (gate) { entries.Remove(id); bookmarks.Remove(id); confirmedIdentity.Remove(id); Persist(); } }
        private void Persist()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(bookmarks.Values.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
        }
        public void Refresh()
        {
            CancellationTokenSource previous, next;
            CancellationToken cancellation;
            int request;
            MultiplayerServerEntry[] endpoints;
            lock (gate)
            {
                if (disposed) return;
                request = Interlocked.Increment(ref generation);
                previous = refreshCancellation; next = refreshCancellation = new CancellationTokenSource();
                cancellation = next.Token;
                foreach (string key in discovered)
                    if (entries.TryGetValue(key, out var entry) && !entry.Saved && key != Key("127.0.0.1", 47778)) entries.Remove(key);
                discovered.Clear();
                confirmedIdentity.Clear();
                foreach (var entry in entries.Values) { entry.Online = false; entry.PingMilliseconds = -1; }
                // Probe every alias, not the deduplicated display projection.
                endpoints = entries.Values.Select(Clone).ToArray();
            }
            previous?.Cancel(); previous?.Dispose();
            foreach (var entry in endpoints) _ = Probe(entry, request, cancellation);
            _ = Discover(request, cancellation);
        }
        private async Task Probe(MultiplayerServerEntry entry, int request, CancellationToken cancellation)
        {
            string status = "Offline";
            bool entered = false;
            try
            {
                await probeSlots.WaitAsync(cancellation).ConfigureAwait(false); entered = true;
                cancellation.ThrowIfCancellationRequested();
                using var connection = await DedicatedConnection.ConnectAsync(entry.Host, entry.Port, 3, cancellation).ConfigureAwait(false);
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed.TotalSeconds < 4 && connection.Connected && !disposed && request == generation && !cancellation.IsCancellationRequested)
                {
                    connection.Tick();
                    while (connection.TryReceive(out var message))
                    {
                        if (message.Type == "serverHello") connection.Send("statusQuery", new { });
                        if (message.Type == "serverStatus")
                        {
                            var result = message.Read<ServerStatus>();
                            if (!ValidStatus(result)) throw new InvalidDataException("Invalid server status response.");
                            RecordStatus(entry.Id, request, result, connection.RemoteEndpoint, (int)clock.ElapsedMilliseconds);
                            return;
                        }
                    }
                    await Task.Delay(20, cancellation).ConfigureAwait(false);
                }
                status = connection.Connected ? "Server status query timed out" : connection.Status;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { status = "Unavailable: " + ex.GetBaseException().Message; }
            finally { if (entered) probeSlots.Release(); }
            lock (gate) if (!disposed && request == generation && entries.TryGetValue(entry.Id, out var current))
            { confirmedIdentity.Remove(entry.Id); current.Online = false; current.PingMilliseconds = -1; current.Description = status; }
        }
        private void RecordStatus(string id, int request, ServerStatus result, IPEndPoint remote, int ping)
        {
            if (!ValidStatus(result)) throw new InvalidDataException("Invalid server status response.");
            lock (gate)
            {
                if (disposed || request != generation || !entries.TryGetValue(id, out var current)) return;
                confirmedIdentity[id] = Guid.TryParseExact(result.ServerInstanceId, "N", out var instance)
                    ? "instance:" + instance.ToString("N") : "endpoint:" + Key(remote.Address.ToString(), remote.Port);
                current.Online = true; current.Name = result.Name; current.Players = result.Players; current.Capacity = result.MaxPlayers;
                current.PasswordRequired = result.PasswordRequired; current.PingMilliseconds = ping;
                current.Description = result.BuildId != DedicatedProtocol.BuildId || result.Version != DedicatedProtocol.Version
                    ? "Incompatible game or protocol version" : result.State;
            }
        }
        private static bool ValidStatus(ServerStatus status) => status != null && status.Version > 0 && status.Version < 1000 &&
            DedicatedProtocol.Text(status.Name, 80) && DedicatedProtocol.Text(status.State, 200, true) &&
            status.BuildId != null && status.BuildId.Length == 64 && status.BuildId.All(Uri.IsHexDigit) &&
            Guid.TryParseExact(status.WorldId, "N", out _) &&
            (string.IsNullOrEmpty(status.ServerInstanceId) || Guid.TryParseExact(status.ServerInstanceId, "N", out _)) && status.Port > 0 && status.Port <= 65534 &&
            status.MaxPlayers >= 1 && status.MaxPlayers <= 32 && status.Players >= 0 && status.Players <= status.MaxPlayers;

        private async Task Discover(int request, CancellationToken cancellation)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.EnableBroadcast = true; socket.Bind(new IPEndPoint(IPAddress.Any, 0)); socket.Blocking = false;
                byte[] query = Encoding.ASCII.GetBytes("NIVALIS_DISCOVER_V1");
                foreach (IPAddress address in new[] { IPAddress.Broadcast, IPAddress.Loopback })
                {
                    try { socket.SendTo(query, new IPEndPoint(address, 47779)); }
                    catch (SocketException) { /* Loopback can still succeed when an interface rejects broadcasts. */ }
                }
                var clock = Stopwatch.StartNew(); var responses = new HashSet<string>(); byte[] bytes = new byte[2049];
                while (clock.Elapsed.TotalSeconds < 1.5 && !cancellation.IsCancellationRequested && !disposed && request == generation && responses.Count < 64)
                {
                    for (int packet = 0; packet < 16; packet++)
                    {
                        EndPoint endpoint = new IPEndPoint(IPAddress.Any, 0); int length;
                        try { length = socket.ReceiveFrom(bytes, ref endpoint); }
                        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock) { break; }
                        catch (SocketException) { continue; }
                        var source = (IPEndPoint)endpoint;
                        if (length < 2 || length > 2048 || source.Port != 47779 || !LocalAddress(source.Address)) continue;
                        ServerStatus result;
                        try { result = JsonSerializer.Deserialize<ServerStatus>(new ReadOnlySpan<byte>(bytes, 0, length), DedicatedProtocol.Json); }
                        catch (JsonException) { continue; }
                        if (!ValidStatus(result) || result.Port + 1 != source.Port) continue;
                        string host = source.Address.ToString(), key = Key(host, result.Port);
                        if (!responses.Add(key)) continue;
                        MultiplayerServerEntry entry;
                        bool added = false;
                        lock (gate)
                        {
                            if (disposed || request != generation || cancellation.IsCancellationRequested) return;
                            if (!entries.TryGetValue(key, out entry))
                            {
                                if (entries.Count >= 100) continue;
                                entry = new MultiplayerServerEntry { Host = host, Port = result.Port, Name = result.Name,
                                    Description = "Discovered on LAN; checking server...", PingMilliseconds = -1 };
                                Add(entry); discovered.Add(key); added = true;
                            }
                            entry = Clone(entry);
                        }
                        // Confirm each advertised endpoint with the actual TCP protocol before reporting it online.
                        if (added) _ = Probe(entry, request, cancellation);
                    }
                    await Task.Delay(20, cancellation).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { /* Manual and saved TCP endpoints remain usable without LAN discovery. */ }
            catch (ObjectDisposedException) { }
        }
        private static bool LocalAddress(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = address.GetAddressBytes();
            return b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) ||
                (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 169 && b[1] == 254);
        }
        public void Dispose()
        {
            CancellationTokenSource cancellation;
            lock (gate) { disposed = true; Interlocked.Increment(ref generation); cancellation = refreshCancellation; refreshCancellation = null; }
            cancellation?.Cancel(); cancellation?.Dispose();
        }
    }
}
