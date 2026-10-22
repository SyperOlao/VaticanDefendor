using Unity.Cinemachine;
using UnityEngine;

[DefaultExecutionOrder(100)]
public sealed class CombatCameraPresenter : MonoBehaviour
{
    public ActiveCombatGame Game;
    public CinemachineCamera FollowCamera;
    public Transform FollowTarget;
    public CinemachineBasicMultiChannelPerlin ImpactNoise;

    public void Track()
    {
        FollowTarget.position = Game.PlayerPosition;
    }

    public void Snap()
    {
        Track();
        FollowCamera.PreviousStateIsValid = false;
    }

    private void LateUpdate()
    {
        Track();
        ImpactNoise.AmplitudeGain = Game.CameraImpactStrength / 0.4f;
        if (Game.bSimulationPaused)
        {
            ImpactNoise.AmplitudeGain = 0;
        }
    }
}
