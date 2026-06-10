using System.Text.Json.Serialization;

namespace Listenarr.Domain.Models.Enumerations
{
    /// <summary>
    /// Aggregate outcome of an identity-verification pass (ADR-0001).
    /// Maps onto VerificationStatus: Match → AgentVerified, Mismatch → AgentFlagged,
    /// Uncertain → AgentFlagged-for-review (surfaced in the "Needs review" view).
    /// This acts as a DTO too for the API layer.
    /// </summary>
    public enum VerificationOutcome
    {
        [JsonStringEnumMemberName("uncertain")]
        Uncertain = 0,
        [JsonStringEnumMemberName("match")]
        Match = 1,
        [JsonStringEnumMemberName("mismatch")]
        Mismatch = 2
    }
}
