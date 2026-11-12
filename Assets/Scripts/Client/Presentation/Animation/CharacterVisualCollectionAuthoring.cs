using System;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

[Serializable]
public struct CharacterVisualEntry
{
    public int Kind;
    public GameObject Prefab;
    public Color Color;
    public float Scale;
}

public sealed class CharacterVisualCollectionAuthoring : MonoBehaviour
{
    [SerializeField] private CharacterVisualEntry[] _characters;

    private sealed class CharacterVisualCollectionBaker : Baker<CharacterVisualCollectionAuthoring>
    {
        public override void Bake(CharacterVisualCollectionAuthoring Authoring)
        {
            DynamicBuffer<CharacterVisualPrefab> Entries = AddBuffer<CharacterVisualPrefab>(GetEntity(TransformUsageFlags.None));
            foreach (CharacterVisualEntry Entry in Authoring._characters)
            {
                if (Entry.Prefab == null || Entry.Scale <= 0)
                {
                    throw new InvalidOperationException("Character visual collection has an invalid prefab.");
                }
                Entries.Add(new CharacterVisualPrefab
                {
                    Kind = Entry.Kind, Value = GetEntity(Entry.Prefab, TransformUsageFlags.Dynamic),
                    Color = new float4(Entry.Color.r, Entry.Color.g, Entry.Color.b, Entry.Color.a), Scale = Entry.Scale
                });
            }
        }
    }
}

