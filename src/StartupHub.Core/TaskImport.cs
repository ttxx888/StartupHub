using System.Runtime.InteropServices;
using System.Text.Json;

namespace StartupHub.Core;

public sealed record ImportCandidate(string Source, TaskEntry? Entry, string Issue);
public sealed record ImportResult(HubConfig Config, IReadOnlyList<TaskEntry> Added, IReadOnlyList<string> Skipped);

public static class TaskImport
{
    private sealed record Identity(string Type, string Target, IReadOnlyList<string> Arguments, string Directory, string Script)
    {
        public bool Matches(Identity other) => Type == other.Type &&
            (Type == "url" ? SameUrl(Target, other.Target) : string.Equals(Target, other.Target, StringComparison.OrdinalIgnoreCase)) &&
            Arguments.SequenceEqual(other.Arguments) && string.Equals(Directory, other.Directory, StringComparison.OrdinalIgnoreCase) && string.Equals(Script, other.Script, StringComparison.OrdinalIgnoreCase);
    }
    private static string FullPath(string value) => value.Length == 0 ? "" : Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
    private static bool SameUrl(string a, string b)
    {
        if (!Uri.TryCreate(a,UriKind.Absolute,out var first) || !Uri.TryCreate(b,UriKind.Absolute,out var second)) return a == b;
        return string.Equals(first.GetLeftPart(UriPartial.Authority),second.GetLeftPart(UriPartial.Authority),StringComparison.OrdinalIgnoreCase) && first.PathAndQuery == second.PathAndQuery && first.Fragment == second.Fragment;
    }
    public static TaskEntry ProcessEntry(TaskEntry entry)
    {
        if (!Path.GetExtension(entry.Path).Equals(".lnk",StringComparison.OrdinalIgnoreCase) || !File.Exists(entry.Path)) return entry;
        var shortcut = WindowsShortcut.Read(entry.Path);
        var resolved = entry.Copy(); resolved.Path = shortcut.Target;
        resolved.Arguments = [..shortcut.Arguments, ..entry.Arguments];
        if (resolved.WorkingDir.Length == 0) resolved.WorkingDir = shortcut.WorkingDirectory;
        return resolved;
    }
    private static Identity Identify(TaskEntry entry)
    {
        if (entry.Kind == TaskKind.Url) return new("url",entry.Url,[],"","");
        TaskEntry resolved;
        try { resolved = ProcessEntry(entry); }
        catch (Exception ex) when (ex is COMException or IOException) { resolved = entry; }
        string type = entry.Kind == TaskKind.File ? "file" : entry.Kind == TaskKind.Network ? "network" : "command";
        return new(type,FullPath(resolved.Path),resolved.Arguments,FullPath(resolved.WorkingDir),FullPath(resolved.Script));
    }
    private static TaskEntry Create(string input, HubConfig config, string group)
    {
        string path = Path.GetFullPath(input);
        if (!File.Exists(path) && !Directory.Exists(path)) throw new InvalidDataException("文件或目录不存在。");
        var parsed = TaskEntrance.Parse(path);
        var entry = new TaskEntry { Name = parsed.SuggestedName, Kind = parsed.Kind, Path = path, Group = group, WaitSeconds = config.DefaultWaitSeconds, AutoStart = false };
        if (Directory.Exists(path)) { entry.Kind = TaskKind.File; entry.Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)); return entry; }
        if (entry.Kind == TaskKind.Url)
        {
            if (!Uri.TryCreate(parsed.Target,UriKind.Absolute,out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidDataException("网址快捷方式缺少有效的 HTTP/HTTPS 地址。");
            entry.Url = parsed.Target; entry.Path = "";
        }
        if (Path.GetExtension(path).Equals(".url",StringComparison.OrdinalIgnoreCase) && entry.Kind != TaskKind.Url) throw new InvalidDataException("网址快捷方式缺少有效地址。");
        if (Path.GetExtension(path).Equals(".lnk",StringComparison.OrdinalIgnoreCase))
        {
            var shortcut = WindowsShortcut.Read(path);
            if (!File.Exists(shortcut.Target) && !Directory.Exists(shortcut.Target)) throw new InvalidDataException("快捷方式指向的文件或目录不存在。");
            entry.Kind = Path.GetExtension(shortcut.Target).Equals(".exe",StringComparison.OrdinalIgnoreCase) ? TaskKind.Desktop : TaskKind.File;
            entry.WorkingDir = shortcut.WorkingDirectory;
        }
        if (TaskEngine.Missing(entry) is { } problem) throw new InvalidDataException(problem);
        return entry;
    }
    public static IReadOnlyList<ImportCandidate> Prepare(IEnumerable<string> paths,HubConfig config,string group)
    {
        var known = config.Entries.Select(e => (Identity:Identify(e),e.Name)).ToList();
        var rows = new List<ImportCandidate>();
        foreach (string path in paths)
        {
            try
            {
                var entry = Create(path,config,group); var identity = Identify(entry);
                var duplicate = known.FindIndex(x => x.Identity.Matches(identity));
                string issue = duplicate >= 0 ? "重复入口：" + known[duplicate].Name : "";
                rows.Add(new(path,entry,issue));
                if (duplicate < 0) known.Add((identity,entry.Name));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or COMException or ArgumentException)
            { rows.Add(new(path,null,ex.Message)); }
        }
        return rows;
    }
    public static ImportResult Append(HubConfig source,IEnumerable<TaskEntry> entries)
    {
        var config = JsonSerializer.Deserialize<HubConfig>(JsonSerializer.Serialize(source,DataStore.Json),DataStore.Json)!;
        var known = source.Entries.Select(Identify).ToList(); var added = new List<TaskEntry>(); var skipped = new List<string>();
        foreach (var entry in entries)
        {
            string? missing = TaskEngine.Missing(entry);
            if (missing != null) { skipped.Add(entry.Name + "：" + missing); continue; }
            var identity = Identify(entry);
            if (known.Any(x => x.Matches(identity))) { skipped.Add(entry.Name + "：入口已存在。"); continue; }
            var copy = entry.Copy(); config.Entries.Add(copy); added.Add(copy); known.Add(identity);
        }
        DataStore.Validate(config);
        return new(config,added,skipped);
    }
}
