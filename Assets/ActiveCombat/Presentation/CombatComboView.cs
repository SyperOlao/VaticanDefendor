public sealed class CombatComboView
{
    private readonly ActiveCombatGame Game;
    public CombatComboView(ActiveCombatGame Game)
    {
        this.Game = Game;
    }

    public float Points => Game.Player.ComboPoints;
    public int Uses => Game.Player.SpecialUses;
    public float IdleSeconds => Game.Player.ComboIdleSeconds;
    public float PendingLoss => Game.Player.ComboPendingLoss;
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

    public void Reset()
    {
        Restore(0, 0);
    }

    public void Restore(float Points, int Uses)
    {
        Game.Dispatch(new CombatCommand { Kind = CombatCommandKind.RestoreCombo, Value = Points, Index = Uses }, true);
    }

    public void RecordDamage(float Amount)
    {
        Game.Dispatch(new CombatCommand { Kind = CombatCommandKind.RecordPersonalDamage, Value = Amount }, true);
    }
}
