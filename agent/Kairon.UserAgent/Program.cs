using Kairon.UserAgent;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<UserAgentOptions>(builder.Configuration.GetSection("UserAgent"));

builder.Services.AddHttpClient<SessionProcessCollector>((sp, http) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<UserAgentOptions>>().Value;
    // Validated here (config/environment) AND again at actual send time in
    // SessionProcessCollector itself - never trusted only once.
    AgentEndpointSecurity.EnsureAllowed(options.Endpoint);
    http.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(Math.Max(2, options.TimeoutSeconds + 1));
})
    .ConfigurePrimaryHttpMessageHandler(AgentEndpointSecurity.CreateNonRedirectingHandler);

builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionProcessCollector>());

var host = builder.Build();
host.Run();
