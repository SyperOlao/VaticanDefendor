using System.Collections.Generic;
using UnityEngine;

public partial class ActiveCombatGame
{
    public int WaveNumber;
    public float WaveCountdown = -1;
    private readonly List<Vector3> PreviousWaveGroups = new List<Vector3>();

    private void UpdateWaves(float DeltaSeconds)
    {
        if (Session != null && (Session.bMainMenuOpen || Session.Tutorial.bActive))
        {
            return;
        }
        if (WaveNumber == 0)
        {
            SpawnWave();
            return;
        }
        int Threshold = Mathf.Min(EnemyLimit - 1, WaveNumber + 1);
        if (WaveCountdown < 0 && Enemies.Count <= Threshold)
        {
            WaveCountdown = Rules.WaveDelay;
        }
        if (WaveCountdown >= 0)
        {
            WaveCountdown -= DeltaSeconds;
            if (WaveCountdown <= 0)
            {
                SpawnWave();
            }
        }
    }

    private void SpawnWave()
    {
        WaveNumber++;
        WaveCountdown = -1;
        int GroupCount = Mathf.Clamp(Mathf.CeilToInt(EnemyLimit / 4f), 1, 4);
        List<Vector3> Groups = new List<Vector3>();
        for (int GroupIndex = 0; GroupIndex < GroupCount; GroupIndex++)
        {
            Vector3 Best = Vector3.zero;
            float BestScore = float.NegativeInfinity;
            for (int Attempt = 0; Attempt < 100; Attempt++)
            {
                float Distance = Random.Range(Rules.GridSize * Rules.CellSize * 0.5f + 3, ArenaBound - 0.5f);
                float AlongEdge = Random.Range(-ArenaBound + 1, ArenaBound - 1);
                int Side = Random.Range(0, 4);
                Vector3 Candidate = new Vector3(Distance, 0, AlongEdge);
                if (Side == 1)
                {
                    Candidate.x = -Distance;
                }
                else if (Side == 2)
                {
                    Candidate = new Vector3(AlongEdge, 0, Distance);
                }
                else if (Side == 3)
                {
                    Candidate = new Vector3(AlongEdge, 0, -Distance);
                }
                float Score = Vector3.Distance(Candidate, PlayerPosition) / 12;
                foreach (Vector3 Previous in PreviousWaveGroups)
                {
                    Score = Mathf.Min(Score, Vector3.Distance(Candidate, Previous) / 6);
                }
                foreach (Vector3 Selected in Groups)
                {
                    Score = Mathf.Min(Score, Vector3.Distance(Candidate, Selected) / 6);
                }
                if (Score > BestScore)
                {
                    BestScore = Score;
                    Best = Candidate;
                }
                if (Score >= 1)
                {
                    break;
                }
            }
            Groups.Add(Best);
        }
        PreviousWaveGroups.Clear();
        PreviousWaveGroups.AddRange(Groups);
        for (int Index = 0; Index < EnemyLimit; Index++)
        {
            Vector3 Position = ClampToArena(Groups[Index % Groups.Count] + new Vector3(Random.Range(-0.7f, 0.7f), 0, Random.Range(-0.7f, 0.7f)));
            SpawnEnemy(Random.Range(0, Rules.Enemies.Length), Position);
        }
    }
}
