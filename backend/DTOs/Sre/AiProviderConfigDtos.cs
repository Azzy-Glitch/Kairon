using System.Text.Json.Serialization;

namespace Kairon.Backend.DTOs.Sre;

// Wire contract for the AI service's runtime provider-configuration routes (/configure,
// /configure/test, /models) - the transport backing the frontend AI Configuration panel.
// snake_case on the wire, matching every other AI-service DTO in this folder.

public class AiConfigureRequestDto
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("api_key")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }
}

public class AiConfigureResponseDto
{
    [JsonPropertyName("applied")]
    public bool Applied { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("effective_provider")]
    public string EffectiveProvider { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;
}

public class AiTestConnectionResponseDto
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("effective_provider")]
    public string EffectiveProvider { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class AiModelsResponseDto
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("supported")]
    public bool Supported { get; set; }

    [JsonPropertyName("models")]
    public List<string> Models { get; set; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
