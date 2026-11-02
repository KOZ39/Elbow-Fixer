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
        // Float error grows with the avatar's size, about 0.0003 mm per meter of height, so this
        // lets avatars a few hundred meters tall through while still catching a wrong bindpose.
        private const float MaxRestDrift = 0.0001f;

        private const float MaxRingSpacing = 0.08f;
        private const int MaxSubdivisionLevels = 3;

        internal const string MissingMesh = nameof(MissingMesh);
        internal const string MeshNotReadable = nameof(MeshNotReadable);

        internal readonly SkinnedMeshRenderer Renderer;
        private readonly int[] _lowerIndices;
        private readonly ForearmAxis[] _axes;
        private readonly int[] _supportCounts;
        private readonly bool _isSkin;
        private readonly List<(int Side, int BoneIndex)> _hiddenSections = new();
        private Vector3[] _vertices;
        private BoneWeight[] _weights;
        private ForearmSubdivision _subdivision;
        private Transform[] _bones;
        private Matrix4x4[] _bindposes;

        internal int RedistributedCount { get; private set; }
        internal int NearestCount { get; private set; }

        internal bool IsSkin => _isSkin;

        internal int MirrorCount => _hiddenSections.Sum(section => Nodes(section.Side).Length);

        private MeshWeightPlan(
            SkinnedMeshRenderer renderer,
            Vector3[] vertices,
            BoneWeight[] weights,
            int[] supportCounts,
            bool isSkin
        )
        {
            Renderer = renderer;
            _vertices = vertices;
            _weights = weights;
            _lowerIndices = Enumerable.Repeat(-1, supportCounts.Length).ToArray();
            _axes = new ForearmAxis[supportCounts.Length];
            _supportCounts = supportCounts;
            _isSkin = isSkin;
        }

        internal static MeshWeightPlan Create(
            SkinnedMeshRenderer renderer,
            Arm[] arms,
            int[] supportCounts,
            bool isSkin,
            bool subdivide
        )
        {
            var problem = FindMeshProblem(renderer);
            Require(problem == null, problem, renderer);

            var mesh = renderer.sharedMesh;
            var bones = renderer.bones;
            var bindposes = mesh.bindposes;
            var vertices = mesh.vertices;
            var weights = mesh.boneWeights;
            Validate(renderer, mesh, bones, bindposes, vertices, weights);

            var plan = new MeshWeightPlan(renderer, vertices, weights, supportCounts, isSkin);
            (bones, bindposes) = plan.RebindExistingTwist(bones, bindposes, arms);
            plan._bones = bones;
            plan._bindposes = bindposes;

            for (var side = 0; side < arms.Length; side++)
            {
                plan.ResolveLower(side, arms[side], bones, bindposes);

                for (var boneIndex = 0; boneIndex < bones.Length; boneIndex++)
                {
                    if (plan.IsHiddenSectionOf(side, arms[side], bones[boneIndex]))
                    {
                        plan._hiddenSections.Add((side, boneIndex));
                    }
                }
            }

            if (subdivide)
            {
                plan.Subdivide(mesh);
            }

            for (var side = 0; side < arms.Length; side++)
            {
                var lowerIndex = plan._lowerIndices[side];

                if (lowerIndex >= 0)
                {
                    var first = bones.Length + supportCounts.Take(side).Sum();
                    plan.Redistribute(
                        lowerIndex,
                        plan.WeightNodes(plan.Nodes(side).Select(node => first + node), lowerIndex),
                        plan._axes[side]
                    );
                }
            }

            var mirrorIndex = bones.Length + supportCounts.Sum();

            foreach (var (side, boneIndex) in plan._hiddenSections)
            {
                var nodeCount = plan.Nodes(side).Length;
                plan.Redistribute(
                    boneIndex,
                    plan.WeightNodes(Enumerable.Range(mirrorIndex, nodeCount), boneIndex),
                    plan._axes[side]
                );
                mirrorIndex += nodeCount;
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
                foreach (
                    var mirror in TwistRig.Mirror(
                        bones[boneIndex],
                        Nodes(side).Select(node => supports[side][node])
                    )
                )
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

            if (_subdivision == null)
            {
                mesh.boneWeights = _weights;
            }
            else
            {
                _subdivision.Write(mesh, _weights);
            }

            mesh.bindposes = bindposes.ToArray();

            Renderer.bones = bones.ToArray();
            Renderer.sharedMesh = mesh;
            Renderer.quality = SkinQuality.Bone4;

            return drift;
        }

        private void Subdivide(Mesh mesh)
        {
            var sources = _lowerIndices
                .Select(
                    (lowerIndex, side) =>
                        _hiddenSections
                            .Where(section => section.Side == side)
                            .Select(section => section.BoneIndex)
                            .Append(lowerIndex)
                            .ToHashSet()
                )
                .ToArray();

            _subdivision = new ForearmSubdivision(mesh, _weights);
            _subdivision.Subdivide(
                MaxSubdivisionLevels,
                (first, firstWeight, second, secondWeight) =>
                    Enumerable
                        .Range(0, sources.Length)
                        .Any(side =>
                            _lowerIndices[side] >= 0
                            && UsesAny(firstWeight, sources[side])
                            && UsesAny(secondWeight, sources[side])
                            && Mathf.Abs(_axes[side].Position(first) - _axes[side].Position(second))
                                > MaxRingSpacing
                        )
            );
            var positions = _subdivision.Positions;

            if (positions.Length == mesh.vertexCount)
            {
                _subdivision = null;
                return;
            }

            _vertices = positions;
            _weights = _subdivision.Weights;
        }

        private int[] Nodes(int side) => TwistRig.Nodes(_isSkin, _supportCounts[side]);

        private int[] WeightNodes(IEnumerable<int> supports, int source) =>
            (_isSkin ? supports : supports.Append(source)).ToArray();

        private static bool UsesAny(BoneWeight weight, HashSet<int> bones) =>
            ForearmWeights.Slots(weight).Any(slot => slot.Weight > 0 && bones.Contains(slot.Bone));

        private void ResolveLower(int side, Arm arm, Transform[] bones, Matrix4x4[] bindposes)
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

        internal static int RigidFingerVertexCount(SkinnedMeshRenderer renderer, Arm[] arms)
        {
            if (FindMeshProblem(renderer) != null)
            {
                return 0;
            }

            var isFinger = renderer
                .bones.Select(bone =>
                    bone != null && arms.Any(arm => bone != arm.Hand && bone.IsChildOf(arm.Hand))
                )
                .ToArray();

            return renderer.sharedMesh.boneWeights.Count(weight =>
                ForearmWeights
                    .Slots(weight)
                    .Where(slot => slot.Bone < isFinger.Length && isFinger[slot.Bone])
                    .Sum(slot => slot.Weight) >= ForearmWeights.RigidWeight
            );
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

            for (var i = 0; i < oldWeights.Length; i++)
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
