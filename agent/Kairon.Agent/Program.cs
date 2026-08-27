using Kairon.Agent;
using Kairon.Agent.LogTailing;
using Kairon.Agent.ProcessWatch;

var builder = Host.CreateApplicationBuilder(args);

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
