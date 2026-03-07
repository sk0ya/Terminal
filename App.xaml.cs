using System.Windows;

namespace ConPtyTerminal;

public partial class App : Application
{
    internal TsfUiElementManager? TsfUiElements { get; }

    public App()
    {
        TsfUiElements = TsfUiElementManager.TryInitialize();
        Exit += OnExit;
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        TsfUiElements?.Dispose();
    }
}
