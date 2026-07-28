using System;
using System.Linq;
using System.Diagnostics;
using System.Windows.Media;
using Terminal.Buffer;
using Terminal.Rendering;

const int lineCount = 100_000;
const int iterations = 500;
var segment = new AnsiTerminalBuffer.TerminalRenderSegmentSnapshot(
    "0123456789", 10, Colors.White, Colors.Black, false, false,
    UnderlineStyle.None, null, false, false, null, false);
var line = new AnsiTerminalBuffer.TerminalRenderLineSnapshot(-1, 10, [segment]);
var lines = Enumerable.Repeat(line, lineCount).ToArray();
using var cache = new TerminalLineRenderCache<ProbeDrawable>();
cache.SetSnapshot(lines, ambiguousAsWide: false);

for (int i = 0; i < 20; i++)
{
    cache.SetSnapshot(lines, ambiguousAsWide: false);
}

long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
var stopwatch = Stopwatch.StartNew();
for (int i = 0; i < iterations; i++)
{
    cache.SetSnapshot(lines, ambiguousAsWide: false);
}
stopwatch.Stop();
long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

Console.WriteLine($"SetSnapshot: {iterations} updates, {lineCount} lines");
Console.WriteLine($"ElapsedMs={stopwatch.Elapsed.TotalMilliseconds:F2}");
Console.WriteLine($"MeanMs={stopwatch.Elapsed.TotalMilliseconds / iterations:F4}");
Console.WriteLine($"AllocatedBytes={allocated}");

var buffer = new AnsiTerminalBuffer(120, 30, lineCount);
stopwatch.Restart();
buffer.Process(string.Concat(Enumerable.Repeat("history line\r\n", lineCount)));
buffer.CreateRenderSnapshot(showCursor: false);
stopwatch.Stop();
Console.WriteLine($"History build: {lineCount} lines");
Console.WriteLine($"ElapsedMs={stopwatch.Elapsed.TotalMilliseconds:F2}");
allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
stopwatch.Restart();
for (int i = 0; i < iterations; i++)
{
    buffer.Process("x");
    buffer.CreateRenderSnapshot(showCursor: false);
}
stopwatch.Stop();
allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
Console.WriteLine($"Buffer snapshot: {iterations} screen-only updates, {lineCount} scrollback lines");
Console.WriteLine($"ElapsedMs={stopwatch.Elapsed.TotalMilliseconds:F2}");
Console.WriteLine($"MeanMs={stopwatch.Elapsed.TotalMilliseconds / iterations:F4}");
Console.WriteLine($"AllocatedBytes={allocated}");

sealed class ProbeDrawable : IDisposable
{
    public void Dispose() { }
}
