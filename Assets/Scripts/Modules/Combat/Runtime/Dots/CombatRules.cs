using Unity.Mathematics;

namespace VaticanDefendor.Combat
{
    public static class CombatRules
    {
        public static bool TryNormalizeAim(float2 aim, out float2 normalized)
        {
            normalized = float2.zero;
            if (!math.all(math.isfinite(aim))) return false;
            float lengthSquared = math.lengthsq(aim);
            if (lengthSquared < 0.25f || lengthSquared > 1.0201f) return false;
            normalized = aim * math.rsqrt(lengthSquared);
            return true;
        }

        // Saved sword area: forward wedge with 0.4 m tolerance at its edges.
        public static bool SwordHits(float2 attacker, float2 target, float2 facing,
            float rangeMeters, float paddingMeters)
        {
            float2 offset = target - attacker;
            float forward = math.dot(offset, facing);
            float side = math.abs(math.dot(offset, new float2(facing.y, -facing.x)));
            float limit = rangeMeters + paddingMeters;
            return forward >= -paddingMeters && forward <= limit &&
                side <= math.max(0f, forward) + paddingMeters &&
                math.lengthsq(offset) <= limit * limit;
        }
    }
}
