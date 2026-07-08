using Terminal.Rendering;

namespace Terminal.Tests;

public sealed class DisposableResourceOwnerTests
{
    [Fact]
    public void ExecuteAllBestEffortAttemptsEveryOperationAndRethrowsSingleFailure()
    {
        int firstCalls = 0;
        int lastCalls = 0;
        var failure = new InvalidOperationException("operation failed");

        Exception caught = Assert.Throws<InvalidOperationException>(() =>
            DisposableResourceOwner.ExecuteAllBestEffort(
            [
                () => firstCalls++,
                () => throw failure,
                () => lastCalls++
            ]));

        Assert.Same(failure, caught);
        Assert.Equal(1, firstCalls);
        Assert.Equal(1, lastCalls);
    }

    [Fact]
    public void ExecuteAllBestEffortAttemptsEveryOperationAndAggregatesMultipleFailures()
    {
        int middleCalls = 0;
        var firstFailure = new InvalidOperationException("first");
        var lastFailure = new FormatException("last");

        AggregateException caught = Assert.Throws<AggregateException>(() =>
            DisposableResourceOwner.ExecuteAllBestEffort(
            [
                () => throw firstFailure,
                () => middleCalls++,
                () => throw lastFailure
            ]));

        Assert.Equal([firstFailure, lastFailure], caught.InnerExceptions);
        Assert.Equal(1, middleCalls);
    }

    [Fact]
    public void RollBackBestEffortAttemptsEveryResourceAndPreservesConstructionFailure()
    {
        var first = new FakeDisposable(throwOnDispose: true);
        var second = new FakeDisposable();
        var constructionFailure = new FormatException("build failed");

        Exception caught = Assert.Throws<FormatException>((Action)(() =>
        {
            try
            {
                throw constructionFailure;
            }
            catch
            {
                DisposableResourceOwner.RollBackBestEffort([first, second]);
                throw;
            }
        }));

        Assert.Same(constructionFailure, caught);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public void DisposeAllBestEffortAttemptsEveryResourceAndAggregatesFailures()
    {
        var first = new FakeDisposable(throwOnDispose: true);
        var middle = new FakeDisposable();
        var last = new FakeDisposable(throwOnDispose: true);

        AggregateException error = Assert.Throws<AggregateException>(() =>
            DisposableResourceOwner.DisposeAllBestEffort([first, middle, last]));

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, middle.DisposeCount);
        Assert.Equal(1, last.DisposeCount);
    }

    private sealed class FakeDisposable(bool throwOnDispose = false) : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            if (throwOnDispose)
            {
                throw new InvalidOperationException("dispose failed");
            }
        }
    }
}
