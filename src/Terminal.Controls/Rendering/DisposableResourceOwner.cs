using System.Runtime.ExceptionServices;

namespace Terminal.Rendering;

internal static class DisposableResourceOwner
{
    public static void ExecuteAllBestEffort(IEnumerable<Action> operations)
    {
        List<Exception>? errors = null;
        foreach (Action operation in operations)
        {
            try
            {
                operation();
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }

        if (errors is { Count: 1 })
        {
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        }

        if (errors is { Count: > 1 })
        {
            throw new AggregateException(errors);
        }
    }

    public static void DisposeAllBestEffort(IEnumerable<IDisposable?> resources)
        => ExecuteAllBestEffort(
            resources
                .Where(static resource => resource is not null)
                .Select(static resource => (Action)resource!.Dispose));

    public static void RollBackBestEffort(IEnumerable<IDisposable?> resources)
    {
        try
        {
            DisposeAllBestEffort(resources);
        }
        catch
        {
            // Preserve the construction exception. All resources have still been attempted.
        }
    }
}
