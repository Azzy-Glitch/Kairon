using KAIRON.Agent;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // Windows Services normally start from System32. Configuration must follow the installed
    // Agent executable, not the caller's working directory.
    ContentRootPath = AppContext.BaseDirectory
});
builder.Services.AddWindowsService(options => options.ServiceName = "KAIRON Agent");
// Do not implicitly bind the desktop Agent to Windows Event Log permissions. Packaging can add a
// controlled sink later; the worker itself must run safely as an ordinary user and as a service.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.AddSingleton<MachineIdentityStore>();
builder.Services.AddSingleton<ProcessCollector>();
builder.Services.AddHttpClient<BackendAgentClient>((provider, client) =>
{
    var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
    client.BaseAddress = new Uri(options.BackendEndpoint.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 1, 30));
});
builder.Services.AddHostedService<AgentWorker>();
await builder.Build().RunAsync();
