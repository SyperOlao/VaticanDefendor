using System;
using UnityEngine;

public enum ProgressionBonus
{
    SpecialDamage, DashDistance, DashDamage, ChargeDuration, SpecialRadius,
    MovementSpeed, StaminaRecovery, IncomingDamage, DefenseStrength
}

[Serializable]
public sealed class UpgradeMilestone
{
    public bool bHealth;
    public int Level;
    public ProgressionBonus Bonus;
    public float Multiplier;
}

public partial class ActiveCombatGame
{
    private readonly int[] WeaponUpgradeCounts = new int[3];

    public float SpecialChargeSeconds
    {
        get
        {
            return Rules.ChargeSeconds * GetProgressionMultiplier(ProgressionBonus.ChargeDuration);
        }
    }

    public float GetProgressionMultiplier(ProgressionBonus Bonus)
    {
        float Multiplier = 1;
        foreach (UpgradeMilestone Milestone in Rules.UpgradeMilestones)
        {
            int Count = AttackUpgrades;
            if (Milestone.bHealth)
            {
                Count = HealthUpgrades;
            }
            if (Milestone.Bonus == Bonus && Count >= Milestone.Level)
            {
                Multiplier *= Milestone.Multiplier;
            }
        }
        return Multiplier;
    }

    public void ResetProgression()
    {
        Rules.MaximumHealth -= HealthUpgrades * 25;
        for (int Index = 0; Index < WeaponUpgradeCounts.Length; Index++)
        {
            Rules.Weapons[Index].AutoDamage -= WeaponUpgradeCounts[Index] * 10;
            WeaponUpgradeCounts[Index] = 0;
        }
        AttackUpgrades = 0;
        HealthUpgrades = 0;
    }
}
