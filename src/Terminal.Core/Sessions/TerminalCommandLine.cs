using System.Runtime.InteropServices;

namespace Terminal.Sessions;

internal static class TerminalCommandLine
{
    internal static (string FileName, string[] Arguments) SplitCommandLine(string commandLine)
    {
        string text = commandLine.Trim();
        if (text.Length == 0)
        {
            throw new ArgumentException("Command line is required.", nameof(commandLine));
        }

        int argumentCount;
        IntPtr argv = CommandLineToArgvW(text, out argumentCount);
        if (argv == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to parse command line.");
        }

        try
        {
            if (argumentCount <= 0)
            {
                throw new ArgumentException("Command line is required.", nameof(commandLine));
            }

            var parsedArguments = new string[argumentCount];
            for (int index = 0; index < argumentCount; index++)
            {
                IntPtr valuePtr = Marshal.ReadIntPtr(argv, index * IntPtr.Size);
                parsedArguments[index] = Marshal.PtrToStringUni(valuePtr) ?? string.Empty;
            }

            return (parsedArguments[0], parsedArguments.Skip(1).ToArray());
        }
        finally
        {
            _ = LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
