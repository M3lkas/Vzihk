using Vzihk.Common;
using Vzihk.Common.Reliability;
using Vzihk.Common.Telemetry;

var tests = new (string Name, Action Run)[]
{
    ("retry exactly at RTO, then at most once per interval", () => {
        var c = new ReliableChannel(3); c.OnSent(1, [4, 0, 1], 0);
        Equal(0, c.CollectForRetransmission(199_999, 200_000).Count);
        var retry = c.CollectForRetransmission(200_000, 200_000).Single(); Equal(2, retry.Attempts);
        Equal(0, c.CollectForRetransmission(200_000, 200_000).Count);
        Equal(0, c.CollectForRetransmission(399_999, 200_000).Count);
        Equal(3, c.CollectForRetransmission(400_000, 200_000).Single().Attempts);
    }),
    ("maxAttempts includes original, final ACK still has a full RTO", () => {
        var c = new ReliableChannel(2); c.OnSent(7, [], 0);
        Equal(1, c.CollectForRetransmission(300_000, 200_000).Count);
        Equal(0, c.FailedCount); c.CollectForRetransmission(499_999, 200_000); Equal(0, c.FailedCount);
        Equal(0, c.CollectForRetransmission(500_000, 200_000).Count);
        Equal(1, c.FailedCount); Equal(0, c.PendingCount); Equal(2, c.Failed.Single().Attempts);
        True(!c.OnAckReceived(7, 600_000, out _)); c.CollectForRetransmission(900_000, 200_000); Equal(1, c.FailedCount);
    }),
    ("duplicate/unknown ACK ignored", () => {
        var c = new ReliableChannel(); c.OnSent(3, [], 0);
        True(!c.OnAckReceived(4, 10, out _)); Equal(1, c.PendingCount);
        True(c.OnAckReceived(3, 100, out _)); True(!c.OnAckReceived(3, 200, out _)); Equal(0, c.PendingCount);
    }),
    ("Karn excludes ACK after retry and keeps total delivery time", () => {
        var c = new ReliableChannel(); c.OnSent(1, [], 0);
        c.CollectForRetransmission(200_000, 200_000); c.OnAckReceived(1, 250_000, out var ack);
        True(ack!.RttSampleMs is null); Near(250, ack.TimeToAckMs);
        c.OnSent(2, [], 300_000); c.OnAckReceived(2, 350_000, out ack); Near(50, ack!.RttSampleMs!.Value);
    }),
    ("multiple pending packets independent; bytes copied", () => {
        var c = new ReliableChannel(); byte[] data = [1, 2]; c.OnSent(1, data, 0); data[0] = 9;
        c.OnSent(2, [], 50); Throws<InvalidOperationException>(() => c.OnSent(1, [], 60));
        c.OnAckReceived(2, 90, out _);
        var retry = c.CollectForRetransmission(100, 100).Single(); Equal((byte)1, retry.RawBytes.Span[0]); Equal(1, c.PendingCount);
    }),
    ("one allowed attempt never retransmits", () => {
        var c = new ReliableChannel(1); c.OnSent(9, [], 0);
        Equal(0, c.CollectForRetransmission(100, 100).Count); Equal(1, c.FailedCount);
    }),
    ("manager validates time and configuration", () => {
        Throws<ArgumentOutOfRangeException>(() => new ReliableChannel(0));
        var c = new ReliableChannel(); c.OnSent(1, [], 100);
        Throws<ArgumentOutOfRangeException>(() => c.CollectForRetransmission(101, 0));
        Throws<ArgumentOutOfRangeException>(() => c.CollectForRetransmission(99, 100));
        Throws<ArgumentOutOfRangeException>(() => c.OnAckReceived(1, 99, out _)); Equal(1, c.PendingCount);
    }),
    ("Jacobson first and second samples use previous SRTT", () => {
        var r = new AdaptiveTimeout(); Near(1000, r.RtoMs);
        r.OnSample(100); Near(100, r.SrttMs); Near(50, r.RttVarMs); Near(300, r.RtoMs);
        r.OnSample(140); Near(105, r.SrttMs); Near(47.5, r.RttVarMs); Near(295, r.RtoMs);
    }),
    ("RTO bounded, invalid RTT rejected", () => {
        var r = new AdaptiveTimeout(); r.OnSample(1); Near(100, r.RtoMs);
        r.OnSample(10000); Near(3000, r.RtoMs);
        Throws<ArgumentOutOfRangeException>(() => r.OnSample(double.NaN));
        Throws<ArgumentOutOfRangeException>(() => r.OnSample(double.PositiveInfinity));
        Throws<ArgumentOutOfRangeException>(() => r.OnSample(-1));
    }),
    ("wire format has exact big-endian header and ACK payload", () => {
        var data = PacketHeader.Build(CommandType.Ack, 0x1234, new AckPayload(0xabcd).ToBytes());
        True(data.SequenceEqual(new byte[] { 5, 0x12, 0x34, 0, 2, 0, 3, 0, 0xab, 0xcd }));
        var p = PacketHeader.Parse(data); Equal((ushort)0x1234, p.Sequence);
        Equal((ushort)0xabcd, AckPayload.FromBytes(p.Payload).AcknowledgedSequence);
        var shoot = PacketHeader.Parse(PacketHeader.Build(CommandType.Shoot, 1, new ShootPayload(90).ToBytes(), true));
        True(shoot.RequiresAck); Near(90, ShootPayload.FromBytes(shoot.Payload).AngleDegrees);
        True(new MovementPayload(1, -2).ToBytes().SequenceEqual(new byte[] { 0x3f, 0x80, 0, 0, 0xc0, 0, 0, 0 }));
    }),
    ("malformed length, version, type, flags rejected", () => {
        var valid = PacketHeader.Build(CommandType.Ack, 1, new AckPayload(2).ToBytes());
        Throws<FormatException>(() => PacketHeader.Parse(valid[..7]));
        Throws<FormatException>(() => PacketHeader.Parse(valid[..9]));
        Throws<FormatException>(() => PacketHeader.Parse([.. valid, 0]));
        foreach (var (offset, value) in new[] { (0, 42), (6, 99), (7, 2), (7, 1), (4, 3) }) {
            var data = valid.ToArray(); data[offset] = (byte)value; Throws<FormatException>(() => PacketHeader.Parse(data));
        }
        Throws<FormatException>(() => PacketHeader.Build(CommandType.Ping, 1, [], true));
        Throws<FormatException>(() => PacketHeader.Build(CommandType.Shoot, 1, new ShootPayload(0).ToBytes()));
    }),
    ("all payload readers enforce exact length and finite values", () => {
        Throws<FormatException>(() => MovementPayload.FromBytes(new byte[7]));
        Throws<FormatException>(() => ShootPayload.FromBytes(new byte[5]));
        Throws<FormatException>(() => StateUpdatePayload.FromBytes(new byte[1]));
        Throws<FormatException>(() => ShootResultPayload.FromBytes([2, 0, 1]));
        Throws<FormatException>(() => AckPayload.FromBytes([0]));
        var bad = PacketHeader.Build(CommandType.Shoot, 1, new byte[] { 0x7f, 0x80, 0, 0 }, true);
        Throws<FormatException>(() => PacketHeader.Parse(bad));
    }),
    ("server applies a retransmitted shot exactly once", () => {
        var g = new GameCommandProcessor(); var packet = Shot(10);
        True(g.Process(packet) is not null); Equal(1, g.Player.ShotsFired); Equal((ushort)9, g.Player.Ammo);
        True(g.Process(packet) is null); Equal(1, g.Player.ShotsFired); Equal((ushort)9, g.Player.Ammo); Equal(1, g.DuplicateShots);
        g.Process(Shot(11)); Equal(2, g.Player.ShotsFired);
    }),
    ("deduplication window permits reordering, rejects stale packets", () => {
        var w = new SequenceWindow(4); True(w.TryAccept(10)); True(w.TryAccept(12)); True(w.TryAccept(11));
        True(!w.TryAccept(11)); True(w.TryAccept(15)); True(!w.TryAccept(10)); True(w.Count <= 4);
    }),
    ("serial window handles wrap and stays bounded", () => {
        var w = new SequenceWindow(4); True(w.TryAccept(65534)); True(w.TryAccept(0)); True(w.TryAccept(65535));
        True(!w.TryAccept(65534)); True(w.TryAccept(3)); True(!w.TryAccept(65535)); True(w.Count <= 4);
        for (int i = 4; i < 10000; i++) w.TryAccept((ushort)i); True(w.Count <= 4);
    }),
    ("PING/PONG shares estimator; duplicate/late/unknown do not change it", () => {
        var r = new AdaptiveTimeout(); var t = new TelemetryTracker(r);
        t.OnPingSent(1, 0); Equal(PongStatus.Accepted, t.OnPong(1, 100_000)); Near(100, r.SrttMs);
        Equal(PongStatus.Duplicate, t.OnPong(1, 110_000)); Equal(PongStatus.Unknown, t.OnPong(99, 120_000));
        t.OnPingSent(2, 200_000); t.Expire(1_200_000); Equal(PongStatus.Late, t.OnPong(2, 1_300_000)); Near(100, r.SrttMs);
        t.OnPingSent(3, 1_400_000); Equal(PongStatus.Accepted, t.OnPong(3, 1_600_000)); Near(6.25, t.JitterMs);
        t.OnPingSent(4, 2_000_000); Equal(PongStatus.Late, t.OnPong(4, 3_000_000));
        Equal(2, t.AcceptedCount); Equal(2, t.LateCount); Equal(1, t.DuplicateCount); Equal(1, t.UnknownCount);
    }),
};
int failed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;

static Packet Shot(ushort sequence) => PacketHeader.Parse(PacketHeader.Build(CommandType.Shoot, sequence, new ShootPayload(45).ToBytes(), true));
static void True(bool value) { if (!value) throw new Exception("Expected true"); }
static void Equal<T>(T expected, T actual) where T : notnull { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
static void Near(double expected, double actual) { if (Math.Abs(expected - actual) > 0.000001) throw new Exception($"Expected {expected}, got {actual}"); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}");
}
