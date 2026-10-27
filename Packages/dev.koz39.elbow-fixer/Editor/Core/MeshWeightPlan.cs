using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;
using static KOZ39.ElbowFixer.ElbowFixerException;

namespace KOZ39.ElbowFixer
{
    internal sealed class MeshWeightPlan
    {
        private const float MaxRestDrift = 0.00001f;

        internal const string MissingMesh = nameof(MissingMesh);

        internal const string MeshNotReadable = nameof(MeshNotReadable);

        internal readonly SkinnedMeshRenderer Renderer;
        private readonly Vector3[] _vertices;
        private readonly BoneWeight[] _weights;
        private readonly int[] _lowerIndices;
        private readonly ForearmAxis[] _axes;
        private readonly List<(int Side, int BoneIndex)> _hiddenSections = new();

        private Transform[] _bones;

        private Matrix4x4[] _bindposes;

        internal int RedistributedCount { get; private set; }
        internal int NearestCount { get; private set; }

        internal int MirrorCount => _hiddenSections.Count * TwistRig.SupportCount;

        private MeshWeightPlan(
            SkinnedMeshRenderer renderer,
            Vector3[] vertices,
            BoneWeight[] weights,
            int armCount
        )
        {
            Renderer = renderer;
            _vertices = vertices;
            _weights = weights;
            _lowerIndices = Enumerable.Repeat(-1, armCount).ToArray();
            _axes = new ForearmAxis[armCount];
        }

        internal static MeshWeightPlan Create(SkinnedMeshRenderer renderer, Arm[] arms)
        {
            var problem = FindMeshProblem(renderer);
            Require(problem == null, problem, renderer);

            var mesh = renderer.sharedMesh;
            var bones = renderer.bones;
            var bindposes = mesh.bindposes;
            var vertices = mesh.vertices;
            var weights = mesh.boneWeights;
            Validate(renderer, mesh, bones, bindposes, vertices, weights);

            var plan = new MeshWeightPlan(renderer, vertices, weights, arms.Length);
            (bones, bindposes) = plan.RebindExistingTwist(bones, bindposes, arms);
            plan._bones = bones;
            plan._bindposes = bindposes;

            for (var side = 0; side < arms.Length; side++)
            {
                plan.RedistributeLower(side, arms[side], bones, bindposes);
            }

            for (var side = 0; side < arms.Length; side++)
            {
                for (var boneIndex = 0; boneIndex < bones.Length; boneIndex++)
                {
                    if (!plan.IsHiddenSectionOf(side, arms[side], bones[boneIndex]))
                    {
                        continue;
                    }

                    var group = arms.Length + plan._hiddenSections.Count;
                    plan._hiddenSections.Add((side, boneIndex));
                    plan.Redistribute(
                        boneIndex,
                        TwistRig.WeightNodes(bones.Length, group),
                        plan._axes[side]
                    );
                }
            }

            return plan;
        }

        internal float Apply(Transform[][] supports)
        {
            var source = Renderer.sharedMesh;
            var bones = _bones.ToList();
            var bindposes = _bindposes.ToList();
            var before = SkinMatrices(bones, bindposes).ToArray();

            // Order must match the weight nodes from Create: supports, then mirrors.
            for (var side = 0; side < supports.Length; side++)
            {
                foreach (var support in supports[side])
                {
                    bindposes.Add(SupportBindpose(support, _lowerIndices[side], bones, bindposes));
                    bones.Add(support);
                }
            }

            var mirrors = new List<Transform>();

            foreach (var (side, boneIndex) in _hiddenSections)
            {
                foreach (var mirror in TwistRig.Mirror(bones[boneIndex], supports[side]))
                {
                    bindposes.Add(SupportBindpose(mirror, boneIndex, bones, bindposes));
                    bones.Add(mirror);
                    mirrors.Add(mirror);
                }
            }

            var after = before
                .Concat(SkinMatrices(bones.Skip(before.Length), bindposes.Skip(before.Length)))
                .ToArray();
            float drift;

            try
            {
                drift = RestDrift(source.boneWeights, before, after);

                Require(
                    drift < MaxRestDrift,
                    "RestPoseDrift",
                    Renderer,
                    (drift * 1000).ToString("F5", CultureInfo.CurrentCulture)
                );
            }
            catch (ElbowFixerException)
            {
                foreach (var mirror in mirrors)
                {
                    UnityEngine.Object.DestroyImmediate(mirror.gameObject);
                }

                throw;
            }

            var mesh = UnityEngine.Object.Instantiate(source);
            ObjectRegistry.RegisterReplacedObject(source, mesh);
            mesh.name = $"{source.name}_ElbowFixer";
            mesh.boneWeights = _weights;
            mesh.bindposes = bindposes.ToArray();

            Renderer.bones = bones.ToArray();
            Renderer.sharedMesh = mesh;
            Renderer.quality = SkinQuality.Bone4;

            return drift;
        }

        private void RedistributeLower(int side, Arm arm, Transform[] bones, Matrix4x4[] bindposes)
        {
            var lowerIndex = Array.IndexOf(bones, arm.Lower);

            if (lowerIndex < 0)
            {
                return;
            }

            Require(
                Array.LastIndexOf(bones, arm.Lower) == lowerIndex,
                "DuplicateLowerArm",
                Renderer,
                arm.Name
            );
            _lowerIndices[side] = lowerIndex;
            _axes[side] = new ForearmAxis(bindposes[lowerIndex], arm.Hand.localPosition);

            Require(
                float.IsFinite(_axes[side].Length) && _axes[side].Length > 0,
                "InvalidBindpose",
                Renderer
            );
            Redistribute(lowerIndex, TwistRig.WeightNodes(bones.Length, side), _axes[side]);
        }

        private void Redistribute(int source, int[] nodes, ForearmAxis forearm)
        {
            for (var v = 0; v < _vertices.Length; v++)
            {
                var sourceWeight = ForearmWeights.SourceWeight(_weights[v], source);

                if (sourceWeight <= 0)
                {
                    continue;
                }

                var t = forearm.Position(_vertices[v]);

                if (
                    !ForearmWeights.TryRedistribute(
                        _weights[v],
                        source,
                        sourceWeight,
                        nodes,
                        t,
                        out var weight
                    )
                )
                {
                    weight = ForearmWeights.Nearest(_weights[v], source, sourceWeight, nodes, t);
                    NearestCount++;
                }

                _weights[v] = weight;
                RedistributedCount++;
            }
        }

        private bool IsHiddenSectionOf(int side, Arm arm, Transform candidate) =>
            _lowerIndices[side] >= 0
            && candidate != null
            && ElbowFixerUtility.IsHiddenBone(candidate)
            && CopiesLowerArm(arm, candidate);

        private static bool CopiesLowerArm(Arm arm, Transform hiddenBone)
        {
            var copied = ElbowFixerUtility.CopiedBone(hiddenBone);

            return copied == arm.Lower || arm.FindRebind(copied).ArmBone == arm.Lower;
        }

        // The bones keep their constraints for other meshes.
        private (Transform[] Bones, Matrix4x4[] Bindposes) RebindExistingTwist(
            Transform[] bones,
            Matrix4x4[] bindposes,
            Arm[] arms
        )
        {
            var boneList = bones.ToList();
            var bindposeList = bindposes.ToList();
            var rebind = new Dictionary<int, int>();

            foreach (var bone in ForearmWeights.UsedBones(_weights, bones))
            {
                foreach (var arm in arms)
                {
                    var (armBone, blocker) = arm.FindRebind(bone);
                    var isHiddenCopy =
                        blocker != null
                        && ElbowFixerUtility.IsHiddenBone(bone)
                        && CopiesLowerArm(arm, bone);
                    Require(
                        blocker == null || isHiddenCopy,
                        "MeshExistingTwist",
                        Renderer,
                        blocker
                    );

                    // Mirrors need the LowerArm even when only the copy uses the bone it copies.
                    if (isHiddenCopy)
                    {
                        armBone = arm.Lower;
                    }
                    else if (armBone == null)
                    {
                        continue;
                    }

                    var armIndex = boneList.IndexOf(armBone);

                    if (armIndex < 0)
                    {
                        armIndex = boneList.Count;
                        boneList.Add(armBone);
                        bindposeList.Add(
                            BindposeFrom(armBone, bone, bindposes[Array.IndexOf(bones, bone)])
                        );
                    }

                    if (isHiddenCopy)
                    {
                        continue;
                    }

                    for (var i = 0; i < bones.Length; i++)
                    {
                        if (bones[i] == bone)
                        {
                            rebind[i] = armIndex;
                        }
                    }
                }
            }

            if (rebind.Count > 0)
            {
                for (var v = 0; v < _weights.Length; v++)
                {
                    _weights[v] = ForearmWeights.Rebind(_weights[v], rebind);
                }
            }

            return (boneList.ToArray(), bindposeList.ToArray());
        }

        internal static string FindMeshProblem(SkinnedMeshRenderer renderer)
        {
            var mesh = renderer.sharedMesh;

            if (mesh == null)
            {
                return MissingMesh;
            }

            if (!mesh.isReadable)
            {
                return MeshNotReadable;
            }

            return renderer.quality == SkinQuality.Auto || renderer.quality == SkinQuality.Bone4
                ? null
                : "SkinQuality";
        }

        private static void Validate(
            SkinnedMeshRenderer renderer,
            Mesh mesh,
            Transform[] bones,
            Matrix4x4[] bindposes,
            Vector3[] vertices,
            BoneWeight[] weights
        )
        {
            // Mesh.boneWeights holds four influences per vertex and would silently drop the rest.
            using (var bonesPerVertex = mesh.GetBonesPerVertex())
            {
                Require(
                    bonesPerVertex.All(count => count <= ForearmWeights.MaxInfluences),
                    "TooManyInfluences",
                    renderer
                );
            }

            Require(bindposes.Length == bones.Length, "BindposeMismatch", renderer);
            Require(bindposes.All(IsValidBindpose), "InvalidBindpose", renderer);
            Require(
                vertices.All(vertex =>
                    float.IsFinite(vertex.x) && float.IsFinite(vertex.y) && float.IsFinite(vertex.z)
                ),
                "NonFiniteVertices",
                renderer
            );
            Require(
                weights.Length == vertices.Length
                    && weights.All(weight => IsValidWeight(weight, bones)),
                "InvalidWeights",
                renderer
            );
        }

        private static bool IsValidBindpose(Matrix4x4 matrix)
        {
            if (!IsFinite(matrix))
            {
                return false;
            }

            var determinant = matrix.determinant;

            return float.IsFinite(determinant) && determinant != 0 && IsFinite(matrix.inverse);
        }

        private static bool IsFinite(Matrix4x4 matrix) =>
            Enumerable.Range(0, 16).All(i => float.IsFinite(matrix[i]));

        private static bool IsValidWeight(BoneWeight weight, Transform[] bones)
        {
            var slots = ForearmWeights.Slots(weight);
            var sum = slots.Sum(slot => slot.Weight);

            return slots.All(slot =>
                    float.IsFinite(slot.Weight)
                    && slot.Weight >= 0
                    && (
                        slot.Weight == 0
                        || (slot.Bone >= 0 && slot.Bone < bones.Length && bones[slot.Bone] != null)
                    )
                ) && (sum == 0 || Mathf.Abs(sum - 1) < 0.0001f);
        }

        internal static Matrix4x4 BindposeFrom(
            Transform bone,
            Transform reference,
            Matrix4x4 referenceBindpose
        ) => bone.worldToLocalMatrix * reference.localToWorldMatrix * referenceBindpose;

        private Matrix4x4 SupportBindpose(
            Transform support,
            int source,
            List<Transform> bones,
            List<Matrix4x4> bindposes
        ) =>
            source >= 0
                ? BindposeFrom(support, bones[source], bindposes[source])
                : support.worldToLocalMatrix * Renderer.transform.localToWorldMatrix;

        private static IEnumerable<Matrix4x4> SkinMatrices(
            IEnumerable<Transform> bones,
            IEnumerable<Matrix4x4> bindposes
        ) =>
            bones.Zip(
                bindposes,
                (bone, bindpose) =>
                    bone != null ? bone.localToWorldMatrix * bindpose : Matrix4x4.identity
            );

        private float RestDrift(BoneWeight[] oldWeights, Matrix4x4[] before, Matrix4x4[] after)
        {
            var drift = 0f;

            for (var i = 0; i < _vertices.Length; i++)
            {
                var oldPosition = Skin(before, oldWeights[i], _vertices[i]);
                var newPosition = Skin(after, _weights[i], _vertices[i]);
                var distance = Vector3.Distance(oldPosition, newPosition);
                Require(float.IsFinite(distance), "NonFiniteSkinning", Renderer);
                drift = Mathf.Max(drift, distance);
            }

            return drift;
        }

        private static Vector3 Skin(Matrix4x4[] matrices, BoneWeight weight, Vector3 vertex) =>
            ForearmWeights
                .Slots(weight)
                .Where(slot => slot.Weight > 0)
                .Aggregate(
                    Vector3.zero,
                    (sum, slot) => sum + matrices[slot.Bone].MultiplyPoint3x4(vertex) * slot.Weight
                );
    }
}
