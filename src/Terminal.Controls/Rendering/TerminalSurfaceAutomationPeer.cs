using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;

namespace Terminal.Rendering;

internal sealed class TerminalSurfaceAutomationPeer : FrameworkElementAutomationPeer, ITextProvider, IValueProvider
{
    private TerminalSurfaceControl Surface => (TerminalSurfaceControl)Owner;
    private string _reportedText = string.Empty;

    internal TerminalSurfaceAutomationPeer(TerminalSurfaceControl owner) : base(owner) { }

    protected override string GetClassNameCore() => nameof(TerminalSurfaceControl);
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
    protected override string GetNameCore() => string.IsNullOrWhiteSpace(base.GetNameCore()) ? "Terminal output" : base.GetNameCore();
    protected override bool IsControlElementCore() => true;
    protected override bool IsContentElementCore() => true;

    // Keyboard focus physically lives on a hidden proxy TextBox (IME needs a real editor), and that
    // proxy is hidden from the automation tree. The surface is the only element an accessibility
    // client can see here, so it has to claim the focus the proxy is holding on its behalf --
    // otherwise nothing in the tree ever reports as focused.
    protected override bool IsKeyboardFocusableCore() => true;
    protected override bool HasKeyboardFocusCore() => base.HasKeyboardFocusCore() || Surface.HasDelegatedKeyboardFocus;

    // The proxy's TextBoxAutomationPeer used to be what announced typed and echoed characters; with
    // it out of the tree the surface peer owns change notification for the whole terminal.
    internal void NotifyTextChanged()
    {
        if (!ListenerExists(AutomationEvents.PropertyChanged) && !ListenerExists(AutomationEvents.TextPatternOnTextChanged))
        {
            return;
        }

        string current = Surface.GetAutomationText();
        if (string.Equals(current, _reportedText, StringComparison.Ordinal))
        {
            return;
        }

        string previous = _reportedText;
        _reportedText = current;
        RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, previous, current);
        RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);
    }

    public override object? GetPattern(PatternInterface patternInterface) => patternInterface switch
    {
        PatternInterface.Text or PatternInterface.Value => this,
        _ => base.GetPattern(patternInterface)
    };

    public ITextRangeProvider DocumentRange => Range(0, Surface.GetAutomationText().Length);
    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;
    public ITextRangeProvider[] GetSelection()
    {
        string document = Surface.GetAutomationText();
        string selected = Surface.GetSelectedText();
        int start = selected.Length == 0 ? 0 : document.IndexOf(selected, StringComparison.Ordinal);
        return selected.Length == 0 || start < 0 ? [] : [Range(start, start + selected.Length)];
    }
    public ITextRangeProvider[] GetVisibleRanges() => [DocumentRange];
    public ITextRangeProvider? RangeFromChild(IRawElementProviderSimple childElement) => null;
    public ITextRangeProvider RangeFromPoint(Point screenLocation) => DocumentRange;
    public bool IsReadOnly => true;
    public string Value => Surface.GetAutomationText();
    public void SetValue(string value) => throw new InvalidOperationException("Terminal output is read-only.");

    private TerminalTextRangeProvider Range(int start, int end) => new(this, start, end);
    internal string Text => Surface.GetAutomationText();
    internal IRawElementProviderSimple Provider => ProviderFromPeer(this);
}

internal sealed class TerminalTextRangeProvider : ITextRangeProvider
{
    private readonly TerminalSurfaceAutomationPeer _peer;
    private int _start;
    private int _end;
    internal TerminalTextRangeProvider(TerminalSurfaceAutomationPeer peer, int start, int end)
    {
        _peer = peer; _start = Math.Max(0, start); _end = Math.Max(_start, end); Clamp();
    }
    public void AddToSelection() { }
    public ITextRangeProvider Clone() => new TerminalTextRangeProvider(_peer, _start, _end);
    public bool Compare(ITextRangeProvider range) => range is TerminalTextRangeProvider other && other._peer == _peer && other._start == _start && other._end == _end;
    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        var other = (TerminalTextRangeProvider)targetRange;
        return Endpoint(endpoint).CompareTo(other.Endpoint(targetEndpoint));
    }
    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        string text = _peer.Text;
        if (unit == TextUnit.Document) { _start = 0; _end = text.Length; return; }
        if (unit is TextUnit.Line or TextUnit.Paragraph)
        {
            _start = text.LastIndexOf('\n', Math.Max(0, _start - 1)) + 1;
            int next = text.IndexOf('\n', _end); _end = next < 0 ? text.Length : next;
            return;
        }
        _end = Math.Min(text.Length, _start + 1);
    }
    public ITextRangeProvider? FindAttribute(int attributeId, object value, bool backward) => null;
    public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase)
    {
        StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int index = backward ? _peer.Text.LastIndexOf(text, Math.Max(0, _end - 1), comparison) : _peer.Text.IndexOf(text, _start, comparison);
        return index < 0 ? null : new TerminalTextRangeProvider(_peer, index, index + text.Length);
    }
    public object GetAttributeValue(int attributeId) => AutomationElement.NotSupported;
    public double[] GetBoundingRectangles() => [];
    public IRawElementProviderSimple[] GetChildren() => [];
    public IRawElementProviderSimple GetEnclosingElement() => _peer.Provider;
    public string GetText(int maxLength)
    {
        string value = _peer.Text[_start..Math.Min(_end, _peer.Text.Length)];
        return maxLength < 0 || value.Length <= maxLength ? value : value[..maxLength];
    }
    public int Move(TextUnit unit, int count) { int old = _start; _start = Math.Clamp(_start + count, 0, _peer.Text.Length); _end = _start; return _start - old; }
    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        int value = ((TerminalTextRangeProvider)targetRange).Endpoint(targetEndpoint);
        if (endpoint == TextPatternRangeEndpoint.Start) _start = value; else _end = value; Clamp();
    }
    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        int old = Endpoint(endpoint); int value = Math.Clamp(old + count, 0, _peer.Text.Length);
        if (endpoint == TextPatternRangeEndpoint.Start) _start = value; else _end = value; Clamp(); return value - old;
    }
    public void RemoveFromSelection() { }
    public void ScrollIntoView(bool alignToTop) { }
    public void Select() { }
    private int Endpoint(TextPatternRangeEndpoint endpoint) => endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
    private void Clamp() { int length = _peer.Text.Length; _start = Math.Clamp(_start, 0, length); _end = Math.Clamp(_end, _start, length); }
}
