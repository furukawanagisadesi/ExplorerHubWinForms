namespace ExplorerHubWinForms;

/// <summary>
/// 计算标签标题的纯逻辑(不依赖 WinForms, 便于单独验证)。
/// 平时显示叶子名; 同名时向上找能区分开的最小父级层级并括注; 路径完全相同的标签
/// 无法靠父级名区分, 只对这部分按出现顺序编号。
/// </summary>
internal static class TabTitlePlanner
{
    internal readonly record struct TabInfo(
        string DisplayName,
        string FullPath,
        IReadOnlyList<string> PathSegments);

    /// <summary>返回与输入顺序一一对应的标题。</summary>
    internal static IReadOnlyList<string> Plan(IReadOnlyList<TabInfo> tabs)
    {
        var titles = new string[tabs.Count];
        var groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < tabs.Count; i++)
        {
            if (!groups.TryGetValue(tabs[i].DisplayName, out var list))
            {
                list = new List<int>();
                groups[tabs[i].DisplayName] = list;
            }

            list.Add(i);
        }

        foreach (var group in groups.Values)
        {
            if (group.Count == 1)
            {
                titles[group[0]] = tabs[group[0]].DisplayName;
                continue;
            }

            // 组内再按完整路径拆子组: 路径完全相同的标签无法靠父级名区分, 只能编号。
            var pathGroups = new List<List<int>>();
            var pathGroupByKey = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var index in group)
            {
                var key = tabs[index].FullPath;
                if (!pathGroupByKey.TryGetValue(key, out var sub))
                {
                    sub = new List<int>();
                    pathGroupByKey[key] = sub;
                    pathGroups.Add(sub);
                }

                sub.Add(index);
            }

            // 全部是同一文件夹: 按出现顺序编号兜底(第一个保持叶子名)。
            var depth = pathGroups.Count == 1
                ? -1
                : FindDistinguishingDepth(pathGroups.Select(g => tabs[g[0]]).ToList());

            if (depth < 0)
            {
                for (var i = 0; i < group.Count; i++)
                {
                    titles[group[i]] = i == 0
                        ? tabs[group[i]].DisplayName
                        : $"{tabs[group[i]].DisplayName} ({i + 1})";
                }

                continue;
            }

            // 优先只显示“最先区分开”的那一级祖先名; 若它们在组内仍会撞名, 就用完整祖先链。
            var reps = pathGroups.Select(g => tabs[g[0]]).ToList();
            var hints = reps.Select(r => AncestorAtLevel(r, depth)).ToList();
            var useChain = hints.Distinct(StringComparer.OrdinalIgnoreCase).Count() != hints.Count;

            foreach (var pathGroup in pathGroups)
            {
                var rep = tabs[pathGroup[0]];
                var suffix = useChain ? AncestorChain(rep, depth) : AncestorAtLevel(rep, depth);
                for (var i = 0; i < pathGroup.Count; i++)
                {
                    var name = tabs[pathGroup[i]].DisplayName;
                    titles[pathGroup[i]] = i == 0
                        ? $"{name} ({suffix})"
                        : $"{name} ({suffix}) ({i + 1})";
                }
            }
        }

        return titles;
    }

    /// <summary>
    /// 找到能让本组路径互不相同的最小父级层级(1 表示只加一级父级)。找不到返回 -1。
    /// </summary>
    private static int FindDistinguishingDepth(List<TabInfo> members)
    {
        var maxDepth = members.Max(p => p.PathSegments.Count - 1);
        for (var depth = 1; depth <= maxDepth; depth++)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unique = true;
            foreach (var member in members)
            {
                if (!seen.Add(QualifiedName(member, depth)))
                {
                    unique = false;
                    break;
                }
            }

            if (unique)
            {
                return depth;
            }
        }

        return -1;
    }

    /// <summary>取路径末尾 depth+1 级(叶子 + depth 级祖先)拼成的限定名。</summary>
    private static string QualifiedName(TabInfo tab, int depth)
    {
        var segments = tab.PathSegments;
        var take = Math.Min(depth + 1, segments.Count);
        return string.Join("\\", segments.Skip(segments.Count - take));
    }

    /// <summary>取从叶子往上数第 depth 级的祖先名(即最先区分开的那一级)。</summary>
    private static string AncestorAtLevel(TabInfo tab, int depth)
    {
        var segments = tab.PathSegments;
        var index = segments.Count - 1 - depth;
        return index >= 0 && index < segments.Count ? segments[index] : tab.DisplayName;
    }

    /// <summary>取路径末尾 depth 级祖先拼成的链, 用于祖先名本身仍会撞名时的兜底。</summary>
    private static string AncestorChain(TabInfo tab, int depth)
    {
        var segments = tab.PathSegments;
        var end = segments.Count - 1;
        var start = Math.Max(0, end - depth);
        return start >= end ? tab.DisplayName : string.Join("\\", segments.Skip(start).Take(end - start));
    }
}
