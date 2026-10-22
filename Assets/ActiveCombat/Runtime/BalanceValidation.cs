using UnityEngine;
public partial class ActiveCombatGame
{
    public string ValidateBalance(CombatBalance Candidate)
    {
        if (Candidate.SpecialReadyEdgeOpacity < 0 || Candidate.SpecialReadyEdgeOpacity > 1)
        {
            return "Яркость эффекта готовности должна быть от 0 до 1.";
        }
        if(Candidate.SpecialSacrificeBaseCost < 1 || Candidate.SpecialSacrificeCostIncrease < 0)
        {
            return "Первая спецатака должна требовать хотя бы одну жертву. Рост цены не может быть отрицательным.";
        }
        if(Candidate.MobileView.SlowMotionFactor<=0 || Candidate.MobileView.SlowMotionFactor>1 || Candidate.MobileView.SlowMotionDuration<=0 || Candidate.MobileView.SwipeSensitivity<1 || Candidate.MobileView.SwipeSensitivity>10 || Candidate.MobileView.SwipeEdgePercent<1 || Candidate.MobileView.SwipeEdgePercent>49)
        {
            return "Скорость времени: больше 0 и не выше 1. Длительность: больше 0. Чувствительность: 1–10. Ширина края: 1–49%.";
        }
        if(Candidate.MaximumHealth<=0 || Candidate.MaximumStamina<=0 || Candidate.KillsPerIncrease<1 || Candidate.ChargeSeconds<=Candidate.TapMaximumSeconds || Candidate.DashDuration<=0 || Candidate.PlayerHitDuration<=0 || Candidate.AutoPushDuration<=0 || Candidate.DashPushDuration<=0 || Candidate.LowStaminaThreshold<=0)
        {
            return "Здоровье, длительности, максимум выносливости и порог должны быть положительными. Заряд должен быть дольше тапа.";
        }
        if(Candidate.JoystickCenterRadius<=0 || Candidate.JoystickMovementRadius<=Candidate.JoystickCenterRadius || Candidate.JoystickOuterRadius<=Candidate.JoystickMovementRadius)
        {
            return "Радиусы должны возрастать: центр, движение, внешняя зона.";
        }
        if(Candidate.GridSize!=Rules.GridSize || !Mathf.Approximately(Candidate.CellSize,Rules.CellSize))
        {
            return "Размер сохранённой арены меняется в Inspector и пересозданием сцены.";
        }
        if(Candidate.InitialEnemies>100 || Candidate.InitialPickups>Candidate.MaximumPickups || Candidate.PickupMinimumInterval<=0 || Candidate.PickupMaximumInterval<Candidate.PickupMinimumInterval || Candidate.TrebuchetDelay<=0 || Candidate.PrestigeGrowth<1)
        {
            return "Проверьте количество объектов, интервалы аптечек, задержку требушета и рост ранга.";
        }
        foreach(WeaponBalance Weapon in Candidate.Weapons)
        {
            if(Weapon.AutoInterval<=0 || Weapon.Radius<=0 || Weapon.SpecialDuration<=0 || Weapon.FinishRadius<=0 || Weapon.DefenseDuration<=0)
            {
                return "Интервалы, радиусы и длительности оружия должны быть положительными.";
            }
        }
        foreach(EnemyBalance Enemy in Candidate.Enemies)
        {
            if(Enemy.Health<=0 || Enemy.Windup<=0 || Enemy.Recovery<=0 || Enemy.Kind==CombatEnemyKind.Spearman && Enemy.DashDuration<=0)
            {
                return "Здоровье и длительности противников должны быть положительными.";
            }
        }
        return "";
    }
    public void ApplyBalance(CombatBalance Candidate)
    {
        float HealthFraction = Health / MaximumHealth;
        float Excess = Mathf.Max(0, Stamina - Rules.MaximumStamina);
        float StaminaFraction = Mathf.Min(1, Stamina / Rules.MaximumStamina);
        CombatBalance Previous = Rules;
        Rules = Instantiate(Candidate);
        Root.Simulation.ReplaceConfiguration(Rules);
        Health = MaximumHealth * HealthFraction;
        Stamina = Rules.MaximumStamina * StaminaFraction + Excess;
        Destroy(Previous);
    }
}
