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
        private readonly HashSet<int>[] _lowerPhysBones;
        private readonly List<int> _skinnedSides = new();
        private readonly List<(int Side, int BoneIndex)> _hiddenSections = new();
        private readonly Dictionary<int, Matrix4x4> _restCorrections = new();
        private readonly Vector3[] _normals;
        private readonly Vector4[] _tangents;
        private Vector3[] _vertices;
        private BoneWeight[] _weights;
        private ForearmSubdivision _subdivision;
        private Transform[] _bones;
        private Matrix4x4[] _bindposes;

        internal int RedistributedCount { get; private set; }
        internal int NearestCount { get; private set; }
        internal int AddedVertexCount { get; private set; }
        internal bool IsSkin { get; }

        internal int MirrorCount => _hiddenSections.Count * TwistRig.SupportCount;

        private MeshWeightPlan(
            SkinnedMeshRenderer renderer,
            Vector3[] vertices,
            BoneWeight[] weights,
            int armCount,
            bool isSkin
        )
        {
            Renderer = renderer;
            _normals = renderer.sharedMesh.normals;
            _tangents = renderer.sharedMesh.tangents;
            _vertices = vertices;
            _weights = weights;
            _lowerIndices = Enumerable.Repeat(-1, armCount).ToArray();
            _axes = new ForearmAxis[armCount];
            _lowerPhysBones = new HashSet<int>[armCount];
            IsSkin = isSkin;
        }

        internal static MeshWeightPlan Create(
            SkinnedMeshRenderer renderer,
            Arm[] arms,
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

            var plan = new MeshWeightPlan(renderer, vertices, weights, arms.Length, isSkin);
            (bones, bindposes) = plan.RebindExistingTwist(bones, bindposes, arms);
            plan._bones = bones;
            plan._bindposes = bindposes;

            for (var side = 0; side < arms.Length; side++)
            {
                plan.ResolveLower(side, arms[side], bones, bindposes);
                plan._lowerPhysBones[side] = Enumerable
                    .Range(0, bones.Length)
                    .Where(i => bones[i] != null && arms[side].IsLowerPhysBone(bones[i]))
                    .ToHashSet();

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

            var firstNode = bones.Length;

            for (var side = 0; side < arms.Length; side++)
            {
                var lowerIndex = plan._lowerIndices[side];

                if (lowerIndex >= 0 && plan.Redistribute(side, lowerIndex, Nodes(firstNode)))
                {
                    plan._skinnedSides.Add(side);
                    firstNode += TwistRig.SupportCount;
                }
            }

            foreach (var (side, boneIndex) in plan._hiddenSections)
            {
                plan.Redistribute(side, boneIndex, Nodes(firstNode));
                firstNode += TwistRig.SupportCount;
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
            foreach (var side in _skinnedSides)
            {
                var lowerIndex = _lowerIndices[side];

                foreach (var support in supports[side])
                {
                    bindposes.Add(BindposeFrom(support, bones[lowerIndex], bindposes[lowerIndex]));
                    bones.Add(support);
                }
            }

            var mirrors = new List<Transform>();

            foreach (var (side, boneIndex) in _hiddenSections)
            {
                foreach (var mirror in TwistRig.Mirror(bones[boneIndex], supports[side]))
                {
                    bindposes.Add(BindposeFrom(mirror, bones[boneIndex], bindposes[boneIndex]));
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
                drift = RestDrift(source.boneWeights, source.vertices, before, after);

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
                mesh.vertices = _vertices;
                mesh.normals = _normals;
                mesh.tangents = _tangents;
                mesh.boneWeights = _weights;

                if (_restCorrections.Count > 0)
                {
                    mesh.ClearBlendShapes();
                    ForearmSubdivision.WriteBlendShapes(
                        source,
                        mesh,
                        _restCorrections,
                        Array.Empty<(int, int)>()
                    );
                }
            }
            else
            {
                _subdivision.Write(mesh, _weights, _restCorrections);
            }

            mesh.bindposes = bindposes.ToArray();

            Renderer.bones = bones.ToArray();
            Renderer.sharedMesh = mesh;
            Renderer.quality = SkinQuality.Bone4;

            return drift;
        }

        internal static int RigidHandVertexCount(
            SkinnedMeshRenderer renderer,
            Arm[] arms,
            bool includeHand
        )
        {
            if (FindMeshProblem(renderer) != null)
            {
                return 0;
            }

            var isHand = renderer
                .bones.Select(bone =>
                    bone != null
                    && arms.Any(arm =>
                        (includeHand || bone != arm.Hand) && bone.IsChildOf(arm.Hand)
                    )
                )
                .ToArray();

            return renderer.sharedMesh.boneWeights.Count(weight =>
                ForearmWeights
                    .Slots(weight)
                    .Where(slot => slot.Bone < isHand.Length && isHand[slot.Bone])
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

        internal static Matrix4x4 BindposeFrom(
            Transform bone,
            Transform reference,
            Matrix4x4 referenceBindpose
        ) => bone.worldToLocalMatrix * reference.localToWorldMatrix * referenceBindpose;

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

            _subdivision = new(mesh, _vertices, _normals, _tangents, _weights);
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

            AddedVertexCount = positions.Length - mesh.vertexCount;
            _vertices = positions;
            _weights = _subdivision.Weights;
        }

        private static int[] Nodes(int first) =>
            Enumerable.Range(first, TwistRig.SupportCount).ToArray();

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
            _axes[side] = new(bindposes[lowerIndex], arm.Hand.localPosition);

            Require(
                float.IsFinite(_axes[side].Length) && _axes[side].Length > 0,
                "InvalidBindpose",
                Renderer
            );
        }

        private bool Redistribute(int side, int source, int[] nodes)
        {
            var previousCount = RedistributedCount;

            for (var v = 0; v < _vertices.Length; v++)
            {
                var sourceWeight = ForearmWeights.SourceWeight(_weights[v], source);

                if (sourceWeight <= 0)
                {
                    continue;
                }

                var physBoneWeight = ForearmWeights
                    .Slots(_weights[v])
                    .Where(slot => slot.Weight > 0 && _lowerPhysBones[side].Contains(slot.Bone))
                    .Sum(slot => slot.Weight);

                // A PhysBone chain under the LowerArm turns far more than the nodes near the elbow,
                // and mixing them pulls the vertex toward the forearm axis.
                var t = _axes[side].Position(_vertices[v]);
                t += (1 - t) * physBoneWeight / (sourceWeight + physBoneWeight);

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

            return RedistributedCount > previousCount;
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
                var skin = SkinMatrices(boneList, bindposeList).ToArray();

                for (var v = 0; v < _weights.Length; v++)
                {
                    var rebound = ForearmWeights.Rebind(_weights[v], rebind);

                    if (rebound == _weights[v])
                    {
                        continue;
                    }

                    // A twist bone can rest away from its bindpose relative to the arm bone,
                    // so the vertex moves and turns to look the same in the rest pose.
                    var correction =
                        BlendedSkin(skin, rebound).inverse * BlendedSkin(skin, _weights[v]);
                    _restCorrections[v] = correction;
                    _vertices[v] = correction.MultiplyPoint3x4(_vertices[v]);

                    if (_normals.Length > 0)
                    {
                        _normals[v] = correction.MultiplyVector(_normals[v]).normalized;
                    }

                    if (_tangents.Length > 0)
                    {
                        Vector4 tangent = correction.MultiplyVector(_tangents[v]).normalized;
                        tangent.w = _tangents[v].w;
                        _tangents[v] = tangent;
                    }

                    _weights[v] = rebound;
                }
            }

            return (boneList.ToArray(), bindposeList.ToArray());
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

        private static IEnumerable<Matrix4x4> SkinMatrices(
            IEnumerable<Transform> bones,
            IEnumerable<Matrix4x4> bindposes
        ) =>
            bones.Zip(
                bindposes,
                (bone, bindpose) =>
                    bone != null ? bone.localToWorldMatrix * bindpose : Matrix4x4.identity
            );

        private float RestDrift(
            BoneWeight[] oldWeights,
            Vector3[] oldVertices,
            Matrix4x4[] before,
            Matrix4x4[] after
        )
        {
            var drift = 0f;

            for (var i = 0; i < oldWeights.Length; i++)
            {
                var oldPosition = Skin(before, oldWeights[i], oldVertices[i]);
                var newPosition = Skin(after, _weights[i], _vertices[i]);
                var distance = Vector3.Distance(oldPosition, newPosition);
                Require(float.IsFinite(distance), "NonFiniteSkinning", Renderer);
                drift = Mathf.Max(drift, distance);
            }

            return drift;
        }

        private static Vector3 Skin(Matrix4x4[] matrices, BoneWeight weight, Vector3 vertex) =>
            BlendedSkin(matrices, weight).MultiplyPoint3x4(vertex);

        private static Matrix4x4 BlendedSkin(Matrix4x4[] matrices, BoneWeight weight)
        {
            var blended = new Matrix4x4();

            foreach (var slot in ForearmWeights.Slots(weight).Where(slot => slot.Weight > 0))
            {
                for (var i = 0; i < 16; i++)
                {
                    blended[i] += matrices[slot.Bone][i] * slot.Weight;
                }
            }

            return blended;
        }
    }
}
