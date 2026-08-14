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
    Action<char> decLineSize,
    Action<string>? apc = null)
{
    private readonly Action<string> _apc = apc ?? new Action<string>(_ => { });
    internal const int MaxControlStringLength = 64 * 1024;
    // Inline image payloads (sixel DCS and kitty graphics APC) are routinely larger than any other
    // control string. Only those two states get the raised budget, so an unterminated OSC - a
    // truncated hyperlink, or a binary file sent to the tty - still aborts after 64 KB.
    internal const int MaxImageControlStringLength = 16 * 1024 * 1024;

    private readonly StringBuilder _sequence = new();
    private int _controlStringLimit = MaxControlStringLength;
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
            case State.ControlString:
                ProcessControlString(ch);
                break;
            case State.ControlStringEscape:
                ProcessControlStringEscape(ch);
                break;
            case State.Apc:
                ProcessApc(ch, _apc);
                break;
            case State.ApcEscape:
                ProcessApcEscape(ch, _apc);
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
            case '\u0090':
                Begin(State.DcsEntry);
                return;
            // SOS (0x98) and PM (0x9e) stay opaque; only APC (0x9f) carries kitty graphics. This
            // must agree with the ESC-form dispatch below.
            case '\u0098':
            case '\u009e':
                Begin(State.ControlString);
                return;
            case '\u009f':
                Begin(State.Apc);
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
            case '^':
            case 'X':
            case '_':
                Begin(ch == '_' ? State.Apc : State.ControlString);
                return;
            default:
                escape(ch);
                _state = State.Normal;
                return;
        }
    }

    private void ProcessCsi(char ch)
    {
        if (TryCancel(ch))
        {
            return;
        }

        if (ch == '\u001b')
        {
            _sequence.Clear();
            _state = State.Escape;
            return;
        }

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
        if (TryCancel(ch))
        {
            return;
        }

        if (ch is '\a' or '\u009c')
        {
            osc(_sequence.ToString());
            _state = State.Normal;
        }
        else if (ch == '\u001b')
        {
            _state = State.OscEscape;
        }
        else if (TryAppendControlString(ch) && ch == ';')
        {
            RaiseLimitForImageOsc();
        }
    }

    // OSC 1337;File= carries a base64 image, which legitimately runs past the ordinary control
    // string budget. The command number is everything before the first ';', so the decision can be
    // made as soon as that separator arrives - every other OSC keeps the 64 KB cap.
    private void RaiseLimitForImageOsc()
    {
        const string imageOscPrefix = "1337;";
        if (_controlStringLimit != MaxControlStringLength || _sequence.Length != imageOscPrefix.Length)
        {
            return;
        }

        for (int index = 0; index < imageOscPrefix.Length; index++)
        {
            if (_sequence[index] != imageOscPrefix[index])
            {
                return;
            }
        }

        _controlStringLimit = MaxImageControlStringLength;
    }

    private void ProcessOscEscape(char ch)
    {
        if (TryCancel(ch))
        {
            return;
        }

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
        if (TryCancel(ch))
        {
            return;
        }

        if (TryFinishDcs(ch) || TryEnterDcsEscape(ch))
        {
            return;
        }

        if (ch is >= '\x20' and <= '\x2f')
        {
            if (!TryAppendControlString(ch))
            {
                return;
            }

            _state = State.DcsIntermediate;
        }
        else if (ch is >= '\x30' and <= '\x3f')
        {
            if (!TryAppendControlString(ch))
            {
                return;
            }

            _state = State.DcsParam;
        }
        else if (ch is >= '\x40' and <= '\x7e')
        {
            if (!TryAppendControlString(ch))
            {
                return;
            }

            EnterDcsPassthrough(ch);
        }
    }

    private void ProcessDcsParam(char ch)
    {
        if (TryCancel(ch))
        {
            return;
        }

        if (TryFinishDcs(ch) || TryEnterDcsEscape(ch))
        {
            return;
        }

        if (ch is >= '\x30' and <= '\x3f')
        {
            TryAppendControlString(ch);
        }
        else if (ch is >= '\x20' and <= '\x2f')
        {
            if (!TryAppendControlString(ch))
            {
                return;
            }

            _state = State.DcsIntermediate;
        }
        else if (ch is >= '\x40' and <= '\x7e')
        {
            if (!TryAppendControlString(ch))
            {
                return;
            }

            EnterDcsPassthrough(ch);
        }
    }

    private void ProcessDcsIntermediate(char ch)
    {
        if (TryCancel(ch))
        {
            return;
        }

        if (TryFinishDcs(ch) || TryEnterDcsEscape(ch))
        {
            return;
        }

        if (ch is >= '\x20' and <= '\x2f')
        {
            TryAppendControlString(ch);
        }
        else if (ch is >= '\x40' and <= '\x7e')
        {
            if (!TryAppendControlString(ch))
            {
                return;
            }

            EnterDcsPassthrough(ch);
        }
    }

    private void ProcessDcsPassthrough(char ch)
    {
        if (TryCancel(ch))
        {
            return;
        }

        if (TryFinishDcs(ch) || TryEnterDcsEscape(ch))
        {
            return;
        }

        TryAppendControlString(ch);
    }

    private void ProcessDcsPassthroughEscape(char ch)
    {
        if (TryCancel(ch))
        {
            return;
        }

        if (ch == '\\')
        {
            dcs(_sequence.ToString());
            _state = State.Normal;
            return;
        }

        if (TryAppendControlString('\u001b', ch))
        {
            _state = State.DcsPassthrough;
        }
    }

    private void ProcessControlString(char ch)
    {
        if (ch == '\u009c')
        {
            _state = State.Normal;
        }
        else if (ch == '\u001b')
        {
            _state = State.ControlStringEscape;
        }
    }

    private void ProcessControlStringEscape(char ch)
    {
        if (ch == '\\')
        {
            _state = State.Normal;
            return;
        }

        // PM and SOS are unsupported, so their content must remain opaque even when an ESC is
        // not followed by the ST terminator. APC has a separate image-aware state machine below.
        _state = State.ControlString;
        ProcessControlString(ch);
    }

    private void ProcessApc(char ch, Action<string> apc)
    {
        if (TryCancel(ch)) return;
        if (ch == '\u009c')
        {
            apc(_sequence.ToString());
            _state = State.Normal;
        }
        else if (ch == '\u001b')
        {
            _state = State.ApcEscape;
        }
        else
        {
            TryAppendControlString(ch);
        }
    }

    private void ProcessApcEscape(char ch, Action<string> apc)
    {
        if (TryCancel(ch)) return;
        if (ch == '\\')
        {
            apc(_sequence.ToString());
            _state = State.Normal;
            return;
        }

        if (TryAppendControlString('\u001b', ch)) _state = State.Apc;
    }

    private bool TryCancel(char ch)
    {
        if (ch is not ('\u0018' or '\u001a'))
        {
            return false;
        }

        _sequence.Clear();
        _state = State.Normal;
        return true;
    }

    private bool TryAppendControlString(char ch)
    {
        if (_sequence.Length >= _controlStringLimit)
        {
            AbortControlString();
            return false;
        }

        _sequence.Append(ch);
        return true;
    }

    private bool TryAppendControlString(char first, char second)
    {
        if (_sequence.Length > _controlStringLimit - 2)
        {
            AbortControlString();
            return false;
        }

        _sequence.Append(first).Append(second);
        return true;
    }

    private void AbortControlString()
    {
        _sequence.Clear();
        _state = State.Normal;
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
        _controlStringLimit = state == State.Apc ? MaxImageControlStringLength : MaxControlStringLength;
    }

    // The final character of the DCS introducer is already in the sequence buffer; 'q' is sixel,
    // which is the only DCS whose payload needs the raised budget.
    private void EnterDcsPassthrough(char final)
    {
        _state = State.DcsPassthrough;
        _controlStringLimit = final == 'q' ? MaxImageControlStringLength : MaxControlStringLength;
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
        DcsPassthroughEscape,
        ControlString,
        ControlStringEscape,
        Apc,
        ApcEscape
    }
}
