using System.Buffers;
using Microsoft.Extensions.Logging;
using Utf8StringInterpolation;
using ZLogger;
using static Wlrix.Common.Logging.AnsiColors;

namespace Wlrix.Common.Logging;

/// <summary>
/// Formatter for logging to the console.
/// </summary>
public class LogConsoleFormatter : IZLoggerFormatter
{
    public void FormatLogEntry(IBufferWriter<byte> writer, IZLoggerEntry entry)
    {
        using var utf8Writer = new Utf8StringWriter<IBufferWriter<byte>>(writer);

        switch (entry.LogInfo.LogLevel)
        {
            case LogLevel.Trace:
                utf8Writer.Append($"{White}TRACE{Reset}");
                break;
            case LogLevel.Debug:
                utf8Writer.Append($"{Blue}DEBUG{Reset}");
                break;
            case LogLevel.Information:
                utf8Writer.Append($"{Green}INFO {Reset}");
                break;
            case LogLevel.Warning:
                utf8Writer.Append($"{Yellow}WARN {Reset}");
                break;
            case LogLevel.Error:
                utf8Writer.Append($"{Red}ERROR{Reset}");
                break;
            case LogLevel.Critical:
                utf8Writer.Append($"{BrightRed}CRIT {Reset}");
                break;
            case LogLevel.None:
                utf8Writer.Append("NONE");
                break;
        }

        utf8Writer.Append($" ({entry.LogInfo.Category}) ");
        utf8Writer.Append(entry.ToString());

        if (entry.LogInfo.Exception is not { } ex)
            return;

        utf8Writer.AppendLine();
        utf8Writer.Append(ex.ToString());
    }

    public bool WithLineBreak => true;
}
