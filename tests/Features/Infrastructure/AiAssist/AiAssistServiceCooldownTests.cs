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
using Listenarr.Domain.Configuration;
using Listenarr.Infrastructure.AiAssist;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.AiAssist
{
    /// <summary>
    /// A dead AI endpoint must cost one timeout per cooldown window, not one per consumer
    /// call (live: 100s per automatic-search pick while the Ollama box was firewalled).
    /// </summary>
    [Trait("Name", "AiAssistServiceCooldownTests")]
    [Trait("Category", "AiAssist")]
    public class AiAssistServiceCooldownTests : BaseTests
    {
        private const string BaseUrl = "http://ai.example:11434/v1";

        private sealed class MutableTimeProvider : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => Now;
        }

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            public int Calls { get; private set; }
            public Func<CancellationToken, Task<HttpResponseMessage>> Script { get; set; } =
                _ => throw new HttpRequestException("Connection refused");

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                return Script(cancellationToken);
            }
        }

        private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

        private static IConfigurationService EnabledSettings()
        {
            var config = new Mock<IConfigurationService>();
            config.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings
            {
                AiAssistEnabled = true,
                AiAssistBaseUrl = BaseUrl,
                AiAssistModel = "test-model"
            });
            return config.Object;
        }

        private static (AiAssistService Service, ScriptedHandler Handler, MutableTimeProvider Clock, AiAssistEndpointHealth Health) Build(TimeSpan? clientTimeout = null)
        {
            var handler = new ScriptedHandler();
            var client = new HttpClient(handler);
            if (clientTimeout.HasValue) client.Timeout = clientTimeout.Value;
            var clock = new MutableTimeProvider();
            var health = new AiAssistEndpointHealth();
            var service = new AiAssistService(client, EnabledSettings(), NullLogger<AiAssistService>.Instance, health, clock);
            return (service, handler, clock, health);
        }

        [Fact]
        public async Task CompleteJsonAsync_UnreachableEndpoint_OpensCooldownAndSkipsFurtherCalls()
        {
            var (service, handler, clock, health) = Build();

            var first = await service.CompleteJsonAsync("sys", "user");
            var second = await service.CompleteJsonAsync("sys", "user");

            Assert.Null(first);
            Assert.Null(second);
            Assert.Equal(1, handler.Calls);
            Assert.True(health.IsUnavailable(BaseUrl, clock.Now));
        }

        [Fact]
        public async Task CompleteJsonAsync_AfterCooldown_TriesTheEndpointAgain()
        {
            var (service, handler, clock, _) = Build();
            await service.CompleteJsonAsync("sys", "user");

            clock.Now += AiAssistEndpointHealth.DefaultCooldown + TimeSpan.FromSeconds(1);
            await service.CompleteJsonAsync("sys", "user");

            Assert.Equal(2, handler.Calls);
        }

        [Fact]
        public async Task CompleteJsonAsync_RequestTimeout_AlsoOpensCooldown()
        {
            var (service, handler, clock, health) = Build(clientTimeout: TimeSpan.FromMilliseconds(100));
            handler.Script = async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            };

            var result = await service.CompleteJsonAsync("sys", "user");

            Assert.Null(result);
            Assert.True(health.IsUnavailable(BaseUrl, clock.Now));
        }

        [Fact]
        public async Task CompleteJsonAsync_SuccessAfterCooldown_ClearsIt()
        {
            var (service, handler, clock, health) = Build();
            await service.CompleteJsonAsync("sys", "user");
            clock.Now += AiAssistEndpointHealth.DefaultCooldown + TimeSpan.FromSeconds(1);
            handler.Script = _ => Task.FromResult(Ok("{\"choices\":[{\"message\":{\"content\":\"{\\\"ok\\\":true}\"}}]}"));

            var result = await service.CompleteJsonAsync("sys", "user");

            Assert.Equal("{\"ok\":true}", result);
            Assert.False(health.IsUnavailable(BaseUrl, clock.Now));
        }

        [Fact]
        public async Task TestConnectionAsync_Success_LiftsTheCooldown()
        {
            var (service, handler, clock, health) = Build();
            await service.CompleteJsonAsync("sys", "user");
            Assert.True(health.IsUnavailable(BaseUrl, clock.Now));
            handler.Script = _ => Task.FromResult(Ok("{\"model\":\"test-model\",\"choices\":[{\"message\":{\"content\":\"ok\"}}]}"));

            var probe = await service.TestConnectionAsync();
            await service.CompleteJsonAsync("sys", "user");

            Assert.True(probe.Ok);
            Assert.Equal(3, handler.Calls);
            Assert.False(health.IsUnavailable(BaseUrl, clock.Now));
        }

        [Fact]
        public void EndpointHealth_IsKeyedByBaseUrl()
        {
            var health = new AiAssistEndpointHealth();
            var now = DateTimeOffset.UtcNow;

            Assert.True(health.MarkUnavailable("http://a/v1", now));
            Assert.False(health.MarkUnavailable("http://a/v1", now), "a second failure inside the window must not re-open it");
            Assert.True(health.IsUnavailable("http://A/v1", now));
            Assert.False(health.IsUnavailable("http://b/v1", now));
            Assert.False(health.IsUnavailable("http://a/v1", now + AiAssistEndpointHealth.DefaultCooldown));
        }
    }
}
