using System;
using UnityEngine;

[Serializable]
public struct CharacterAnimationClipDefinition
{
    public AnimationClip Clip;
    public bool Loop;
}

[CreateAssetMenu(menuName = "VaticanDefendor/Presentation/Character Animation")]
public sealed class CharacterAnimationDefinition : ScriptableObject
{
    [SerializeField] private GameObject _model;
    [SerializeField] private RuntimeAnimatorController _controller;
    [SerializeField] private TextAsset _poses;
    [SerializeField] private CharacterAnimationClipDefinition[] _clips = Array.Empty<CharacterAnimationClipDefinition>();
    [SerializeField] private string _sourceHash;
    [SerializeField, Range(15, 60)] private int _sampleRate = 30;
    [SerializeField] private float _height = 1.8f;
    [SerializeField] private Vector3 _rotation;
    [SerializeField] private Vector3 _weaponRotation = new Vector3(0, 0, 90);
    [SerializeField] private Vector3 _weaponOffset;
    [SerializeField] private float _weaponScale = 1;
    [SerializeField] private float _contactTime = 0.5f;

    public GameObject Model => _model;
    public RuntimeAnimatorController Controller => _controller;
    public TextAsset Poses => _poses;
    public CharacterAnimationClipDefinition[] Clips => _clips;
    public string SourceHash => _sourceHash;
    public int SampleRate => _sampleRate;
    public float Height => _height;
    public Vector3 Rotation => _rotation;
    public Vector3 WeaponRotation => _weaponRotation;
    public Vector3 WeaponOffset => _weaponOffset;
    public float WeaponScale => _weaponScale;
    public float ContactTime => _contactTime;
}
