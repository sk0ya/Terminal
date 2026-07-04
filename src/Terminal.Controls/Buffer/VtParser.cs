using System.Text;

namespace Terminal.Buffer;

/// <summary>
/// Stateful recognizer for VT control sequences. It owns only parsing state; terminal semantics
/// remain with <see cref="AnsiTerminalBuffer"/> through explicit callbacks.
/// </summary>
internal sealed class VtParser(
    Action<char> control,
    Action<char> escape,
    Action<char, string> csi,
    Action<string> osc,
    Action<string> dcs,
    Action<int, char> charset,
    Action<char> decLineSize)
{
    private readonly StringBuilder _sequence = new();
    private State _state;
    private int _charsetTarget;

    public bool IsNormal => _state == State.Normal;

    public void Reset()
    {
        _state = State.Normal;
        _charsetTarget = 0;
        _sequence.Clear();
    }

    public void Process(char ch)
    {
        switch (_state)
        {
            case State.Normal:
                ProcessNormal(ch);
                break;
            case State.Escape:
                ProcessEscape(ch);
                break;
            case State.Csi:
                ProcessCsi(ch);
                break;
            case State.Osc:
                ProcessOsc(ch);
                break;
            case State.OscEscape:
                ProcessOscEscape(ch);
                break;
            case State.Charset:
                charset(_charsetTarget, ch);
                _state = State.Normal;
                break;
            case State.DecLineSize:
                decLineSize(ch);
                _state = State.Normal;
                break;
            case State.DcsEntry:
                ProcessDcsEntry(ch);
                break;
            case State.DcsParam:
                ProcessDcsParam(ch);
                break;
            case State.DcsIntermediate:
                ProcessDcsIntermediate(ch);
                break;
            case State.DcsPassthrough:
                ProcessDcsPassthrough(ch);
                break;
            case State.DcsPassthroughEscape:
                ProcessDcsPassthroughEscape(ch);
                break;
        }
    }

    private void ProcessNormal(char ch)
    {
        switch (ch)
        {
            case '\u001b':
                _state = State.Escape;
                return;
            case '\u009b':
                Begin(State.Csi);
                return;
            case '\u009d':
                Begin(State.Osc);
                return;
            case '\u009f':
                Begin(State.DcsEntry);
                return;
            case '\u009c':
                return;
            default:
                control(ch);
                return;
        }
    }

    private void ProcessEscape(char ch)
    {
        switch (ch)
        {
            case 'P':
                Begin(State.DcsEntry);
                return;
            case '[':
                Begin(State.Csi);
                return;
            case ']':
                Begin(State.Osc);
                return;
            case '(':
                BeginCharset(0);
                return;
            case ')':
                BeginCharset(1);
                return;
            case '*':
                BeginCharset(2);
                return;
            case '+':
                BeginCharset(3);
                return;
            case '#':
                _state = State.DecLineSize;
                return;
            default:
                escape(ch);
                _state = State.Normal;
                return;
        }
    }

    private void ProcessCsi(char ch)
    {
        if (ch is >= '@' and <= '~')
        {
            csi(ch, _sequence.ToString());
            _state = State.Normal;
            return;
        }

        _sequence.Append(ch);
    }

    private void ProcessOsc(char ch)
    {
        if (ch is '\a' or '\u009c')
        {
            osc(_sequence.ToString());
            _state = State.Normal;
        }
        else if (ch == '\u001b')
        {
            _state = State.OscEscape;
        }
        else
        {
            _sequence.Append(ch);
        }
    }

    private void ProcessOscEscape(char ch)
    {
        if (ch == '\\')
        {
            osc(_sequence.ToString());
            _state = State.Normal;
            return;
        }

        _state = State.Escape;
        ProcessEscape(ch);
    }

    private void ProcessDcsEntry(char ch)
    {
        if (TryFinishDcs(ch) || TryEnterDcsEscape(ch))
        {
            return;
        }

        if (ch is >= '\x20' and <= '\x2f')
        {
            _sequence.Append(ch);
            _state = State.DcsIntermediate;
        }
        else if (ch is >= '\x30' and <= '\x3f')
        {
            _sequence.Append(ch);
            _state = State.DcsParam;
        }
        else if (ch is >= '\x40' and <= '\x7e')
        {
            _sequence.Append(ch);
            _state = State.DcsPassthrough;
        }
    }

    private void ProcessDcsParam(char ch)
    {
        if (TryFinishDcs(ch) || TryEnterDcsEscape(ch))
        {
            return;
        }

        if (ch is >= '\x30' and <= '\x3f')
        {
            _sequence.Append(ch);
        }
        else if (ch is >= '\x20' and <= '\x2f')
        {
            _sequence.Append(ch);
            _state = State.DcsIntermediate;
        }
        else if (ch is >= '\x40' and <= '\x7e')
        {
            _sequence.Append(ch);
            _state = State.DcsPassthrough;
        }
    }

    private void ProcessDcsIntermediate(char ch)
    {
        if (TryFinishDcs(ch) || TryEnterDcsEscape(ch))
        {
            return;
        }

        if (ch is >= '\x20' and <= '\x2f')
        {
            _sequence.Append(ch);
        }
        else if (ch is >= '\x40' and <= '\x7e')
        {
            _sequence.Append(ch);
            _state = State.DcsPassthrough;
        }
    }

    private void ProcessDcsPassthrough(char ch)
    {
        if (TryFinishDcs(ch) || TryEnterDcsEscape(ch))
        {
            return;
        }

        _sequence.Append(ch);
    }

    private void ProcessDcsPassthroughEscape(char ch)
    {
        if (ch == '\\')
        {
            dcs(_sequence.ToString());
            _state = State.Normal;
            return;
        }

        _sequence.Append('\u001b').Append(ch);
        _state = State.DcsPassthrough;
    }

    private bool TryFinishDcs(char ch)
    {
        if (ch != '\u009c')
        {
            return false;
        }

        dcs(_sequence.ToString());
        _state = State.Normal;
        return true;
    }

    private bool TryEnterDcsEscape(char ch)
    {
        if (ch != '\u001b')
        {
            return false;
        }

        _state = State.DcsPassthroughEscape;
        return true;
    }

    private void Begin(State state)
    {
        _sequence.Clear();
        _state = state;
    }

    private void BeginCharset(int target)
    {
        _charsetTarget = target;
        _state = State.Charset;
    }

    private enum State
    {
        Normal,
        Escape,
        Csi,
        Osc,
        OscEscape,
        Charset,
        DecLineSize,
        DcsEntry,
        DcsParam,
        DcsIntermediate,
        DcsPassthrough,
        DcsPassthroughEscape
    }
}
