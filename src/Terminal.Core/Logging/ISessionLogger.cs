namespace Terminal.Logging;

internal interface ISessionLogger : IDisposable
{
    void LogSessionStart(string tool, string command, string cwd, int pid, short cols, short rows);
    void LogInput(string text);
    void LogOutput(string text);
    void LogSessionEnd(int exitCode);
}
