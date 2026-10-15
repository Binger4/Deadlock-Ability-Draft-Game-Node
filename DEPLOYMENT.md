# Install a Game Node on another Windows machine

This guide connects a remote match host to an existing MAIN installation. For build prerequisites, see [README.md](README.md#requirements).

## Choose the addresses

Replace the examples with your own addresses:

| Example | Purpose |
| --- | --- |
| `https://draft.example.org/` | MAIN website and coordinator. |
| `192.168.1.50` | Remote PC's reserved LAN address. |
| `node1.example.org` | DNS A record pointing to the remote public IPv4. |
| `https://node1.example.org/ping` | Remote HTTPS latency probe. |
| `27069–27072` | Four match ports. |

For a home PC, reserve its LAN address in the router and check the router's WAN IPv4. ISP CGNAT prevents direct inbound connections; request a public IPv4 or use a VPS. For a VPS, use its assigned public address and provider firewall.

## Cloudflare DNS and the MAIN connection

| Hostname | Configuration |
| --- | --- |
| `draft.example.org` | MAIN's HTTPS proxy or Cloudflare Tunnel. |
| `node1.example.org` | **DNS-only A record** pointing to the remote public IPv4. |

Keep the game hostname DNS-only. Cloudflare's standard HTTP proxy does not forward these game ports; see [supported ports](https://developers.cloudflare.com/fundamentals/reference/network-ports/).

The agent connects outbound to `wss://draft.example.org/api/game-nodes/v1/connect`. It does not need a tunnel on the remote PC. If MAIN already works over HTTPS, use that URL.

For a new MAIN Tunnel, publish the existing local website binding, such as `http://127.0.0.1:5050`, under `draft.example.org`. Follow [Cloudflare's tunnel setup](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/get-started/create-remote-tunnel/). Enable WebSockets and let `/api/game-nodes/v1/connect` pass without caching, browser challenges or an interactive Access login. MAIN uses the node's bearer secret for authentication.

Run the tunnel connector on MAIN to use its default trusted loopback proxy addresses. If the TLS proxy runs elsewhere, add that proxy's exact IP to `GameNodes:TrustedProxyAddresses` so MAIN accepts its forwarded HTTPS scheme.

MAIN's draft hubs also need reachable game ports, normally `27067` and `27068`. See [MAIN hosting](https://github.com/Binger4/Deadlock-Ability-Draft/blob/main/Integration/HOSTING.md#cloudflare-and-ports).

## Install the files on the remote PC

1. Install .NET 10 ASP.NET Core Runtime, the Visual C++ x64 runtime, Deadlock and Deadworks 0.5.6 as described in [Install on Windows](README.md#install-on-windows).
2. Copy the matching three plugin DLLs into `C:\DeadlockServer\game\bin\win64\managed\plugins\`.
3. Publish the agent on the build machine:

   ```powershell
   dotnet publish AbilityDraft.GameNode.csproj -c Release -o publish
   ```

4. Copy the complete `publish` directory contents to `C:\AbilityDraftGameNode\app`. Copy `appsettings.example.json` as `appsettings.json` for the first installation.
5. Create `C:\AbilityDraftGameNode\state`. Give the hosting account access to this directory and the game installation.

## Configure both ends

Remote `appsettings.json`:

```json
{
  "GameNode": {
    "Id": "node-01", "Name": "Game server 1", "Region": "Region 1",
    "CoordinatorUrl": "https://draft.example.org/",
    "NodeSecret": "",
    "GameRoot": "C:/DeadlockServer",
    "StateDirectory": "C:/AbilityDraftGameNode/state",
    "PublicGameHost": "node1.example.org",
    "FirstGamePort": 27069, "MaxWorkers": 4,
    "PublicProbeUrl": "https://node1.example.org/ping",
    "ProbeListenUrl": "http://127.0.0.1:5081",
    "Enabled": true, "Simulation": false
  }
}
```

Add this entry to MAIN's private configuration, preserving its other settings and nodes:

```json
{
  "InGame": { "Enabled": true },
  "GameNodes": {
    "RemoteNodes": [{
      "Id": "node-01", "Name": "Game server 1", "Region": "Region 1",
      "Enabled": true, "Secret": "",
      "PublicGameHost": "node1.example.org",
      "FirstGamePort": 27069, "MaxWorkers": 4,
      "PublicProbeUrl": "https://node1.example.org/ping"
    }]
  }
}
```

Generate a secret using [README.md](README.md#configuration). Set the same value in the agent's `NodeSecret` and MAIN's `Secret`. The ID, public host, port range, capacity and probe URL must match. Restart MAIN after changing its configuration; schedule this around active drafts.

## Open the remote ports

For the four-worker example:

| Purpose | Ports | Destination |
| --- | --- | --- |
| Matches | TCP and UDP `27069–27072` | Remote PC, same ports. |
| HTTPS probe | TCP `443` | Caddy on the remote PC. |
| Certificate challenge and HTTP redirect | TCP `80` | Caddy on the remote PC. |
| Agent's local probe | TCP `5081` | Loopback only; do not forward. |

Open the game and HTTPS ports in Windows Firewall. On a home router, forward them to the reserved LAN IP. On a VPS, add the provider's inbound rules. The remote node does not run draft hubs, so it does not need `27067–27068`.

Run in administrator PowerShell on the remote PC:

```powershell
New-NetFirewallRule -DisplayName 'Ability Draft game UDP' -Direction Inbound -Action Allow -Protocol UDP -LocalPort 27069-27072
New-NetFirewallRule -DisplayName 'Ability Draft game TCP' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 27069-27072
New-NetFirewallRule -DisplayName 'Ability Draft probe HTTPS' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 443
New-NetFirewallRule -DisplayName 'Ability Draft probe HTTP' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 80
```

If multiple nodes share one public IP, assign separate game-port ranges and HTTPS proxy routes.

## Serve the direct HTTPS probe

Install [Caddy](https://caddyserver.com/download) and save this as `C:\Caddy\Caddyfile`:

```caddyfile
node1.example.org {
    @probe path /ping
    handle @probe {
        reverse_proxy 127.0.0.1:5081
    }
    handle {
        respond 404
    }
}
```

Start it with:

```powershell
C:/Caddy/caddy.exe run --config C:/Caddy/Caddyfile --adapter caddyfile
```

Caddy obtains and renews the public certificate. Keep its certificate data between restarts. The probe needs a browser-trusted certificate; a Cloudflare Origin CA certificate is insufficient for this DNS-only endpoint.

Create a separate startup task for Caddy using the [agent task settings](README.md#start-automatically-with-task-scheduler), with arguments `run --config C:\Caddy\Caddyfile --adapter caddyfile`.

If HTTPS probing is unavailable, omit `PublicProbeUrl` on both MAIN and the agent. Match hosting can work with ping shown as unknown.

## Start and verify

On the remote PC:

```powershell
Set-Location C:/AbilityDraftGameNode/app
dotnet ./AbilityDraft.GameNode.dll
```

Check the local probe:

```powershell
curl.exe -i http://127.0.0.1:5081/ping
```

From another Internet connection:

```powershell
Resolve-DnsName node1.example.org -Type A
curl.exe -i https://node1.example.org/ping -H "Origin: https://draft.example.org"
```

Expect the remote public IPv4, HTTP `204`, `Cache-Control: no-store` and `Access-Control-Allow-Origin: https://draft.example.org`.

Check that MAIN's `/admin` shows the node Online. In Deadlock, create a lobby on that node, complete a draft and start the match. Join from an external connection, check spectator admission, then finish the match and confirm that the worker slot is released. Game ports listen only while a worker is running.

Configure the [startup task](README.md#start-automatically-with-task-scheduler) after these checks pass, then check the agent and Caddy again after a reboot.

| Symptom | Check |
| --- | --- |
| Online, but players cannot join | Public IPv4, DNS-only record, TCP/UDP forwarding and firewall rules. |
| DNS returns Cloudflare proxy addresses | Change the game hostname to DNS only. |
| Node stays Offline | MAIN URL, ID/secret, WebSocket upgrade and forwarded HTTPS scheme. |
| Ping is unavailable | Certificate, CORS origin and probe proxy route. |
| Worker does not start | Plugin/Deadworks versions, game path, permissions, port conflicts and worker logs. |
