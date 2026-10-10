using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static KOZ39.ElbowFixer.ElbowFixerException;
using Object = UnityEngine.Object;

namespace KOZ39.ElbowFixer
{
    internal sealed class MeshWeightPlan
    {
        private const float MaxRestDrift = 0.00001f;

        internal readonly SkinnedMeshRenderer Renderer;
        private readonly BoneWeight[] _weights;

        // LowerArm index of each arm in Renderer.bones, or -1 when the mesh does not use it.
        private readonly int[] _lowerIndices;

        internal int Redistributed { get; private set; }
        internal int Nearest { get; private set; }

        private MeshWeightPlan(SkinnedMeshRenderer renderer, BoneWeight[] weights, int armCount)
        {
            Renderer = renderer;
            _weights = weights;
            _lowerIndices = Enumerable.Repeat(-1, armCount).ToArray();
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
                plan.Redistribute(side, arms[side], bones, bindposes, vertices);
            }

            Require(plan.Redistributed > 0, "NoForearmWeights", renderer.name);

            return plan;
        }

        // Returns the largest rest pose drift in metres.
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

            // Order must match TwistRig.WeightNodes.
            for (var side = 0; side < supports.Length; side++)
            {
                foreach (var support in supports[side])
                {
                    bindposes.Add(SupportBindpose(support, _lowerIndices[side], bones, bindposes));
                    bones.Add(support);
                }
            }

            var after = SkinMatrices(bones, bindposes, avatarSpace);
            var drift = RestDrift(source.vertices, source.boneWeights, before, after);

            // Negated so that NaN also fails.
            if (!(drift < MaxRestDrift))
            {
                throw new ElbowFixerException("RestPoseDrift", Renderer.name, drift * 1000);
            }

            var mesh = Object.Instantiate(source);
            mesh.name = $"{source.name}_ElbowFixer";
            mesh.boneWeights = _weights;
            mesh.bindposes = bindposes.ToArray();

            Renderer.bones = bones.ToArray();
            Renderer.sharedMesh = mesh;
            Renderer.quality = SkinQuality.Bone4;

            return drift;
        }

        private void Redistribute(
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
                Renderer.name,
                arm.Name
            );
            _lowerIndices[side] = lower;

            var forearm = new ForearmAxis(bindposes[lower], arm.Hand.localPosition);
            // Arm.Resolve checks length in world units; mesh coordinates can use a different unit.
            Require(
                float.IsFinite(forearm.Length) && forearm.Length > 0,
                "InvalidBindpose",
                Renderer.name
            );

            var nodes = TwistRig.WeightNodes(bones.Length, side, lower);

            for (var v = 0; v < vertices.Length; v++)
            {
                if (ForearmWeights.LowerWeight(_weights[v], lower) <= 0)
                {
                    continue;
                }

                var t = forearm.Position(vertices[v]);

                if (t >= 1)
                {
                    continue;
                }

                if (!ForearmWeights.TryRedistribute(_weights[v], lower, nodes, t, out var weight))
                {
                    weight = ForearmWeights.Nearest(_weights[v], lower, nodes, t);
                    Nearest++;
                }

                _weights[v] = weight;
                Redistributed++;
            }
        }

        private static Mesh Validate(SkinnedMeshRenderer renderer)
        {
            var name = renderer.name;
            var mesh = renderer.sharedMesh;

            Require(mesh != null, "MissingMesh", name);
            Require(mesh.isReadable, "MeshNotReadable", name);
            Require(!renderer.TryGetComponent<Cloth>(out _), "ClothNotSupported", name);
            Require(
                renderer.quality == SkinQuality.Auto || renderer.quality == SkinQuality.Bone4,
                "SkinQuality",
                name
            );

            // Mesh.boneWeights holds four influences per vertex and would silently drop the rest.
            using (var bonesPerVertex = mesh.GetBonesPerVertex())
            {
                Require(
                    bonesPerVertex.All(count => count <= ForearmWeights.MaxInfluences),
                    "TooManyInfluences",
                    name
                );
            }

            var bones = renderer.bones;
            var weights = mesh.boneWeights;
            Require(mesh.bindposes.Length == bones.Length, "BindposeMismatch", name);
            Require(
                mesh.bindposes.All(matrix =>
                    Enumerable.Range(0, 16).All(i => float.IsFinite(matrix[i]))
                    && float.IsFinite(matrix.determinant)
                    && matrix.determinant != 0
                    && Enumerable.Range(0, 16).All(i => float.IsFinite(matrix.inverse[i]))
                ),
                "InvalidBindpose",
                name
            );
            Require(
                mesh.vertices.All(vertex =>
                    float.IsFinite(vertex.x) && float.IsFinite(vertex.y) && float.IsFinite(vertex.z)
                ),
                "NonFiniteVertices",
                name
            );
            Require(
                weights.Length == mesh.vertexCount
                    && weights.All(weight => IsValidWeight(weight, bones)),
                "InvalidWeights",
                name
            );

            return mesh;
        }

        private static bool IsValidWeight(BoneWeight weight, Transform[] bones)
        {
            var slots = ForearmWeights.Slots(weight);

            return slots.All(slot =>
                    float.IsFinite(slot.Weight)
                    && slot.Weight >= 0
                    && (
                        slot.Weight == 0
                        || (slot.Bone >= 0 && slot.Bone < bones.Length && bones[slot.Bone] != null)
                    )
                )
                && Mathf.Abs(slots.Sum(slot => slot.Weight) - 1) < 0.0001f;
        }

        // A support starts at the LowerArm, so deriving its bindpose from the LowerArm's
        // keeps the rest pose exact even when the current pose differs from the bind pose.
        private Matrix4x4 SupportBindpose(
            Transform support,
            int lower,
            List<Transform> bones,
            List<Matrix4x4> bindposes
        ) =>
            lower >= 0
                ? support.worldToLocalMatrix * bones[lower].localToWorldMatrix * bindposes[lower]
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
                Require(float.IsFinite(distance), "NonFiniteSkinning", Renderer.name);
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
