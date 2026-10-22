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

        internal readonly SkinnedMeshRenderer Renderer;
        private readonly Vector3[] _vertices;
        private readonly BoneWeight[] _weights;
        private readonly int[] _lowerIndices;
        private readonly ForearmAxis[] _axes;
        private readonly List<(int Side, int BoneIndex)> _hiddenSections = new();

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
            var mesh = renderer.sharedMesh;
            Require(mesh != null, "MissingMesh", renderer);
            Require(mesh.isReadable, "MeshNotReadable", renderer);
            Require(
                renderer.quality == SkinQuality.Auto || renderer.quality == SkinQuality.Bone4,
                "SkinQuality",
                renderer
            );

            // Each read of these properties copies the whole array.
            var bones = renderer.bones;
            var bindposes = mesh.bindposes;
            var vertices = mesh.vertices;
            var weights = mesh.boneWeights;
            Validate(renderer, mesh, bones, bindposes, vertices, weights);

            var plan = new MeshWeightPlan(renderer, vertices, weights, arms.Length);

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
            var bones = Renderer.bones.ToList();
            var bindposes = source.bindposes.ToList();
            var before = SkinMatrices(bones, bindposes).ToArray();

            // Order must match the weight nodes from Create.
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
        // and animating the copy's scale to NaN. A copy of the LowerArm moves with the LowerArm.
        // The copy sits under a buffer object that Merge Armature may move and rename, but it keeps
        // the LowerArm's bindpose and stays below the LowerArm.
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
                && bindposes[boneIndex] == bindposes[lowerIndex];
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

        // A support starts at the LowerArm, so deriving its bindpose from the LowerArm's
        // keeps the rest pose exact even when the current pose differs from the bind pose.
        private Matrix4x4 SupportBindpose(
            Transform support,
            int source,
            List<Transform> bones,
            List<Matrix4x4> bindposes
        ) =>
            source >= 0
                ? support.worldToLocalMatrix * bones[source].localToWorldMatrix * bindposes[source]
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
