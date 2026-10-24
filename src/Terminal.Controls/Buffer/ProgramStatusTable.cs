using System.Text;
using Terminal.Tabs;

namespace Terminal.Buffer;

/// <summary>
/// OSC 7501（Program Status Protocol, https://www.superlogical.com/rex/docs/build/program-status）のレコード表。
/// 端末の中のプログラムが <c>ESC ] 7501 ; state=blocked:kind=permission:app=claude-code:msg=&lt;base64&gt; ST</c> のように
/// 自分の状態を申告する。画面を読んで推すのではなく、本人の申告を持つ。
/// <para>仕様の MUST をここで守る：各報告はそのレコードを丸ごと置き換える／<c>state=clear</c> はその id と下の全部を消す
/// （id 無しなら全部）／<c>app</c> は最も近い祖先から継ぐ／上限を破る報告は丸ごと捨てる／解いた <c>msg</c>・<c>title</c> に
/// 制御文字があれば捨てる／プロセスが終わるか次のプロンプト（OSC 133 A）が来たら <c>working</c> と <c>blocked</c> を落とす
/// （<c>done</c>・<c>error</c> は残す。<c>idle</c> も落とす——仕様は MAY で、落とさないと <c>clear</c> を送らずに終わった
/// プログラムがいつまでも「居る」ことになる）。表示の仕方と、<c>done</c>・<c>error</c> をいつ消すかは受け手が決める。</para>
/// </summary>
internal sealed class ProgramStatusTable
{
    public const int MaxSequenceBytes = 4096;
    public const int MaxRecords = 256;
    private const int MaxKeyBytes = 16;
    private const int MaxMessageEncoded = 2732;
    private const int MaxMessageDecoded = 2048;
    private const int MaxTitleEncoded = 256;
    private const int MaxTitleDecoded = 192;
    private const int MaxAppBytes = 32;
    private const int MaxIdBytes = 128;
    private const int MaxIdSegmentBytes = 32;
    private const int MaxIdDepth = 8;

    // 申告そのまま（app は自分が書いたものだけ）。継いだ app は読み出すときに解く。
    private sealed record Stored(
        ProgramStatusState State, ProgramStatusKind Kind, int? Progress, string? App, string? Title, string? Message,
        DateTimeOffset UpdatedAt);

    private readonly Dictionary<string, Stored> _records = new(StringComparer.Ordinal);

    public bool IsEmpty => _records.Count == 0;

    /// <summary>報告（<c>7501;</c> の後ろ）を当てる。表が変わったら true。読めない・上限を破る報告は何もしない。</summary>
    public bool Apply(string value, DateTimeOffset now)
    {
        if (Encoding.UTF8.GetByteCount(value) + 8 > MaxSequenceBytes)
        {
            return false;
        }

        string? state = null, id = null, kind = null, progress = null, app = null, title = null, msg = null;
        foreach (string pair in value.Split(':'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = pair[..eq];
            if (Encoding.UTF8.GetByteCount(key) > MaxKeyBytes)
            {
                return false;
            }

            string v = pair[(eq + 1)..];
            switch (key)
            {
                case "state": state = v; break;
                case "id": id = v; break;
                case "kind": kind = v; break;
                case "progress": progress = v; break;
                case "app": app = v; break;
                case "title": title = v; break;
                case "msg": msg = v; break;
            }
        }

        string recordId = id ?? "";
        if (!IsValidId(recordId))
        {
            return false;
        }

        if (state == "clear")
        {
            return Clear(recordId, all: id is null);
        }

        ProgramStatusState? parsed = state switch
        {
            "idle" => ProgramStatusState.Idle,
            "working" => ProgramStatusState.Working,
            "done" => ProgramStatusState.Done,
            "blocked" => ProgramStatusState.Blocked,
            "error" => ProgramStatusState.Error,
            _ => null,
        };
        if (parsed is not { } status)
        {
            return false;   // state が無い・知らない報告は無視（仕様）
        }

        if (app is not null && (app.Length == 0 || Encoding.UTF8.GetByteCount(app) > MaxAppBytes))
        {
            return false;
        }

        if (!TryDecodeText(title, MaxTitleEncoded, MaxTitleDecoded, out string? decodedTitle)
            || !TryDecodeText(msg, MaxMessageEncoded, MaxMessageDecoded, out string? decodedMessage))
        {
            return false;
        }

        ProgramStatusKind statusKind = status != ProgramStatusState.Blocked ? ProgramStatusKind.None : kind switch
        {
            "permission" => ProgramStatusKind.Permission,
            "question" => ProgramStatusKind.Question,
            "auth" => ProgramStatusKind.Auth,
            _ => ProgramStatusKind.None,
        };

        int? percent = null;
        if (status is ProgramStatusState.Working or ProgramStatusState.Blocked
            && progress is not null && int.TryParse(progress, out int p) && p is >= 0 and <= 100)
        {
            percent = p;
        }

        if (!_records.ContainsKey(recordId) && _records.Count >= MaxRecords)
        {
            return false;
        }

        var next = new Stored(status, statusKind, percent, app, decodedTitle, decodedMessage, now);
        if (_records.TryGetValue(recordId, out Stored? current) && current with { UpdatedAt = now } == next)
        {
            return false;   // 同じ申告の繰り返し（ハートビート）は変化として流さない
        }

        _records[recordId] = next;
        return true;
    }

    /// <summary>プロセスが終わった・次のプロンプトが来た：<c>working</c>・<c>blocked</c>・<c>idle</c> を落とす（<c>done</c>・<c>error</c> は残す）。
    /// 変わったら true。</summary>
    public bool DropTransient()
    {
        List<string> stale = _records
            .Where(r => r.Value.State is ProgramStatusState.Working or ProgramStatusState.Blocked or ProgramStatusState.Idle)
            .Select(r => r.Key)
            .ToList();
        foreach (string key in stale)
        {
            _records.Remove(key);
        }

        return stale.Count > 0;
    }

    /// <summary>いまのレコード（Id 順・app は祖先から継いだ値）。</summary>
    public IReadOnlyList<ProgramStatusRecord> Snapshot()
        => _records
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => new ProgramStatusRecord(
                r.Key, r.Value.State, r.Value.Kind, r.Value.Progress, InheritedApp(r.Key),
                r.Value.Title, r.Value.Message, r.Value.UpdatedAt))
            .ToList();

    /// <summary>付け直した画面へ同じ表を渡すための報告の本文（<c>7501;</c> の後ろ）。祖先から先に並べる。</summary>
    public IEnumerable<string> Reports()
    {
        foreach ((string id, Stored s) in _records.OrderBy(r => r.Key.Count(c => c == '/')).ThenBy(r => r.Key, StringComparer.Ordinal))
        {
            var parts = new List<string> { "state=" + StateName(s.State) };
            if (id.Length > 0) parts.Add("id=" + id);
            if (s.Kind != ProgramStatusKind.None) parts.Add("kind=" + s.Kind.ToString().ToLowerInvariant());
            if (s.Progress is { } p) parts.Add("progress=" + p);
            if (s.App is { } a) parts.Add("app=" + a);
            if (s.Title is { } t) parts.Add("title=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(t)));
            if (s.Message is { } m) parts.Add("msg=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(m)));
            yield return string.Join(":", parts);
        }
    }

    private bool Clear(string id, bool all)
    {
        if (all)
        {
            bool had = _records.Count > 0;
            _records.Clear();
            return had;
        }

        List<string> doomed = _records.Keys
            .Where(k => k == id || k.StartsWith(id + "/", StringComparison.Ordinal))
            .ToList();
        foreach (string key in doomed)
        {
            _records.Remove(key);
        }

        return doomed.Count > 0;
    }

    private string? InheritedApp(string id)
    {
        string current = id;
        while (true)
        {
            if (_records.TryGetValue(current, out Stored? s) && s.App is { } app)
            {
                return app;
            }

            if (current.Length == 0)
            {
                return null;
            }

            int slash = current.LastIndexOf('/');
            current = slash < 0 ? "" : current[..slash];
        }
    }

    private static string StateName(ProgramStatusState state) => state.ToString().ToLowerInvariant();

    private static bool IsValidId(string id)
    {
        if (id.Length == 0)
        {
            return true;
        }

        if (Encoding.UTF8.GetByteCount(id) > MaxIdBytes)
        {
            return false;
        }

        string[] segments = id.Split('/');
        if (segments.Length > MaxIdDepth)
        {
            return false;
        }

        foreach (string segment in segments)
        {
            if (segment.Length is 0 or > MaxIdSegmentBytes)
            {
                return false;
            }

            foreach (char c in segment)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '+' or '-'))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryDecodeText(string? encoded, int maxEncoded, int maxDecoded, out string? decoded)
    {
        decoded = null;
        if (encoded is null)
        {
            return true;
        }

        if (encoded.Length > maxEncoded)
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length > maxDecoded)
        {
            return false;
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        // 制御文字を含む報告は捨てる（仕様の MUST。表示に紛れ込ませない）。
        if (text.Any(char.IsControl))
        {
            return false;
        }

        decoded = text;
        return true;
    }
}
