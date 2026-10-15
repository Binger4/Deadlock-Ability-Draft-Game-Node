using AbilityDraft.GameNode;
using abilitydraft.Services.InGame;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration.GetSection("GameNode").Get<GameNodeAgentOptions>() ?? new();
config.Validate();
Directory.CreateDirectory(config.StateDirectory);
// One process per state directory prevents two agents from owning the same port pool.
using var lease = new FileStream(Path.Combine(config.StateDirectory, "agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
builder.WebHost.UseUrls(config.ProbeListenUrl);
builder.Logging.AddProvider(new LocalFileLogger(Path.Combine(config.StateDirectory, "logs")));
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<IOptions<MatchWorkerOptions>>(Options.Create(new MatchWorkerOptions
{
    Enabled = config.Enabled, GameRoot = config.GameRoot, StateDirectory = Path.Combine(config.StateDirectory, "matches"),
    PublicHost = config.PublicGameHost, FirstPort = config.FirstGamePort, MaxWorkers = config.MaxWorkers
}));
builder.Services.AddSingleton<IMatchWorkerHost>(sp => config.Simulation ? new SimulatedMatchWorkerHost() :
    new DeadworksMatchWorkerHost(sp.GetRequiredService<IOptions<MatchWorkerOptions>>()));
builder.Services.AddSingleton<MatchWorkerCoordinator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MatchWorkerCoordinator>());
builder.Services.AddHostedService<GameNodeAgent>();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(new Uri(config.CoordinatorUrl).GetLeftPart(UriPartial.Authority)).WithMethods("GET")));
var app = builder.Build();
app.UseCors();
app.MapGet("/ping", (HttpContext http) =>
{
    http.Response.Headers.CacheControl = "no-store, max-age=0";
    return Results.NoContent();
});
await app.RunAsync();
