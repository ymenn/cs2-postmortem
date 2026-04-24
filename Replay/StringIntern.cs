namespace Postmortem.Replay;

// Plugin-owned string intern table for ModelName + ActiveWeaponDesignerName.
// Typical map has ~22 unique entries; bounded, no eviction needed. Purpose:
// ensure each unique string lives once in memory instead of being re-allocated
// per sampler tick — CSSharp schema reads return fresh string instances even
// for the same underlying native value.
//
// Not thread-safe; caller (the sampler) runs on the game thread.
public sealed class StringIntern
{
    private readonly Dictionary<string, string> _map = new(32);

    public string? Intern(string? s)
    {
        if (s is null) return null;
        if (_map.TryGetValue(s, out var canon)) return canon;
        _map[s] = s;
        return s;
    }

    public int Count => _map.Count;

    public void Clear() => _map.Clear();
}
