using System.Text.Json;
using AbilityDraft.Contracts;
using abilitydraft.Services.InGame;

namespace AbilityDraft.GameNode;

// Exercises control, idempotency and readiness without Deadlock. Never opens a game port.
public sealed class SimulatedMatchWorkerHost : IMatchWorkerHost
{
    public IMatchWorkerProcess Start(MatchWorkerLaunch launch) => new Simulated(launch);
    private sealed class Simulated : IMatchWorkerProcess
    {
        private readonly CancellationTokenSource stop = new();
        private readonly Task reporting;
        public Simulated(MatchWorkerLaunch launch)
        {
            reporting = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var report = new MatchWorkerReport(launch.Result.ResultId, "Ready", 0,
                        launch.Result.Players.Count(p => !p.IsBot), DateTime.UtcNow);
                    var file = Path.Combine(launch.Directory, "status.json");
                    File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(report, GameNodeProtocol.Json));
                    File.Move(file + ".tmp", file, true);
                    await Task.Delay(1000, stop.Token);
                }
            });
        }
        public bool HasExited => stop.IsCancellationRequested;
        public void Stop() => stop.Cancel();
        public void Dispose() { Stop(); try { reporting.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } stop.Dispose(); }
    }
}
