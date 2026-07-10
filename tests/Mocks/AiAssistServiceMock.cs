using Listenarr.Application.Common.Contracts;

namespace Listenarr.Tests.Mocks
{
    /// <summary>
    /// AI-assist stub for tests: unconfigured by default (the production
    /// default), so every consumer exercises its deterministic path. Tests
    /// exercising the AI pass set Configured=true and a canned Response.
    /// </summary>
    public class AiAssistServiceMock : IAiAssistService
    {
        public bool Configured { get; set; } = false;
        public string? Response { get; set; } = null;

        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(Configured);

        public Task<string?> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
            => Task.FromResult(Configured ? Response : null);

        public Task<AiAssistTestResult> TestConnectionAsync(CancellationToken ct = default)
            => Task.FromResult(new AiAssistTestResult(Configured, Configured ? "mock" : "not configured"));
    }
}
