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
    public void SampleConfigIsDetectedButRealDomainIsNot()
    {
        Assert.False(AdControllerDiscovery.IsSamplePlaceholder(Config()));
        Assert.True(AdControllerDiscovery.IsSamplePlaceholder(new ActiveDirectoryConfig { DomainNetbios = "CONTOSO", PrimaryDomainController = "dc01.contoso.org" }));
        Assert.True(AdControllerDiscovery.IsSamplePlaceholder(new ActiveDirectoryConfig { DomainNetbios = "REAL", PrimaryDomainController = "dc01.contoso.org" }));
    }

    [Fact]
    public void PlaceholderLocalConfigIsReplacedByRealBundledConfigAndBackedUp()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var local = Path.Combine(dir, "local.json");
            var bundled = Path.Combine(dir, "bundled.json");
            File.WriteAllText(local, "{}");
            File.WriteAllText(bundled, "{\"Settings\":{\"ActiveDirectory\":{\"DomainNetbios\":\"REAL\",\"PrimaryDomainController\":\"dc01.real.org\"}}}");
            var sample = new ToolConfig { Settings = new SettingsConfig { ActiveDirectory = new ActiveDirectoryConfig { DomainNetbios = "CONTOSO", PrimaryDomainController = "dc01.contoso.org" } } };

            var adopted = ConfigService.TryAdoptBundledConfig(sample, local, bundled);

            Assert.Equal("REAL", adopted?.Settings.ActiveDirectory?.DomainNetbios);
            Assert.Equal("{}", File.ReadAllText(local + ".bak"));
            Assert.Contains("dc01.real.org", File.ReadAllText(local));
            Assert.Null(ConfigService.TryAdoptBundledConfig(adopted!, local, bundled));
            File.WriteAllText(bundled, "{\"Settings\":{\"ActiveDirectory\":{\"DomainNetbios\":\"CONTOSO\"}}}");
            Assert.Null(ConfigService.TryAdoptBundledConfig(sample, local, bundled));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void DnsDomainsComeFromControllerFqdnsAndDomainDns()
    {
        var domains = AdControllerDiscovery.GetDnsDomains(Config());
        Assert.Equal(new[] { "corp.local", "corp.org" }, domains);
    }
}
