using System.Threading.Tasks;
using System.Windows;

using Terminal.Logging;

namespace Terminal;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _ = Task.Run(() => SessionLogWriter.CompressOldDayFiles());
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (!SystemParameters.HighContrast) return;
        foreach (Window window in Windows)
            if (window is not global::Terminal.MainWindow) HighContrastAppearance.Apply(window, active: true);
    }
}
