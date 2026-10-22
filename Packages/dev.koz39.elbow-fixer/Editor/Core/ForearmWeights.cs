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

        internal static Transform[] UsedBones(BoneWeight[] weights, Transform[] bones) =>
            weights
                .SelectMany(Slots)
                .Where(slot => slot.Weight > 0 && slot.Bone >= 0 && slot.Bone < bones.Length)
                .Select(slot => bones[slot.Bone])
                .Where(bone => bone != null)
                .Distinct()
                .ToArray();

        internal static float SourceWeight(BoneWeight weight, int source) =>
            Slots(weight)
                .Where(slot => slot.Bone == source && slot.Weight > 0)
                .Sum(slot => slot.Weight);

        internal static bool TryRedistribute(
            BoneWeight weight,
            int source,
            float sourceWeight,
            int[] nodes,
            float t,
            out BoneWeight result
        )
        {
            var influences = Others(weight, source);
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

            var fits = influences.Count <= MaxInfluences;
            result = fits ? Pack(influences) : default;

            return fits;
        }

        // Compromise for vertices where two nodes do not fit in the four influences.
        internal static BoneWeight Nearest(
            BoneWeight weight,
            int source,
            float sourceWeight,
            int[] nodes,
            float t
        )
        {
            var influences = Others(weight, source);
            Add(influences, nodes[Mathf.RoundToInt(t * (nodes.Length - 1))], sourceWeight);

            return Pack(influences);
        }

        internal static BoneWeight Rebind(BoneWeight weight, IReadOnlyDictionary<int, int> targets)
        {
            var slots = Slots(weight);

            if (!slots.Any(slot => slot.Weight > 0 && targets.ContainsKey(slot.Bone)))
            {
                return weight;
            }

            var influences = new Dictionary<int, float>();

            foreach (var (bone, slotWeight) in slots)
            {
                Add(
                    influences,
                    targets.TryGetValue(bone, out var target) ? target : bone,
                    slotWeight
                );
            }

            return Pack(influences);
        }

        private static Dictionary<int, float> Others(BoneWeight weight, int source) =>
            Slots(weight)
                .Where(slot => slot.Bone != source && slot.Weight > 0)
                .GroupBy(slot => slot.Bone)
                .ToDictionary(group => group.Key, group => group.Sum(slot => slot.Weight));

        private static void Add(Dictionary<int, float> influences, int bone, float weight)
        {
            if (weight > 0)
            {
                influences.TryGetValue(bone, out var current);
                influences[bone] = current + weight;
            }
        }

        private static BoneWeight Pack(Dictionary<int, float> influences)
        {
            var sorted = influences
                .OrderByDescending(influence => influence.Value)
                .Concat(Enumerable.Repeat(new KeyValuePair<int, float>(0, 0), MaxInfluences))
                .ToArray();

            return new BoneWeight
            {
                boneIndex0 = sorted[0].Key,
                weight0 = sorted[0].Value,
                boneIndex1 = sorted[1].Key,
                weight1 = sorted[1].Value,
                boneIndex2 = sorted[2].Key,
                weight2 = sorted[2].Value,
                boneIndex3 = sorted[3].Key,
                weight3 = sorted[3].Value,
            };
        }
    }
}
