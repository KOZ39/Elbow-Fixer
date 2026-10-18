using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using static KOZ39.ElbowFixer.ElbowFixerException;

namespace KOZ39.ElbowFixer
{
    internal sealed class MeshWeightPlan
    {
        private const float MaxRestDrift = 0.00001f;

        internal readonly SkinnedMeshRenderer Renderer;
        private readonly BoneWeight[] _weights;
        private readonly int[] _lowerIndices;
        private readonly ForearmAxis[] _axes;
        private readonly List<(int Side, int Bone)> _hiddenSections = new();

        internal int RedistributedCount { get; private set; }
        internal int NearestCount { get; private set; }

        internal int MirrorCount => _hiddenSections.Count * TwistRig.SupportCount;

        private MeshWeightPlan(SkinnedMeshRenderer renderer, BoneWeight[] weights, int armCount)
        {
            Renderer = renderer;
            _weights = weights;
            _lowerIndices = Enumerable.Repeat(-1, armCount).ToArray();
            _axes = new ForearmAxis[armCount];
        }

        internal static MeshWeightPlan Create(SkinnedMeshRenderer renderer, Arm[] arms)
        {
            var mesh = Validate(renderer);
            var plan = new MeshWeightPlan(renderer, mesh.boneWeights, arms.Length);
            var bones = renderer.bones;
            var bindposes = mesh.bindposes;
            var vertices = mesh.vertices;

            for (var side = 0; side < arms.Length; side++)
            {
                plan.RedistributeLower(side, arms[side], bones, bindposes, vertices);
            }

            // Mirrors are added after every arm's supports, in the order found here.
            for (var side = 0; side < arms.Length; side++)
            {
                for (var bone = 0; bone < bones.Length; bone++)
                {
                    if (!plan.IsHiddenSectionOf(side, arms[side], bone, bones, bindposes))
                    {
                        continue;
                    }

                    var group = arms.Length + plan._hiddenSections.Count;
                    plan._hiddenSections.Add((side, bone));
                    plan.Redistribute(
                        bone,
                        TwistRig.WeightNodes(bones.Length, group, bone),
                        plan._axes[side],
                        vertices
                    );
                }
            }

            return plan;
        }

        internal float Apply(Transform[][] supports, Transform avatarRoot)
        {
            var source = Renderer.sharedMesh;
            var bones = Renderer.bones.ToList();
            var bindposes = source.bindposes.ToList();

            // Compare in avatar space so the avatar's world position does not cost float precision.
            var avatarSpace = Matrix4x4
                .TRS(avatarRoot.position, avatarRoot.rotation, Vector3.one)
                .inverse;
            var before = SkinMatrices(bones, bindposes, avatarSpace);

            // Order must match the weight nodes from Create.
            for (var side = 0; side < supports.Length; side++)
            {
                foreach (var support in supports[side])
                {
                    bindposes.Add(SupportBindpose(support, _lowerIndices[side], bones, bindposes));
                    bones.Add(support);
                }
            }

            foreach (var (side, bone) in _hiddenSections)
            {
                foreach (var mirror in TwistRig.Mirror(bones[bone], supports[side]))
                {
                    bindposes.Add(SupportBindpose(mirror, bone, bones, bindposes));
                    bones.Add(mirror);
                }
            }

            var after = SkinMatrices(bones, bindposes, avatarSpace);
            var drift = RestDrift(source.vertices, source.boneWeights, before, after);

            // NDMF's error console only substitutes plain {0} placeholders.
            Require(
                drift < MaxRestDrift,
                "RestPoseDrift",
                Renderer,
                (drift * 1000).ToString("F5", CultureInfo.CurrentCulture)
            );

            var mesh = UnityEngine.Object.Instantiate(source);
            mesh.name = $"{source.name}_ElbowFixer";
            mesh.boneWeights = _weights;
            mesh.bindposes = bindposes.ToArray();

            Renderer.bones = bones.ToArray();
            Renderer.sharedMesh = mesh;
            Renderer.quality = SkinQuality.Bone4;

            return drift;
        }

        private void RedistributeLower(
            int side,
            Arm arm,
            Transform[] bones,
            Matrix4x4[] bindposes,
            Vector3[] vertices
        )
        {
            var lower = Array.IndexOf(bones, arm.Lower);

            if (lower < 0)
            {
                return;
            }

            Require(
                Array.LastIndexOf(bones, arm.Lower) == lower,
                "DuplicateLowerArm",
                Renderer,
                arm.Name
            );
            _lowerIndices[side] = lower;
            _axes[side] = new ForearmAxis(bindposes[lower], arm.Hand.localPosition);

            // Arm.Resolve checks length in world units; mesh coordinates can use a different unit.
            Require(
                float.IsFinite(_axes[side].Length) && _axes[side].Length > 0,
                "InvalidBindpose",
                Renderer
            );
            Redistribute(
                lower,
                TwistRig.WeightNodes(bones.Length, side, lower),
                _axes[side],
                vertices
            );
        }

        // The source must move with the LowerArm, since the axis comes from the LowerArm's bindpose.
        private void Redistribute(int source, int[] nodes, ForearmAxis forearm, Vector3[] vertices)
        {
            for (var v = 0; v < vertices.Length; v++)
            {
                if (ForearmWeights.SourceWeight(_weights[v], source) <= 0)
                {
                    continue;
                }

                var t = forearm.Position(vertices[v]);

                if (t >= 1)
                {
                    continue;
                }

                if (!ForearmWeights.TryRedistribute(_weights[v], source, nodes, t, out var weight))
                {
                    weight = ForearmWeights.Nearest(_weights[v], source, nodes, t);
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
            int bone,
            Transform[] bones,
            Matrix4x4[] bindposes
        )
        {
            var lower = _lowerIndices[side];

            return lower >= 0
                && bones[bone] != null
                && ElbowFixerUtility.IsHiddenBone(bones[bone])
                && bones[bone].IsChildOf(arm.Lower)
                && !bones[bone].IsChildOf(arm.Hand)
                && bindposes[bone] == bindposes[lower];
        }

        private static Mesh Validate(SkinnedMeshRenderer renderer)
        {
            var mesh = renderer.sharedMesh;

            Require(mesh != null, "MissingMesh", renderer);
            Require(mesh.isReadable, "MeshNotReadable", renderer);
            Require(
                renderer.quality == SkinQuality.Auto || renderer.quality == SkinQuality.Bone4,
                "SkinQuality",
                renderer
            );

            // Mesh.boneWeights holds four influences per vertex and would silently drop the rest.
            using (var bonesPerVertex = mesh.GetBonesPerVertex())
            {
                Require(
                    bonesPerVertex.All(count => count <= ForearmWeights.MaxInfluences),
                    "TooManyInfluences",
                    renderer
                );
            }

            var bones = renderer.bones;
            var weights = mesh.boneWeights;
            Require(mesh.bindposes.Length == bones.Length, "BindposeMismatch", renderer);
            Require(
                mesh.bindposes.All(matrix =>
                    Enumerable.Range(0, 16).All(i => float.IsFinite(matrix[i]))
                    && float.IsFinite(matrix.determinant)
                    && matrix.determinant != 0
                    && Enumerable.Range(0, 16).All(i => float.IsFinite(matrix.inverse[i]))
                ),
                "InvalidBindpose",
                renderer
            );
            Require(
                mesh.vertices.All(vertex =>
                    float.IsFinite(vertex.x) && float.IsFinite(vertex.y) && float.IsFinite(vertex.z)
                ),
                "NonFiniteVertices",
                renderer
            );
            Require(
                weights.Length == mesh.vertexCount
                    && weights.All(weight => IsValidWeight(weight, bones)),
                "InvalidWeights",
                renderer
            );

            return mesh;
        }

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

        private static Matrix4x4[] SkinMatrices(
            List<Transform> bones,
            List<Matrix4x4> bindposes,
            Matrix4x4 space
        ) =>
            bones
                .Select(
                    (bone, i) =>
                        bone != null
                            ? space * bone.localToWorldMatrix * bindposes[i]
                            : Matrix4x4.identity
                )
                .ToArray();

        private float RestDrift(
            Vector3[] vertices,
            BoneWeight[] oldWeights,
            Matrix4x4[] before,
            Matrix4x4[] after
        )
        {
            var drift = 0f;

            for (var i = 0; i < vertices.Length; i++)
            {
                var oldPosition = Skin(before, oldWeights[i], vertices[i]);
                var newPosition = Skin(after, _weights[i], vertices[i]);
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
