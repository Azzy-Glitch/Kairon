using Kairon.Agent;
using Kairon.Agent.LogTailing;
using Kairon.Agent.ProcessWatch;

var builder = Host.CreateApplicationBuilder(args);

// Additive: only changes behavior when actually launched by the Windows Service Control
// Manager (sets the working directory to the service's install path, wires up the SCM control
// handler). Plain `dotnet run`/console launches are completely unaffected - this is what lets
// the Agent be installed as a real Windows service (docs/DESKTOP_SHELL.md) without giving up the
// simple console-mode dev workflow this session has used throughout.
builder.Services.AddWindowsService(options => options.ServiceName = "Kairon.Agent");

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));

builder.Services.AddHttpClient<AgentEventClient>((sp, http) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
    http.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(Math.Max(2, options.TimeoutSeconds + 1));
});

builder.Services.AddHostedService<LogTailer>();
builder.Services.AddHostedService<ProcessWatcher>();

var host = builder.Build();
host.Run();
