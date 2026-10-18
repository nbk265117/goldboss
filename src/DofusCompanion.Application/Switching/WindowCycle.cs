namespace DofusCompanion.Application.Switching;

/// <summary>
/// Order of the Dofus windows for the TAB switch: windows keep the place they had when first seen, so the
/// characters always come in the same order; new windows go at the end (in the order given).
/// </summary>
public sealed class WindowCycle
{
    private readonly List<nint> _order = [];

    public IReadOnlyList<nint> Order => _order;

    /// <summary>Forgets closed windows and appends new ones.</summary>
    public void Update(IEnumerable<nint> current)
    {
        var alive = current.Distinct().ToList();
        _order.RemoveAll(h => !alive.Contains(h));
        foreach (var h in alive)
            if (!_order.Contains(h)) _order.Add(h);
    }

    /// <summary>The window after <paramref name="from"/> (step 1) or before it (step -1); 0 when there is no other window.</summary>
    public nint Next(nint from, int step = 1)
    {
        var n = _order.Count;
        if (n == 0) return 0;
        var i = _order.IndexOf(from);
        if (i < 0) return _order[step >= 0 ? 0 : n - 1];
        if (n == 1) return 0;
        return _order[((i + step) % n + n) % n];
    }
}
