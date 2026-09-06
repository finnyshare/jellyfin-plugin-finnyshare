# Security model

FinnyShare carries your Jellyfin traffic, so you should know what it can see, what it cannot
do, and where its limits are. This document covers all three.

**The short version:**

- Your Jellyfin users, passwords and permissions are untouched. FinnyShare never creates,
  reads or bypasses them.
- The plugin can only ever talk to your own Jellyfin. It cannot reach anything else on your
  network, and that restriction is enforced in code and tested.
- It needs no root, no open ports and no router changes. It makes one outbound connection.
- The relay does terminate HTTPS, so it is technically able to see traffic passing through.
  That is inherent to letting unmodified Jellyfin apps connect. [Details below](#what-the-relay-can-see).

Two kinds of claim appear here, and the difference matters when you decide what to trust:

- **Enforced in this repository**: links to the code, and to the test that fails if it stops
  being true. You can verify these yourself.
- **Asserted about the relay**: the relay and control plane are closed source and not in this
  repository, so nothing here proves them. They are marked *(asserted)*.

---

## What stays entirely yours

Jellyfin's own authentication is untouched. FinnyShare has no accounts, no passwords and no
sign-in of its own, so there is no FinnyShare credential belonging to you that could be
phished, leaked or lost.

| | |
|---|---|
| Your Jellyfin users and passwords | Jellyfin's, exclusively |
| Who may see which libraries | Jellyfin's, exclusively |
| Sessions and devices | Jellyfin's, exclusively |

A viewer reaching your server through FinnyShare sees the same login screen and needs the same
credentials as one on your local network. The relay carries bytes; it does not evaluate them,
and it cannot grant access to anything.

**One precise note.** The plugin never *stores* and never *logs* your Jellyfin credentials,
but "never sees" would be untrue: every proxied request passes through the plugin's memory,
including `Authorization` headers, `api_key` parameters, and the body of a sign-in request.
Anything that proxies your traffic necessarily handles it. The relay is in the same position,
for the same reason.

---

## What the relay can see

The relay terminates TLS in order to route your traffic:

```
viewer ──HTTPS──▶ relay ──wss──▶ plugin ──HTTP──▶ Jellyfin
                    ▲
            decrypts here
```

It decrypts the viewer's HTTPS, re-encrypts onto the tunnel, and the plugin then talks to
Jellyfin over loopback. So the relay is *technically capable* of observing request paths,
headers, including the Jellyfin session token a viewer sends, and media bytes.

This is a property of the design rather than an oversight. End-to-end encryption would require
the viewer's Jellyfin app to hold a FinnyShare key, and a core constraint of this project is
that **unmodified Jellyfin clients work**. Any design where a stock client can connect is one
where the relay terminates TLS. Hosted tunnels and reverse proxies all share this property.

What limits it in practice:

| | |
|---|---|
| The relay records no request data *(asserted)* | no paths, no headers, no bodies |
| It does record how much it carried *(asserted)* | bytes per installation per day, plus tunnel connect/disconnect. That is what the bandwidth cap is enforced from. It says nothing about what was watched |
| The plugin logs paths without query strings | Jellyfin URLs carry `api_key`; `TunnelService.cs` splits on `?` |
| Nothing logs request or response bodies | |
| Media is streamed, never stored | 64 KiB chunks, nothing buffered to disk |

**If you would rather not involve a third party in transport at all**, that is a reasonable
position: a VPN such as WireGuard or Tailscale between your own devices fits it better.
FinnyShare exists for the case where that is impractical: viewers who cannot install anything,
on networks you do not control.

---

## What the plugin will not do

Enforced in code and covered by tests, not merely intended.

**It connects to nothing except your local Jellyfin.** Every tunnelled request is resolved
against the configured local endpoint and rejected if the result would reach any other scheme,
host or port. That includes the URL tricks which make naive string joining unsafe, such as
`@evil.com/x` (which parses your local address as *credentials* and `evil.com` as the host)
and protocol-relative `//evil.com/x`. Without this check, a hostile or compromised relay could
use the plugin to reach other devices on your home network.
[LocalTarget.cs](Jellyfin.Plugin.FinnyShare/LocalTarget.cs) ·
[tests](Jellyfin.Plugin.FinnyShare.Tests/LocalTargetTests.cs)

**It is not a general-purpose proxy.** No configuration makes it forward to an arbitrary host.
That restriction is also what stops FinnyShare from being usable as an open relay by anyone
who installs it.

**It forwards no connection-scoped headers.** `Connection`, `Upgrade`, `Transfer-Encoding` and
similar are dropped rather than relayed; forwarding them invites request smuggling and breaks
WebSocket handshakes.
[HopByHop.cs](Jellyfin.Plugin.FinnyShare/HopByHop.cs) ·
[tests](Jellyfin.Plugin.FinnyShare.Tests/HopByHopTests.cs)

**It never sends its credential in the clear.** The relay endpoint must be `wss://`. Plaintext
must be opted into explicitly for local development, and the plugin refuses to start rather
than downgrading silently.
[RelayEndpoint.cs](Jellyfin.Plugin.FinnyShare/RelayEndpoint.cs)

**It requires no elevated privileges.** No root, no raw sockets, no TUN device, no router
configuration, no listening port. One outbound connection, and loopback.

---

## The installation token

Your server gets one credential automatically the first time the plugin starts. No human step,
and it is never shown to anyone:

```
inst_da03ec0de5dd . RSwZUxzvSvVjDOsw5G6BH6U8bKaezKKjobLzMlrGRqQ
└─ installation id ┘ └───────── secret, 32 random bytes ─────────┘
```

The relay stores only `SHA-256(secret)` and compares it in constant time, so a timing probe
cannot extract it and a copy of the relay's database yields no working credentials. The
plaintext exists in your plugin configuration and in the `/app/rename` link built from it,
and nowhere else. The control plane returns it exactly once, at registration.

**What it grants:** the right to hold *this one server's* tunnel, and to rename or disable
*this one server*. It cannot sign in to Jellyfin and cannot affect any other installation:
presenting one installation's id with another's secret is rejected.

**If it leaked**, someone could connect a tunnel claiming to be your server, and viewer traffic
for your address would reach them instead. Two things bound that: the scope is a single server,
and the relay accepts one tunnel per installation *(asserted)*, so an impostor **disconnects
your real server**. The failure is visible (your server shows as offline) rather than a
silent interception.

**Worth knowing:** Jellyfin serves plugin configuration to authenticated administrators, so
the token crosses an administrator's browser when the settings page loads. This grants an
administrator nothing they did not already have: the same value sits in
`plugins/configurations/Jellyfin.Plugin.FinnyShare.xml`, readable by anyone with admin or
filesystem access. The practical consequence: **treat Jellyfin administrator access as
equivalent to holding this token.**

---

## Your address is hard to guess, which is not the same as protected

An assigned address is `adjective-noun-<6 characters>`, about **40 bits**, which is decades of
scanning at a thousand guesses a second. The relay also meters guesses: requests for addresses
that do not exist are rate limited per client IP, far more tightly than real traffic, so
scanning is charged for as well as slow.

Two things this deliberately does not claim:

- **A name you choose yourself is guessable by definition.** Unguessability is a property of
  the *assigned* address only. The rename page says so plainly rather than pretending the
  choice is free.
- **It is defence in depth, not access control.** It keeps opportunistic scanners away from
  your Jellyfin sign-in page. It does **not** protect your library; your Jellyfin passwords
  do that, exactly as they would on a port-forwarded server.

---

## Known limitations

Stated plainly, because a security document that lists only strengths is not one you can act
on:

- **No token rotation.** A token is valid until the installation is removed. Remediating a
  suspected leak means removing the installation and letting the plugin register again; it
  detects sustained rejection and re-registers on its own. The new installation gets a **new
  address**, so any link already shared stops working.
- **A credential in a query string.** The `/app/rename` link carries the token where URLs are
  easiest to leak: a screenshot, a shared screen, browser sync. It is served with
  `Referrer-Policy: no-referrer` and `Cache-Control: no-store`, and no request path is ever
  logged. This is the cost of having nothing to sign in to: a deliberate trade, not an
  oversight.
- **All servers share one registrable domain.** Servers live under `*.j.finnyshare.space`, so
  no name anyone picks can shadow the console. But until `j.finnyshare.space` and
  `finnyshare.space` are both on the Public Suffix List, one server could set a cookie scoped
  to `.j.finnyshare.space` (reaching other servers) or `.finnyshare.space` (reaching the
  console). The 12-character minimum and the reserved-word list address the impersonation
  half; the Public Suffix List entries address the cookie half.
- **All traffic transits the relay.** There is no direct, relay-bypassing mode yet. See
  [what the relay can see](#what-the-relay-can-see).
- **Beta service.** One relay, in one region, with a 10 Mbps cap per server and no uptime
  guarantee. If FinnyShare is unavailable, your server is simply not reachable remotely.
  Nothing about your Jellyfin install or your media is affected.

---

## Corrections to this document

Four statements in an earlier draft of this document were inaccurate and have been corrected:
about Jellyfin credentials being "never seen", about every request being resolved against the
local endpoint, and about credential handling during setup. They are noted here rather than
quietly edited, because a security document you cannot trust to correct itself is not worth
much.

---

## Reporting a vulnerability

Please report it privately rather than opening a public issue, and allow time for a fix before
disclosure. The fastest way to reach us is Discord: **https://discord.gg/r7vaMs7Zg6** — say
you have a security report and we will take it to a private channel.

Reports about the relay or control plane are equally welcome, even though their source is not
in this repository.
