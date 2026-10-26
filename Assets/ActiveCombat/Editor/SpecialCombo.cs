using System;

public sealed class SpecialCombo
{
    public float Points { get; private set; }
    public int Uses { get; private set; }
    public float IdleSeconds { get; private set; }
    public float PendingLoss { get; private set; }
    public int Cost
    {
        get
        {
            if (Uses == 0)
            {
                return 6;
            }
            return 11 + Uses;
        }
    }
    public bool bReady => Points >= Cost;
    public float Fraction => Points / Cost;

    public void RecordDamage(float ActualDamage)
    {
        if (ActualDamage <= 0)
        {
            return;
        }
        Points = Math.Min(Cost, Points + ActualDamage / 50f);
        IdleSeconds = 0;
    }

    public void RecordInjury(float ActualDamage)
    {
        if (ActualDamage > 0)
        {
            PendingLoss = Math.Min(Cost, PendingLoss + Cost * 0.15f);
        }
    }

    public void Advance(float DeltaSeconds)
    {
        float PreviousIdle = IdleSeconds;
        IdleSeconds += DeltaSeconds;
        float DecaySeconds = Math.Max(0, IdleSeconds - 3) - Math.Max(0, PreviousIdle - 3);
        float InjuryLoss = Math.Min(PendingLoss, Cost * 0.15f * DeltaSeconds);
        PendingLoss -= InjuryLoss;
        Points = Math.Max(0, Points - Cost * 0.1f * DecaySeconds - InjuryLoss);
    }

    public bool TrySpend()
    {
        if (!bReady)
        {
            return false;
        }
        Uses++;
        Points = 0;
        PendingLoss = 0;
        IdleSeconds = 0;
        return true;
    }

    public void Reset()
    {
        Uses = 0;
        Points = 0;
        PendingLoss = 0;
        IdleSeconds = 0;
    }

    public void Restore(float SavedPoints, int SavedUses)
    {
        Uses = Math.Max(0, SavedUses);
        Points = Math.Clamp(SavedPoints, 0, Cost);
        PendingLoss = 0;
        IdleSeconds = 0;
    }
}
