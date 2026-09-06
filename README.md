<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="logo.svg">
    <img src="logo-light.svg" alt="FinnyShare" width="140">
  </picture>
</p>

# FinnyShare - Jellyfin plugin

Remote access for Jellyfin that works behind CGNAT, without port forwarding, a static IP, or
touching your router.

Install the plugin and a public HTTPS address is already waiting in its settings page: no
sign-up, no account, nothing to pair. It works in any unmodified Jellyfin client.

> **v0.1 beta.** The tunnel, streaming, WebSockets and reconnect are tested and working,
> but this is a young project running on a single relay, with a **10 Mbps cap per server**
> (see [Limits](#limits-while-in-beta)). Read [SECURITY.md](SECURITY.md) before pointing it
> at a server you care about.

## Install

In Jellyfin, go to **Dashboard → Plugins → Repositories**, press **+**, and add:

| | |
|---|---|
| Repository Name | `FinnyShare` |
| Repository URL | `https://finnyshare.space/plugin/manifest.json` |

Then **Catalog → General → FinnyShare → Install**, and restart Jellyfin.

Open **Dashboard → Plugins → FinnyShare**. Your address is already there:

```
https://gentle-crane-k7m2qx.j.finnyshare.space
```

Copy it, paste it into any Jellyfin app on any network, and sign in with your normal Jellyfin
account. Nothing else to configure.

Requires **Jellyfin 10.11** or newer.

### Two domains, on purpose

| | |
|---|---|
| `finnyshare.space` | the FinnyShare site, the plugin repository, and the console |
| `<your-name>.j.finnyshare.space` | your server, and everybody else's |

Servers only ever live one level down, under `j.`, so no address anyone picks can impersonate
the site, the plugin repository, or a login page. That is also why a name you choose has to be
at least 12 characters and cannot contain words like `login` or `account`.

The address you are given ends in six random characters. That is deliberate: it makes your
server impractical to find by scanning, which keeps opportunistic bots away from your Jellyfin
sign-in page. It is **not** what protects your library; your Jellyfin password is. See
[SECURITY.md](SECURITY.md#your-address-is-hard-to-guess-which-is-not-the-same-as-protected).

### Changing or stopping it

The settings page has three controls beyond the address:

- **Get a different address**: assigns a new random one. The old address stops working
  immediately.
- **Choose my own address**: pick your own name, at least 12 characters, lowercase letters,
  numbers and hyphens. It lands under `j.finnyshare.space` like every other server.
- **Stop sharing**: takes your server off the air without uninstalling the plugin.

## Limits while in beta

**10 Mbps per server, shared by everyone watching it.**

That is a cap on the relay bandwidth your server uses, not on people:

- **No limit on viewers.** Share with as many people as you like: the whole point of running
  a Jellyfin server is sharing it. Two people watching split the 10 Mbps; four split it four
  ways.
- **Comfortable for one 1080p stream.** A high-bitrate 1080p remux is tight, and 4K may not
  play well, depending on the file. Jellyfin transcodes down to fit, so playback adapts rather
  than failing, but it costs your server CPU.
- **Local viewers are unaffected.** This only applies to traffic going through the relay.
  Anyone on your own network still streams at full speed, directly.

The cap exists because relay bandwidth is the one part of this that costs real money, and it
is per server so that one enthusiastic setup cannot starve everybody else's. Expect this
number to move as we learn what the traffic actually looks like.

Also true while in beta: **one relay, in one region**, so viewers far from it will see higher
latency; and no uptime promise. If FinnyShare is down, your server is simply not reachable
remotely. Nothing about your Jellyfin install or your media is affected.

## What this plugin does

It opens one outbound WebSocket to a FinnyShare relay and forwards requests that arrive on it
to your local Jellyfin. That is the whole job.

```
your Jellyfin  <-- localhost only --  plugin  -- outbound wss -->  relay  <-- https --  viewer
```

**This repository is the half that runs on your machine, and it is public for one reason: so
you can read exactly what it does before trusting it with your media server.**

**[SECURITY.md](SECURITY.md)** covers this in full: what the relay can see, what the plugin
is prevented from doing, and where the current limits are. It is worth a read before you point
this at a server you care about.

## What it does not do

These are enforced by code and covered by tests, not just promised here:

- **It never connects anywhere except your local Jellyfin.** Every tunnelled request is
  resolved against the configured local endpoint and rejected if the result would address any
  other host, port or scheme. See [LocalTarget.cs](Jellyfin.Plugin.FinnyShare/LocalTarget.cs)
  and its tests. Without that check, a hostile relay could use this plugin to reach anything
  inside your home network.
- **It never sends your credential in clear.** The relay endpoint must be `wss://`; plaintext
  has to be opted into explicitly for local development, and the plugin refuses to start
  otherwise rather than downgrading silently. See
  [RelayEndpoint.cs](Jellyfin.Plugin.FinnyShare/RelayEndpoint.cs).
- **It never stores or logs your Jellyfin credentials.** Jellyfin authentication is untouched:
  users, sessions and permissions stay entirely Jellyfin's business. Note it does *handle*
  them in transit; anything proxying your traffic must. See
  [SECURITY.md](SECURITY.md#what-stays-entirely-yours).
- **It never logs URLs, query strings or request bodies.** Jellyfin URLs contain API keys,
  so only the path, never the query, reaches a log line.
- **It forwards no connection-scoped headers.** See
  [HopByHop.cs](Jellyfin.Plugin.FinnyShare/HopByHop.cs).

## What it stores

In the Jellyfin plugin configuration, and nowhere else:

| | |
|---|---|
| `Token` | this installation's credential, issued automatically at first start |
| `Hostname` | the public address assigned to this server |
| `RenameUrl` | ready-made link to the page for choosing a custom address |
| `Disabled` | whether sharing is switched off |

There is no account and no password, so there is nothing else to keep. The token is issued to
the machine, never shown to a person, and never typed by one.

## The wire protocol

[protocol/frames.json](protocol/frames.json) is the byte-level contract between this plugin
and the relay: `[type:1][streamId:4 big-endian][payload]`. It is not documentation that can
drift: this repository's tests read that file, and so do the relay's, so a change to either
side fails both builds.

Frame layout and semantics are documented in
[TunnelFrame.cs](Jellyfin.Plugin.FinnyShare/TunnelFrame.cs).

## Building it yourself

```sh
dotnet test    Jellyfin.Plugin.FinnyShare.Tests
dotnet publish Jellyfin.Plugin.FinnyShare -c Release
```

Copy the resulting `Jellyfin.Plugin.FinnyShare.dll` and `meta.json` into
`<jellyfin-config>/plugins/FinnyShare/` and restart Jellyfin. Requires .NET 9 and targets
Jellyfin 10.11.

A build from source behaves exactly like the released one and connects to the same FinnyShare
service, so you can run the code you have read rather than a binary you have to trust.

## Security

See [SECURITY.md](SECURITY.md) for the threat model, the credentials involved, known
exposures and current gaps. Report vulnerabilities privately rather than in a public issue:
join the [Discord](https://discord.gg/r7vaMs7Zg6), say you have a security report, and we will
take it to a private channel.

## Help and questions

Ask in the [Discord](https://discord.gg/r7vaMs7Zg6). It is the quickest way to get an answer
and where problems with the relay get noticed first.

## Licence

See [LICENSE](LICENSE).
