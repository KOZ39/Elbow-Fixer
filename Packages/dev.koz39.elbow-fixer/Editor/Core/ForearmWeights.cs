// Forearm smoothing informed by Avatar Elbow Repair (MIT). See LICENSE.
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KOZ39.ElbowFixer
{
    internal readonly struct ForearmAxis
    {
        internal readonly float Length;
        private readonly Vector3 _elbow;
        private readonly Vector3 _direction;

        internal ForearmAxis(Matrix4x4 lowerBindpose, Vector3 handLocalPosition)
        {
            var meshFromLower = lowerBindpose.inverse;
            var axis = meshFromLower.MultiplyVector(handLocalPosition);
            _elbow = meshFromLower.MultiplyPoint3x4(Vector3.zero);
            Length = axis.magnitude;
            _direction = axis / Length;
        }

        // Callers check Length first.
        internal float Position(Vector3 vertex) =>
            Mathf.Clamp01(Vector3.Dot(vertex - _elbow, _direction) / Length);
    }

    internal static class ForearmWeights
    {
        internal const int MaxInfluences = 4;

        internal static (int Bone, float Weight)[] Slots(BoneWeight weight) =>
            new (int Bone, float Weight)[]
            {
                (weight.boneIndex0, weight.weight0),
                (weight.boneIndex1, weight.weight1),
                (weight.boneIndex2, weight.weight2),
                (weight.boneIndex3, weight.weight3),
            };

        internal static float SourceWeight(BoneWeight weight, int source) =>
            Slots(weight)
                .Where(slot => slot.Bone == source && slot.Weight > 0)
                .Sum(slot => slot.Weight);

        internal static bool TryRedistribute(
            BoneWeight weight,
            int source,
            int[] nodes,
            float t,
            out BoneWeight result
        )
        {
            var influences = Others(weight, source);
            var sourceWeight = SourceWeight(weight, source);
            var last = nodes.Length - 1;
            var phase = t * last;
            var first = Mathf.FloorToInt(phase);
            var fraction = phase - first;

            // Spreading over three nodes evens out the bulge each node leaves on a strongly twisted
            // forearm. It needs three slots, so vertices with two or more other bones stay linear.
            // It fades out toward the elbow and wrist, where the clamped kernel would shift the twist.
            var smoothing =
                influences.Count <= 1
                    ? Mathf.SmoothStep(0, 1, Mathf.Min(phase, last - phase) * 2)
                    : 0;

            if (smoothing > 0)
            {
                var center = Mathf.FloorToInt(phase + 0.5f);
                var offset = phase - center;

                Add(
                    influences,
                    nodes[Mathf.Max(center - 1, 0)],
                    sourceWeight * smoothing * 0.5f * (0.5f - offset) * (0.5f - offset)
                );
                Add(
                    influences,
                    nodes[center],
                    sourceWeight * smoothing * (0.75f - offset * offset)
                );
                Add(
                    influences,
                    nodes[Mathf.Min(center + 1, last)],
                    sourceWeight * smoothing * 0.5f * (0.5f + offset) * (0.5f + offset)
                );
            }

            Add(influences, nodes[first], sourceWeight * (1 - fraction) * (1 - smoothing));
            Add(influences, nodes[first + 1], sourceWeight * fraction * (1 - smoothing));

            result = influences.Count <= MaxInfluences ? Pack(influences) : default;

            return influences.Count <= MaxInfluences;
        }

        // Compromise for vertices where two nodes do not fit in the four influences.
        internal static BoneWeight Nearest(BoneWeight weight, int source, int[] nodes, float t)
        {
            var influences = Others(weight, source);
            Add(
                influences,
                nodes[Mathf.RoundToInt(t * (nodes.Length - 1))],
                SourceWeight(weight, source)
            );

            return Pack(influences);
        }

        private static List<(int Bone, float Weight)> Others(BoneWeight weight, int source) =>
            Slots(weight)
                .Where(slot => slot.Bone != source && slot.Weight > 0)
                .GroupBy(slot => slot.Bone)
                .Select(group => (Bone: group.Key, Weight: group.Sum(slot => slot.Weight)))
                .ToList();

        private static void Add(List<(int Bone, float Weight)> influences, int bone, float weight)
        {
            if (weight <= 0)
            {
                return;
            }

            var index = influences.FindIndex(influence => influence.Bone == bone);

            if (index < 0)
            {
                influences.Add((bone, weight));
            }
            else
            {
                influences[index] = (bone, influences[index].Weight + weight);
            }
        }

        private static BoneWeight Pack(List<(int Bone, float Weight)> influences)
        {
            var sorted = influences.OrderByDescending(influence => influence.Weight).ToList();

            while (sorted.Count < MaxInfluences)
            {
                sorted.Add((0, 0));
            }

            return new BoneWeight
            {
                boneIndex0 = sorted[0].Bone,
                weight0 = sorted[0].Weight,
                boneIndex1 = sorted[1].Bone,
                weight1 = sorted[1].Weight,
                boneIndex2 = sorted[2].Bone,
                weight2 = sorted[2].Weight,
                boneIndex3 = sorted[3].Bone,
                weight3 = sorted[3].Weight,
            };
        }
    }
}
