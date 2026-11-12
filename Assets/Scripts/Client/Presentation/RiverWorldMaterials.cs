using UnityEngine;

public sealed class RiverWorldMaterials : ScriptableObject
{
    [SerializeField] private Material[] _materials;
    [SerializeField] private RiverVegetationModel[] _trees;
    [SerializeField] private RiverVegetationModel[] _grass;
    [SerializeField, Min(0)] private int _grassInstances = 5200;
    public Material[] Materials => _materials;
    public RiverVegetationModel[] Trees => _trees;
    public RiverVegetationModel[] Grass => _grass;
    public int GrassInstances => _grassInstances;

#if UNITY_EDITOR
    public void SetVegetation(RiverVegetationModel[] Trees, RiverVegetationModel[] Grass)
    {
        _trees = Trees;
        _grass = Grass;
    }
#endif
}

[System.Serializable]
public sealed class RiverVegetationModel
{
    [SerializeField] private Mesh _mesh;
    [SerializeField] private Material[] _materials;
    public Mesh Mesh => _mesh;
    public Material[] Materials => _materials;

    public RiverVegetationModel(Mesh Mesh, Material[] Materials)
    {
        _mesh = Mesh;
        _materials = Materials;
    }
}
