#!/usr/bin/env python3
"""Real loopback UDP through a seeded loss/delay proxy. Standard library only."""
import argparse
import asyncio
import csv
import json
import random
import socket
import statistics
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SERIES = [
    ("baseline", 0.0, 0, 0),
    ("loss_5", 0.05, 0, 0),
    ("loss_10", 0.10, 0, 0),
    ("loss_20", 0.20, 0, 0),
    ("delay_100_loss_5", 0.05, 100, 0),
    ("jitter_loss_10", 0.10, 50, 50),
]


def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class Proxy(asyncio.DatagramProtocol):
    def __init__(self, server, loss, delay, jitter, seed):
        self.server = server
        self.loss, self.delay, self.jitter = loss, delay, jitter
        self.rng = random.Random(seed)
        self.client = None
        self.seen = self.dropped = 0
        self.handles = []

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, addr):
        if addr == self.server:
            target = self.client
        else:
            self.client = addr
            target = self.server
        if target is None:
            return
        self.seen += 1
        if self.rng.random() < self.loss:
            self.dropped += 1
            return
        delay_ms = max(0, self.delay + self.rng.uniform(-self.jitter, self.jitter))
        loop = asyncio.get_running_loop()
        handle = loop.call_later(delay_ms / 1000, self.transport.sendto, data, target)
        self.handles.append(handle)

    def close(self):
        for handle in self.handles:
            handle.cancel()
        self.transport.close()


def merge_csv(paths, output):
    all_rows = []
    for path in paths:
        with path.open() as stream:
            reader = csv.DictReader(stream)
            fields = reader.fieldnames
            all_rows.extend(reader)
    with output.open("w", newline="") as stream:
        writer = csv.DictWriter(stream, fields)
        writer.writeheader()
        writer.writerows(all_rows)
    return all_rows


async def main(count):
    out = ROOT / "docs"
    out.mkdir(exist_ok=True)
    runs = ROOT / "artifacts" / "experiments"
    runs.mkdir(parents=True, exist_ok=True)
    summaries = []
    for index, (name, loss, delay, jitter) in enumerate(SERIES):
        run = runs / name
        run.mkdir(exist_ok=True)
        port = free_port()
        server_log = (run / "server.log").open("w")
        server = await asyncio.create_subprocess_exec(
            "dotnet", str(ROOT / "Server/bin/Debug/net10.0/Vzihk.Server.dll"), str(port),
            stdout=server_log, stderr=server_log)
        proxy = None
        try:
            # Readiness uses the protocol instead of a fixed sleep or guessing process startup time.
            loop = asyncio.get_running_loop()
            ready = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            ready.setblocking(False)
            ready.bind(("127.0.0.1", 0))
            try:
                for _ in range(100):
                    await loop.sock_sendto(ready, bytes([3, 0, 0, 0, 0, 0, 3, 0]), ("127.0.0.1", port))
                    try:
                        await asyncio.wait_for(loop.sock_recv(ready, 100), 0.1)
                        break
                    except asyncio.TimeoutError:
                        if server.returncode is not None:
                            raise RuntimeError("Server exited; inspect " + str(run / "server.log"))
                else:
                    raise RuntimeError("Server readiness timeout")
            finally:
                ready.close()
            seed = 7300 + index
            transport, proxy = await loop.create_datagram_endpoint(
                lambda: Proxy(("127.0.0.1", port), loss, delay, jitter, seed), local_addr=("127.0.0.1", 0))
            proxy_port = transport.get_extra_info("sockname")[1]
            with (run / "client.log").open("w") as log:
                client = await asyncio.create_subprocess_exec(
                    "dotnet", str(ROOT / "Client/bin/Debug/net10.0/Vzihk.Client.dll"), "127.0.0.1", str(proxy_port),
                    "--count", str(count), "--interval-ms", "50", "--series", name, "--output", str(run),
                    stdout=log, stderr=log)
                try:
                    code = await asyncio.wait_for(client.wait(), 90)
                except BaseException:
                    if client.returncode is None:
                        client.terminate()
                        await client.wait()
                    raise
                if code:
                    raise RuntimeError("Client failed; inspect " + str(run / "client.log"))
            with (run / "reliability_samples.csv").open() as f:
                rows = list(csv.DictReader(f))
            with (run / "rto_samples.csv").open() as f:
                rto_rows = list(csv.DictReader(f))
            assert len(rows) == count, (name, len(rows), count)
            assert len({r["sequence"] for r in rows}) == count
            acked = [r for r in rows if r["outcome"] == "acked"]
            first = sum(r["attempts"] == "1" and r["outcome"] == "acked" for r in rows)
            final_rto = float(rto_rows[-1]["rto_ms"]) if rto_rows else 1000
            summary = dict(series=name, commands=count, delivered=len(acked), first_attempt=first,
                retransmit_total=sum(int(r["attempts"]) - 1 for r in rows), failed=count - len(acked),
                avg_attempts=round(statistics.mean(int(r["attempts"]) for r in rows), 4),
                first_attempt_fraction=round(first / count, 4), failed_fraction=round((count - len(acked)) / count, 4),
                avg_time_to_ack_ms=round(statistics.mean(float(r["time_to_ack_ms"]) for r in acked), 3) if acked else "",
                avg_rto_ms=round(statistics.mean(float(r["rto_ms"]) for r in rto_rows), 3) if rto_rows else 1000,
                final_rto_ms=final_rto, configured_loss_pct=loss * 100, one_way_delay_ms=delay,
                one_way_jitter_ms=jitter, proxy_datagrams=proxy.seen, proxy_dropped=proxy.dropped, seed=seed)
            summaries.append(summary)
            print(json.dumps(summary), flush=True)
        finally:
            if proxy:
                proxy.close()
            if server.returncode is None:
                server.terminate()
                await server.wait()
            server_log.close()
    for filename in ("reliability_samples.csv", "reliability_events.csv", "rto_samples.csv"):
        merge_csv([runs / name / filename for name, *_ in SERIES], out / filename)
    with (out / "reliability_summary.csv").open("w", newline="") as f:
        writer = csv.DictWriter(f, summaries[0].keys())
        writer.writeheader()
        writer.writerows(summaries)
    (out / "experiment_metadata.json").write_text(json.dumps({
        "transport": "real UDP over IPv4 loopback through an asyncio user-space impairment proxy",
        "loss_model": "independent drop probability in EACH direction, applied to commands, ACK, responses and PING/PONG",
        "delay_model": "one-way fixed delay + uniform jitter; negative delays clamped to zero",
        "commands_per_series": count, "max_attempts": 5, "rto_bounds_ms": [100, 3000],
        "initial_rto_ms": 1000, "command_interval_ms": 50, "ping_interval_ms": 250,
        "note": "Seed fixes PRNG draws; OS scheduling and datagram order still affect timings and exact outcomes."
    }, indent=2) + "\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--count", type=int, default=60)
    args = parser.parse_args()
    if not 50 <= args.count <= 30000:
        parser.error("--count must be 50..30000")
    asyncio.run(main(args.count))
