using System.Net.WebSockets;
using System.Text.Json;
using AbilityDraft.Contracts;
using abilitydraft.Services.InGame;

namespace AbilityDraft.GameNode;

public sealed class GameNodeAgentOptions
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Region { get; set; } = "";
    public string CoordinatorUrl { get; set; } = "";
    public string NodeSecret { get; set; } = "";
    public string GameRoot { get; set; } = "";
    public string StateDirectory { get; set; } = "";
    public string PublicGameHost { get; set; } = "";
    public int FirstGamePort { get; set; } = 27069;
    public int MaxWorkers { get; set; } = 4;
    public string? PublicProbeUrl { get; set; }
    public string ProbeListenUrl { get; set; } = "http://127.0.0.1:5081";
    public bool Enabled { get; set; } = true;
    public bool Simulation { get; set; }
    public void Validate()
    {
        if (Id is "" or "main" || Id.Length > 64 || !Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
            NodeSecret.Length < 32 || string.IsNullOrWhiteSpace(Name) || !GameNodeProtocol.ValidHost(PublicGameHost) ||
            !Uri.TryCreate(CoordinatorUrl, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !CoordinatorUrl.EndsWith('/') || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            !Path.IsPathFullyQualified(StateDirectory) || (!Simulation && !Path.IsPathFullyQualified(GameRoot)) ||
            MaxWorkers is < 1 or > 16 || FirstGamePort is < 1024 or > 65535 || FirstGamePort + MaxWorkers - 1 > 65535)
            throw new InvalidOperationException("Invalid GameNode configuration. See README.md; never put the secret in command-line arguments.");
        if (PublicProbeUrl is { Length: > 0 } && (!Uri.TryCreate(PublicProbeUrl, UriKind.Absolute, out var probe) ||
            !string.IsNullOrEmpty(probe.UserInfo) || (probe.Scheme != "https" && !(probe.Scheme == "http" && probe.IsLoopback))))
            throw new InvalidOperationException("PublicProbeUrl must use HTTPS (HTTP is allowed only on loopback for local tests).");
        if (Simulation && (!uri.IsLoopback || PublicGameHost is not ("127.0.0.1" or "localhost")))
            throw new InvalidOperationException("Simulation is restricted to a loopback coordinator and game host.");
    }
}

public sealed class GameNodeAgent(GameNodeAgentOptions config, MatchWorkerCoordinator workers,
    ILogger<GameNodeAgent> logger) : BackgroundService
{
    private readonly string tombstoneFile = Path.Combine(config.StateDirectory, "finished-matches.json");
    private readonly string admissionsFile = Path.Combine(config.StateDirectory, "admission-revisions.json");
    private readonly Dictionary<string, GameNodeMatchStatus> failures = new();
    private readonly Dictionary<string, int> admissions = new();
    private HashSet<string> finished = new();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.Enabled) return;
        finished = File.Exists(tombstoneFile) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(tombstoneFile))
            ?? throw new InvalidDataException("Unreadable Game Node completion journal.") : [];
        if (File.Exists(admissionsFile))
            foreach (var entry in JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(admissionsFile))
                ?? throw new InvalidDataException("Unreadable admission journal.")) admissions.Add(entry.Key, entry.Value);
        var registration = new GameNodeRegistration(GameNodeProtocol.Version,
            typeof(GameNodeAgent).Assembly.GetName().Version?.ToString() ?? "1.0.0", config.PublicGameHost,
            config.FirstGamePort, config.MaxWorkers, config.PublicProbeUrl);
        var endpoint = new UriBuilder(new Uri(new Uri(config.CoordinatorUrl), "api/game-nodes/v1/connect"));
        endpoint.Scheme = endpoint.Scheme == "https" ? "wss" : "ws";
        var backoff = 1;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("X-Game-Node-Id", config.Id);
                socket.Options.SetRequestHeader("Authorization", "Bearer " + config.NodeSecret);
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                { timeout.CancelAfter(TimeSpan.FromSeconds(15)); await socket.ConnectAsync(endpoint.Uri, timeout.Token); }
                logger.LogInformation("Connected to Game Node Coordinator as {Node}", config.Id);
                while (!stoppingToken.IsCancellationRequested)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    var reports = workers.NodeReports().Concat(failures.Values).GroupBy(m => m.MatchId)
                        .Select(g => g.First() with { AdmissionRevision = admissions.GetValueOrDefault(g.Key, -1) }).ToArray();
                    await GameNodeProtocol.Send(socket, new GameNodeHeartbeat(registration, reports), timeout.Token);
                    var commands = await GameNodeProtocol.Receive<GameNodeCommands>(socket, timeout.Token);
                    if (commands.ProtocolVersion != GameNodeProtocol.Version || commands.Error is not null)
                        throw new InvalidOperationException(commands.Error ?? "Incompatible Game Node protocol.");
                    backoff = 1;
                    logger.LogDebug("Heartbeat acknowledged: {Matches} active matches", reports.Count(r => r.ProcessActive));
                    Apply(commands);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Never log headers, options, payloads or secrets.
                logger.LogWarning("Coordinator disconnected: {Reason}. Reconnecting in {Seconds}s", 
                    ex is InvalidOperationException ? ex.Message : ex.GetType().Name, backoff);
            }
            await Task.Delay(TimeSpan.FromSeconds(backoff) + TimeSpan.FromMilliseconds(Random.Shared.Next(500)), stoppingToken);
            backoff = Math.Min(60, backoff * 2);
        }
    }
    private void Apply(GameNodeCommands commands)
    {
        foreach (var matchId in commands.StopMatches) workers.Stop(matchId);
        foreach (var matchId in commands.ForgetMatches)
        {
            if (workers.NodeReports().Any(m => m.MatchId == matchId && m.ProcessActive)) continue;
            finished.Add(matchId);
            var temporary = tombstoneFile + ".tmp";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, finished); file.Flush(true); }
            File.Move(temporary, tombstoneFile, true);
            workers.Forget(matchId); failures.Remove(matchId); admissions.Remove(matchId); SaveAdmissions();
        }
        foreach (var command in commands.StartMatches)
        {
            if (finished.Contains(command.MatchId) || failures.ContainsKey(command.MatchId)) continue;
            try
            {
                if (!Guid.TryParseExact(command.MatchId, "N", out _) || command.Result.ProtocolVersion != 1 ||
                    command.Catalog.ProtocolVersion != 1 || command.Result.Players.Length > 64 || command.Spectators.Length > 64)
                    throw new InvalidOperationException("Invalid StartMatch payload.");
                var result = command.Result with { ResultId = command.MatchId, Spectators = command.Spectators };
                RuntimePlayerId.Validate(result);
                var wasKnown = workers.Get(command.MatchId) is not null;
                var view = workers.Request(result, command.Catalog, retryFailed: false);
                if (admissions.GetValueOrDefault(command.MatchId, -1) < command.AdmissionRevision)
                    foreach (var spectator in command.Spectators) workers.AdmitSpectator(command.MatchId, spectator);
                foreach (var steam in command.Abandoned) workers.Abandon(command.MatchId, steam);
                admissions[command.MatchId] = command.AdmissionRevision;
                SaveAdmissions();
                if (!wasKnown) logger.LogInformation("Received StartMatch {Match}: {State}", command.MatchId, view.State);
            }
            catch (Exception ex)
            {
                workers.Stop(command.MatchId);
                failures[command.MatchId] = new(command.MatchId, "Failed", 0, false, 0, []);
                logger.LogError("Match {Match} failed: {Type}", command.MatchId, ex.GetType().Name);
            }
        }
    }
    private void SaveAdmissions()
    {
        using (var file = new FileStream(admissionsFile + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, admissions); file.Flush(true); }
        File.Move(admissionsFile + ".tmp", admissionsFile, true);
    }
}
