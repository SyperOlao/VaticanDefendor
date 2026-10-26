using System;
using System.Collections.Generic;
using UnityEngine;
public static class CombatVerification
{
    private static List<string> Passed;
    private static void Require(bool bCondition,string Message)
    {
        if(!bCondition)
        {
            throw new Exception("Combat verification failed: "+Message);
        }
        Passed.Add(Message);
    }
    private static void ClearEnemies(ActiveCombatGame Game)
    {
        foreach(ActiveCombatGame.EnemyState Enemy in Game.Enemies.ToArray())
        {
            UnityEngine.Object.Destroy(Enemy.Model);
            if(Enemy.Telegraph!=null)
            {
                UnityEngine.Object.Destroy(Enemy.Telegraph);
            }
        }
        Game.Enemies.Clear();
    }
    private static void Advance(ActiveCombatGame Game,float Seconds)
    {
        for(float Elapsed=0;Elapsed<Seconds;Elapsed+=0.01f)
        {
            Game.Simulate(0.01f);
        }
    }
    private static void Prepare(ActiveCombatGame Game,int Weapon)
    {
        Game.ResetMatch();
        Game.EquipWeapon(Weapon);
        Game.SpecialSacrifices=10000;
        Game.Rules.InitialEnemies=0;
        Game.Rules.MaximumSimultaneousAttacks=0;
        foreach(EnemyBalance Enemy in Game.Rules.Enemies)
        {
            Enemy.Speed=0;
            Enemy.SpeedVariation=0;
        }
        ClearEnemies(Game);
        foreach(ActiveCombatGame.PickupState Pickup in Game.Pickups)
        {
            UnityEngine.Object.Destroy(Pickup.Model);
        }
        Game.Pickups.Clear();
    }
    public static object Run()
    {
        ActiveCombatGame Game=UnityEngine.Object.FindAnyObjectByType<ActiveCombatGame>();
        if(Game==null || Game.Rules==null)
        {
            throw new Exception("Enter Play Mode before verification.");
        }
        Game.Session.StartGame();
        string Original=JsonUtility.ToJson(Game.Rules);
        Game.enabled=false;
        Passed=new List<string>();
        try
        {
            Prepare(Game,2);
            ActiveCombatGame.EnemyState Victim=Game.SpawnEnemy(0,new Vector3(0,0,1));
            Victim.Health=500;
            Game.BeginGesture(Vector2.zero);
            Game.EndGesture(Vector2.zero);
            Game.BeginGesture(Vector2.zero);
            Game.EndGesture(Vector2.zero);
            Require(Mathf.Approximately(Victim.Health,440),"Consecutive manual axe taps each deal damage without cooldown");
            Game.BeginGesture(Vector2.zero);
            Advance(Game,0.72f);
            Require(Mathf.Approximately(Victim.Health,440),"Holding center does not trigger the normal axe attack");
            Game.Health=30;
            Game.Stamina=100;
            Game.EndGesture(Vector2.zero);
            Require(Mathf.Approximately(Game.Health,85),"Axe defense heals 55 health");
            Require(Mathf.Approximately(Game.Stamina,150),"Axe defense restores stamina TO 150");
            Advance(Game,0.5f);
            Require(Mathf.Approximately(Game.Stamina,150),"Regeneration preserves stamina overflow");
            Require(Game.DamageBonusRemaining>0,"Axe defense grants temporary damage buff");
            Victim.Position=Game.PlayerPosition+Vector3.forward;
            Victim.PushRemaining=0;
            Game.BeginGesture(Vector2.zero);
            Game.EndGesture(Vector2.zero);
            Require(Mathf.Approximately(Victim.Health,395),"Buff increases manual attack damage to 45");
            Advance(Game,0.5f);
            Game.StartSpecial(false,Vector3.forward);
            Require(Mathf.Approximately(Game.Stamina,150),"Repeated axe defense does not accumulate stamina");
            Prepare(Game,2);
            Game.Stamina=150;
            Game.StartDash(Vector3.forward);
            Require(Mathf.Approximately(Game.Stamina,117),"Dash costs 33 stamina from overflow");
            Advance(Game,0.4f);
            Game.Stamina=0;
            Advance(Game,0.1f);
            Require(Game.Stamina>6 && Game.Stamina<10,"Low-stamina recovery is accelerated");
            for(int Weapon=0;Weapon<3;Weapon++)
            {
                Prepare(Game,Weapon);
                Vector3 Start=Game.PlayerPosition;
                Vector3 Target=Vector3.back*Game.CurrentWeapon.SpecialDistance;
                Victim=Game.SpawnEnemy(1,Target);
                Victim.Health=90;
                Game.BeginGesture(Vector2.zero);
                Advance(Game,0.72f);
                Game.MoveGesture(new Vector2(0,80));
                Game.EndGesture(new Vector2(0,80));
                Require(Game.bAttacking,"Charged outer gesture starts offensive special "+Weapon);
                Require(Mathf.Approximately(Game.Stamina,Game.Rules.MaximumStamina),"Offensive special preserves stamina "+Weapon);
                Advance(Game,Game.CurrentWeapon.SpecialDuration+0.3f);
                Require(Vector3.Distance(Start,Game.PlayerPosition)>Game.CurrentWeapon.SpecialDistance*0.9f,"Offensive special moves player "+Weapon);
                Require(!Game.Enemies.Contains(Victim),"Offensive special hits its destination area "+Weapon);
            }
            Prepare(Game,0);
            Game.StartSpecial(false,Vector3.forward);
            float Health=Game.Health;
            Game.DamagePlayer(50,Vector3.back,1);
            Require(Mathf.Approximately(Game.Health,Health) && Mathf.Approximately(Game.Shield,70),"Sword shield absorbs damage before health");
            Prepare(Game,1);
            Victim=Game.SpawnEnemy(0,new Vector3(1,0,0));
            Game.BeginGesture(Vector2.zero);
            Advance(Game,0.72f);
            Game.MoveGesture(new Vector2(0,80));
            Game.MoveGesture(Vector2.zero);
            Game.EndGesture(Vector2.zero);
            Require(Game.DefenseRemaining>0 && Game.PlayerPosition==Vector3.zero && Victim.PushRemaining>0,"Returning charged gesture to center selects stationary spear defense");
            Prepare(Game,0);
            Game.bTrebuchet=true;
            Game.Gold=100;
            Victim=Game.SpawnEnemy(1,new Vector3(1,0,1)*Game.Rules.CellSize);
            Require(Game.PlaceAtCell(Vector2Int.zero,false),"Trebuchet accepts placement and payment");
            Advance(Game,1.3f);
            Require(Game.Enemies.Contains(Victim),"Trebuchet does not deal damage before delay");
            Advance(Game,0.3f);
            Require(!Game.Enemies.Contains(Victim),"Trebuchet damages diagonal neighboring cell after 1.5 seconds");
            Prepare(Game,0);
            Game.Gold=100;
            Game.bTrebuchet=false;
            Game.StructureIndex=0;
            Vector2Int FreeCell=Vector2Int.zero;
            Require(Game.PlaceAtCell(FreeCell,false) && Game.Gold==75,"Tower placement spends its configured price");
            Require(!Game.PlaceAtCell(FreeCell,false) && Game.Gold==75,"Occupied cell rejects placement without charging");
            Require(Game.PlaceAtCell(FreeCell,true) && Game.Gold==87,"Removing a tower refunds half its price");
            Prepare(Game,0);
            Game.Rules.InitialEnemies=7;
            Game.Rules.MaximumSimultaneousAttacks=0;
            Advance(Game,0.01f);
            Victim=Game.Enemies[0];
            Game.DamageEnemy(Victim,1000,Vector3.forward,0,0.2f,0,true);
            Advance(Game,0.01f);
            Require(Game.Enemies.Count==7,"Killed enemies are replaced without a new wave");
            Game.Kills=9;
            Victim=Game.Enemies[0];
            Game.DamageEnemy(Victim,1000,Vector3.forward,0,0.2f,0,true);
            Advance(Game,0.01f);
            Require(Game.EnemyLimit==8 && Game.Enemies.Count==8,"Every tenth kill increases population by one");
            foreach(ActiveCombatGame.EnemyState Enemy in Game.Enemies)
            {
                Enemy.Position=Vector3.zero;
            }
            Game.PlayerPosition=Vector3.zero;
            Game.DamagePlayer(1000,Vector3.back,1);
            Require(Game.Health==Game.Rules.MaximumHealth && Game.PlayerPosition.magnitude>10,"Death respawns away from the crowd with full health");
            Game.Stamina=0;
            Advance(Game,0.1f);
            Require(Game.Stamina>0,"Stamina recovery resumes after respawn");
            return new
            {
                Success=true, Count=Passed.Count, Checks=Passed.ToArray()
            }
            ;
        }
        finally
        {
            JsonUtility.FromJsonOverwrite(Original,Game.Rules);
            Game.ResetMatch();
            Game.bSettingsOpen=false;
            Game.Hud.CloseSettings();
            Game.EquipWeapon(0);
            Game.enabled=true;
        }
    }
}
