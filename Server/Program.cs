using System.Net;
using System.Net.Sockets;
using Vzihk.Common;

var port = args.Length > 0 ? int.Parse(args[0]) : 11000;
using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var clients = new Dictionary<IPEndPoint, (GameCommandProcessor Game, DateTime LastSeen)>();
ushort sequence = 0;
Console.WriteLine($"Vzihk protocol v3: UDP 0.0.0.0:{port}; Ctrl+C для выхода");
try
{
    // One owner of each client's state avoids races between retransmitted commands.
    while (!stop.IsCancellationRequested)
    {
        var received = await socket.ReceiveAsync(stop.Token);
        Packet packet;
        try { packet = PacketHeader.Parse(received.Buffer); }
        catch (FormatException ex) { Console.WriteLine($"INVALID {received.RemoteEndPoint}: {ex.Message}"); continue; }
        if (packet.Type == CommandType.Ping)
        {
            await Send(PacketHeader.Build(CommandType.Pong, packet.Sequence, []));
            continue;
        }
        if (packet.Type is not (CommandType.Shoot or CommandType.Movement)) continue;
        if (!clients.ContainsKey(received.RemoteEndPoint))
        {
            // Bound memory; idle sessions expire only when admitting a new peer.
            foreach (var key in clients.Where(x => DateTime.UtcNow - x.Value.LastSeen > TimeSpan.FromMinutes(5)).Select(x => x.Key).ToArray())
                clients.Remove(key);
            if (clients.Count >= 1024) continue;
            clients.Add(received.RemoteEndPoint, (new GameCommandProcessor(), DateTime.UtcNow));
        }
        var game = clients[received.RemoteEndPoint].Game;
        clients[received.RemoteEndPoint] = (game, DateTime.UtcNow);
        if (packet.RequiresAck)
            await Send(PacketHeader.Build(CommandType.Ack, sequence++, new AckPayload(packet.Sequence).ToBytes()));
        var response = game.Process(packet);
        if (response is not null) await Send(response);
        Console.WriteLine($"{received.RemoteEndPoint} {packet.Type} seq={packet.Sequence} shots={game.Player.ShotsFired} duplicates={game.DuplicateShots}");

        async Task Send(byte[] data) => await socket.SendAsync(data, received.RemoteEndPoint, stop.Token);
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
