using KeelMatrix.LogLeak;

var builder = WebApplication.CreateBuilder(args);
using var probe = new LogLeakProbe()
    .AddSecret("request-token", "synthetic-aspnet-token-4c31");

builder.Logging.AddProvider(probe.Provider);

var app = builder.Build();
app.MapGet("/health", (ILogger<Program> logger) =>
{
    logger.LogInformation("health check completed");
    probe.AssertNoLeaks();
    return Results.Ok();
});

app.Run();
