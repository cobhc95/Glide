namespace Glide.App.Services;

internal static class BrowserSelectionPolicy
{
    public static void Apply(int count, Func<int, string> pathAt, HashSet<string> selection,
        ref int anchor, ref int current, int index, bool extend, bool toggle)
    {
        if ((uint)index >= (uint)count) return;
        if (extend)
        {
            if (anchor < 0) anchor = current >= 0 ? current : index;
            selection.Clear();
            var lo = Math.Min(anchor, index); var hi = Math.Max(anchor, index);
            for (var i = lo; i <= hi; i++) selection.Add(pathAt(i));
        }
        else if (toggle)
        {
            var path = pathAt(index);
            if (!selection.Add(path)) selection.Remove(path);
            anchor = index;
        }
        else
        {
            selection.Clear(); selection.Add(pathAt(index)); anchor = index;
        }
        current = index;
    }
}
