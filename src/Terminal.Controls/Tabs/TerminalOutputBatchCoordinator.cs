using System.Text;

namespace Terminal.Tabs;

internal sealed class TerminalOutputBatchCoordinator
{
    private readonly object _syncRoot = new();
    private readonly StringBuilder _pending = new();
    private bool _flushScheduled;
    private bool _prioritizeNextRender;

    public bool Enqueue(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        lock (_syncRoot)
        {
            _pending.Append(text);
            if (_flushScheduled)
            {
                return false;
            }

            _flushScheduled = true;
            return true;
        }
    }

    public string? Drain(int maximumCharacters = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);

        lock (_syncRoot)
        {
            int count = Math.Min(_pending.Length, maximumCharacters);
            string? batch = count > 0 ? _pending.ToString(0, count) : null;
            if (count > 0)
            {
                _pending.Remove(0, count);
            }
            _flushScheduled = false;
            return batch;
        }
    }

    public bool EnsureFlushScheduled()
    {
        lock (_syncRoot)
        {
            if (_pending.Length == 0 || _flushScheduled)
            {
                return false;
            }

            _flushScheduled = true;
            return true;
        }
    }

    public void SetPrioritizeNextRender(bool prioritize)
    {
        lock (_syncRoot)
        {
            _prioritizeNextRender = prioritize;
        }
    }

    public bool ConsumeRenderPriority()
    {
        lock (_syncRoot)
        {
            bool prioritize = _prioritizeNextRender;
            _prioritizeNextRender = false;
            return prioritize;
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _pending.Clear();
            _flushScheduled = false;
        }
    }
}
