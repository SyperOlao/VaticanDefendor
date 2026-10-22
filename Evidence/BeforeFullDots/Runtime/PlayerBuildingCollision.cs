using UnityEngine;

public partial class ActiveCombatGame
{
    private void ResolvePlayerBuildings()
    {
        foreach (StructureState Structure in Structures)
        {
            if (Structure.Rules.Kind == CombatStructureKind.Trap)
            {
                continue;
            }
            if (MovementAttack != null && !MovementAttack.bSpecial && Structure.Rules.Kind == CombatStructureKind.Wall)
            {
                continue;
            }
            Vector3 Offset = PlayerPosition - Structure.Position;
            float Horizontal = Structure.HalfExtents.x + 0.35f;
            float Vertical = Structure.HalfExtents.y + 0.35f;
            if (Mathf.Abs(Offset.x) < Horizontal && Mathf.Abs(Offset.z) < Vertical)
            {
                if (Horizontal - Mathf.Abs(Offset.x) < Vertical - Mathf.Abs(Offset.z))
                {
                    float Sign = Mathf.Sign(Offset.x);
                    PlayerPosition.x = Structure.Position.x + Sign * Horizontal;
                }
                else
                {
                    float Sign = Mathf.Sign(Offset.z);
                    PlayerPosition.z = Structure.Position.z + Sign * Vertical;
                }
            }
        }
    }
}
