# Ability Draft Game Node

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Runs Deadlock Ability Draft matches on a separate machine. [MAIN](https://github.com/Binger4/Deadlock-Ability-Draft) hosts the website and drafts, then sends each completed roster to the selected node. Players connect directly to that node's game port.

```text
MAIN <---- outbound WebSocket ---- Game Node
                                      |
Players ----------------------> Deadworks match workers
```

A single-machine installation can use MAIN's local workers without this agent. For a second machine, follow [DEPLOYMENT.md](DEPLOYMENT.md).

## Requirements

- Windows x64 with Deadlock server files and [Deadworks 0.5.6](https://github.com/Deadworks-net/deadworks/releases/tag/v0.5.6).
- [Visual C++ Redistributable 2015–2022 x64](https://aka.ms/vs/17/release/vc_redist.x64.exe).
- .NET 10 ASP.NET Core Runtime to run the agent, or the SDK to build it. A self-contained agent includes its runtime; the Deadworks plugin still needs .NET Runtime 10 x64.
- Matching `AbilityDraft.Deadworks.dll`, `AbilityDraft.Runtime.dll` and `AbilityDraft.Contracts.dll` from MAIN.
- Reachable TCP/UDP game ports and outbound HTTPS access to MAIN. The optional browser latency probe needs its own HTTPS endpoint.

## Install on Windows

1. Install Deadlock and Deadworks into `C:\DeadlockServer`. Check that `game\bin\win64\deadworks.exe` exists. MAIN's [installation guide](https://github.com/Binger4/Deadlock-Ability-Draft/blob/main/Integration/INSTALLATION.md) covers the downloads and build tools.
2. Build the plugin from the MAIN checkout:

   ```powershell
   dotnet build Integration/Deadworks/AbilityDraft.Deadworks.csproj -c Release `
     -p:DeadworksManagedDir=C:/DeadlockServer/game/bin/win64/managed
   ```

   Copy the three plugin DLLs from `Integration/Deadworks/bin/Release/net10.0/` to `C:\DeadlockServer\game\bin\win64\managed\plugins\`.

3. Publish this agent:

   ```powershell
   dotnet publish AbilityDraft.GameNode.csproj -c Release -o publish
   ```

   Copy the contents of `publish` to `C:\AbilityDraftGameNode\app`. Create a private state directory at `C:\AbilityDraftGameNode\state`.

4. Copy `appsettings.example.json` to `appsettings.json` and fill in the settings below. Add the matching entry to MAIN's private configuration.
5. Follow [DEPLOYMENT.md](DEPLOYMENT.md) for DNS, ports, HTTPS and the first connection check.

## Configuration

```json
{
  "GameNode": {
    "Id": "node-01",
    "Name": "Game server 1",
    "Region": "Region 1",
    "CoordinatorUrl": "https://draft.example.org/",
    "NodeSecret": "",
    "GameRoot": "C:/DeadlockServer",
    "StateDirectory": "C:/AbilityDraftGameNode/state",
    "PublicGameHost": "node1.example.org",
    "FirstGamePort": 27069,
    "MaxWorkers": 4,
    "PublicProbeUrl": "https://node1.example.org/ping",
    "ProbeListenUrl": "http://127.0.0.1:5081",
    "Enabled": true,
    "Simulation": false
  }
}
```

| Setting | Value |
| --- | --- |
| `Id` | Unique ID using letters, digits, `-` or `_`; up to 64 characters. `main` is reserved. |
| `Name`, `Region` | Server labels. MAIN controls the labels shown to players. |
| `CoordinatorUrl` | MAIN's HTTPS URL, ending in `/`. |
| `NodeSecret` | Unique secret of at least 32 characters, matching MAIN's entry. |
| `GameRoot` | Absolute path containing `game/bin/win64/deadworks.exe`. |
| `StateDirectory` | Private absolute path used by one agent instance. |
| `PublicGameHost` | Public IPv4 address or DNS hostname, without scheme, path or port. |
| `FirstGamePort`, `MaxWorkers` | Contiguous port pool; up to 16 workers, ending at or below port 65535. |
| `PublicProbeUrl` | HTTPS `/ping` URL. Set the same URL on MAIN, or omit it on both sides. |
| `ProbeListenUrl` | Agent's local HTTP listener; normally `http://127.0.0.1:5081`. |
| `Enabled` | Enables workers and the connection to MAIN. |
| `Simulation` | File-based test workers, restricted to a loopback coordinator and game host. |

On MAIN, set `InGame.Enabled=true` and add the node under `GameNodes.RemoteNodes`. Its ID, secret, public host, first port, capacity and probe URL must match. See [Configure both ends](DEPLOYMENT.md#configure-both-ends).

Generate a secret in PowerShell 7:

```powershell
$nodeSecret = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:GameNode__NodeSecret = $nodeSecret
```

Set the same value in MAIN's node entry. Keep it in private configuration or the hosting account's environment. Environment variables override JSON. Local configuration, state and build output are excluded by `.gitignore`.

## Run and stop

```powershell
Set-Location C:/AbilityDraftGameNode/app
dotnet ./AbilityDraft.GameNode.dll
```

The node appears Online in MAIN's `/admin` after its first heartbeat. Ctrl+C stops the agent and its owned matches. Let matches finish before restarting or updating.

Agent logs are in `state/logs/agent-yyyy-MM-dd.log` (UTC). Worker output is in `state/matches/<worker-id>/server.log` and `server.err.log`.

### Start automatically with Task Scheduler

Create a task named **Ability Draft Game Node**:

- Trigger: at startup, with a short network delay.
- Account: a hosting account with access to the game, configuration and state directories.
- Program: the full path to `dotnet.exe`.
- Arguments: `C:\AbilityDraftGameNode\app\AbilityDraft.GameNode.dll`.
- Start in: `C:\AbilityDraftGameNode\app`.
- Enable restart on failure, remove the default running-time limit, and select **Do not start a new instance**.

For a self-contained publish, use `AbilityDraft.GameNode.exe` as the program. Stopping a scheduled task terminates the process; use Ctrl+C for an interactive shutdown.

## Connections and state

The agent connects to `/api/game-nodes/v1/connect` on MAIN using its node ID and secret. MAIN sends match rosters and the resource catalog. Players connect to the node's public game address; MAIN does not relay game traffic.

Repeated assignments start one worker. After a connection interruption, the agent reconnects and reports its worker inventory. MAIN keeps the assigned slots reserved until that inventory is reconciled. Keep the state directories on both machines between restarts.

The `/ping` endpoint returns HTTP 204 and allows requests from MAIN's website origin. It measures browser-to-node latency independently of the control connection. See [the HTTPS setup](DEPLOYMENT.md#serve-the-direct-https-probe).

## Local simulation and tests

Build this project, then run from the MAIN checkout:

```powershell
$env:GAME_NODE_TEST_DLL = (Resolve-Path ../Deadlock-Ability-Draft-Game-Node/bin/Debug/net10.0/AbilityDraft.GameNode.dll).Path
dotnet run --project Integration/Tests/AbilityDraft.IntegrationTests.csproj -- .
node Integration/Tests/GameNodeLatencyChecks.mjs
```

The tests connect the agent to a local coordinator and check authentication, assignment, readiness, reconnects, capacity and cleanup using simulated workers. Test game-port reachability separately from another Internet connection.

## Updating and shared source

Stop the agent after matches finish. Back up its private configuration and state, replace the published files and matching plugin, then restart.

`Shared/Contracts` and `Shared/MatchHosting` come from MAIN. The node builds independently and checks their SHA-256 hashes. Edit shared code in MAIN, run `Integration/Sync-GameNodeShared.ps1`, then commit the source, `manifest.json` and `Snapshot.props` together. `.gitattributes` preserves the LF line endings used by these hashes.

## Troubleshooting and limits

| Symptom | Check |
| --- | --- |
| Node stays Offline | MAIN URL, outbound HTTPS, WebSocket proxying, ID and secret. |
| Registration rejected | Matching protocol, host, port range, capacity and probe URL. |
| Online but ping unavailable | Probe DNS, certificate, CORS and proxy caching. |
| Players cannot join | Public address, CGNAT, TCP/UDP forwarding and Windows/provider firewall. |
| Worker fails to start | Game path, Deadworks/plugin versions, permissions, occupied ports and worker logs. |
| Capacity stays reserved | Restore the node connection and let MAIN reconcile its workers. |
| Second agent exits | Another instance owns the state-directory lock. |

Each node supports up to 16 workers and IPv4/DNS game addresses. Public Queue uses MAIN's local pool. Website rooms remain in memory on MAIN, so restarting MAIN clears them. There is no automatic region failover or game relay.
