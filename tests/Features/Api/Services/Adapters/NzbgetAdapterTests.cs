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
using System.Text;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Adapters;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services.Adapters
{
    public class NzbgetAdapterTests
    {
        private sealed class TestHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _client;

            public TestHttpClientFactory(HttpClient client)
            {
                _client = client;
            }

            public HttpClient CreateClient(string name) => _client;
        }

        [Fact]
        public async Task TestConnectionAsync_NormalizesHostWithSchemeAndPath()
        {
            Uri? capturedUri = null;
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<?xml version=\"1.0\"?><methodResponse><params><param><value><string>25.4</string></value></param></params></methodResponse>")
            };
            var handler = new DelegatingHandlerMock((req, _) =>
            {
                capturedUri = req.RequestUri;
                return Task.FromResult(response);
            });

            using var http = new HttpClient(handler);
            var adapter = new NzbgetAdapter(
                new TestHttpClientFactory(http),
                Mock.Of<INzbUrlResolver>(),
                NullLogger<NzbgetAdapter>.Instance);

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
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<?xml version=\"1.0\"?><methodResponse><params><param><value><string>25.4</string></value></param></params></methodResponse>")
            };
            var handler = new DelegatingHandlerMock((req, _) =>
            {
                capturedUri = req.RequestUri;
                return Task.FromResult(response);
            });

            using var http = new HttpClient(handler);
            var adapter = new NzbgetAdapter(
                new TestHttpClientFactory(http),
                Mock.Of<INzbUrlResolver>(),
                NullLogger<NzbgetAdapter>.Instance);

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
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<?xml version=\"1.0\"?><methodResponse><params><param><value><array><data></data></array></value></param></params></methodResponse>")
            };
            var handler = new DelegatingHandlerMock((req, _) =>
            {
                capturedUri = req.RequestUri;
                return Task.FromResult(response);
            });

            using var http = new HttpClient(handler);
            var adapter = new NzbgetAdapter(
                new TestHttpClientFactory(http),
                Mock.Of<INzbUrlResolver>(),
                NullLogger<NzbgetAdapter>.Instance);

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

        // Regression: NZBGet's XML-RPC endpoint authenticates via credentials embedded in the URL
        // (http://user:pass@host/xmlrpc). Prior to the fix, BuildUri was called without
        // includeCredentials, so the URL UserInfo was empty and NZBGet returned 401 Unauthorized
        // — even though credentials were configured — whenever the server's auth path required
        // URL-embedded creds (or the Authorization header was lost across a redirect).
        [Fact]
        public async Task TestConnectionAsync_EmbedsCredentialsInXmlRpcUrl()
        {
            HttpRequestMessage? capturedRequest = null;
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<?xml version=\"1.0\"?><methodResponse><params><param><value><string>26.1</string></value></param></params></methodResponse>")
            };
            var handler = new DelegatingHandlerMock((req, _) =>
            {
                capturedRequest = req;
                return Task.FromResult(response);
            });

            using var http = new HttpClient(handler);
            var adapter = new NzbgetAdapter(
                new TestHttpClientFactory(http),
                Mock.Of<INzbUrlResolver>(),
                NullLogger<NzbgetAdapter>.Instance);

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

            var uri = capturedRequest!.RequestUri!;
            Assert.Equal("/xmlrpc", uri.AbsolutePath);
            Assert.False(string.IsNullOrEmpty(uri.UserInfo), "Expected XML-RPC URL to carry user:pass in UserInfo");
            // UriBuilder URL-encodes username/password — decode before comparing
            var parts = uri.UserInfo.Split(':', 2);
            Assert.Equal(2, parts.Length);
            Assert.Equal("nzbuser", Uri.UnescapeDataString(parts[0]));
            Assert.Equal("n!zbP@ss", Uri.UnescapeDataString(parts[1]));
        }

        [Fact]
        public async Task TestConnectionAsync_SendsBasicAuthorizationHeader()
        {
            HttpRequestMessage? capturedRequest = null;
            string? capturedBody = null;
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<?xml version=\"1.0\"?><methodResponse><params><param><value><string>26.1</string></value></param></params></methodResponse>")
            };
            var handler = new DelegatingHandlerMock(async (req, ct) =>
            {
                capturedRequest = req;
                if (req.Content != null)
                {
                    capturedBody = await req.Content.ReadAsStringAsync(ct);
                }
                return response;
            });

            using var http = new HttpClient(handler);
            var adapter = new NzbgetAdapter(
                new TestHttpClientFactory(http),
                Mock.Of<INzbUrlResolver>(),
                NullLogger<NzbgetAdapter>.Instance);

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
    }
}
