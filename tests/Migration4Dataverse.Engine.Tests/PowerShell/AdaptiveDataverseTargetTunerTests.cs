using System.Diagnostics;
using Migration4Dataverse.Engine.PowerShell;

namespace Migration4Dataverse.Engine.Tests.PowerShell;

public sealed class AdaptiveDataverseTargetTunerTests
{
    [Fact]
    public void BatchSizeTuningStopsAfterTwoCandidatesWithoutImprovementAndKeepsBest()
    {
        long timestamp = 0;
        var tuner = new AdaptiveDataverseTargetTuner(
            maxBatchSize: 10,
            maxConcurrency: 1,
            autoBatchSize: true,
            autoConcurrency: false,
            fixedBatchSize: 10,
            fixedConcurrency: 1,
            getTimestamp: () => timestamp);

        var initialState = tuner.GetDashboardState();
        Assert.True(initialState.BatchSizeAuto);
        Assert.Equal("tuning candidate (0/3 samples)", initialState.BatchSizeState);
        Assert.False(initialState.ConcurrencyAuto);
        Assert.Equal("fixed", initialState.ConcurrencyState);

        CompleteWindow(tuner, ref timestamp, expectedBatchSize: 10, elapsedPerBatchSeconds: 1);
        CompleteWindow(tuner, ref timestamp, expectedBatchSize: 5, elapsedPerBatchSeconds: 0.5);
        CompleteWindow(tuner, ref timestamp, expectedBatchSize: 2, elapsedPerBatchSeconds: 1);
        CompleteWindow(tuner, ref timestamp, expectedBatchSize: 1, elapsedPerBatchSeconds: 1);

        Assert.Equal(5, tuner.BatchSize);
        Assert.Equal(1, tuner.MaxConcurrency);
        Assert.Contains("tuning complete", tuner.GetSummary());
        Assert.Equal("complete", tuner.GetDashboardState().BatchSizeState);
    }

    [Fact]
    public void BatchSizeTuningKeepsConfiguredConcurrencyWhenConcurrencyIsFixed()
    {
        long timestamp = 0;
        var tuner = new AdaptiveDataverseTargetTuner(
            maxBatchSize: 10,
            maxConcurrency: 8,
            autoBatchSize: true,
            autoConcurrency: false,
            fixedBatchSize: 10,
            fixedConcurrency: 4,
            getTimestamp: () => timestamp);

        Assert.Equal(4, tuner.MaxConcurrency);
        CompleteWindow(tuner, ref timestamp, expectedBatchSize: 10, elapsedPerBatchSeconds: 1);

        Assert.Equal(4, tuner.MaxConcurrency);
    }

    [Fact]
    public void ConcurrencyTuningStartsAfterBatchSizeAndRespectsConfiguredCap()
    {
        long timestamp = 0;
        var tuner = new AdaptiveDataverseTargetTuner(
            maxBatchSize: 1,
            maxConcurrency: 2,
            autoBatchSize: true,
            autoConcurrency: true,
            fixedBatchSize: 1,
            fixedConcurrency: 1,
            getTimestamp: () => timestamp);

        CompleteWindow(tuner, ref timestamp, expectedBatchSize: 1, elapsedPerBatchSeconds: 1);
        Assert.Equal(1, tuner.MaxConcurrency);

        CompleteWindow(tuner, ref timestamp, expectedBatchSize: 1, elapsedPerBatchSeconds: 1);

        Assert.Equal(2, tuner.MaxConcurrency);
        Assert.Equal(1, tuner.BatchSize);
    }

    [Fact]
    public void PersistedBatchSizeIsUsedAsWarmStartAndRetuned()
    {
        long timestamp = 0;
        int? savedBatchSize = null;
        int? savedConcurrency = null;
        var tuner = new AdaptiveDataverseTargetTuner(
            maxBatchSize: 2,
            maxConcurrency: 2,
            autoBatchSize: true,
            autoConcurrency: true,
            fixedBatchSize: 2,
            fixedConcurrency: 1,
            getTimestamp: () => timestamp,
            persistTunedValues: (batchSize, concurrency) =>
            {
                savedBatchSize = batchSize;
                savedConcurrency = concurrency;
            });

        CompleteWindow(tuner, ref timestamp, expectedBatchSize: 2, elapsedPerBatchSeconds: 1);
        CompleteWindow(tuner, ref timestamp, expectedBatchSize: 1, elapsedPerBatchSeconds: 0.5);
        CompleteWindow(tuner, ref timestamp, expectedBatchSize: 1, elapsedPerBatchSeconds: 1);
        CompleteWindow(tuner, ref timestamp, expectedBatchSize: 1, elapsedPerBatchSeconds: 0.5);

        Assert.Equal(1, savedBatchSize);
        Assert.Equal(2, savedConcurrency);

        var restoredTuner = new AdaptiveDataverseTargetTuner(
            maxBatchSize: 2,
            maxConcurrency: 2,
            autoBatchSize: true,
            autoConcurrency: true,
            fixedBatchSize: 2,
            fixedConcurrency: 1,
            persistedBatchSize: savedBatchSize,
            persistedConcurrency: savedConcurrency);
        var state = restoredTuner.GetDashboardState();
        Assert.Equal("BatchSize", state.Stage);
        Assert.Equal(1, restoredTuner.BatchSize);
        Assert.Equal(1, restoredTuner.MaxConcurrency);
        using var lease = restoredTuner.Acquire();
        Assert.Equal(1, lease.BatchSize);
    }

    [Fact]
    public void StoredConcurrencyIsAppliedAfterAnUncachedBatchSizeIsTuned()
    {
        long timestamp = 0;
        var tuner = new AdaptiveDataverseTargetTuner(
            maxBatchSize: 1,
            maxConcurrency: 4,
            autoBatchSize: true,
            autoConcurrency: true,
            fixedBatchSize: 1,
            fixedConcurrency: 1,
            getTimestamp: () => timestamp,
            persistedConcurrency: 4);

        Assert.Equal(1, tuner.MaxConcurrency);
        CompleteWindow(tuner, ref timestamp, expectedBatchSize: 1, elapsedPerBatchSeconds: 1);

        Assert.Equal(4, tuner.MaxConcurrency);
        Assert.Equal("Complete", tuner.GetDashboardState().Stage);
    }

    [Fact]
    public void FixedSettingsRemainFixedAndConcurrencyIsCappedByInvocationDop()
    {
        var tuner = new AdaptiveDataverseTargetTuner(
            maxBatchSize: 500,
            maxConcurrency: 4,
            autoBatchSize: false,
            autoConcurrency: false,
            fixedBatchSize: 250,
            fixedConcurrency: 32);

        Assert.Equal(250, tuner.BatchSize);
        Assert.Equal(4, tuner.MaxConcurrency);
        Assert.Contains("tuning fixed", tuner.GetSummary());
    }

    [Fact]
    public void TryAcquireDoesNotWaitAndDisposalReleasesTheExecutionPermit()
    {
        var tuner = new AdaptiveDataverseTargetTuner(
            maxBatchSize: 1,
            maxConcurrency: 1,
            autoBatchSize: false,
            autoConcurrency: false,
            fixedBatchSize: 1,
            fixedConcurrency: 1);

        var firstLease = tuner.TryAcquire();
        Assert.NotNull(firstLease);
        Assert.Null(tuner.TryAcquire());

        firstLease.Dispose();
        using var secondLease = tuner.TryAcquire();
        Assert.NotNull(secondLease);
    }

    private static void CompleteWindow(
        AdaptiveDataverseTargetTuner tuner,
        ref long timestamp,
        int expectedBatchSize,
        double elapsedPerBatchSeconds)
    {
        for (var index = 0; index < 3; index++)
        {
            var lease = tuner.Acquire();
            Assert.Equal(expectedBatchSize, lease.BatchSize);
            timestamp += (long)(elapsedPerBatchSeconds * Stopwatch.Frequency);
            tuner.Complete(lease, rowsWritten: 1, succeeded: true);
        }
    }
}
