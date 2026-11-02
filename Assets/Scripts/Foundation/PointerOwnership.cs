using System.Collections.Generic;

public enum PointerRegion
{
    Interface, Map, Combat, Wheel, Divider
}

public sealed class PointerOwnership
{
    private readonly Dictionary<int, PointerRegion> Owners = new Dictionary<int, PointerRegion>();

    public bool TryAcquire(int Identifier, PointerRegion Region)
    {
        if (Owners.ContainsKey(Identifier))
        {
            return false;
        }
        Owners.Add(Identifier, Region);
        return true;
    }

    public bool IsOwnedBy(int Identifier, PointerRegion Region)
    {
        return Owners.TryGetValue(Identifier, out PointerRegion Owner) && Owner == Region;
    }

    public void Release(int Identifier)
    {
        Owners.Remove(Identifier);
    }

    public void Clear()
    {
        Owners.Clear();
    }
}
