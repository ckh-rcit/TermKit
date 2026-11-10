using TermKit.Services;
using Xunit;

namespace TermKit.Tests;

public sealed class AdControllerDiscoveryTests
{
    private static ActiveDirectoryConfig Config() => new()
    {
        DomainNetbios = "CORP",
        DomainDNS = "corp.org",
        PrimaryDomainController = "dc01.corp.local",
        FallbackDomainControllers = new List<string> { "DC01.corp.local", " dc02.corp.local ", "10.0.0.5", "" }
    };

    [Fact]
    public void CandidatesAreTrimmedDeduplicatedAndConfiguredFirst()
    {
        var list = AdControllerDiscovery.BuildCandidates(Config(), new[] { "dc03.corp.local.", "dc02.corp.local" });
        Assert.Equal(new[] { "dc01.corp.local", "dc02.corp.local", "10.0.0.5", "dc03.corp.local" }, list);
    }

    [Fact]
    public void DnsDomainsComeFromControllerFqdnsAndDomainDns()
    {
        var domains = AdControllerDiscovery.GetDnsDomains(Config());
        Assert.Equal(new[] { "corp.local", "corp.org" }, domains);
    }
}
