using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Entities;

public sealed class CombatSnapshotCollection<TState, TView> : IReadOnlyList<TView> where TState : unmanaged, IComponentData where TView : class
{
    private readonly CombatSnapshotStore<TState> Store;
    private readonly Func<Entity, TView> CreateView;
    private readonly Func<TState, bool> Include;
    private readonly Action ClearCommand;
    private readonly Dictionary<Entity, TView> Views = new Dictionary<Entity, TView>();
    private readonly List<TView> Items = new List<TView>();
    private readonly List<Entity> Removed = new List<Entity>();
    private readonly List<Entity> Order = new List<Entity>();

    public CombatSnapshotCollection(CombatSnapshotStore<TState> Store, Func<Entity, TView> CreateView, Action ClearCommand = null, Func<TState, bool> Include = null)
    {
        this.Store = Store;
        this.CreateView = CreateView;
        this.ClearCommand = ClearCommand;
        this.Include = Include;
    }

    public int Count => Items.Count;
    public TView this[int Index] => Items[Index];

    public void Refresh()
    {
        int Position = 0;
        bool bChanged = false;
        foreach (CombatSnapshotEntry<TState> Entry in Store.Entries)
        {
            if (Include != null && !Include(Entry.State))
            {
                continue;
            }
            if (Position >= Order.Count || Order[Position] != Entry.Entity)
            {
                bChanged = true;
            }
            Position++;
        }
        bChanged |= Position != Order.Count;
        if (bChanged)
        {
            RebuildItems();
        }
        Removed.Clear();
        foreach (Entity Entity in Views.Keys)
        {
            if (!Store.Values.ContainsKey(Entity))
            {
                Removed.Add(Entity);
            }
        }
        foreach (Entity Entity in Removed)
        {
            Views.Remove(Entity);
        }
    }

    private void RebuildItems()
    {
        Items.Clear();
        Order.Clear();
        foreach (CombatSnapshotEntry<TState> Entry in Store.Entries)
        {
            if (Include != null && !Include(Entry.State))
            {
                continue;
            }
            if (!Views.TryGetValue(Entry.Entity, out TView View))
            {
                View = CreateView(Entry.Entity);
                Views.Add(Entry.Entity, View);
            }
            Items.Add(View);
            Order.Add(Entry.Entity);
        }
    }

    public bool Contains(TView View)
    {
        return Items.Contains(View);
    }

    public void Clear()
    {
        ClearCommand();
    }

    public TView[] ToArray()
    {
        return Items.ToArray();
    }

    public List<TView>.Enumerator GetEnumerator()
    {
        return Items.GetEnumerator();
    }

    IEnumerator<TView> IEnumerable<TView>.GetEnumerator()
    {
        return Items.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return Items.GetEnumerator();
    }
}
