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
}
