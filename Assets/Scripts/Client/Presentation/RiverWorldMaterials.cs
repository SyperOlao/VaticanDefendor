using UnityEngine;

public sealed class RiverWorldMaterials : ScriptableObject
{
    [SerializeField] private Material[] _materials;
    [SerializeField] private RiverVegetationModel[] _trees;
    [SerializeField] private RiverVegetationModel[] _grass;
    [SerializeField] private RiverVegetationModel _infiniteWood;
    [SerializeField] private RiverVegetationModel _infiniteStone;
    [SerializeField] private RiverTreeOcclusionMaterial[] _treeOcclusionMaterials;
    [SerializeField, Min(0)] private int _grassInstances = 5200;
    public Material[] Materials => _materials;
    public RiverVegetationModel[] Trees => _trees;
    public RiverVegetationModel[] Grass => _grass;
    public RiverVegetationModel InfiniteWood => _infiniteWood;
    public RiverVegetationModel InfiniteStone => _infiniteStone;
    public RiverTreeOcclusionMaterial[] TreeOcclusionMaterials => _treeOcclusionMaterials;
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
public sealed class RiverTreeOcclusionMaterial
{
    [SerializeField] private Material _opaque;
    [SerializeField] private Material _translucent;
    public Material Opaque => _opaque;
    public Material Translucent => _translucent;
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
