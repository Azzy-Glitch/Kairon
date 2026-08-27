using System.Net.Http.Json;

namespace KAIRON.Agent;

public sealed class BackendAgentClient
{
    private readonly HttpClient _http;
    public BackendAgentClient(HttpClient http) => _http = http;

    public async Task<bool> RegisterAsync(MachineIdentity identity, CancellationToken cancellationToken)
    {
        var request = new RegistrationRequest(identity.MachineId, Environment.MachineName,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            typeof(BackendAgentClient).Assembly.GetName().Version?.ToString() ?? "1.0.0", identity.AgentKey);
        using var response = await _http.PostAsJsonAsync("api/agent/register", request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> HeartbeatAsync(MachineIdentity identity, IReadOnlyList<ProcessSnapshot> processes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"api/agent/machines/{identity.MachineId}/heartbeat")
        {
            Content = JsonContent.Create(new HeartbeatRequest(DateTime.UtcNow, processes))
        };
        request.Headers.Add("X-KAIRON-Agent-Key", identity.AgentKey);
        using var response = await _http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
