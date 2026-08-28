using System.Net;
using Xunit;

namespace AIDIP.SDK.Tests;

public sealed class PairingClientTests
{
    [Fact]
    public async Task UnavailableBackendIsContained()
    {
        var result = await AIDIPPairingClient.PairAsync("http://127.0.0.1:1", "pair_invalid");

        Assert.False(result.Success);
        Assert.Null(result.Credential);
    }
}
