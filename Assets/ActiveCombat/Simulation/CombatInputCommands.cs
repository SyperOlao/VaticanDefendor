using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

[BurstCompile]
public struct CombatInputCommandsJob : IJob
{
    public CombatGameplayContext Context;
    [ReadOnly] public NativeArray<CombatCommand> InputCommands;
    public NativeList<int2> WallCells;
    public NativeParallelHashSet<int2> UniqueWallCells;

    public void Execute()
    {
        for (int Index = 0; Index < InputCommands.Length; Index++)
        {
            CombatCommand Command = InputCommands[Index];
            Context.Match.LastCommandIdentity = Command.Identity;
            Context.Match.LastCommandSucceeded = 1;
            if (Command.Kind == CombatCommandKind.SetSession)
            {
                Context.Match.bMainMenu = Command.Flags & 1;
                Context.Match.bTutorial = (Command.Flags >> 1) & 1;
                Context.Match.bTutorialBlocked = (Command.Flags >> 2) & 1;
                Context.Match.bPaused = (Command.Flags >> 3) & 1;
                Context.Match.bSettings = (Command.Flags >> 4) & 1;
                Context.Match.bMapOpen = (Command.Flags >> 5) & 1;
                Context.Match.bWeaponWheelOpen = (Command.Flags >> 6) & 1;
            }
            else if (Command.Kind == CombatCommandKind.NavigationMeshReady)
            {
                Context.Match.NavigationReadyRevision = Command.Index;
            }
            else if (Command.Kind == CombatCommandKind.SetMapTime)
            {
                Context.Match.SlowMotionFactor = Command.Value;
                Context.Match.SlowMotionDuration = Command.ExtraValue;
                Context.Match.SlowMotionRemaining = Command.Parameters.x;
                Context.Match.MapTimeMode = Command.Index;
                Context.Match.bInfiniteSlowMotion = Command.Flags;
            }
            else if (Command.Kind == CombatCommandKind.KeyboardMovement)
            {
                Context.Player.KeyboardMovement = Command.Position.xy;
                Context.Player.CameraRight = Command.Direction;
                Context.Player.CameraForward = Command.SecondaryDirection;
            }
            else if (Command.Kind == CombatCommandKind.CancelGesture)
            {
                Context.CancelGesture();
            }
            else if (Command.Kind == CombatCommandKind.EquipWeapon)
            {
                Context.CancelGesture();
                Context.Player.WeaponIndex = Command.Index;
            }
            else if (Command.Kind == CombatCommandKind.SetPlayerValue || Command.Kind == CombatCommandKind.SetEnemyValue
                || Command.Kind == CombatCommandKind.RestoreCombo || Command.Kind == CombatCommandKind.ClearProgression
                || Command.Kind == CombatCommandKind.SpawnEnemy || Command.Kind == CombatCommandKind.ClearEnemies
                || Command.Kind == CombatCommandKind.DamageEnemy || Command.Kind == CombatCommandKind.DamagePlayer
                || Command.Kind == CombatCommandKind.SetBuildingValue || Command.Kind == CombatCommandKind.RecordPersonalDamage
                || Command.Kind == CombatCommandKind.AddExperience || Command.Kind == CombatCommandKind.Respawn)
            {
                Context.ApplySetupCommand(Command);
            }
            else if (Context.Match.bMainMenu != 0 || Context.Match.bTutorialBlocked != 0 || Context.Match.bPaused != 0 || Context.Match.bSettings != 0 || Context.Match.bDefeated != 0)
            {
                Context.Match.LastCommandSucceeded = 0;
            }
            else if (Command.Kind == CombatCommandKind.PlaceWall)
            {
                WallCells.Clear();
                UniqueWallCells.Clear();
                int Identity = Command.Identity;
                while (Index < InputCommands.Length && InputCommands[Index].Kind == CombatCommandKind.PlaceWall && InputCommands[Index].Identity == Identity)
                {
                    if (UniqueWallCells.Add(InputCommands[Index].Cell))
                    {
                        WallCells.Add(InputCommands[Index].Cell);
                    }
                    Index++;
                }
                Index--;
                Context.BuildWallPath(WallCells.AsArray());
            }
            else
            {
                Context.ApplyGameplayCommand(Command);
            }
        }
    }
}

public partial struct CombatGameplayContext
{
    public void CancelGesture()
    {
        Player.bGestureActive = 0;
        Player.Charge = 0;
        Player.bAttackSelected = 0;
    }

    public void ApplyGameplayCommand(CombatCommand Command)
    {
        if (Command.Kind == CombatCommandKind.BeginGesture)
        {
            Player.bGestureActive = 1;
            Player.GestureStart = Command.Position.xy;
            Player.GestureOffset = float2.zero;
            Player.GestureAge = 0;
            Player.GestureStartedAt = Command.Timestamp;
            Player.CameraRight = Command.Direction;
            Player.CameraForward = Command.SecondaryDirection;
            Player.Charge = 0;
            Player.MaximumGestureDistance = 0;
            Player.bAttackSelected = 0;
            Player.bDashArmed = 1;
            FocusNearest();
        }
        else if (Command.Kind == CombatCommandKind.MoveGesture)
        {
            MoveGesture(Command);
        }
        else if (Command.Kind == CombatCommandKind.EndGesture)
        {
            EndGesture(Command);
        }
        else if (Command.Kind == CombatCommandKind.Dash)
        {
            StartDash(Command.Direction);
        }
        else if (Command.Kind == CombatCommandKind.OffensiveSpecial)
        {
            StartSpecial(true, Command.Direction);
        }
        else if (Command.Kind == CombatCommandKind.DefensiveSpecial)
        {
            StartSpecial(false, Command.Direction);
        }
        else if (Command.Kind == CombatCommandKind.ManualAttack)
        {
            PerformAutoAttack(Command.Flags != 0);
        }
        else if (Command.Kind == CombatCommandKind.PlaceBuilding)
        {
            if (!PlaceBuilding(Command.Cell, Command.Index))
            {
                Match.LastCommandSucceeded = 0;
            }
        }
        else if (Command.Kind == CombatCommandKind.SellBuilding)
        {
            SellBuilding(Command.Cell);
        }
        else if (Command.Kind == CombatCommandKind.Trebuchet)
        {
            FireTrebuchet(Command.Cell);
        }
    }

    public void MoveGesture(CombatCommand Command)
    {
        if (Player.bGestureActive == 0)
        {
            return;
        }
        Player.GestureOffset = (Command.Position.xy - Player.GestureStart) / Command.Value;
        Player.CameraRight = Command.Direction;
        Player.CameraForward = Command.SecondaryDirection;
        float Distance = math.length(Player.GestureOffset);
        Player.MaximumGestureDistance = math.max(Player.MaximumGestureDistance, Distance);
        if (Distance > Settings.Rules.JoystickCenterRadius && Player.Charge < SpecialChargeSeconds)
        {
            Player.Charge = 0;
        }
        if (Distance > Settings.Rules.JoystickCenterRadius && Player.bMovementAttack == 0)
        {
            Player.Facing = InputDirection(Player.GestureOffset);
        }
        if (Player.Charge >= SpecialChargeSeconds)
        {
            if (Distance >= Settings.Rules.JoystickMovementRadius)
            {
                Player.bAttackSelected = 1;
                StartSpecial(true, InputDirection(Player.GestureOffset));
                Player.bDashArmed = 0;
                return;
            }
            if (Distance <= Settings.Rules.JoystickCenterRadius)
            {
                Player.bAttackSelected = 0;
            }
        }
        if (Distance < Settings.Rules.JoystickMovementRadius)
        {
            Player.bDashArmed = 1;
        }
        else if (Player.bDashArmed != 0)
        {
            Player.bDashArmed = 0;
            if (Player.Charge < SpecialChargeSeconds)
            {
                StartDash(Player.Facing);
            }
        }
    }

    public void EndGesture(CombatCommand Command)
    {
        if (Player.bGestureActive == 0)
        {
            return;
        }
        float2 Offset = (Command.Position.xy - Player.GestureStart) / Command.Value;
        bool bReady = Player.Charge >= SpecialChargeSeconds;
        bool bOffensive = math.length(Offset) > Settings.Rules.JoystickCenterRadius
            && (Player.bAttackSelected != 0 || math.length(Offset) >= Settings.Rules.JoystickMovementRadius);
        float3 Direction = Player.AttackDirection;
        if (math.length(Offset) >= Settings.Rules.JoystickMovementRadius)
        {
            Direction = InputDirection(Offset);
        }
        bool bTap = math.max(Player.MaximumGestureDistance, math.length(Offset)) <= Settings.Rules.JoystickCenterRadius
            && Command.Timestamp - Player.GestureStartedAt <= Settings.Rules.TapMaximumSeconds;
        CancelGesture();
        if (bTap && Command.Flags != 0 && !TerrainOccluded(Command))
        {
            if (Command.Auxiliary == 1)
            {
                SellBuilding(Command.Cell);
            }
            else if (Command.Auxiliary == 2)
            {
                FireTrebuchet(Command.Cell);
            }
            else
            {
                PlaceBuilding(Command.Cell, Command.Index);
            }
            return;
        }
        if (bReady)
        {
            StartSpecial(bOffensive, Direction);
        }
        else if (bTap && CurrentWeapon().Kind == 2 && !CurrentWeapon().bAutomaticAttack && Player.bMovementAttack == 0 && Player.CastRemaining <= 0)
        {
            PerformAutoAttack(true);
        }
    }

    public void ApplySetupCommand(CombatCommand Command)
    {
        if (Command.Kind == CombatCommandKind.RecordPersonalDamage)
        {
            RecordPersonalDamage(Command.Value);
        }
        else if (Command.Kind == CombatCommandKind.AddExperience)
        {
            AddExperience((int)Command.Value);
        }
        else if (Command.Kind == CombatCommandKind.Respawn)
        {
            Respawn();
        }
        else if (Command.Kind == CombatCommandKind.SetBuildingValue)
        {
            CombatBuildingState Building = Buildings[Command.Target];
            if (Command.Index == 0)
            {
                Building.Health = Command.Value;
            }
            else if (Command.Index == 1)
            {
                Building.ForgeRemaining = Command.Value;
            }
            Buildings[Command.Target] = Building;
        }
        else if (Command.Kind == CombatCommandKind.SpawnEnemy)
        {
            SpawnEnemy(Command.Index, Command.Position);
        }
        else if (Command.Kind == CombatCommandKind.ClearEnemies)
        {
            foreach (Entity Entity in EnemyEntities)
            {
                Commands.DestroyEntity(Entity);
            }
        }
        else if (Command.Kind == CombatCommandKind.DamageEnemy)
        {
            DamageEnemy(Command.Target, Command.Value, Command.Direction, Command.Parameters.x, Command.Parameters.y, Command.Parameters.z, (Command.Flags & 1) != 0, (Command.Flags & 2) != 0);
        }
        else if (Command.Kind == CombatCommandKind.DamagePlayer)
        {
            DamagePlayer(Command.Value, Command.Direction, Command.ExtraValue);
        }
        else if (Command.Kind == CombatCommandKind.RestoreCombo)
        {
            Player.SpecialUses = math.max(0, Command.Index);
            Player.ComboPoints = math.clamp(Command.Value, 0, SpecialCost);
            Player.ComboPendingLoss = 0;
            Player.ComboIdleSeconds = 0;
        }
        else if (Command.Kind == CombatCommandKind.ClearProgression)
        {
            Player.HealthUpgrades = 0;
            Player.AttackUpgrades = 0;
            Player.WeaponUpgrades = int3.zero;
        }
        else if (Command.Kind == CombatCommandKind.SetEnemyValue)
        {
            CombatBotState Enemy = Enemies[Command.Target];
            if (Command.Index == 0)
            {
                Enemy.Health = Command.Value;
            }
            else if (Command.Index == 1)
            {
                Enemy.Position = Command.Position;
            }
            else if (Command.Index == 2)
            {
                Enemy.Speed = Command.Value;
            }
            else if (Command.Index == 3)
            {
                Enemy.Cooldown = Command.Value;
            }
            if (Command.Index == 4)
            {
                Enemy.FormationAngle = Command.Value;
            }
            else if (Command.Index == 5)
            {
                Enemy.PushRemaining = Command.Value;
            }
            Enemies[Command.Target] = Enemy;
        }
        else if (Command.Kind == CombatCommandKind.SetPlayerValue)
        {
            if (Command.Index == 0)
            {
                Player.Health = Command.Value;
            }
            else if (Command.Index == 1)
            {
                Player.Stamina = Command.Value;
            }
            else if (Command.Index == 2)
            {
                Player.Position = Command.Position;
            }
            else if (Command.Index == 3)
            {
                Player.Facing = Command.Direction;
            }
            else if (Command.Index == 4)
            {
                Player.Gold = (int)Command.Value;
            }
        }
    }
}
