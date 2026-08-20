<div align="center">

# Method

**A VPN client that decides for itself which way to send your traffic.**

[![License](https://img.shields.io/badge/license-GPL--3.0--or--later-blue)](LICENSE)
[![Platforms](https://img.shields.io/badge/platforms-macOS%20·%20Android%20·%20Windows-lightgrey)](#status)
[![Status](https://img.shields.io/badge/status-early%20development-orange)](#status)

[Русский](README.md) · **English**

</div>

---

## What this is

A client for **your own** servers. No bundled subscriptions, no third-party
nodes: paste your links or a subscription URL and it works.

Supports **Hysteria2**, **VLESS + Reality** (Vision and gRPC), **Trojan**
and **Shadowsocks-2022**.

There are many such clients. Method differs in one way: it does not connect to
a server — it **maintains a state of the network**.

## Why not just another client

Every existing client picks a server by latency. That is wrong, and here is a
measurement from our own nodes — one round, 20 requests through each tunnel:

| Route | Median | p95 | Spread |
|---|---:|---:|---:|
| Finland · gRPC | **76 ms** | 579 ms | ×7.6 |
| USA · gRPC | 138 ms | 141 ms | ×1.02 |

Latency-based selection picks Finland: twice as fast. But its tail is 579 ms
against 141. For a game, a call or a video meeting that is a distinctly worse
route despite the twice-better median. A client that looks at a single number
systematically picks the wrong one.

Method measures **seven metrics** — latency, jitter, loss, handshake time,
availability, throughput and stability over time — and weighs them **by
workload**. Games care about jitter and loss, downloads about throughput,
browsing about a fast handshake. These are different answers, and a single
"best server" does not exist.

## Routes are not interchangeable

The second difference matters more than the first. Every known client treats
outbounds as equivalent and ranks them by one number. But protocols rest on
**different ways through**, and protocols sharing a way die together:

| Way through | Covered by | Killed by |
|---|---|---|
| QUIC over UDP | Hysteria2, TUIC | QUIC filtering |
| HTTP/2 in forged TLS | Reality gRPC | detecting the Reality forgery |
| Bare TCP in forged TLS | Reality Vision | the same |
| Genuine TLS | Trojan | only blocking the domain or IP |
| Encrypted stream, no handshake | Shadowsocks-2022 | none of the above |

A network can pass UDP and still kill QUIC — we measured it. In such a network,
switching from Hysteria2 to TUIC achieves **nothing**: both sit on the same way
through. Method knows this and fails over to a different way, not to a
neighbouring protocol.

## How it works

```mermaid
flowchart TD
    UI["User interface"] --> PR["Profiles and policies"]
    PR --> PE["Policy Engine<br/><i>what should happen</i>"]
    HE["Health Engine<br/><i>how routes feel</i>"] --> RE
    PE --> RE["Routing Engine<br/><i>what is bound where, now</i>"]
    RE --> TP["Transports<br/><i>Hysteria2 · Reality · Trojan · Shadowsocks · direct</i>"]
```

The core configuration describes not servers but **lanes** — gaming, streaming,
browsing, bulk, sensitive, direct. Rules mapping "application, domain or port →
lane" are compiled once. The binding "lane → node" changes at runtime,
**without tearing down the tunnel or dropping connections**.

That is why failover is invisible: it is a rebinding, not a reconnect.

## Status

This repository has just been opened. Honestly: **there is no working code here
yet** — the client is being moved over from private development, and the
orchestrator engine is being designed.

| Platform | Client | Orchestrator |
|---|---|---|
| macOS | moving in | in progress |
| Android | moving in | after macOS |
| Windows 10/11 | moving in | after Android |
| iOS | planned | — |

Commits and issues are the best way to follow along.

## Building

Instructions will arrive with the code. Building requires the
[sing-box](https://github.com/SagerNet/sing-box) core, which is **not part of**
this repository and is fetched separately (see the licence section).

## Licence

Method's code is distributed under the **GNU GPL version 3 or later** — see
[LICENSE](LICENSE).

Method uses the [sing-box](https://github.com/SagerNet/sing-box) core, running
it as a **separate process** and talking to it over local HTTP. The core is not
part of this repository and is downloaded at build or first run. sing-box is
distributed under its own terms; its authors have no connection to this project,
and this project is not affiliated with them in any way.

## Standing on shoulders

Network behaviour policies and route groups are not a new idea:
[Mihomo](https://github.com/MetaCubeX/mihomo), Surge and
[Psiphon](https://github.com/Psiphon-Labs/psiphon-tunnel-core) have implemented
versions of it. Method borrows from them openly and says so. What is new here is
choosing a route by its way through the censor, not by the node's latency.
