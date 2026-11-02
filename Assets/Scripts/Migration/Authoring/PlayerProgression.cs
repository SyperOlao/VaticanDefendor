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

