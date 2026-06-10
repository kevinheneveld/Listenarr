using System.Text.Json.Serialization;

namespace Listenarr.Domain.Models.Enumerations
{
    /// <summary>
    /// Identity-verification state of an audiobook (ADR-0001).
    /// Manual states (ManuallyVerified, Rejected) are sticky: an agent pass
    /// must never overwrite them — see <see cref="VerificationStatusExtensions.IsAgentWritable"/>.
    /// This acts as a DTO too for the API layer.
    /// </summary>
    public enum VerificationStatus
    {
        [JsonStringEnumMemberName("unverified")]
        Unverified = 0,
        [JsonStringEnumMemberName("agentVerified")]
        AgentVerified = 1,
        [JsonStringEnumMemberName("agentFlagged")]
        AgentFlagged = 2,
        [JsonStringEnumMemberName("manuallyVerified")]
        ManuallyVerified = 3,
        [JsonStringEnumMemberName("rejected")]
        Rejected = 4
    }

    public static class VerificationStatusExtensions
    {
        /// <summary>
        /// True when an agent verification pass is allowed to replace this status.
        /// Human decisions (ManuallyVerified, Rejected) outrank agent verdicts.
        /// </summary>
        public static bool IsAgentWritable(this VerificationStatus status) =>
            status is not (VerificationStatus.ManuallyVerified or VerificationStatus.Rejected);
    }
}
