using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace TermKit.Services
{
    public enum UpdateStatus { Failed, UpToDate, Available }

    public sealed class UpdateCheckResult
    {
        public UpdateStatus Status { get; init; }
        public string Message { get; init; } = string.Empty;
        public Version? LatestVersion { get; init; }
    }

    /// <summary>
    /// Reads the published .appinstaller feed and reports whether it advertises a newer package.
    /// Installing is left to Windows App Installer, which validates the package signature.
    /// </summary>
    public static class UpdateChecker
    {
        private const int MaxFeedBytes = 64 * 1024;
        private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(15) };

        public static bool TryGetFeedUri(string? feedUrl, out Uri uri)
        {
            uri = null!;
            return Uri.TryCreate(feedUrl, UriKind.Absolute, out var parsed)
                && parsed.Scheme == Uri.UriSchemeHttps
                && string.IsNullOrEmpty(parsed.UserInfo)
                && (uri = parsed) != null;
        }

        public static async Task<UpdateCheckResult> CheckAsync(string? feedUrl, string packageName, Version current, HttpClient? client = null, CancellationToken cancellationToken = default)
        {
            if (!TryGetFeedUri(feedUrl, out var feedUri))
                return Fail("The update feed must be an absolute HTTPS URL without credentials.");

            byte[] content;
            try
            {
                using var response = await (client ?? SharedClient).GetAsync(feedUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return Fail($"The update feed returned {(int)response.StatusCode} {response.ReasonPhrase}.");
                if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                    return Fail("The update feed redirected to a non-HTTPS address.");
                if (response.Content.Headers.ContentLength > MaxFeedBytes)
                    return Fail("The update feed is larger than expected.");
                content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (content.Length > MaxFeedBytes) return Fail("The update feed is larger than expected.");
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                return Fail($"Could not reach the update feed: {exception.Message}");
            }

            return Evaluate(content, packageName, current);
        }

        internal static UpdateCheckResult Evaluate(byte[] feed, string packageName, Version current)
        {
            XmlDocument document = new() { XmlResolver = null };
            try
            {
                using var stream = new MemoryStream(feed);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                document.Load(reader);
            }
            catch (XmlException exception)
            {
                return Fail($"The update feed is not valid XML: {exception.Message}");
            }

            var package = document.SelectSingleNode("/*[local-name()='AppInstaller']/*[local-name()='MainPackage']");
            var name = package?.Attributes?["Name"]?.Value;
            if (package == null || string.IsNullOrWhiteSpace(name)) return Fail("The update feed has no main package.");
            if (!string.Equals(name, packageName, StringComparison.Ordinal)) return Fail("The update feed belongs to a different package.");
            if (!Version.TryParse(package.Attributes?["Version"]?.Value, out var latest)) return Fail("The update feed has no valid package version.");

            return latest > current
                ? new UpdateCheckResult { Status = UpdateStatus.Available, LatestVersion = latest, Message = $"TermKit {latest} is available (current {current})." }
                : new UpdateCheckResult { Status = UpdateStatus.UpToDate, LatestVersion = latest, Message = $"TermKit {current} is up to date." };
        }

        private static UpdateCheckResult Fail(string message) => new() { Status = UpdateStatus.Failed, Message = message };
    }
}
