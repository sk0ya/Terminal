using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalWorkbenchShortcutCoordinatorTests
{
    [Theory]
    [InlineData((int)TerminalWorkbenchShortcutKey.S, (int)TerminalWorkbenchShortcutAction.SaveTranscript)]
    [InlineData((int)TerminalWorkbenchShortcutKey.R, (int)TerminalWorkbenchShortcutAction.Restart)]
    public void ControlShiftCommandsRequireExactModifiers(int keyValue, int expectedActionValue)
    {
        TerminalWorkbenchShortcutAction action = TerminalWorkbenchShortcutCoordinator.Resolve(
            (TerminalWorkbenchShortcutKey)keyValue,
            TerminalWorkbenchShortcutModifiers.Control | TerminalWorkbenchShortcutModifiers.Shift);

        Assert.Equal((TerminalWorkbenchShortcutAction)expectedActionValue, action);
    }

    [Theory]
    [InlineData((int)TerminalWorkbenchShortcutKey.Add, (int)TerminalWorkbenchShortcutAction.IncreaseFontSize)]
    [InlineData((int)TerminalWorkbenchShortcutKey.OemPlus, (int)TerminalWorkbenchShortcutAction.IncreaseFontSize)]
    [InlineData((int)TerminalWorkbenchShortcutKey.Subtract, (int)TerminalWorkbenchShortcutAction.DecreaseFontSize)]
    [InlineData((int)TerminalWorkbenchShortcutKey.OemMinus, (int)TerminalWorkbenchShortcutAction.DecreaseFontSize)]
    [InlineData((int)TerminalWorkbenchShortcutKey.D0, (int)TerminalWorkbenchShortcutAction.ResetFontSize)]
    [InlineData((int)TerminalWorkbenchShortcutKey.NumPad0, (int)TerminalWorkbenchShortcutAction.ResetFontSize)]
    public void ControlFontCommandsResolveEveryAlias(int keyValue, int expectedActionValue)
    {
        TerminalWorkbenchShortcutAction action = TerminalWorkbenchShortcutCoordinator.Resolve(
            (TerminalWorkbenchShortcutKey)keyValue,
            TerminalWorkbenchShortcutModifiers.Control);

        Assert.Equal((TerminalWorkbenchShortcutAction)expectedActionValue, action);
    }

    [Theory]
    [InlineData((int)TerminalWorkbenchShortcutKey.S, (int)TerminalWorkbenchShortcutModifiers.Control)]
    [InlineData((int)TerminalWorkbenchShortcutKey.R, (int)TerminalWorkbenchShortcutModifiers.Shift)]
    [InlineData((int)TerminalWorkbenchShortcutKey.S,
        (int)(TerminalWorkbenchShortcutModifiers.Control |
            TerminalWorkbenchShortcutModifiers.Shift |
            TerminalWorkbenchShortcutModifiers.Alt))]
    [InlineData((int)TerminalWorkbenchShortcutKey.R,
        (int)(TerminalWorkbenchShortcutModifiers.Control |
            TerminalWorkbenchShortcutModifiers.Shift |
            TerminalWorkbenchShortcutModifiers.Windows))]
    public void SaveAndRestartRejectMissingOrAdditionalModifiers(int keyValue, int modifierValue)
    {
        Assert.Equal(
            TerminalWorkbenchShortcutAction.None,
            TerminalWorkbenchShortcutCoordinator.Resolve(
                (TerminalWorkbenchShortcutKey)keyValue,
                (TerminalWorkbenchShortcutModifiers)modifierValue));
    }

    [Theory]
    [InlineData((int)TerminalWorkbenchShortcutKey.Add)]
    [InlineData((int)TerminalWorkbenchShortcutKey.OemPlus)]
    [InlineData((int)TerminalWorkbenchShortcutKey.Subtract)]
    [InlineData((int)TerminalWorkbenchShortcutKey.OemMinus)]
    [InlineData((int)TerminalWorkbenchShortcutKey.D0)]
    [InlineData((int)TerminalWorkbenchShortcutKey.NumPad0)]
    public void FontCommandsRejectMissingOrAdditionalModifiers(int keyValue)
    {
        TerminalWorkbenchShortcutKey key = (TerminalWorkbenchShortcutKey)keyValue;

        Assert.Equal(
            TerminalWorkbenchShortcutAction.None,
            TerminalWorkbenchShortcutCoordinator.Resolve(key, TerminalWorkbenchShortcutModifiers.None));
        Assert.Equal(
            TerminalWorkbenchShortcutAction.None,
            TerminalWorkbenchShortcutCoordinator.Resolve(
                key,
                TerminalWorkbenchShortcutModifiers.Control | TerminalWorkbenchShortcutModifiers.Shift));
    }

    [Fact]
    public void UnknownKeyDoesNotResolveWithKnownModifierCombinations()
    {
        Assert.Equal(
            TerminalWorkbenchShortcutAction.None,
            TerminalWorkbenchShortcutCoordinator.Resolve(
                TerminalWorkbenchShortcutKey.Other,
                TerminalWorkbenchShortcutModifiers.Control | TerminalWorkbenchShortcutModifiers.Shift));
    }
}
