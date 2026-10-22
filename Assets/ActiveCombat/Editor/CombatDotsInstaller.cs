using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class CombatDotsInstaller
{
    public static string Configure()
    {
        if (Application.isPlaying)
        {
            throw new System.InvalidOperationException("DOTS authoring must be configured in Edit Mode.");
        }
        ActiveCombatGame Game = Object.FindAnyObjectByType<ActiveCombatGame>();
        CombatRenderCatalog Catalog = CombatRenderCatalogBuilder.Build();
        Transform Authoring = Game.transform.Find("EntityAuthoring");
        if (Authoring == null)
        {
            Authoring = new GameObject("EntityAuthoring").transform;
            Authoring.SetParent(Game.transform, false);
            Game.SceneryRoot.SetParent(Authoring, true);
            Game.transform.Find("ReferenceArena").SetParent(Authoring, true);
        }
        Authoring.gameObject.tag = "EditorOnly";
        Game.Root.AuthoringRoot = Authoring.gameObject;
        Game.Root.RenderCatalog = Catalog;
        Game.DynamicRoot.name = "WorldInterface";
        Game.Hud.Mobile.gameObject.SetActive(true);
        EditorUtility.SetDirty(Game.Root);
        EditorUtility.SetDirty(Game);
        EditorSceneManager.MarkSceneDirty(Game.gameObject.scene);
        EditorSceneManager.SaveScene(Game.gameObject.scene);
        AssetDatabase.SaveAssets();
        return $"DOTS scene configured with {Catalog.Models.Length} models and {Catalog.Materials.Length} materials.";
    }
}
