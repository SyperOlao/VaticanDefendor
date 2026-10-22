using System;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

public static class CombatEcsRenderingVerification
{
    private static int Checks;

    public static string Run()
    {
        ActiveCombatGame Game = UnityEngine.Object.FindAnyObjectByType<ActiveCombatGame>();
        bool bEnabled = Game.enabled;
        Game.enabled = false;
        Checks = 0;
        EntityManager Manager = Game.Root.Simulation.Manager;
        try
        {
            Game.Session.StartTutorial();
            Entity Player = Game.Root.Simulation.PlayerEntity;
            for (int WeaponIndex = 0; WeaponIndex < 3; WeaponIndex++)
            {
                Game.EquipWeapon(WeaponIndex);
                Game.FlushCommands();
                Entity WeaponPart = Entity.Null;
                foreach (LinkedEntityGroup Entry in Manager.GetBuffer<LinkedEntityGroup>(Player))
                {
                    if (!Manager.HasComponent<CombatRenderPart>(Entry.Value))
                    {
                        continue;
                    }
                    CombatRenderPart Part = Manager.GetComponentData<CombatRenderPart>(Entry.Value);
                    if (Part.WeaponIndex >= 0)
                    {
                        Require(Manager.IsComponentEnabled<MaterialMeshInfo>(Entry.Value) == (Part.WeaponIndex == WeaponIndex), "Only the selected weapon may be rendered.");
                        if (Part.WeaponIndex == WeaponIndex)
                        {
                            WeaponPart = Entry.Value;
                        }
                    }
                    if (Part.Animation < 4)
                    {
                        float4 Expected = Part.Color;
                        if (QualitySettings.activeColorSpace == ColorSpace.Linear)
                        {
                            Expected = CombatRenderColor.ToLinear(Expected);
                        }
                        Require(math.cmax(math.abs(Manager.GetComponentData<URPMaterialPropertyBaseColor>(Entry.Value).Value - Expected)) < 0.0001f,
                            "Native material properties must retain the authoring color space.");
                    }
                }
                float4x4 Rest = Manager.GetComponentData<LocalToWorld>(WeaponPart).Value;
                Game.Combo.Restore(Game.SpecialSacrificeCost, 0);
                Game.BeginGesture(new Vector2(100, 200));
                Game.FlushCommands();
                Game.Simulate(0.3f);
                float4x4 Charged = Manager.GetComponentData<LocalToWorld>(WeaponPart).Value;
                Require(math.cmax(math.abs(Rest.c0 - Charged.c0)) + math.cmax(math.abs(Rest.c1 - Charged.c1)) > 0.01f,
                    "Charging must animate the selected weapon through native matrices.");
                int ChargeParts = 0;
                foreach (LinkedEntityGroup Entry in Manager.GetBuffer<LinkedEntityGroup>(Player))
                {
                    if (Manager.HasComponent<CombatRenderPart>(Entry.Value) && Manager.GetComponentData<CombatRenderPart>(Entry.Value).Animation == 4
                        && Manager.IsComponentEnabled<MaterialMeshInfo>(Entry.Value))
                    {
                        ChargeParts++;
                    }
                }
                Require(ChargeParts > 0, "Charging must enable its native effect entities.");
                Game.CancelGesture();
                Game.FlushCommands();
            }
            Game.Session.StartGame();
            for (int Wave = 1; Wave <= 9; Wave++)
            {
                if (Wave > 1)
                {
                    Game.ClearEnemies();
                    Game.Simulate(Game.Rules.WaveDelay + 0.01f);
                }
                Require(Game.WaveNumber == Wave && Game.Enemies.Count == Game.Rules.InitialEnemies + (Wave - 1) * Game.Rules.EnemiesPerWave,
                    "Wave progression must retain its population rules.");
                int Tier = 0;
                if (Wave >= 3)
                {
                    Tier = 1;
                }
                if (Wave >= 6)
                {
                    Tier = 2;
                }
                if (Wave >= 9)
                {
                    Tier = 3;
                }
                foreach (ActiveCombatGame.EnemyState Enemy in Game.Enemies)
                {
                    CombatEnemyIdentity Identity = Manager.GetComponentData<CombatEnemyIdentity>(Enemy.Entity);
                    int Model = Manager.GetComponentData<CombatModelState>(Enemy.Entity).ModelIndex;
                    Require(Identity.ArmorTier == Tier && Model == CombatModelIndex.Enemy + Identity.KindIndex * 4 + Tier,
                        "Enemy armor must select the corresponding Entities Graphics model.");
                }
            }
            return $"Passed {Checks} native rendering, color, weapon animation and armor checks.";
        }
        finally
        {
            Game.Session.ShowMainMenu();
            Game.enabled = bEnabled;
        }
    }

    private static void Require(bool bCondition, string Message)
    {
        Checks++;
        if (!bCondition)
        {
            throw new InvalidOperationException(Message);
        }
    }
}
