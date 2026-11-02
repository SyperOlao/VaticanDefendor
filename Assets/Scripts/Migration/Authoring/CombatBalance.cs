using System;
using UnityEngine;
public enum CombatWeapon
{
    Sword, Spear, Axe
}
public enum CombatEnemyKind
{
    Weak, Strong, Spearman, Archer
}
public enum CombatStructureKind
{
    Tower, Wall, Trap, Banner, Armory, Sawmill, Church
}
[Serializable]
public class WeaponBalance
{
    public string Name;
    public CombatWeapon Kind;
    public Color Color;
    [InspectorName("Урон обычного удара")] public float AutoDamage;
    [InspectorName("Интервал автоматической атаки")] public float AutoInterval;
    [InspectorName("Доля радиуса обычного удара")] public float AutoRangeScale;
    [InspectorName("Автоатака топора")] public bool bAutomaticAttack;
    [InspectorName("Радиус оружия")] public float Radius;
    [InspectorName("Половина ширины копья")] public float HalfWidth = 0.65f;
    public float TipWidthScale = 1;
    [InspectorName("Урон дэша")] public float DashDamage;
    [InspectorName("Дистанция дэша")] public float DashDistance;
    [InspectorName("Название атакующей спецатаки")] public string AttackName;
    [InspectorName("Урон спецатаки по пути")] public float SpecialDamage;
    [InspectorName("Радиус спецатаки по пути")] public float SpecialRadius;
    [InspectorName("Дистанция спецатаки")] public float SpecialDistance;
    [InspectorName("Время перемещения спецатаки")] public float SpecialDuration;
    [InspectorName("Урон финала спецатаки")] public float FinishDamage;
    [InspectorName("Радиус финала спецатаки")] public float FinishRadius;
    [InspectorName("Отталкивание спецатаки")] public float SpecialPush;
    [InspectorName("Подброс противников")] public float LaunchHeight;
    [InspectorName("Высота прыжка")] public float JumpHeight;
    public string DefenseName;
    [InspectorName("Радиус защиты")] public float DefenseRadius;
    [InspectorName("Отталкивание защиты")] public float DefensePush;
    [InspectorName("Длительность защиты")] public float DefenseDuration;
    [InspectorName("Интервал защитных волн")] public float DefensePulse;
    [InspectorName("Прочность щита")] public float Shield;
    [InspectorName("Лечение")] public float Healing;
    [InspectorName("Восстановить выносливость до")] public float StaminaTarget;
    [InspectorName("Бонус урона, %")] public float DamageBonusPercent;
    [InspectorName("Длительность усиления")] public float DamageBonusDuration;
}
[Serializable]
public class EnemyBalance
{
    public string Name;
    public CombatEnemyKind Kind;
    public Color Color;
    [InspectorName("Здоровье")] public float Health;
    [InspectorName("Скорость движения")] public float Speed;
    [InspectorName("Разброс скорости")] public float SpeedVariation;
    [InspectorName("Урон")] public float Damage;
    [InspectorName("Дальность атаки")] public float AttackRadius;
    [InspectorName("Половина ширины атаки")] public float HalfWidth;
    [InspectorName("Подготовка атаки, с")] public float Windup;
    [InspectorName("Перерыв между атаками, с")] public float Recovery;
    [InspectorName("Дистанция окружения")] public float FormationDistance;
    [InspectorName("Минимальная дистанция выстрела")] public float MinimumRange;
    [InspectorName("Дистанция рывка")] public float DashDistance;
    [InspectorName("Длительность дэша, с")] public float DashDuration;
    [InspectorName("Длина выпада")] public float ThrustLength;
    [InspectorName("Скорость стрелы")] public float ProjectileSpeed;
    [Range(0,1)] public float SpawnWeight = 0.25f;
}
[Serializable]
public class StructureBalance
{
    public string Name;
    public CombatStructureKind Kind;
    [InspectorName("Цена")] public int Price;
    [InspectorName("Здоровье")] public float Health;
    [InspectorName("Радиус действия")] public float Range;
    [InspectorName("Интервал срабатывания")] public float Interval;
    [InspectorName("Урон")] public float Damage;
    [InspectorName("Отталкивание")] public float Push;
    [InspectorName("Лечение")] public float Healing;
}
[CreateAssetMenu(menuName = "Active Combat/Balance")]
public class CombatBalance : ScriptableObject
{
    public UpgradeMilestone[] UpgradeMilestones =
    {
        new UpgradeMilestone { Level = 2, Bonus = ProgressionBonus.SpecialDamage, Multiplier = 1.25f },
        new UpgradeMilestone { Level = 4, Bonus = ProgressionBonus.DashDistance, Multiplier = 1.15f },
        new UpgradeMilestone { Level = 4, Bonus = ProgressionBonus.DashDamage, Multiplier = 1.2f },
        new UpgradeMilestone { Level = 6, Bonus = ProgressionBonus.ChargeDuration, Multiplier = 0.8f },
        new UpgradeMilestone { Level = 8, Bonus = ProgressionBonus.SpecialRadius, Multiplier = 1.2f },
        new UpgradeMilestone { bHealth = true, Level = 2, Bonus = ProgressionBonus.MovementSpeed, Multiplier = 1.1f },
        new UpgradeMilestone { bHealth = true, Level = 4, Bonus = ProgressionBonus.StaminaRecovery, Multiplier = 1.25f },
        new UpgradeMilestone { bHealth = true, Level = 6, Bonus = ProgressionBonus.IncomingDamage, Multiplier = 0.9f },
        new UpgradeMilestone { bHealth = true, Level = 8, Bonus = ProgressionBonus.DefenseStrength, Multiplier = 1.3f }
    };
    public MobileViewSettings MobileView=new MobileViewSettings();
    [Header("Рыцарь и управление")]
    [InspectorName("Максимальное здоровье")] public float MaximumHealth = 100;
    [InspectorName("Скорость ходьбы")] public float WalkSpeed = 4.8f;
    [InspectorName("Скорость бега")] public float RunSpeed = 8.6f;
    [InspectorName("Радиус центра джойстика")] public float JoystickCenterRadius = 22;
    [InspectorName("Радиус зоны движения")] public float JoystickMovementRadius = 52;
    [InspectorName("Внешний радиус джойстика")] public float JoystickOuterRadius = 64;
    [InspectorName("Подготовка спецатаки, с")] public float ChargeSeconds = 0.7f;
    [InspectorName("Жертвы для первой спецатаки")] public int SpecialSacrificeBaseCost = 12;
    [InspectorName("Рост цены спецатаки в жертвах")] public int SpecialSacrificeCostIncrease = 1;
    [InspectorName("Длительность короткого тапа, с")] public float TapMaximumSeconds = 0.22f;
    [InspectorName("Эффект краёв при готовой спецатаке")] public bool bSpecialReadyEdgeEffect = true;
    [InspectorName("Яркость эффекта готовности")] public float SpecialReadyEdgeOpacity = 0.28f;
    [Header("Бой")]
    [InspectorName("Максимальная выносливость")] public float MaximumStamina = 100;
    [InspectorName("Стоимость дэша")] public float DashStaminaCost = 33;
    [InspectorName("Восстановление выносливости, ед./с")] public float StaminaRecovery = 40;
    [InspectorName("Добавка восстановления при истощении")] public float LowStaminaRecovery = 40;
    [InspectorName("Длительность дэша, с")] public float DashDuration = 0.3f;
    [InspectorName("Разброс урона дэша, %")] public float DashDamageVariationPercent = 15;
    [InspectorName("Отталкивание дэшем")] public float DashPush = 6.2f;
    [InspectorName("Длительность отталкивания дэшем")] public float DashPushDuration = 0.45f;
    [InspectorName("Боковое отталкивание")] public float SidePushStrength = 0.95f;
    [InspectorName("Отталкивание обычным ударом")] public float AutoPush = 1.2f;
    [InspectorName("Длительность отталкивания ударом")] public float AutoPushDuration = 0.2f;
    [InspectorName("Длительность отдачи")] public float AutoRecoilDuration = 0.3f;
    [InspectorName("Доля скорости при отдаче")] public float AutoForwardSpeed = 0.35f;
    [InspectorName("Отталкивание рыцаря")] public float PlayerHitPush = 0.7f;
    [InspectorName("Длительность толчка рыцаря")] public float PlayerHitDuration = 0.18f;
    [InspectorName("Множитель толчка стрелой")] public float ArrowPushScale = 0.6f;
    [InspectorName("Порог затемнения при истощении")] public float LowStaminaThreshold = 0.35f;
    [InspectorName("Сила затемнения")] public float LowStaminaDarkness = 0.18f;
    [Header("Арена и противники")]
    [InspectorName("Число клеток арены")] public int GridSize = 15;
    [InspectorName("Размер клетки")] public float CellSize = 2.4f;
    [InspectorName("Исходное число врагов")] public int InitialEnemies = 7;
    public int EnemiesPerWave = 2;
    public float WaveDelay = 5;
    public float EnemyHealthGrowth = 0.08f;
    public float EnemyDamageGrowth = 0.05f;
    public float EnemyDamageMultiplier = 2;
    public float PlayableBorder = 8;
    [InspectorName("Убийств до увеличения лимита")] public int KillsPerIncrease = 10;
    [InspectorName("Одновременно атакующих врагов")] public int MaximumSimultaneousAttacks = 2;
    [InspectorName("Сила разделения врагов")] public float SeparationStrength = 2;
    [InspectorName("Скорость окружения")] public float FormationAngularSpeed = 0.18f;
    [Header("Аптечки")]
    [InspectorName("Исходное число аптечек")] public int InitialPickups = 2;
    [InspectorName("Максимум аптечек")] public int MaximumPickups = 3;
    [InspectorName("Лечение аптечкой")] public float PickupHealing = 35;
    [InspectorName("Радиус подбора")] public float PickupRadius = 1.1f;
    [InspectorName("Минимальный интервал аптечек")] public float PickupMinimumInterval = 8;
    [InspectorName("Максимальный интервал аптечек")] public float PickupMaximumInterval = 14;
    [Header("Экономика и требушет")]
    [InspectorName("Начальное золото")] public int InitialGold = 150;
    [InspectorName("Золото за убийство")] public int KillGold = 8;
    [InspectorName("Опыт за убийство")] public int KillExperience = 18;
    [InspectorName("Опыт за строительство")] public int BuildingExperience = 12;
    [InspectorName("Рост требований ранга")] public float PrestigeGrowth = 1.25f;
    [InspectorName("Доля возврата при сносе")] public float RefundFraction = 0.5f;
    [InspectorName("Цена требушета")] public int TrebuchetPrice = 20;
    [InspectorName("Задержка обстрела, с")] public float TrebuchetDelay = 1.5f;
    [InspectorName("Урон требушета")] public float TrebuchetDamage = 100;
    [InspectorName("Радиус обстрела в клетках")] public int TrebuchetCellRadius = 1;
    [InspectorName("Скорость стрел башни")] public float TowerArrowSpeed = 14;
    public WeaponBalance[] Weapons =
    {
        new WeaponBalance
        {
            Name="Меч", Kind=CombatWeapon.Sword, Color=new Color(1,0.67f,0.24f), AutoDamage=30, AutoInterval=0.25f, AutoRangeScale=0.65f, bAutomaticAttack=true, Radius=3.6f, DashDamage=55, DashDistance=4.2f, AttackName="Стальной вихрь", SpecialDamage=90, SpecialRadius=3.4f, SpecialDistance=6.5f, SpecialDuration=0.85f, FinishDamage=100, FinishRadius=4.2f, SpecialPush=4, LaunchHeight=0.3f, DefenseName="Клинковый заслон", DefenseRadius=4.5f, DefensePush=4, DefenseDuration=4, Shield=120
        }
        ,
        new WeaponBalance
        {
            Name="Копьё", Kind=CombatWeapon.Spear, Color=new Color(0.25f,0.8f,1), AutoDamage=20, AutoInterval=0.25f, AutoRangeScale=0.65f, bAutomaticAttack=true, Radius=6, DashDamage=68, DashDistance=6.75f, AttackName="Громовой прорыв", SpecialDamage=100, SpecialRadius=3, HalfWidth=0.65f, SpecialDistance=10, SpecialDuration=0.5f, FinishDamage=130, FinishRadius=4.8f, SpecialPush=5, LaunchHeight=0.5f, DefenseName="Кольцо стражей", DefenseRadius=6.5f, DefensePush=7, DefenseDuration=3.5f, DefensePulse=0.6f
        }
        ,
        new WeaponBalance
        {
            Name="Топор", Kind=CombatWeapon.Axe, Color=new Color(1,0.35f,0.25f), AutoDamage=30, AutoInterval=0.4f, AutoRangeScale=1.1f, bAutomaticAttack=false, Radius=2.5f, DashDamage=77, DashDistance=2.25f, AttackName="Прыжок раскола", SpecialRadius=5.2f, SpecialDistance=5.5f, SpecialDuration=0.7f, FinishDamage=180, FinishRadius=5.2f, SpecialPush=7, LaunchHeight=1.2f, JumpHeight=2.6f, DefenseName="Второе дыхание", DefenseRadius=7, DefensePush=8, DefenseDuration=0.7f, Healing=55, StaminaTarget=150, DamageBonusPercent=50, DamageBonusDuration=5
        }
    }
    ;
    public EnemyBalance[] Enemies =
    {
        new EnemyBalance
        {
            Name="Воин", Kind=CombatEnemyKind.Weak, Color=new Color(0.9f,0.35f,0.15f), Health=53, Speed=2.55f, SpeedVariation=0.4f, Damage=7, AttackRadius=3.2f, HalfWidth=0.65f, Windup=0.7f, Recovery=1.3f, FormationDistance=2.2f
        }
        ,
        new EnemyBalance
        {
            Name="Тяжёлый воин", Kind=CombatEnemyKind.Strong, Color=new Color(0.7f,0.12f,0.2f), Health=97, Speed=1.85f, Damage=12, AttackRadius=2.9f, HalfWidth=2.9f, Windup=1.15f, Recovery=2, FormationDistance=2.2f
        }
        ,
        new EnemyBalance
        {
            Name="Копейщик", Kind=CombatEnemyKind.Spearman, Color=new Color(0.2f,0.6f,0.85f), Health=65, Speed=2.4f, SpeedVariation=0.2f, Damage=14, AttackRadius=9, HalfWidth=0.65f, Windup=0.9f, Recovery=2.4f, FormationDistance=6, DashDistance=6, DashDuration=0.45f, ThrustLength=2.5f
        }
        ,
        new EnemyBalance
        {
            Name="Лучник", Kind=CombatEnemyKind.Archer, Color=new Color(0.65f,0.35f,0.8f), Health=40, Speed=2.5f, SpeedVariation=0.2f, Damage=9, AttackRadius=13, HalfWidth=0.18f, Windup=0.85f, Recovery=1.8f, FormationDistance=10, MinimumRange=4, ProjectileSpeed=11
        }
    }
    ;
    public StructureBalance[] Structures =
    {
        new StructureBalance
        {
            Name="Башня", Kind=CombatStructureKind.Tower, Price=25, Health=160, Range=8.5f, Interval=1.1f, Damage=22, Push=0.5f
        }
        ,
        new StructureBalance
        {
            Name="Стена", Kind=CombatStructureKind.Wall, Price=10, Health=300
        }
        ,
        new StructureBalance
        {
            Name="Ловушка", Kind=CombatStructureKind.Trap, Price=15, Health=120, Range=1.35f, Interval=0.9f, Damage=16, Push=0.8f
        }
        ,
        new StructureBalance
        {
            Name="Знамя", Kind=CombatStructureKind.Banner, Price=30, Health=140, Range=4.5f, Interval=1.5f, Healing=5
        }
    }
    ;
}
