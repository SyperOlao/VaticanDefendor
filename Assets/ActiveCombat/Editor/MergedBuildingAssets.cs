using UnityEngine;

public static partial class ReferenceSceneAssets
{
    public static void CreateMergedBuildings(ActiveCombatGame Game)
    {
        for (int Index = 4; Index < 7; Index++)
        {
            GameObject Building = new GameObject(Game.Balance.Structures[Index].Kind.ToString());
            if (Index == 6)
            {
                Box(Building.transform, "Walls", new Vector3(3, 2.4f, 3), new Vector3(0, 1.2f, 0), Metal("ChurchWalls", "#e5d4a0"));
                GameObject Roof = Part(Building.transform, "Roof", ReferenceGeometry.Cylinder(0, 2.5f, 1.7f, 4), new Vector3(0, 3.1f, 0), Metal("ChurchRoof", "#576d83"));
                Roof.transform.localRotation = Quaternion.Euler(0, 45, 0);
                Material CrossMaterial = Metal("ChurchCross", "#ffd978");
                Box(Building.transform, "Cross", new Vector3(0.18f, 1.3f, 0.18f), new Vector3(0, 4.35f, 0), CrossMaterial);
                Box(Building.transform, "Crossbar", new Vector3(0.8f, 0.18f, 0.18f), new Vector3(0, 4.55f, 0), CrossMaterial);
                Box(Building.transform, "Door", new Vector3(0.9f, 1.5f, 0.12f), new Vector3(0, 0.75f, 1.51f), Metal("ChurchDoor", "#533e2a"));
            }
            else
            {
                string Color = "#7191bd";
                if (Index == 5)
                {
                    Color = "#b98746";
                }
                Box(Building.transform, "Walls", new Vector3(3.1f, 1.8f, 2.8f), new Vector3(0, 0.9f, 0), Metal(Building.name + "Walls", Color));
                GameObject Roof = Part(Building.transform, "Roof", ReferenceGeometry.Cylinder(0, 2.4f, 1.3f, 4), new Vector3(0, 2.4f, 0), Metal("UtilityRoof", "#503c30"));
                Roof.transform.localRotation = Quaternion.Euler(0, 45, 0);
                Box(Building.transform, "Door", new Vector3(0.8f, 1.3f, 0.1f), new Vector3(0, 0.65f, 1.45f), Metal("UtilityDoor", "#201b17"));
                if (Index == 5)
                {
                    GameObject Saw = Part(Building.transform, "Saw", ReferenceGeometry.Cylinder(0.75f, 0.75f, 0.14f, 16), new Vector3(1.5f, 1, 0), Metal("SawmillSaw", "#bfc5cc"));
                    Saw.transform.localRotation = Quaternion.Euler(0, 0, 90);
                }
            }
            Game.StructurePrefabs[Index] = Save(Building, "Merged" + Building.name);
        }
        GameObject Wall = new GameObject("MergedWall");
        Box(Wall.transform, "Wall", new Vector3(1.4f, 1.35f, 0.35f), new Vector3(0, 0.68f, 0), Metal("MergedWall", "#9ca3af"));
        Game.StructurePrefabs[1] = Save(Wall, "MergedWall");
        GameObject GoldBag = new GameObject("GoldBag");
        GameObject Pouch = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Object.DestroyImmediate(Pouch.GetComponent<Collider>());
        Pouch.transform.SetParent(GoldBag.transform, false);
        Pouch.transform.localPosition = new Vector3(0, 0.45f, 0);
        Pouch.transform.localScale = new Vector3(0.72f, 0.96f, 0.64f);
        Pouch.GetComponent<Renderer>().sharedMaterial = Metal("GoldBag", "#d9a83c");
        Game.GoldBagPrefab = Save(GoldBag, "GoldBag");
    }
}
