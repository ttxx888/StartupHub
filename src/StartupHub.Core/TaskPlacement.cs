namespace StartupHub.Core;

public static class TaskPlacement
{
    public static bool ReorderGroup(HubConfig config, string source, string target, bool after)
    {
        int oldIndex = config.Groups.IndexOf(source), targetIndex = config.Groups.IndexOf(target);
        if (oldIndex < 0 || targetIndex < 0) throw new ArgumentException("请选择具体分类调整顺序。");
        if (source == target) return false;
        int destination = targetIndex + (after ? 1 : 0);
        if (oldIndex < destination) destination--;
        if (destination == oldIndex) return false;
        config.Groups.RemoveAt(oldIndex); config.Groups.Insert(destination, source);
        return true;
    }
    public static bool Reorder(HubConfig config, Guid sourceId, Guid targetId, bool after)
    {
        var source = config.Entries.FirstOrDefault(x => x.Id == sourceId) ?? throw new ArgumentException("找不到要移动的任务。");
        var target = config.Entries.FirstOrDefault(x => x.Id == targetId) ?? throw new ArgumentException("找不到目标任务。");
        if (source == target) return false;
        int oldIndex = config.Entries.IndexOf(source), targetIndex = config.Entries.IndexOf(target);
        int destination = targetIndex + (after ? 1 : 0);
        if (oldIndex < destination) destination--;
        if (destination == oldIndex) return false;
        config.Entries.RemoveAt(oldIndex); config.Entries.Insert(destination, source);
        return true;
    }

    public static bool ChangeGroup(HubConfig config, Guid sourceId, string group)
    {
        if (group == "全部任务" || !config.Groups.Contains(group)) throw new ArgumentException("请拖到一个具体分类。");
        var source = config.Entries.FirstOrDefault(x => x.Id == sourceId) ?? throw new ArgumentException("找不到要移动的任务。");
        if (source.Group == group) return false;
        config.Entries.Remove(source);
        int last = config.Entries.FindLastIndex(x => x.Group == group);
        config.Entries.Insert(last < 0 ? config.Entries.Count : last + 1, source);
        source.Group = group;
        return true;
    }
}
