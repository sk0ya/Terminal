using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Terminal.Tabs;

/// <summary>
/// IME/keyboard capture implementation detail for <see cref="TerminalTabView"/>.
/// The visible terminal surface owns the user-facing Text/Value automation patterns;
/// exposing this transparent proxy as another Edit control makes accessibility clients
/// report it as the focused document even after focus has moved to a sibling editor.
/// </summary>
internal sealed class TerminalInputTextBox : TextBox
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ProxyAutomationPeer(this);

    private sealed class ProxyAutomationPeer(TerminalInputTextBox owner) : TextBoxAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;
        protected override bool IsContentElementCore() => false;
    }
}
