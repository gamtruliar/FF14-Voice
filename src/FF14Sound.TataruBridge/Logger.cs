using System.Diagnostics;

namespace FFXIVTataruHelper
{
    // Adapter for the static diagnostics used by the linked reader sources.
    internal static class Logger
    {
        public static bool RawDialogLogEnabled => false;
        public static void WriteRawDialogLog(string text) => Trace.WriteLine(text);
        public static void WriteLog(string text) => Trace.WriteLine(text);
    }
}
