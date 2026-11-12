using System;

namespace VaticanDefendor
{
    [Serializable]
    public sealed class EnemyCombatConfiguration
    {
        public int Kind;
        public float Health;
        public float Speed;
        public float SpeedVariation;
        public float Damage;
        public float Range;
        public float HalfWidth;
        public float Windup;
        public float Recovery;
        public float FormationDistance;
        public float MinimumRange;
        public float DashDistance;
        public float DashDuration;
        public float ThrustLength;
        public float ProjectileSpeed;
        public int SpawnWeight;

        public void Validate()
        {
            foreach (float Value in new[] { Health, Speed, Damage, Range, HalfWidth, Windup, Recovery, FormationDistance })
            {
                if (!float.IsFinite(Value) || Value <= 0)
                {
                    throw new ArgumentException("Invalid enemy combat profile.");
                }
            }
            foreach (float Value in new[] { SpeedVariation, MinimumRange, DashDistance, DashDuration, ThrustLength, ProjectileSpeed })
            {
                if (!float.IsFinite(Value) || Value < 0)
                {
                    throw new ArgumentException("Invalid enemy combat profile parameter.");
                }
            }
            if (Kind < 0 || Kind > 3 || SpawnWeight < 1 || MinimumRange >= Range ||
                Kind == 2 && (DashDistance <= 0 || DashDuration <= 0 || ThrustLength <= 0) ||
                Kind == 3 && ProjectileSpeed <= 0)
            {
                throw new ArgumentException("Invalid enemy combat profile behavior.");
            }
        }

        public static EnemyCombatConfiguration[] CreateDefaults()
        {
            return new[]
            {
                new EnemyCombatConfiguration { Kind = 0, Health = 53, Speed = 2.55f, SpeedVariation = 0.4f, Damage = 7, Range = 3.2f, HalfWidth = 0.65f, Windup = 0.7f, Recovery = 1.3f, FormationDistance = 2.2f, SpawnWeight = 5 },
                new EnemyCombatConfiguration { Kind = 1, Health = 97, Speed = 1.85f, Damage = 12, Range = 2.9f, HalfWidth = 2.9f, Windup = 1.15f, Recovery = 2, FormationDistance = 2.2f, SpawnWeight = 2 },
                new EnemyCombatConfiguration { Kind = 2, Health = 65, Speed = 2.4f, SpeedVariation = 0.2f, Damage = 14, Range = 9, HalfWidth = 0.65f, Windup = 0.9f, Recovery = 2.4f, FormationDistance = 6, DashDistance = 6, DashDuration = 0.45f, ThrustLength = 2.5f, SpawnWeight = 2 },
                new EnemyCombatConfiguration { Kind = 3, Health = 40, Speed = 2.5f, SpeedVariation = 0.2f, Damage = 9, Range = 13, HalfWidth = 0.18f, Windup = 0.85f, Recovery = 1.8f, FormationDistance = 10, MinimumRange = 4, ProjectileSpeed = 11, SpawnWeight = 1 }
            };
        }
    }
}
