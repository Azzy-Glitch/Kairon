using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace KAIRON.Agent;

public sealed record MachineIdentity(Guid MachineId, string AgentKey);

public sealed class MachineIdentityStore
{
    private readonly string _path;

    public MachineIdentityStore(IOptions<AgentOptions> options)
    {
        _path = string.IsNullOrWhiteSpace(options.Value.IdentityPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KAIRON", "config", "agent-identity.json")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.Value.IdentityPath));
    }

    public async Task<MachineIdentity> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_path))
        {
            await using var input = File.OpenRead(_path);
            var existing = await JsonSerializer.DeserializeAsync<MachineIdentity>(input, cancellationToken: cancellationToken);
            if (existing is null || existing.MachineId == Guid.Empty || string.IsNullOrWhiteSpace(existing.AgentKey))
                throw new InvalidDataException("KAIRON Agent identity is invalid.");
            return existing;
        }

        var identity = new MachineIdentity(Guid.NewGuid(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Agent identity path is invalid.");
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(output, identity, cancellationToken: cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        File.Move(temporary, _path, overwrite: false);
        return identity;
    }
}
