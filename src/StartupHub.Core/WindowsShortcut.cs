using System.Runtime.InteropServices;

namespace StartupHub.Core;

public sealed record WindowsShortcut(string Target, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    public static WindowsShortcut Read(string path)
    {
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object? link = null;
        try
        {
            link = ((dynamic)shell).CreateShortcut(Path.GetFullPath(path));
            string target = Environment.ExpandEnvironmentVariables((string)((dynamic)link).TargetPath);
            string arguments = (string)((dynamic)link).Arguments;
            string directory = Environment.ExpandEnvironmentVariables((string)((dynamic)link).WorkingDirectory);
            return new(target, NativeProcess.SplitArguments(arguments), directory);
        }
        finally
        {
            if (link != null) Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
