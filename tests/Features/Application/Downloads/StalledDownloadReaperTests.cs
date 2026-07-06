using Listenarr.Application.Downloads;
using Listenarr.Domain.Downloads;
using Xunit;

namespace Listenarr.Tests.Features.Application.Downloads
{
    [Trait("Name", "StalledDownloadReaperTests")]
    [Trait("Category", "StalledDownloadReaper")]
    public class StalledDownloadReaperTests
    {
        private static readonly DateTime Now = new(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc);

        [Theory]
        [InlineData(DownloadStatus.Queued, true)]
        [InlineData(DownloadStatus.Downloading, true)]
        [InlineData(DownloadStatus.Paused, false)]
        [InlineData(DownloadStatus.Completed, false)]
        [InlineData(DownloadStatus.Processing, false)]
        [InlineData(DownloadStatus.ImportPending, false)]
        [InlineData(DownloadStatus.ImportBlocked, false)]
        [InlineData(DownloadStatus.Moved, false)]
        [InlineData(DownloadStatus.Failed, false)]
        [Trait("Method", "IsReapEligibleStatus")]
        public void IsReapEligibleStatus_OnlyQueuedAndDownloading(DownloadStatus status, bool expected)
        {
            Assert.Equal(expected, StalledDownloadReaper.IsReapEligibleStatus(status));
        }

        [Fact]
        [Trait("Method", "Evaluate")]
        public void Evaluate_FirstObservation_SeedsSnapshotAndDoesNotReap()
        {
            var result = StalledDownloadReaper.Evaluate(
                DownloadStatus.Downloading, currentProgress: 6.58m,
                previousSnapshot: null, now: Now, timeoutMinutes: 60);

            Assert.False(result.ShouldReap);
            Assert.Equal(6.58m, result.SnapshotProgress);
            Assert.Equal(Now, result.SnapshotAt);
        }

        [Fact]
        [Trait("Method", "Evaluate")]
        public void Evaluate_ProgressUnchangedWithinTimeout_DoesNotReap_PreservesWindowStart()
        {
            var windowStart = Now.AddMinutes(-30);
            var result = StalledDownloadReaper.Evaluate(
                DownloadStatus.Downloading, currentProgress: 6.58m,
                previousSnapshot: (6.58m, windowStart), now: Now, timeoutMinutes: 60);

            Assert.False(result.ShouldReap);
            // The window start must be preserved so the stall keeps accumulating across polls.
            Assert.Equal(windowStart, result.SnapshotAt);
            Assert.Equal(6.58m, result.SnapshotProgress);
        }

        [Fact]
        [Trait("Method", "Evaluate")]
        public void Evaluate_ProgressUnchangedPastTimeout_Reaps()
        {
            var windowStart = Now.AddMinutes(-61);
            var result = StalledDownloadReaper.Evaluate(
                DownloadStatus.Queued, currentProgress: 6.58m,
                previousSnapshot: (6.58m, windowStart), now: Now, timeoutMinutes: 60);

            Assert.True(result.ShouldReap);
            Assert.Equal(windowStart, result.SnapshotAt);
        }

        [Fact]
        [Trait("Method", "Evaluate")]
        public void Evaluate_ProgressAdvanced_ResetsWindowAndDoesNotReap()
        {
            var windowStart = Now.AddMinutes(-120);
            // Even one fractional byte of progress resets the stall window: a slow-but-alive
            // download must never be reaped.
            var result = StalledDownloadReaper.Evaluate(
                DownloadStatus.Downloading, currentProgress: 6.59m,
                previousSnapshot: (6.58m, windowStart), now: Now, timeoutMinutes: 60);

            Assert.False(result.ShouldReap);
            Assert.Equal(6.59m, result.SnapshotProgress);
            Assert.Equal(Now, result.SnapshotAt);
        }

        [Fact]
        [Trait("Method", "Evaluate")]
        public void Evaluate_ExactlyAtTimeout_Reaps()
        {
            var windowStart = Now.AddMinutes(-60);
            var result = StalledDownloadReaper.Evaluate(
                DownloadStatus.Downloading, currentProgress: 0m,
                previousSnapshot: (0m, windowStart), now: Now, timeoutMinutes: 60);

            Assert.True(result.ShouldReap);
        }

        [Fact]
        [Trait("Method", "Evaluate")]
        public void Evaluate_CompleteProgress_NeverReaps()
        {
            var windowStart = Now.AddMinutes(-1000);
            var result = StalledDownloadReaper.Evaluate(
                DownloadStatus.Downloading, currentProgress: 100m,
                previousSnapshot: (100m, windowStart), now: Now, timeoutMinutes: 60);

            Assert.False(result.ShouldReap);
            Assert.Equal(Now, result.SnapshotAt);
        }

        [Fact]
        [Trait("Method", "Evaluate")]
        public void Evaluate_IneligibleStatus_NeverReaps()
        {
            var windowStart = Now.AddMinutes(-1000);
            var result = StalledDownloadReaper.Evaluate(
                DownloadStatus.Paused, currentProgress: 6.58m,
                previousSnapshot: (6.58m, windowStart), now: Now, timeoutMinutes: 60);

            Assert.False(result.ShouldReap);
            Assert.Equal(Now, result.SnapshotAt);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        [Trait("Method", "Evaluate")]
        public void Evaluate_NonPositiveTimeout_DisablesReaping(int timeout)
        {
            var windowStart = Now.AddMinutes(-1000);
            var result = StalledDownloadReaper.Evaluate(
                DownloadStatus.Downloading, currentProgress: 6.58m,
                previousSnapshot: (6.58m, windowStart), now: Now, timeoutMinutes: timeout);

            Assert.False(result.ShouldReap);
        }

        // ---- client-queued guard (don't reap items waiting in the client's own queue) ----

        [Theory]
        [InlineData("QUEUED")]      // NZBGet waiting in queue (processes serially)
        [InlineData("queued")]      // case-insensitive
        [InlineData("PP_QUEUED")]   // NZBGet post-processing queue
        [InlineData("queuedDL")]    // qBittorrent queued behind active-torrent limit
        [InlineData("queuedUP")]
        [Trait("Method", "Evaluate")]
        public void Evaluate_ClientQueued_NeverReaps_EvenPastTimeout(string clientState)
        {
            // Would normally reap (no progress for 1000 min), but the client says it's just waiting.
            var windowStart = Now.AddMinutes(-1000);
            var result = StalledDownloadReaper.Evaluate(
                DownloadStatus.Queued, currentProgress: 0m,
                previousSnapshot: (0m, windowStart), now: Now, timeoutMinutes: 60,
                clientState: clientState);

            Assert.False(result.ShouldReap);
            Assert.Equal(Now, result.SnapshotAt); // snapshot kept fresh so the timer starts clean later
        }

        [Theory]
        [InlineData("metaDL")]      // fetching metadata — genuinely dead/stuck, still reapable
        [InlineData("stalledDL")]   // no peers — reapable once past timeout
        [InlineData("downloading")]
        [InlineData(null)]
        [InlineData("")]
        [Trait("Method", "Evaluate")]
        public void Evaluate_NonQueuedClientState_PastTimeout_StillReaps(string? clientState)
        {
            var windowStart = Now.AddMinutes(-61);
            var result = StalledDownloadReaper.Evaluate(
                DownloadStatus.Downloading, currentProgress: 0m,
                previousSnapshot: (0m, windowStart), now: Now, timeoutMinutes: 60,
                clientState: clientState);

            Assert.True(result.ShouldReap);
        }

        [Theory]
        [InlineData("QUEUED", true)]
        [InlineData("queueddl", true)]
        [InlineData("metaDL", false)]
        [InlineData("downloading", false)]
        [InlineData(null, false)]
        [InlineData("  ", false)]
        [Trait("Method", "IsClientQueued")]
        public void IsClientQueued_DetectsWaitingStates(string? state, bool expected)
        {
            Assert.Equal(expected, StalledDownloadReaper.IsClientQueued(state));
        }

        // ---- StalledReaperOptions.From ----

        [Fact]
        [Trait("Method", "FromEnvironment")]
        public void Options_Defaults_AreSafe()
        {
            var options = StalledReaperOptions.From(_ => null);

            Assert.False(options.Enabled);    // off unless explicitly enabled
            Assert.True(options.DryRun);      // dry-run until explicitly disarmed
            Assert.False(options.DeleteFiles); // leave partial files on disk unless opted in
            Assert.Equal(StalledReaperOptions.DefaultTimeoutMinutes, options.TimeoutMinutes);
        }

        [Fact]
        [Trait("Method", "FromEnvironment")]
        public void Options_ArmedConfiguration_Parses()
        {
            var vars = new Dictionary<string, string?>
            {
                [StalledReaperOptions.EnabledEnv] = "true",
                [StalledReaperOptions.DryRunEnv] = "false",
                [StalledReaperOptions.TimeoutMinutesEnv] = "30",
                [StalledReaperOptions.DeleteFilesEnv] = "1", // opt in to deleting partials
            };

            var options = StalledReaperOptions.From(k => vars.GetValueOrDefault(k));

            Assert.True(options.Enabled);
            Assert.False(options.DryRun);
            Assert.Equal(30, options.TimeoutMinutes);
            Assert.True(options.DeleteFiles);
        }

        [Theory]
        [InlineData("0", false)]
        [InlineData("1", true)]
        [InlineData("TRUE", true)]
        [InlineData("False", false)]
        [Trait("Method", "FromEnvironment")]
        public void Options_BoolParsing_AcceptsCommonForms(string raw, bool expectedEnabled)
        {
            var options = StalledReaperOptions.From(k =>
                k == StalledReaperOptions.EnabledEnv ? raw : null);

            Assert.Equal(expectedEnabled, options.Enabled);
        }

        [Theory]
        [InlineData("notanumber")]
        [InlineData("0")]
        [InlineData("-10")]
        [InlineData("")]
        [Trait("Method", "FromEnvironment")]
        public void Options_InvalidOrNonPositiveTimeout_FallsBackToDefault(string raw)
        {
            var options = StalledReaperOptions.From(k =>
                k == StalledReaperOptions.TimeoutMinutesEnv ? raw : null);

            Assert.Equal(StalledReaperOptions.DefaultTimeoutMinutes, options.TimeoutMinutes);
        }
    }
}
