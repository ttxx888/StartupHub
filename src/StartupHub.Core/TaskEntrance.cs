namespace StartupHub.Core;

public sealed record TaskEntrance(TaskKind Kind, string Target, string SuggestedName)
{
    public static TaskEntrance Parse(string input)
    {
        string value = input.Trim().Trim('"');
        string extension = Path.GetExtension(value).ToLowerInvariant();
        if (extension == ".lnk" && File.Exists(value))
        {
            try
            {
                var shortcut = WindowsShortcut.Read(value);
                return new(Path.GetExtension(shortcut.Target).Equals(".exe",StringComparison.OrdinalIgnoreCase) ? TaskKind.Desktop : TaskKind.File, value, Path.GetFileNameWithoutExtension(value));
            }
            catch (System.Runtime.InteropServices.COMException) { return new(TaskKind.Desktop,value,Path.GetFileNameWithoutExtension(value)); }
        }
        if (extension == ".url" && File.Exists(value))
        {
            string? address = File.ReadLines(value).FirstOrDefault(x => x.StartsWith("URL=", StringComparison.OrdinalIgnoreCase));
            if (address != null) return new(TaskKind.Url, address[4..].Trim(), Path.GetFileNameWithoutExtension(value));
        }
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            string name = Uri.TryCreate(value, UriKind.Absolute, out var address) ? address.Host : "";
            return new(TaskKind.Url, value, name);
        }
        var kind = extension switch
        {
            ".exe" or ".lnk" => TaskKind.Desktop,
            ".ps1" or ".cmd" or ".bat" or ".py" or ".js" or ".mjs" or ".cjs" => TaskKind.Script,
            _ => TaskKind.File
        };
        return new(kind, value, Path.GetFileNameWithoutExtension(value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
    }
}
