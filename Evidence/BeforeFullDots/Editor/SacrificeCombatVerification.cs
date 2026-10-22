using System;
using System.Collections.Generic;
using UnityEngine;
public static class SacrificeCombatVerification
{
    private static List<string> Passed;
    private static void Require(bool bCondition,string Message)
    {
        if(!bCondition)
        {
            throw new Exception("Sacrifice verification failed: "+Message);
        }
        Passed.Add(Message);
    }
    private static void Advance(ActiveCombatGame Game,float Seconds)
    {
        for(float Elapsed=0;Elapsed<Seconds;Elapsed+=0.01f)
        {
            Game.Simulate(0.01f);
        }
    }
    private static void EarnKills(ActiveCombatGame Game,int Amount)
    {
        for(int Index=0;Index<Amount;Index++)
        {
            ActiveCombatGame.EnemyState Enemy=Game.SpawnEnemy(0,Game.PlayerPosition+Vector3.forward*8);
            Game.DamageEnemy(Enemy,10000,Vector3.forward,0,0.2f,0,false);
        }
    }
    private static void Prepare(ActiveCombatGame Game,int Weapon)
    {
        Game.ResetMatch();
        Game.EquipWeapon(Weapon);
        foreach(ActiveCombatGame.EnemyState Enemy in Game.Enemies.ToArray())
        {
            UnityEngine.Object.Destroy(Enemy.Model);
        }
        Game.Enemies.Clear();
        Game.Rules.InitialEnemies=0;
        Game.Rules.MaximumSimultaneousAttacks=0;
        foreach(EnemyBalance Enemy in Game.Rules.Enemies)
        {
            Enemy.Speed=0;
            Enemy.SpeedVariation=0;
        }
    }
    public static object Run()
    {
        ActiveCombatGame Game=UnityEngine.Object.FindAnyObjectByType<ActiveCombatGame>();
        Game.Session.StartGame();
        string Original=JsonUtility.ToJson(Game.Rules);
        Game.enabled=false;
        Passed=new List<string>();
        try
        {
            Game.Hud.CloseSettings();
            Game.Hud.Mobile.CancelWheel();
            Game.Hud.Mobile.SetCurtain(false);
            Game.Rules.SpecialSacrificeBaseCost=12;
            Game.Rules.SpecialSacrificeCostIncrease=1;
            Prepare(Game,0);
            Require(Game.SpecialSacrificeCost==12 && Game.SpecialSacrifices==0 && !Game.bSpecialReady,"A new match starts with zero sacrifices and a cost of twelve");
            EarnKills(Game,11);
            Require(Game.SpecialSacrifices==11 && Game.Kills==11 && !Game.bSpecialReady,"Eleven kills do not unlock the special");
            Game.BeginGesture(Vector2.zero);
            Advance(Game,0.72f);
            Require(Game.Charge==0,"Center hold cannot charge a special before enough kills");
            Game.EndGesture(Vector2.zero);
            Game.Stamina=47;
            Game.StartSpecial(true,Vector3.back);
            Require(!Game.bAttacking && Game.Stamina==47 && Game.SpecialUses==0 && Game.SpecialSacrifices==11,"A rejected special spends neither stamina nor sacrifices");
            EarnKills(Game,1);
            Require(Game.SpecialSacrifices==12 && Game.bSpecialReady,"The twelfth kill unlocks the special");
            ActiveCombatGame.EnemyState Dead=Game.SpawnEnemy(0,Vector3.forward*8);
            Game.DamageEnemy(Dead,10000,Vector3.forward,0,0.2f,0,false);
            int Bank=Game.SpecialSacrifices;
            Game.DamageEnemy(Dead,10000,Vector3.forward,0,0.2f,0,false);
            Require(Game.SpecialSacrifices==Bank,"Hitting an already dead enemy cannot award another sacrifice");
            EarnKills(Game,1);
            Game.StartSpecial(false,Vector3.forward);
            Require(Game.SpecialUses==1 && Game.SpecialSacrifices==2 && Game.SpecialSacrificeCost==13,"First use spends twelve, keeps overflow, and raises the next cost to thirteen");
            Require(Game.Stamina==47 && Game.Shield>0,"Sword defense preserves stamina");
            Advance(Game,0.5f);
            Game.EquipWeapon(1);
            Require(Game.SpecialSacrificeCost==13 && Game.SpecialSacrifices==2,"Changing weapons preserves the shared match cost and sacrifice bank");
            Game.DamagePlayer(10000,Vector3.back,1);
            Require(Game.SpecialUses==1 && Game.SpecialSacrifices==2 && Game.SpecialSacrificeCost==13,"Respawn preserves sacrifice progress and the growing cost");
            Game.ResetMatch();
            Require(Game.SpecialUses==0 && Game.SpecialSacrifices==0 && Game.SpecialSacrificeCost==12,"A new match resets both the bank and the growing cost");
            for(int Weapon=0;Weapon<3;Weapon++)
            {
                for(int Variant=0;Variant<2;Variant++)
                {
                    Prepare(Game,Weapon);
                    EarnKills(Game,12);
                    Game.Stamina=0;
                    Game.StartSpecial(Variant==1,Vector3.back);
                    Require(Game.bAttacking && Game.SpecialUses==1 && Game.SpecialSacrifices==0,"Special variant activates at zero stamina "+Weapon+"/"+Variant);
                    if(Weapon==2 && Variant==0)
                    {
                        Require(Game.Stamina==150,"Axe defense keeps its stamina restoration bonus");
                    }
                    else
                    {
                        Require(Game.Stamina==0,"Special variant does not change stamina "+Weapon+"/"+Variant);
                    }
                    Game.StartSpecial(Variant==1,Vector3.back);
                    Require(Game.SpecialUses==1 && Game.SpecialSacrificeCost==13,"Repeated activation during an attack cannot charge the cost twice "+Weapon+"/"+Variant);
                }
            }
            Prepare(Game,0);
            EarnKills(Game,12);
            ActiveCombatGame.EnemyState Victim=Game.SpawnEnemy(0,Vector3.back);
            Victim.Health=1;
            Game.StartSpecial(true,Vector3.back);
            Advance(Game,0.15f);
            Require(Game.SpecialSacrifices==1 && Game.SpecialUses==1,"Kills dealt by the special count toward the next activation");
            Prepare(Game,1);
            EarnKills(Game,12);
            Game.BeginGesture(Vector2.zero);
            Advance(Game,0.72f);
            Require(Game.Charge>=Game.Rules.ChargeSeconds,"An available special still requires the existing hold gesture");
            Game.MoveGesture(new Vector2(80,0));
            Game.MoveGesture(Vector2.zero);
            Game.EndGesture(Vector2.zero);
            Require(Game.DefenseRemaining>0 && Game.SpecialUses==1 && Game.SpecialSacrifices==0,"Returning to the center selects defense and consumes one activation");
            CombatBalance Candidate=UnityEngine.Object.Instantiate(Game.Rules);
            Candidate.SpecialSacrificeBaseCost=0;
            Require(!string.IsNullOrEmpty(Game.ValidateBalance(Candidate)),"Settings reject a nonpositive initial sacrifice cost");
            Candidate.SpecialSacrificeBaseCost=12;
            Candidate.SpecialSacrificeCostIncrease=-1;
            Require(!string.IsNullOrEmpty(Game.ValidateBalance(Candidate)),"Settings reject a negative cost increase");
            UnityEngine.Object.Destroy(Candidate);
            return new {Success=true,Count=Passed.Count,Checks=Passed.ToArray()};
        }
        finally
        {
            JsonUtility.FromJsonOverwrite(Original,Game.Rules);
            Game.ResetMatch();
            Game.EquipWeapon(0);
            Game.enabled=true;
        }
    }
}
