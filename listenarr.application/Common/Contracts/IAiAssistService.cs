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

namespace Listenarr.Application.Common.Contracts
{
    /// <summary>
    /// Optional OpenAI-compatible chat-completions endpoint (Ollama, LM
    /// Studio, OpenRouter, …) for fuzzy text judgments deterministic code
    /// handles poorly. This is a cross-cutting primitive: consumers build
    /// their own prompts and parse their own responses; this service only
    /// speaks the wire protocol.
    ///
    /// Consumers MUST treat null results as "no opinion" and keep their
    /// deterministic behavior — the endpoint may live on a machine that
    /// sleeps, so absence is an expected steady state, never an error path.
    /// </summary>
    public interface IAiAssistService
    {
        /// <summary>Enabled in settings with a non-empty base URL and model.</summary>
        Task<bool> IsConfiguredAsync(CancellationToken ct = default);

        /// <summary>
        /// One-shot completion expected to yield a JSON object. Returns the
        /// raw assistant text (which callers parse leniently) or null on any
        /// failure: disabled, unreachable, timeout, or a non-2xx response.
        /// </summary>
        Task<string?> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);

        /// <summary>Settings-page probe: is the configured endpoint reachable and does the model answer?</summary>
        Task<AiAssistTestResult> TestConnectionAsync(CancellationToken ct = default);
    }

    public sealed record AiAssistTestResult(bool Ok, string Detail);
}
