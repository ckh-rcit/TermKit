using System.Net;
using System.Text;
using TermKit.Services;
using Xunit;

namespace TermKit.Tests;

public sealed class UpdateCheckerTests
{
    private const string Package = "87ce6be6-f7db-4d56-8aac-5f3e2c2f9f7f";

    private static string Feed(string version, string name = Package) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><AppInstaller xmlns=\"http://schemas.microsoft.com/appx/appinstaller/2018\" Uri=\"https://example.github.io/TermKit/TermKit.appinstaller\" Version=\""
        + version + "\"><MainPackage Name=\"" + name + "\" Publisher=\"CN=CKH-RCIT\" Version=\"" + version + "\" ProcessorArchitecture=\"x64\" Uri=\"https://example.com/TermKit.msix\" /></AppInstaller>";

    private sealed class FeedHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;
        public int Calls { get; private set; }
        public FeedHandler(Func<HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = _respond();
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    private static HttpResponseMessage Ok(string body) => Ok(Encoding.UTF8.GetBytes(body));

    private static Task<UpdateCheckResult> Check(FeedHandler handler, string version = "1.0.20.0", string feed = "https://example.github.io/TermKit/TermKit.appinstaller") =>
        UpdateChecker.CheckAsync(feed, Package, Version.Parse(version), new HttpClient(handler));

    [Fact]
    public async Task NewerFeedVersionIsAvailable()
    {
        var result = await Check(new FeedHandler(() => Ok(Feed("1.0.21.0"))));
        Assert.Equal(UpdateStatus.Available, result.Status);
        Assert.Equal(new Version(1, 0, 21, 0), result.LatestVersion);
    }

    [Theory]
    [InlineData("1.0.20.0")]
    [InlineData("1.0.19.0")]
    public async Task SameOrOlderFeedVersionIsUpToDate(string feedVersion)
    {
        var result = await Check(new FeedHandler(() => Ok(Feed(feedVersion))));
        Assert.Equal(UpdateStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task FeedWithByteOrderMarkIsParsed()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Feed("1.0.21.0"))).ToArray();
        var result = await Check(new FeedHandler(() => Ok(bytes)));
        Assert.Equal(UpdateStatus.Available, result.Status);
    }

    [Fact]
    public async Task FeedForAnotherPackageIsRejected()
    {
        var result = await Check(new FeedHandler(() => Ok(Feed("9.0.0.0", "someone-elses-package"))));
        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Contains("different package", result.Message);
    }

    [Theory]
    [InlineData("http://example.github.io/TermKit.appinstaller")]
    [InlineData("https://user:pass@example.github.io/TermKit.appinstaller")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task UnsafeFeedUrlsNeverTouchTheNetwork(string feed)
    {
        var handler = new FeedHandler(() => Ok(Feed("1.0.21.0")));
        var result = await Check(handler, feed: feed);
        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("<AppInstaller")]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY a \"b\">]><AppInstaller />")]
    [InlineData("<AppInstaller xmlns=\"http://schemas.microsoft.com/appx/appinstaller/2018\" />")]
    public async Task MalformedOrDtdFeedsFailClosed(string body)
    {
        var result = await Check(new FeedHandler(() => Ok(body)));
        Assert.Equal(UpdateStatus.Failed, result.Status);
    }

    [Fact]
    public async Task HttpErrorsAreReportedNotThrown()
    {
        var result = await Check(new FeedHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Contains("404", result.Message);
    }

    [Fact]
    public async Task OversizedFeedIsRejected()
    {
        var result = await Check(new FeedHandler(() => Ok(new string('x', 200_000))));
        Assert.Equal(UpdateStatus.Failed, result.Status);
    }

    [Fact]
    public async Task UnparseableVersionIsRejected()
    {
        var result = await Check(new FeedHandler(() => Ok(Feed("latest"))));
        Assert.Equal(UpdateStatus.Failed, result.Status);
    }
}
