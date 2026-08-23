using AIDIP.Backend.Services;

namespace AIDIP.Backend.Extensions;

public static class ServiceExtensions
{
    public static IServiceCollection AddAiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient<IAiMicroservice, AiMicroservice>(client =>
        {
            client.BaseAddress = new Uri(configuration["AiService:BaseUrl"] ?? "http://localhost:8000");
            client.Timeout = TimeSpan.FromSeconds(configuration.GetValue<int>("AiService:TimeoutSeconds", 30));
        });

        return services;
    }
}