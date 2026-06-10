/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Adapters;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Infrastructure.Adapters
{
    public class NzbgetAdapterTests
    {
        private const string VersionResponseXml =
            "<?xml version=\"1.0\"?><methodResponse><params><param><value><string>26.1</string></value></param></params></methodResponse>";

        private const string EmptyArrayResponseXml =
            "<?xml version=\"1.0\"?><methodResponse><params><param><value><array><data></data></array></value></param></params></methodResponse>";

        private sealed class TestHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _client;

            public TestHttpClientFactory(HttpClient client)
            {
                _client = client;
            }

            public HttpClient CreateClient(string name) => _client;
        }

        // Wrap the safe-redirect handler around a DelegatingHandlerMock so unit tests exercise
        // the production redirect pipeline. Matches how the named "nzbget" HttpClient is configured
        // in Program.cs / ServiceRegistrationExtensions.
        private static HttpClient BuildNzbgetClient(DelegatingHandlerMock leaf)
        {
            var redirectHandler = new NzbgetSafeRedirectHandler { InnerHandler = leaf };
            return new HttpClient(redirectHandler);
        }

        private static NzbgetAdapter BuildAdapter(HttpClient http) =>
            new(new TestHttpClientFactory(http), Mock.Of<INzbUrlResolver>(), NullLogger<NzbgetAdapter>.Instance);

        private static HttpResponseMessage OkXml(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        };

        private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri(location);
            return response;
        }

        [Fact]
        public async Task TestConnectionAsync_NormalizesHostWithSchemeAndPath()
        {
            Uri? capturedUri = null;
            var leaf = new DelegatingHandlerMock((req, _) =>
            {
                capturedUri = req.RequestUri;
                return Task.FromResult(OkXml(VersionResponseXml));
            });

            using var http = BuildNzbgetClient(leaf);
            var adapter = BuildAdapter(http);

            var client = new DownloadClientConfiguration
            {
                Host = "http://192.168.50.111/nzbget",
                Port = 6789,
                UseSSL = false,
                Username = "Talis",
                Password = "secret"
            };

            var (success, message) = await adapter.TestConnectionAsync(client);

            Assert.True(success);
            Assert.Contains("connected", message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(capturedUri);
            Assert.Equal("http", capturedUri!.Scheme);
            Assert.Equal("192.168.50.111", capturedUri.Host);
            Assert.Equal(6789, capturedUri.Port);
            Assert.Equal("/xmlrpc", capturedUri.AbsolutePath);
        }

        [Fact]
        public async Task TestConnectionAsync_PrefersExplicitPortAndSslOverEmbeddedHostUri()
        {
            Uri? capturedUri = null;
            var leaf = new DelegatingHandlerMock((req, _) =>
            {
                capturedUri = req.RequestUri;
                return Task.FromResult(OkXml(VersionResponseXml));
            });

            using var http = BuildNzbgetClient(leaf);
            var adapter = BuildAdapter(http);

            var client = new DownloadClientConfiguration
            {
                Host = "http://192.168.50.111:9999/legacy",
                Port = 6789,
                UseSSL = true
            };

            var (success, _) = await adapter.TestConnectionAsync(client);

            Assert.True(success);
            Assert.NotNull(capturedUri);
            Assert.Equal("https", capturedUri!.Scheme);
            Assert.Equal("192.168.50.111", capturedUri.Host);
            Assert.Equal(6789, capturedUri.Port);
            Assert.Equal("/xmlrpc", capturedUri.AbsolutePath);
        }

        [Fact]
        public async Task GetQueueAsync_NormalizesHostWithSchemeAndPath()
        {
            Uri? capturedUri = null;
            var leaf = new DelegatingHandlerMock((req, _) =>
            {
                capturedUri = req.RequestUri;
                return Task.FromResult(OkXml(EmptyArrayResponseXml));
            });

            using var http = BuildNzbgetClient(leaf);
            var adapter = BuildAdapter(http);

            var client = new DownloadClientConfiguration
            {
                Host = "http://192.168.50.111/nzbget",
                Port = 6789,
                UseSSL = false,
                Username = "Talis",
                Password = "secret"
            };

            var queue = await adapter.GetQueueAsync(client);

            Assert.NotNull(queue);
            Assert.Empty(queue);
            Assert.NotNull(capturedUri);
            Assert.Equal("http", capturedUri!.Scheme);
            Assert.Equal("192.168.50.111", capturedUri.Host);
            Assert.Equal(6789, capturedUri.Port);
            Assert.Equal("/xmlrpc", capturedUri.AbsolutePath);
        }

        // The XML-RPC URL must not embed credentials in UserInfo — the Authorization header is
        // the canonical auth path, and the NzbgetSafeRedirectHandler re-applies it on safe
        // redirects. URL-embedded creds would forward credentials through cross-host redirects
        // and bypass the redirect-stripping security control. See PR #580 review by T4g1.
        [Fact]
        public async Task TestConnectionAsync_DoesNotEmbedCredentialsInUrl()
        {
            HttpRequestMessage? capturedRequest = null;
            var leaf = new DelegatingHandlerMock((req, _) =>
            {
                capturedRequest = req;
                return Task.FromResult(OkXml(VersionResponseXml));
            });

            using var http = BuildNzbgetClient(leaf);
            var adapter = BuildAdapter(http);

            var client = new DownloadClientConfiguration
            {
                Host = "http://192.168.50.111",
                Port = 6789,
                UseSSL = false,
                Username = "nzbuser",
                Password = "n!zbP@ss"
            };

            var (success, _) = await adapter.TestConnectionAsync(client);

            Assert.True(success);
            Assert.NotNull(capturedRequest);
            Assert.True(
                string.IsNullOrEmpty(capturedRequest!.RequestUri!.UserInfo),
                "XML-RPC URL must not embed user:pass — Authorization header is the canonical auth path.");
        }

        [Fact]
        public async Task TestConnectionAsync_SendsBasicAuthorizationHeader()
        {
            HttpRequestMessage? capturedRequest = null;
            string? capturedBody = null;
            var leaf = new DelegatingHandlerMock(async (req, ct) =>
            {
                capturedRequest = req;
                if (req.Content != null)
                {
                    capturedBody = await req.Content.ReadAsStringAsync(ct);
                }
                return OkXml(VersionResponseXml);
            });

            using var http = BuildNzbgetClient(leaf);
            var adapter = BuildAdapter(http);

            var client = new DownloadClientConfiguration
            {
                Host = "http://192.168.50.111",
                Port = 6789,
                UseSSL = false,
                Username = "nzbuser",
                Password = "nzbpass"
            };

            var (success, _) = await adapter.TestConnectionAsync(client);

            Assert.True(success);
            Assert.NotNull(capturedRequest);

            var auth = capturedRequest!.Headers.Authorization;
            Assert.NotNull(auth);
            Assert.Equal("Basic", auth!.Scheme);
            Assert.False(string.IsNullOrEmpty(auth.Parameter));
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!));
            Assert.Equal("nzbuser:nzbpass", decoded);

            Assert.Equal("text/xml", capturedRequest.Content?.Headers.ContentType?.MediaType);
            Assert.NotNull(capturedBody);
            Assert.Contains("<methodName>version</methodName>", capturedBody!);
        }

        // Regression: when a reverse proxy in front of NZBGet issues a same-host 301/302 (e.g.
        // trailing-slash normalization), the default HttpClientHandler follows the redirect but
        // strips Authorization, so NZBGet rejects the request with 401 Unauthorized even though
        // creds are correct. NzbgetSafeRedirectHandler must re-apply the header on safe redirects.
        [Theory]
        [InlineData(HttpStatusCode.MovedPermanently)]   // 301
        [InlineData(HttpStatusCode.Found)]              // 302
        [InlineData(HttpStatusCode.SeeOther)]           // 303
        [InlineData(HttpStatusCode.TemporaryRedirect)]  // 307
        [InlineData(HttpStatusCode.PermanentRedirect)]  // 308
        public async Task TestConnectionAsync_ReAppliesAuthHeaderOnSameHostRedirect(HttpStatusCode redirectStatus)
        {
            var authsSeen = new List<AuthenticationHeaderValue?>();
            var pathsSeen = new List<string>();
            var hops = 0;
            var leaf = new DelegatingHandlerMock((req, _) =>
            {
                pathsSeen.Add(req.RequestUri!.AbsolutePath);
                authsSeen.Add(req.Headers.Authorization);
                hops++;
                if (hops == 1)
                {
                    return Task.FromResult(Redirect(redirectStatus, "http://192.168.50.111:6789/xmlrpc/"));
                }
                return Task.FromResult(OkXml(VersionResponseXml));
            });

            using var http = BuildNzbgetClient(leaf);
            var adapter = BuildAdapter(http);

            var client = new DownloadClientConfiguration
            {
                Host = "http://192.168.50.111",
                Port = 6789,
                UseSSL = false,
                Username = "nzbuser",
                Password = "nzbpass"
            };

            var (success, _) = await adapter.TestConnectionAsync(client);

            Assert.True(success, "Adapter should follow the same-host redirect and succeed");
            Assert.Equal(2, hops);
            Assert.Equal("/xmlrpc", pathsSeen[0]);
            Assert.Equal("/xmlrpc/", pathsSeen[1]);
            Assert.NotNull(authsSeen[0]);
            Assert.NotNull(authsSeen[1]);
            Assert.Equal("Basic", authsSeen[1]!.Scheme);
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authsSeen[1]!.Parameter!));
            Assert.Equal("nzbuser:nzbpass", decoded);
        }

        // Security: if NZBGet (or a misconfigured proxy) redirects to a different host, we must
        // NOT forward Authorization there. Throw with a clear message instead of silently dropping
        // auth so the misconfiguration surfaces obviously rather than as "Unauthorized".
        [Fact]
        public async Task TestConnectionAsync_BlocksCrossHostRedirectWithClearError()
        {
            var leaf = new DelegatingHandlerMock((req, _) =>
            {
                return Task.FromResult(Redirect(HttpStatusCode.Found, "http://attacker.example/xmlrpc"));
            });

            using var http = BuildNzbgetClient(leaf);
            var adapter = BuildAdapter(http);

            var client = new DownloadClientConfiguration
            {
                Host = "http://192.168.50.111",
                Port = 6789,
                UseSSL = false,
                Username = "nzbuser",
                Password = "nzbpass"
            };

            var (success, message) = await adapter.TestConnectionAsync(client);

            Assert.False(success);
            Assert.Contains("different host", message, StringComparison.OrdinalIgnoreCase);
        }

        // Security: an HTTPS->HTTP downgrade redirect would leak Basic creds in the clear.
        // Block with a clear message instead of following.
        [Fact]
        public async Task TestConnectionAsync_BlocksHttpsToHttpDowngradeRedirectWithClearError()
        {
            var leaf = new DelegatingHandlerMock((req, _) =>
            {
                return Task.FromResult(Redirect(HttpStatusCode.Found, "http://192.168.50.111:6789/xmlrpc"));
            });

            using var http = BuildNzbgetClient(leaf);
            var adapter = BuildAdapter(http);

            var client = new DownloadClientConfiguration
            {
                Host = "https://192.168.50.111",
                Port = 6789,
                UseSSL = true,
                Username = "nzbuser",
                Password = "nzbpass"
            };

            var (success, message) = await adapter.TestConnectionAsync(client);

            Assert.False(success);
            Assert.Contains("HTTPS", message);
            Assert.Contains("HTTP", message);
        }

        // Defence against a redirect loop in a misconfigured proxy — bail after the cap with a
        // clear error so the user knows what to investigate.
        [Fact]
        public async Task TestConnectionAsync_BailsOutOnRedirectLoopWithClearError()
        {
            var leaf = new DelegatingHandlerMock((req, _) =>
            {
                return Task.FromResult(Redirect(HttpStatusCode.Found, "http://192.168.50.111:6789/xmlrpc"));
            });

            using var http = BuildNzbgetClient(leaf);
            var adapter = BuildAdapter(http);

            var client = new DownloadClientConfiguration
            {
                Host = "http://192.168.50.111",
                Port = 6789,
                UseSSL = false,
                Username = "nzbuser",
                Password = "nzbpass"
            };

            var (success, message) = await adapter.TestConnectionAsync(client);

            Assert.False(success);
            Assert.Contains("redirect", message, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Regression coverage for Listenarrs/Listenarr#619: NZBGet's listgroups serializes
    /// FileSizeMB/RemainingSizeMB/NZBID as JSON numbers, and reading them with JsonElement.GetString()
    /// throws — which silently broke every progress update and stranded usenet downloads in Queued.
    /// </summary>
    public class NzbgetReadJsonNumberTests
    {
        private static System.Text.Json.JsonElement Prop(string json, string name)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.GetProperty(name).Clone();
        }

        [Fact]
        public void ReadJsonNumber_When_Field_Is_JsonNumber_Returns_Value()
        {
            // The live NZBGet shape: FileSizeMB is an integer, not a string.
            Assert.Equal(1516d, NzbgetAdapter.ReadJsonNumber(Prop("{\"FileSizeMB\":1516}", "FileSizeMB")));
        }

        [Fact]
        public void ReadJsonNumber_When_Field_Is_String_Returns_Value()
        {
            // Older/alternate builds may serialize the same field as a string — still handled.
            Assert.Equal(1234d, NzbgetAdapter.ReadJsonNumber(Prop("{\"RemainingSizeMB\":\"1234\"}", "RemainingSizeMB")));
        }

        [Fact]
        public void ReadJsonNumber_When_Field_Is_Decimal_Number_Returns_Value()
        {
            Assert.Equal(0.5d, NzbgetAdapter.ReadJsonNumber(Prop("{\"x\":0.5}", "x")));
        }

        [Fact]
        public void ReadJsonNumber_When_Field_Is_NonNumeric_Returns_Zero()
        {
            Assert.Equal(0d, NzbgetAdapter.ReadJsonNumber(Prop("{\"x\":\"notanumber\"}", "x")));
            Assert.Equal(0d, NzbgetAdapter.ReadJsonNumber(Prop("{\"x\":null}", "x")));
        }

        // History-reconciliation classification: NZBGet's dupe-deleted grabs (DELETED/COPY|GOOD) must
        // be blocked (terminal, no auto-search re-grab — which with FailedDownloadAutoSearch on would
        // just re-fetch a duplicate NZBGet rejects again), while genuine failures (FAILURE/*, bad/
        // unhealthy DELETEs) fail so the normal retry path applies. SUCCESS/* returns no-action here
        // because the reconcile loop completes successes directly (see ReadHistoryDir); non-terminal
        // states (WARNING/*) are genuinely left untouched.
        [Theory]
        [InlineData("DELETED/COPY")]
        [InlineData("DELETED/DUPE")]
        [InlineData("DELETED/GOOD")]
        [InlineData("DELETED/MANUAL")]
        [InlineData("DELETED/SCAN")]
        public void ClassifyHistoryStatus_DuplicateOrAmbiguousRemoval_Blocks(string status)
        {
            var (act, block, reason) = NzbgetAdapter.ClassifyNzbgetHistoryStatus(status);
            Assert.True(act);
            Assert.True(block);
            Assert.NotEmpty(reason);
        }

        [Theory]
        [InlineData("FAILURE/HEALTH")]
        [InlineData("FAILURE/PAR")]
        [InlineData("FAILURE/UNPACK")]
        [InlineData("DELETED/HEALTH")]
        [InlineData("DELETED/BAD")]
        public void ClassifyHistoryStatus_RealFailure_Fails(string status)
        {
            var (act, block, reason) = NzbgetAdapter.ClassifyNzbgetHistoryStatus(status);
            Assert.True(act);
            Assert.False(block);
            Assert.NotEmpty(reason);
        }

        [Theory]
        [InlineData("SUCCESS/ALL")]
        [InlineData("SUCCESS/UNPACK")]
        [InlineData("SUCCESS/HEALTH")]
        [InlineData("WARNING/HEALTH")]
        [InlineData("")]
        [InlineData("DOWNLOADING")]
        public void ClassifyHistoryStatus_SuccessOrNonTerminal_NoAction(string status)
        {
            var (act, block, _) = NzbgetAdapter.ClassifyNzbgetHistoryStatus(status);
            Assert.False(act);
            Assert.False(block);
        }

        private static System.Text.Json.JsonElement Obj(string json)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        // ReadHistoryDir feeds DownloadPath when completing a SUCCESS grab from history: FinalDir is
        // the post-processed location and wins; DestDir is the pre-move fallback; null when neither is
        // a usable string so the caller leaves any existing path untouched.
        [Fact]
        public void ReadHistoryDir_PrefersFinalDir_FallsBackToDestDir()
        {
            Assert.Equal("/done/Book", NzbgetAdapter.ReadHistoryDir(
                Obj("{\"FinalDir\":\"/done/Book\",\"DestDir\":\"/inter/Book\"}")));
            Assert.Equal("/inter/Book", NzbgetAdapter.ReadHistoryDir(
                Obj("{\"FinalDir\":\"\",\"DestDir\":\"/inter/Book\"}")));
            Assert.Equal("/inter/Book", NzbgetAdapter.ReadHistoryDir(
                Obj("{\"DestDir\":\"/inter/Book\"}")));
        }

        [Fact]
        public void ReadHistoryDir_ReturnsNull_WhenNoUsableDir()
        {
            Assert.Null(NzbgetAdapter.ReadHistoryDir(Obj("{}")));
            Assert.Null(NzbgetAdapter.ReadHistoryDir(Obj("{\"FinalDir\":\"   \",\"DestDir\":\"\"}")));
            Assert.Null(NzbgetAdapter.ReadHistoryDir(Obj("{\"FinalDir\":null,\"DestDir\":null}")));
        }
    }
}
