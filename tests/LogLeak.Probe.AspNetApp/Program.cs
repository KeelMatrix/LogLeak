using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

namespace LogLeak.Probe.AspNetApp;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();

        app.MapGet("/probe/{value}", (string value, ILogger<Program> logger) =>
        {
            logger.LogInformation("ASP.NET Core request value {Value}", value);
            return Results.Ok();
        });

        app.Run();
    }
}
