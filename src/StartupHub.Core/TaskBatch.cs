using System.Text.Json;

namespace StartupHub.Core;

public static class TaskBatch
{
    private static HubConfig Copy(HubConfig config) => JsonSerializer.Deserialize<HubConfig>(JsonSerializer.Serialize(config, DataStore.Json), DataStore.Json)!;

    private static HashSet<Guid> Selected(HubConfig config, IEnumerable<Guid> ids)
    {
        var selected = ids.ToHashSet();
        if (selected.Count == 0) throw new ArgumentException("请先选择任务。");
        if (selected.Any(id => !config.Entries.Any(e => e.Id == id))) throw new ArgumentException("选中的任务已不存在，请重新选择。");
        return selected;
    }

    public static HubConfig ChangeGroup(HubConfig source, IEnumerable<Guid> ids, string group)
    {
        if (group == "全部任务" || !source.Groups.Contains(group)) throw new ArgumentException("请选择具体分类。");
        var selected = Selected(source, ids);
        var config = Copy(source);
        var moved = config.Entries.Where(e => selected.Contains(e.Id)).ToList();
        if (moved.All(e => e.Group == group)) return config;
        config.Entries.RemoveAll(e => selected.Contains(e.Id));
        foreach (var entry in moved) entry.Group = group;
        int last = config.Entries.FindLastIndex(e => e.Group == group);
        config.Entries.InsertRange(last < 0 ? config.Entries.Count : last + 1, moved);
        DataStore.Validate(config);
        return config;
    }

    public static HubConfig Delete(HubConfig source, IEnumerable<Guid> ids)
    {
        var selected = Selected(source, ids);
        var config = Copy(source);
        config.Entries.RemoveAll(e => selected.Contains(e.Id));
        foreach (var entry in config.Entries)
            if (entry.DependsOn is { } parent && selected.Contains(parent)) entry.DependsOn = null;
        DataStore.Validate(config);
        return config;
    }
}
