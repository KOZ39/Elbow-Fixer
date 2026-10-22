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

        // Problems the inspector treats apart from the rest.
        internal const string MissingMesh = nameof(MissingMesh);

        internal const string MeshNotReadable = nameof(MeshNotReadable);

        internal readonly SkinnedMeshRenderer Renderer;
        private readonly Vector3[] _vertices;
        private readonly BoneWeight[] _weights;
        private readonly int[] _lowerIndices;
        private readonly ForearmAxis[] _axes;
        private readonly List<(int Side, int BoneIndex)> _hiddenSections = new();

        // The renderer's bones and bindposes, with the arm bones the rebind adds.
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

            // Each read of these properties copies the whole array.
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

            // Mirrors are added after every arm's supports, in the order found here.
            for (var side = 0; side < arms.Length; side++)
            {
                for (var boneIndex = 0; boneIndex < bones.Length; boneIndex++)
                {
                    if (!plan.IsHiddenSectionOf(side, arms[side], boneIndex, bones, bindposes))
                    {
                        continue;
                    }

                    var group = arms.Length + plan._hiddenSections.Count;
                    plan._hiddenSections.Add((side, boneIndex));
                    plan.Redistribute(
                        boneIndex,
                        TwistRig.WeightNodes(bones.Length, group, boneIndex),
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

            foreach (var (side, boneIndex) in _hiddenSections)
            {
                foreach (var mirror in TwistRig.Mirror(bones[boneIndex], supports[side]))
                {
                    bindposes.Add(SupportBindpose(mirror, boneIndex, bones, bindposes));
                    bones.Add(mirror);
                }
            }

            // The original bones keep their matrices; only the added ones need computing.
            var after = before
                .Concat(SkinMatrices(bones.Skip(before.Length), bindposes.Skip(before.Length)))
                .ToArray();
            var drift = RestDrift(source.boneWeights, before, after);

            // NDMF's error console only substitutes plain {0} placeholders.
            Require(
                drift < MaxRestDrift,
                "RestPoseDrift",
                Renderer,
                (drift * 1000).ToString("F5", CultureInfo.CurrentCulture)
            );

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

            // Arm.Resolve checks length in world units; mesh coordinates can use a different unit.
            Require(
                float.IsFinite(_axes[side].Length) && _axes[side].Length > 0,
                "InvalidBindpose",
                Renderer
            );
            Redistribute(
                lowerIndex,
                TwistRig.WeightNodes(bones.Length, side, lowerIndex),
                _axes[side]
            );
        }

        // The source must move with the LowerArm, since the axis comes from the LowerArm's bindpose.
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

                if (t >= 1)
                {
                    continue;
                }

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

        // Modular Avatar hides a toggled mesh section by moving its weights to a copy of the bone
        // and animating the copy's scale to NaN. The copy sits under a buffer object that Merge
        // Armature may move and rename, but it keeps the copied bone's bindpose and stays below it.
        private bool IsHiddenSectionOf(
            int side,
            Arm arm,
            int boneIndex,
            Transform[] bones,
            Matrix4x4[] bindposes
        )
        {
            var lowerIndex = _lowerIndices[side];
            var candidate = bones[boneIndex];

            return lowerIndex >= 0
                && candidate != null
                && ElbowFixerUtility.IsHiddenBone(candidate)
                && candidate.IsChildOf(arm.Lower)
                && !candidate.IsChildOf(arm.Hand)
                && CopiesLowerArm(arm, boneIndex, bones, bindposes);
        }

        // A copy of the LowerArm, or of a bone whose weights move onto it, follows the rig through mirrors of the
        // supports placed under it.
        private static bool CopiesLowerArm(
            Arm arm,
            int boneIndex,
            Transform[] bones,
            Matrix4x4[] bindposes
        ) =>
            Enumerable
                .Range(0, bones.Length)
                .Any(i =>
                    i != boneIndex
                    && bones[i] != null
                    && bindposes[i] == bindposes[boneIndex]
                    && (bones[i] == arm.Lower || arm.FindRebind(bones[i]).ArmBone == arm.Lower)
                );

        // Moves weights on a twist bone or a bone with a constraint onto the arm bone it sits on, adding the arm bone
        // when the mesh does not list it. The bones keep their constraints for other meshes. A Modular Avatar copy of
        // such a bone keeps its weights, and Create moves them onto mirrors.
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
                    var boneIndex = Array.IndexOf(bones, bone);
                    var isHiddenCopy =
                        blocker != null
                        && ElbowFixerUtility.IsHiddenBone(bone)
                        && CopiesLowerArm(arm, boneIndex, bones, bindposes);
                    Require(
                        blocker == null || isHiddenCopy,
                        "MeshExistingTwist",
                        Renderer,
                        blocker
                    );

                    // The mirrors need the LowerArm even when only the copy uses the bone it copies.
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
                        bindposeList.Add(BindposeFrom(armBone, bone, bindposes[boneIndex]));
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

        // Shared with the inspector. Checked at build time too, since another tool may have replaced the mesh.
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

        // An unweighted vertex is left unchanged, so it skins the same as before.
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

        // Shared with the auto add. The bindpose that makes a bone skin like the reference bone in the current pose.
        // Deriving it from the reference's bindpose keeps the rest pose exact even when the current pose differs from
        // the bind pose.
        internal static Matrix4x4 BindposeFrom(
            Transform bone,
            Transform reference,
            Matrix4x4 referenceBindpose
        ) => bone.worldToLocalMatrix * reference.localToWorldMatrix * referenceBindpose;

        // A support starts at the LowerArm, so its bindpose comes from the LowerArm's.
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
