namespace Terminal.Buffer;

/// <summary>
/// One OSC 8 hyperlink. Identity is the instance: every cell written while it was open points at
/// the same object, so a link that wraps onto the next row (or, with an explicit <c>id=</c>, is
/// written in several separate pieces) is still recognised as one link, while two links that merely
/// share a URI stay apart.
/// </summary>
internal sealed class TerminalHyperlink
{
    public TerminalHyperlink(string uri, string? id)
    {
        Uri = uri;
        Id = id;
    }

    public string Uri { get; }

    /// <summary>The <c>id=</c> parameter, or null when the application gave none.</summary>
    public string? Id { get; }

    /// <summary>
    /// Parses the body of <c>OSC 8 ; params ; URI</c> (everything after <c>8;</c>). Params are
    /// <c>key=value</c> pairs separated by <c>:</c>; only <c>id</c> is used. Returns false when the
    /// body has no <c>;</c>. An empty URI closes the link (<paramref name="uri"/> is empty).
    /// </summary>
    public static bool TryParse(string body, out string uri, out string? id)
    {
        uri = string.Empty;
        id = null;
        int separator = body.IndexOf(';');
        if (separator < 0)
        {
            return false;
        }

        uri = body[(separator + 1)..];
        foreach (string pair in body[..separator].Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals > 0 && pair[..equals] == "id" && equals < pair.Length - 1)
            {
                id = pair[(equals + 1)..];
            }
        }

        return true;
    }
}

/// <summary>
/// Hands out <see cref="TerminalHyperlink"/> instances: the same object again for an explicit id
/// with the same URI, a fresh one for every link opened without an id.
/// </summary>
internal sealed class TerminalHyperlinkRegistry
{
    // Applications rarely reuse ids beyond a screenful; the cap only stops a runaway stream of
    // unique ids from growing without bound (old links then just stop matching new pieces).
    private const int MaxTrackedIds = 4096;

    private readonly Dictionary<(string Id, string Uri), TerminalHyperlink> _byId = [];

    public TerminalHyperlink Open(string uri, string? id)
    {
        if (id is null)
        {
            return new TerminalHyperlink(uri, null);
        }

        if (_byId.TryGetValue((id, uri), out TerminalHyperlink? existing))
        {
            return existing;
        }

        if (_byId.Count >= MaxTrackedIds)
        {
            _byId.Clear();
        }

        var link = new TerminalHyperlink(uri, id);
        _byId[(id, uri)] = link;
        return link;
    }

    public void Clear() => _byId.Clear();
}
