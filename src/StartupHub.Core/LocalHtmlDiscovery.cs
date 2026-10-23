namespace StartupHub.Core;

public static class LocalHtmlDiscovery
{
    private static readonly HashSet<string> SkippedDirectories=new(StringComparer.OrdinalIgnoreCase){".git","node_modules",".venv","venv",".cache",".tools","artifacts","bin","obj"};
    private const int MaxFiles=20000;
    private static bool Html(string path)=>System.IO.Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm";
    public static string? FindCatalog(string appDirectory)
    {
        string? found=null;
        for(var dir=new DirectoryInfo(appDirectory);dir!=null;dir=dir.Parent)
            if(File.Exists(System.IO.Path.Combine(dir.FullName,"data","config.json"))&&File.Exists(System.IO.Path.Combine(dir.FullName,"app","StartupHub.exe")))found=System.IO.Path.Combine(dir.FullName,"data","config.json");
        return found;
    }
    public static DiscoveryReport Scan(IEnumerable<TaskEntry> hints,IEnumerable<TaskEntry> existing,IEnumerable<string>? folders=null)
    {
        var pages=new List<DiscoveredPage>();var issues=new List<DiscoveryIssue>();
        var files=new Dictionary<string,(string Name,string Source)>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in hints.Where(e=>e.Kind==TaskKind.File&&Html(e.Path)))files.TryAdd(System.IO.Path.GetFullPath(entry.Path),(entry.Name,"已有清单"));
        foreach(var folder in files.Keys.Select(System.IO.Path.GetDirectoryName).Where(x=>x!=null).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())ReadDirectory(folder!,false);
        foreach(var folder in folders??[])ReadDirectory(System.IO.Path.GetFullPath(folder),true);
        void ReadDirectory(string directory,bool recursive)
        {
            if(files.Count>MaxFiles)return;
            try
            {
                var options=new EnumerationOptions{AttributesToSkip=FileAttributes.ReparsePoint,IgnoreInaccessible=false};
                foreach(var file in Directory.EnumerateFiles(directory,"*",options))if(Html(file))files.TryAdd(System.IO.Path.GetFullPath(file),("",recursive?"所选文件夹":"项目目录"));
                if(!recursive)return;
                foreach(var child in Directory.EnumerateDirectories(directory,"*",options))
                {
                    if(SkippedDirectories.Contains(System.IO.Path.GetFileName(child)))continue;
                    if(files.Count>MaxFiles){issues.Add(new(directory,"HTML 文件数量超过 "+MaxFiles+"，已提前停止扫描。"));return;}
                    ReadDirectory(child,true);
                }
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){issues.Add(new(directory,"无法读取目录："+ex.Message));}
        }
        foreach(var pair in files)
        {
            try
            {
                using var reader=new StreamReader(pair.Key);var buffer=new char[32768];int count=reader.ReadBlock(buffer,0,buffer.Length);
                if(count==0){issues.Add(new(pair.Key,"HTML 文件为空"));continue;}
                string fallback=System.IO.Path.GetFileNameWithoutExtension(pair.Key);
                pages.Add(new(pair.Value.Name.Length>0?pair.Value.Name:LocalPageDiscovery.Title(new string(buffer,0,count),fallback),"",0,0,"",TaskKind.File,pair.Key,pair.Value.Source));
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){issues.Add(new(pair.Key,"无法读取 HTML："+ex.Message));}
        }
        var filtered=LocalPageDiscovery.ExcludeExisting(pages,existing);
        issues.AddRange(pages.Except(filtered).Select(p=>new DiscoveryIssue(p.Path,"已在当前清单中")));
        return new(filtered.OrderBy(p=>p.Name,StringComparer.CurrentCulture).ToList(),issues);
    }
}
