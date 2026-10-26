using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using Vzihk.Common;
using Vzihk.Common.Reliability;
using Vzihk.Common.Telemetry;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var options = Options.Parse(args);
using var socket = new UdpClient();
socket.Connect(options.Host, options.Port);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var clock = Stopwatch.StartNew();
long NowUs() => (long)(clock.Elapsed.TotalMilliseconds * 1000);
var timeout = new AdaptiveTimeout();
var telemetry = new TelemetryTracker(timeout);
var channel = new ReliableChannel(options.MaxAttempts);
Directory.CreateDirectory(options.Output);
using var samples = new StreamWriter(Path.Combine(options.Output, "reliability_samples.csv"));
using var events = new StreamWriter(Path.Combine(options.Output, "reliability_events.csv"));
using var rto = new StreamWriter(Path.Combine(options.Output, "rto_samples.csv"));
samples.WriteLine("series,sequence,first_sent_ms,completed_ms,attempts,outcome,time_to_ack_ms,srtt_ms,rttvar_ms,rto_ms");
events.WriteLine("series,time_ms,sequence,event,attempts,rto_ms");
rto.WriteLine("series,time_ms,source,rtt_ms,srtt_ms,rttvar_ms,rto_ms,jitter_ms");
int nextSequence = 0, sent = 0, acked = 0, reportedFailures = 0;
long nextCommandUs = 0, nextPingUs = 0;
var receive = socket.ReceiveAsync(stop.Token).AsTask();
Console.WriteLine($"Vzihk v3 -> {options.Host}:{options.Port}, SHOOT count={options.Count}, maxAttempts={options.MaxAttempts}");
try
{
    while (!stop.IsCancellationRequested && (sent < options.Count || channel.PendingCount > 0))
    {
        // Receive independently of the send cadence: a lost reply cannot block the game loop.
        while (receive.IsCompleted)
        {
            var data = await receive;
            receive = socket.ReceiveAsync(stop.Token).AsTask();
            var now = NowUs();
            try
            {
                var packet = PacketHeader.Parse(data.Buffer);
                switch (packet.Type)
                {
                    case CommandType.Ack:
                        var sequence = AckPayload.FromBytes(packet.Payload).AcknowledgedSequence;
                        if (channel.OnAckReceived(sequence, now, out var receipt) && receipt is not null)
                        {
                            acked++;
                            if (receipt.RttSampleMs is double sample) { timeout.OnSample(sample); RtoSample(now, "ack", sample); }
                            samples.WriteLine($"{options.Series},{sequence},{receipt.FirstSentAtUs / 1000.0:F3},{now / 1000.0:F3},{receipt.Attempts},acked,{receipt.TimeToAckMs:F3},{timeout.SrttMs:F3},{timeout.RttVarMs:F3},{timeout.RtoMs:F3}");
                            Event(now, sequence, "acked", receipt.Attempts);
                        }
                        else Event(now, sequence, "duplicate_or_unknown_ack", 0);
                        break;
                    case CommandType.Pong:
                        if (telemetry.OnPong(packet.Sequence, now) == PongStatus.Accepted)
                            RtoSample(now, "pong", telemetry.LastRttMs);
                        break;
                    case CommandType.ShootResult:
                        var result = ShootResultPayload.FromBytes(packet.Payload);
                        Console.WriteLine($"SHOOT_RESULT seq={packet.Sequence} hit={result.Hit} ammo={result.AmmoLeft}");
                        break;
                }
            }
            catch (FormatException ex) { Console.WriteLine($"INVALID: {ex.Message}"); }
        }
        var tick = NowUs();
        telemetry.Expire(tick);
        if (tick >= nextPingUs && sent < options.Count)
        {
            var sequence = NextSequence();
            telemetry.OnPingSent(sequence, tick);
            await Send(PacketHeader.Build(CommandType.Ping, sequence, []));
            nextPingUs = tick + 250_000;
        }
        if (tick >= nextCommandUs && sent < options.Count)
        {
            // Preserve PR1 movement demonstration, but do not queue obsolete positions for retries.
            var movement = NextSequence();
            await Send(PacketHeader.Build(CommandType.Movement, movement, new MovementPayload(sent, sent / 2f).ToBytes()));
            var sequence = NextSequence();
            var bytes = PacketHeader.Build(CommandType.Shoot, sequence, new ShootPayload(sent % 360).ToBytes(), true);
            channel.OnSent(sequence, bytes, tick);
            await Send(bytes); Event(tick, sequence, "sent", 1); sent++;
            nextCommandUs = tick + options.IntervalMs * 1000L;
        }
        foreach (var pending in channel.CollectForRetransmission(tick, timeout.RtoUs))
        {
            await Send(pending.RawBytes); Event(tick, pending.Sequence, "retry", pending.Attempts);
        }
        while (reportedFailures < channel.FailedCount)
        {
            var failure = channel.Failed[reportedFailures++];
            Console.WriteLine($"FAILED seq={failure.Sequence}: выстрел не подтверждён сервером после {failure.Attempts} попыток");
            samples.WriteLine($"{options.Series},{failure.Sequence},{failure.FirstSentAtUs / 1000.0:F3},{failure.FailedAtUs / 1000.0:F3},{failure.Attempts},failed,,{timeout.SrttMs:F3},{timeout.RttVarMs:F3},{timeout.RtoMs:F3}");
            Event(tick, failure.Sequence, "failed", failure.Attempts);
        }
        await Task.Delay(5, stop.Token);
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
catch (SocketException ex) { Console.Error.WriteLine($"UDP: {ex.Message}"); Environment.ExitCode = 2; }
finally
{
    stop.Cancel();
    try { await receive; } catch (Exception ex) when (ex is OperationCanceledException or SocketException) { }
}
if (channel.PendingCount > 0 || sent != options.Count)
{
    Console.Error.WriteLine($"Серия прервана: sent={sent}, pending={channel.PendingCount}");
    Environment.ExitCode = 2;
}
Console.WriteLine($"SUMMARY sent={sent} acked={acked} failed={channel.FailedCount} final_rto_ms={timeout.RtoMs:F3}");

ushort NextSequence()
{
    // Without a session ID, reusing a 16-bit number could accept an ancient ACK.
    if (nextSequence > ushort.MaxValue) throw new InvalidOperationException("Лимит номеров сессии: перезапустите клиент с новым UDP-портом");
    return (ushort)nextSequence++;
}
async Task Send(ReadOnlyMemory<byte> data) => await socket.SendAsync(data, stop.Token);
void Event(long now, ushort sequence, string kind, int attempts) =>
    events.WriteLine($"{options.Series},{now / 1000.0:F3},{sequence},{kind},{attempts},{timeout.RtoMs:F3}");
void RtoSample(long now, string source, double sample) =>
    rto.WriteLine($"{options.Series},{now / 1000.0:F3},{source},{sample:F3},{timeout.SrttMs:F3},{timeout.RttVarMs:F3},{timeout.RtoMs:F3},{telemetry.JitterMs:F3}");

sealed record Options(string Host, int Port, int Count, int IntervalMs, int MaxAttempts, string Output, string Series)
{
    public static Options Parse(string[] args)
    {
        var host = "127.0.0.1"; var port = 11000; var count = 60; var interval = 50; var attempts = 5;
        var output = "artifacts/client"; var series = "manual";
        int index = 0;
        if (index < args.Length && !args[index].StartsWith("--")) host = args[index++];
        if (index < args.Length && !args[index].StartsWith("--")) port = int.Parse(args[index++]);
        while (index < args.Length)
        {
            var key = args[index++];
            if (index == args.Length) throw new ArgumentException($"Нет значения {key}");
            var value = args[index++];
            switch (key)
            {
                case "--count": count = int.Parse(value); break;
                case "--interval-ms": interval = int.Parse(value); break;
                case "--max-attempts": attempts = int.Parse(value); break;
                case "--output": output = value; break;
                case "--series": series = value; break;
                default: throw new ArgumentException($"Неизвестный параметр {key}");
            }
        }
        if (count < 1 || count > 30000 || interval < 1 || attempts < 1 || port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(args));
        if (series.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw new ArgumentException("Некорректное имя серии");
        return new(host, port, count, interval, attempts, output, series);
    }
}
