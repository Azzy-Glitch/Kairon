using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// The address a pairing response hands to an SDK. The SDK persists it and sends its project API
/// key there for the life of the installation, so a centralized deployment that answers
/// "http://127.0.0.1:8000" produces an application that pairs successfully and then reports
/// telemetry into its own machine forever - while looking, from the operator's side, like it
/// worked. The desktop, which genuinely is a loopback product, must keep working unchanged.
/// </summary>
public sealed class PairingEndpointPolicyTests
{
    private static ProductOptions Public(string? url) =>
        new() { BackendUrl = url, RequirePublicBackendUrl = true };

    private static ProductOptions Local(string? url) =>
        new() { BackendUrl = url, RequirePublicBackendUrl = false };

    // --- The desktop, which is genuinely loopback ------------------------------------------

    [Fact]
    public void AnUnconfiguredDesktopGetsTheLoopbackAddressItActuallyBindsTo()
    {
        Assert.True(PairingEndpointPolicy.TryResolve(Local(null), out var endpoint, out var failure));

        Assert.Equal(PairingEndpointFailure.None, failure);
        Assert.Equal("http://127.0.0.1:8000", endpoint);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8000")]
    [InlineData("http://localhost:8000")]
    [InlineData("http://[::1]:8000")]
    public void ADesktopMayStillNameItsOwnLoopbackAddressExplicitly(string url)
    {
        Assert.True(PairingEndpointPolicy.TryResolve(Local(url), out var endpoint, out _));
        Assert.Equal(url, endpoint);
    }

    [Fact]
    public void ATrailingSlashIsNormalizedAwaySoTheSdkNeverBuildsADoubleSlashPath()
    {
        Assert.True(PairingEndpointPolicy.TryResolve(Public("https://kairon.example.com/"), out var endpoint, out _));
        Assert.Equal("https://kairon.example.com", endpoint);
    }

    // --- A centralized deployment ------------------------------------------------------------

    [Fact]
    public void ACentralizedDeploymentWithNoConfiguredUrlIsRefusedRatherThanGivenLoopback()
    {
        Assert.False(PairingEndpointPolicy.TryResolve(Public(null), out _, out var failure));
        Assert.Equal(PairingEndpointFailure.NotConfigured, failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankConfiguredUrlIsTreatedAsUnconfiguredNotAsAValidEmptyAddress(string url)
    {
        Assert.False(PairingEndpointPolicy.TryResolve(Public(url), out _, out var failure));
        Assert.Equal(PairingEndpointFailure.NotConfigured, failure);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8000")]
    [InlineData("https://127.0.0.1:8000")]
    [InlineData("https://localhost:8000")]
    [InlineData("https://sub.localhost")]
    [InlineData("https://[::1]:8000")]
    [InlineData("https://0.0.0.0:8000")]
    [InlineData("https://[::]:8000")]
    public void AnAddressThatOnlyNamesTheServersOwnMachineIsRefused(string url)
    {
        // Includes the bind-address forms: "0.0.0.0" is where this server listens, never somewhere
        // an SDK can send to.
        var resolved = PairingEndpointPolicy.TryResolve(Public(url), out _, out var failure);

        Assert.False(resolved);
        Assert.Contains(failure, new[]
        {
            PairingEndpointFailure.LoopbackNotReachable,
            PairingEndpointFailure.InsecureScheme
        });
    }

    [Theory]
    [InlineData("http://kairon.example.com")]
    [InlineData("ftp://kairon.example.com")]
    public void APlaintextOrNonHttpUrlIsRefusedForACentralizedDeployment(string url)
    {
        // The SDKs refuse a remote plaintext endpoint on their own, so pairing over one issues a
        // credential that can never deliver anything.
        Assert.False(PairingEndpointPolicy.TryResolve(Public(url), out _, out var failure));
        Assert.Equal(PairingEndpointFailure.InsecureScheme, failure);
    }

    [Fact]
    public void ASchemelessHostnameIsRefused()
    {
        Assert.False(PairingEndpointPolicy.TryResolve(Public("kairon.example.com"), out _, out var failure));
        Assert.Equal(PairingEndpointFailure.NotAbsolute, failure);
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("//kairon.example.com")]
    public void APathLikeOrProtocolRelativeUrlIsRefusedRegardlessOfPlatformSpecificUriParsing(string url)
    {
        // Uri.TryCreate's absolute-URI heuristics are platform-dependent for a string that also
        // looks like a filesystem path: confirmed via a real Linux CI run that "/api" parses as an
        // absolute file:// URI there (failing this policy's later https-only check, InsecureScheme)
        // while on Windows it fails to parse as absolute at all (NotAbsolute) - and "//host" already
        // had the mirror image of this problem (a Windows UNC-share parse, file://host/, with no
        // Linux equivalent). Which specific failure code either produces is therefore a platform
        // detail neither this policy nor this test should depend on; that both are refused is the
        // actual, platform-independent guarantee, so that is what this asserts.
        Assert.False(PairingEndpointPolicy.TryResolve(Public(url), out _, out var failure));
        Assert.NotEqual(PairingEndpointFailure.None, failure);
    }

    [Fact]
    public void EmbeddedCredentialsAreRefused()
    {
        // These would be persisted by the SDK and replayed on every request.
        Assert.False(PairingEndpointPolicy.TryResolve(
            Public("https://user:secret@kairon.example.com"), out _, out var failure));
        Assert.Equal(PairingEndpointFailure.ContainsUserInfo, failure);
    }

    [Fact]
    public void AQueryStringIsRefused()
    {
        // The SDK appends its own paths to this base, so a query here lands mid-URL.
        Assert.False(PairingEndpointPolicy.TryResolve(
            Public("https://kairon.example.com?tenant=a"), out _, out var failure));
        Assert.Equal(PairingEndpointFailure.ContainsQuery, failure);
    }

    [Fact]
    public void AFragmentIsRefused()
    {
        Assert.False(PairingEndpointPolicy.TryResolve(
            Public("https://kairon.example.com#anchor"), out _, out var failure));
        Assert.Equal(PairingEndpointFailure.ContainsFragment, failure);
    }

    [Theory]
    [InlineData("https://user:secret@127.0.0.1:8000", PairingEndpointFailure.ContainsUserInfo)]
    [InlineData("http://127.0.0.1:8000?x=1", PairingEndpointFailure.ContainsQuery)]
    [InlineData("http://127.0.0.1:8000#x", PairingEndpointFailure.ContainsFragment)]
    public void CredentialsQueriesAndFragmentsAreRefusedEvenOnTheLoopbackPath(
        string url, PairingEndpointFailure expected)
    {
        // None of these is ever legitimate in a base address, whatever the deployment shape.
        Assert.False(PairingEndpointPolicy.TryResolve(Local(url), out _, out var failure));
        Assert.Equal(expected, failure);
    }

    [Theory]
    [InlineData("https://kairon.example.com")]
    [InlineData("https://kairon.example.com:8443")]
    [InlineData("https://kairon.example.com/kairon")]
    [InlineData("https://203.0.113.5")]
    public void ARealPublicHttpsAddressIsAccepted(string url)
    {
        Assert.True(PairingEndpointPolicy.TryResolve(Public(url), out var endpoint, out var failure));
        Assert.Equal(PairingEndpointFailure.None, failure);
        Assert.Equal(url, endpoint);
    }

    [Fact]
    public void EveryFailureExplainsItselfWithoutEchoingTheConfiguredValue()
    {
        foreach (PairingEndpointFailure failure in Enum.GetValues<PairingEndpointFailure>())
        {
            var explanation = PairingEndpointPolicy.Explain(failure);
            Assert.False(string.IsNullOrWhiteSpace(explanation));
            Assert.Contains("Product:", explanation);
        }
    }
}

/// <summary>
/// The same policy at the point that actually matters: a pairing code is single-use and a
/// credential is real, revocable state. A deployment that cannot say where SDKs should send
/// telemetry must burn neither.
/// </summary>
public sealed class PairingEndpointRedemptionTests : IDisposable
{
    private readonly TestHarness _h = new();

    private SdkPairingService Service(ProductOptions product) => new(_h.Db,
        new ProjectCredentialService(_h.Db, TestHarness.Opt(new PlatformSecurityOptions()), TimeProvider.System,
            new PlatformAuditService(_h.Db, TimeProvider.System, NullLogger<PlatformAuditService>.Instance)),
        TimeProvider.System, TestHarness.Opt(product),
        new PlatformAuditService(_h.Db, TimeProvider.System, NullLogger<PlatformAuditService>.Instance),
        NullLogger<SdkPairingService>.Instance);

    private async Task<(Guid ProjectId, string Code)> MintAsync()
    {
        var project = new Project { Name = "test" };
        _h.Db.Projects.Add(project);
        await _h.Db.SaveChangesAsync();

        var created = await Service(new ProductOptions()).CreateAsync(project.Id, "python", default);
        return (project.Id, created!.Code);
    }

    [Fact]
    public async Task AMisconfiguredDeploymentBurnsNeitherTheCodeNorACredential()
    {
        var (projectId, code) = await MintAsync();
        var misconfigured = Service(new ProductOptions { RequirePublicBackendUrl = true, BackendUrl = null });

        Assert.Null(await misconfigured.RedeemAsync(code, "python", "1.1.0", default));

        // Nothing was consumed: no credential exists, and the session is still redeemable.
        Assert.False(await _h.Db.ProjectApiCredentials.AnyAsync(c => c.ProjectId == projectId));
        var session = await _h.Db.SdkPairingSessions.SingleAsync(s => s.ProjectId == projectId);
        Assert.Null(session.RedeemedAt);
        Assert.Null(session.IssuedCredentialId);
    }

    [Fact]
    public async Task TheSameCodeStillWorksOnceTheConfigurationIsFixed()
    {
        var (_, code) = await MintAsync();
        Assert.Null(await Service(new ProductOptions { RequirePublicBackendUrl = true }).RedeemAsync(code, "python", "1.1.0", default));

        var fixedUp = Service(new ProductOptions
        {
            RequirePublicBackendUrl = true,
            BackendUrl = "https://kairon.example.com"
        });
        var paired = await fixedUp.RedeemAsync(code, "python", "1.1.0", default);

        Assert.NotNull(paired);
        Assert.Equal("https://kairon.example.com", paired!.Endpoint);
    }

    [Fact]
    public async Task ACentralizedDeploymentNeverHandsAnSdkTheLoopbackAddress()
    {
        var (_, code) = await MintAsync();
        var centralized = Service(new ProductOptions
        {
            RequirePublicBackendUrl = true,
            BackendUrl = "https://kairon.example.com"
        });

        var paired = await centralized.RedeemAsync(code, "python", "1.1.0", default);

        Assert.NotNull(paired);
        Assert.DoesNotContain("127.0.0.1", paired!.Endpoint);
        Assert.DoesNotContain("localhost", paired.Endpoint);
    }

    [Fact]
    public async Task TheDesktopStillPairsAgainstItsOwnLoopbackBackend()
    {
        var (_, code) = await MintAsync();

        var paired = await Service(new ProductOptions()).RedeemAsync(code, "python", "1.1.0", default);

        Assert.NotNull(paired);
        Assert.Equal("http://127.0.0.1:8000", paired!.Endpoint);
    }

    public void Dispose() => _h.Dispose();
}
