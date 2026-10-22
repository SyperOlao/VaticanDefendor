using Unity.Mathematics;

public static class CombatBotDamage
{
    public static void CancelAttack(ref CombatBotState Bot)
    {
        Bot.WindupRemaining = 0;
        Bot.DashRemaining = 0;
        Bot.bAttackExecuted = 0;
        Bot.bDashHit = 0;
        Bot.Cooldown = math.max(Bot.Cooldown, 0.5f);
    }

    public static void Apply(ref CombatBotState Bot, float Damage, float3 Direction, float Push, float PushDuration, float LaunchHeight, bool bKeepStronger)
    {
        if (Push > 0)
        {
            float Speed = Push * 2 / PushDuration;
            float Existing = 0;
            if (Bot.PushRemaining > 0)
            {
                Existing = math.length(Bot.Velocity) * Bot.PushRemaining / Bot.PushDuration;
            }
            if (!bKeepStronger || Existing <= Speed)
            {
                Bot.Velocity = Direction * Speed;
                Bot.PushDuration = PushDuration;
                Bot.PushRemaining = PushDuration;
                Bot.LaunchHeight = LaunchHeight;
            }
            CancelAttack(ref Bot);
        }
        Bot.Health -= Damage;
    }
}
