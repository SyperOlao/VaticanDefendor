using UnityEngine;

public sealed class PlayerAppearancePresenter : MonoBehaviour
{
    public ActiveCombatGame Game;
    public Texture2D[] ArmorTextures;
    public Texture2D[] MetalTextures;
    private int PreviousAttack = -1;
    private int PreviousHealth = -1;
    private int PreviousWeapon = -1;
    private MaterialPropertyBlock Properties;

    private void Awake()
    {
        Properties = new MaterialPropertyBlock();
    }

    private void LateUpdate()
    {
        if (Game.PlayerModel == null || PreviousAttack == Game.AttackUpgrades && PreviousHealth == Game.HealthUpgrades && PreviousWeapon == Game.WeaponIndex)
        {
            return;
        }
        PreviousAttack = Game.AttackUpgrades;
        PreviousHealth = Game.HealthUpgrades;
        PreviousWeapon = Game.WeaponIndex;
        foreach (Renderer Renderer in Game.PlayerModel.GetComponentsInChildren<Renderer>())
        {
            bool bArmor = Renderer.name == "Torso" || Renderer.name == "Shield";
            bool bHelmet = Renderer.name == "Helmet";
            bool bWeapon = Renderer.transform.IsChildOf(Game.PlayerModel.transform.Find("WeaponPivot"));
            if (!bArmor && !bHelmet && !bWeapon)
            {
                continue;
            }
            int Count = Game.HealthUpgrades;
            Texture2D[] Textures = MetalTextures;
            if (bWeapon)
            {
                Count = Game.AttackUpgrades;
            }
            if (bArmor)
            {
                Textures = ArmorTextures;
            }
            Properties.Clear();
            if (Count > 0)
            {
                int Tier = Mathf.Clamp(Mathf.CeilToInt(Count / 3f), 1, 3) - 1;
                Properties.SetTexture("_BaseMap", Textures[Tier]);
                Properties.SetColor("_BaseColor", Color.white);
                Properties.SetFloat("_Metallic", 0.65f);
                Properties.SetFloat("_Smoothness", 0.65f);
            }
            Renderer.SetPropertyBlock(Properties);
        }
    }
}
