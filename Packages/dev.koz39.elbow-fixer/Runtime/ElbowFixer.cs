using System;
using UnityEngine;
using VRC.SDKBase;

namespace KOZ39.ElbowFixer
{
    [DisallowMultipleComponent]
    [AddComponentMenu("KOZ39/Elbow Fixer")]
    public class ElbowFixer : MonoBehaviour, IEditorOnly
    {
        public SkinnedMeshRenderer[] targetMeshes = Array.Empty<SkinnedMeshRenderer>();
    }
}
