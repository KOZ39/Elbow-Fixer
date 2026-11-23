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
        private const float MaxPieceSpan = 0.2f;
        private const float MaxRibbonGap = 0.005f;
        internal const float MaxAccessorySpan = 0.5f;
        private const int MaxSubdivisionLevels = 3;

        internal const string MissingMesh = nameof(MissingMesh);
        internal const string MeshNotReadable = nameof(MeshNotReadable);

        internal readonly SkinnedMeshRenderer Renderer;
        internal readonly Dictionary<Transform, float> ChainNodes = new();
        private readonly int[] _lowerIndices;
        private readonly ForearmAxis[] _axes;
        private readonly HashSet<int>[] _lowerChains;
        private readonly Dictionary<int, float>[] _physBoneTwists;
        private readonly List<int> _skinnedSides = new();
        private readonly List<(int Side, int BoneIndex)> _hiddenSections = new();
        private readonly Dictionary<int, int> _hiddenCopies = new();
        private readonly Dictionary<int, Matrix4x4> _restCorrections = new();
        private readonly Vector3[] _normals;
        private readonly Vector4[] _tangents;
        private Vector3[] _vertices;
        private BoneWeight[] _weights;
        private ForearmSubdivision _subdivision;
        private Transform[] _bones;
        private Matrix4x4[] _bindposes;

        internal int RedistributedCount { get; private set; }
        internal int AddedVertexCount { get; private set; }
        internal int MirrorCount { get; private set; }
        internal bool IsSkin { get; }

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
            _lowerChains = new HashSet<int>[armCount];
            _physBoneTwists = new Dictionary<int, float>[armCount];
            IsSkin = isSkin;
        }

        internal static MeshWeightPlan Create(
            SkinnedMeshRenderer renderer,
            Arm[] arms,
            bool isSkin,
            bool subdivide,
            HashSet<Transform> movableRoots,
            Func<Transform, bool> isAnimated
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
            (bones, bindposes) = plan.RebindExistingTwist(bones, bindposes, arms, isAnimated);
            plan._bones = bones;
            plan._bindposes = bindposes;

            for (var side = 0; side < arms.Length; side++)
            {
                var arm = arms[side];
                plan.ResolveLower(side, arm, bones, bindposes);
                plan._lowerChains[side] = Enumerable
                    .Range(0, bones.Length)
                    .Where(i => bones[i] != null && arm.IsLowerPhysBone(bones[i]))
                    .ToHashSet();

                // A PhysBone chain under the LowerArm stays on it or moves onto the LowerArm node,
                // which turns like it.
                plan._physBoneTwists[side] = plan._lowerChains[side]
                    .ToDictionary(
                        i => i,
                        _ => (float)TwistRig.LowerArmNode(arm) / (TwistRig.SupportCount - 1)
                    );

                for (var i = 0; i < bones.Length; i++)
                {
                    if (bones[i] == null)
                    {
                        continue;
                    }

                    if (arm.IsUpperPhysBone(bones[i]))
                    {
                        plan._physBoneTwists[side][i] = 0;
                    }
                    else if (arm.IsHandPhysBone(bones[i]))
                    {
                        plan._physBoneTwists[side][i] = 1;
                    }
                }

                for (var boneIndex = 0; boneIndex < bones.Length; boneIndex++)
                {
                    if (plan.IsHiddenSectionOf(side, arm, bones[boneIndex]))
                    {
                        plan._hiddenSections.Add((side, boneIndex));
                    }
                }
            }

            for (var boneIndex = 0; boneIndex < bones.Length; boneIndex++)
            {
                var copied =
                    bones[boneIndex] != null && ElbowFixerUtility.IsHiddenBone(bones[boneIndex])
                        ? Array.IndexOf(bones, ElbowFixerUtility.CopiedBone(bones[boneIndex]))
                        : -1;

                if (
                    copied >= 0
                    && plan._hiddenSections.All(section => section.BoneIndex != boneIndex)
                )
                {
                    plan._hiddenCopies[boneIndex] = copied;
                }
            }

            if (subdivide)
            {
                plan.Subdivide(mesh);
            }

            var firstNode = bones.Length;
            var supportNodes = new int[arms.Length][];

            for (var side = 0; side < arms.Length; side++)
            {
                if (plan._lowerIndices[side] >= 0 && plan.HasSourceWeight(side))
                {
                    plan._skinnedSides.Add(side);
                    supportNodes[side] = Nodes(firstNode);
                    firstNode += TwistRig.SupportCount;
                }
            }

            var mirrorNodes = new Dictionary<int, int[]>();
            var pieces = plan._subdivision == null ? plan.Pieces() : Array.Empty<int[]>();

            foreach (var (_, boneIndex) in plan._hiddenSections)
            {
                mirrorNodes[boneIndex] = Nodes(firstNode);
                firstNode += TwistRig.SupportCount;
            }

            foreach (var side in plan._skinnedSides)
            {
                plan.Redistribute(
                    side,
                    arms[side],
                    supportNodes[side],
                    mirrorNodes,
                    pieces,
                    movableRoots
                );
            }

            return plan;
        }

        internal void Apply(Transform[][] supports)
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

            var used = _weights
                .SelectMany(ForearmWeights.Slots)
                .Where(slot => slot.Weight > 0)
                .Select(slot => slot.Bone)
                .ToHashSet();
            var mirrors = new List<Transform>();
            var renumbered = new Dictionary<int, int>();
            var node = bones.Count;

            foreach (var (side, boneIndex) in _hiddenSections)
            {
                var keptNodes = Enumerable.Range(node, TwistRig.SupportCount).Where(used.Contains);

                foreach (var keptNode in keptNodes)
                {
                    var mirror = TwistRig.Mirror(bones[boneIndex], supports[side][keptNode - node]);
                    renumbered[keptNode] = bones.Count;
                    bindposes.Add(BindposeFrom(mirror, bones[boneIndex], bindposes[boneIndex]));
                    bones.Add(mirror);
                    mirrors.Add(mirror);
                }

                node += TwistRig.SupportCount;
            }

            _weights = _weights
                .Select(weight => ForearmWeights.Rebind(weight, renumbered))
                .ToArray();
            MirrorCount = mirrors.Count;

            var after = before
                .Concat(SkinMatrices(bones.Skip(before.Length), bindposes.Skip(before.Length)))
                .ToArray();

            try
            {
                var drift = RestDrift(source.boneWeights, source.vertices, before, after);

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
            var sources = Enumerable.Range(0, _lowerIndices.Length).Select(Sources).ToArray();

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

        private HashSet<int> Sources(int side) =>
            _hiddenSections
                .Where(section => section.Side == side)
                .Select(section => section.BoneIndex)
                .Append(_lowerIndices[side])
                .ToHashSet();

        private bool HasSourceWeight(int side)
        {
            var sources = Sources(side);

            return _weights.Any(weight => UsesAny(weight, sources));
        }

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

        private void Redistribute(
            int side,
            Arm arm,
            int[] nodes,
            Dictionary<int, int[]> mirrorNodes,
            int[][] pieces,
            HashSet<Transform> movableRoots
        )
        {
            var sources = Sources(side);
            var rawPositions = _vertices.Select(_axes[side].Position).ToArray();
            var forearm = rawPositions.Where((_, v) => UsesAny(_weights[v], sources)).ToArray();
            var isSleeve = forearm.Max() - forearm.Min() > MaxAccessorySpan;
            var ribbons = Array.Empty<(int[] Piece, float Twist)>();

            if (isSleeve)
            {
                ribbons = FindRibbons(side, arm, pieces, rawPositions, movableRoots);
            }
            else
            {
                HangAccessoryChains(side, arm, movableRoots);
            }

            var sourceWeights = _weights
                .Select(weight => ForearmWeights.SourceWeight(weight, sources))
                .ToArray();
            var positions = new float[_vertices.Length];

            for (var v = 0; v < _vertices.Length; v++)
            {
                var sourceWeight = sourceWeights[v];

                if (sourceWeight <= 0)
                {
                    continue;
                }

                // A hidden copy moves with the bone it copies until hidden, so giving it that
                // bone's weight keeps this vertex in step with the unhidden copy beside it.
                _weights[v] = MergeIntoHiddenCopies(_weights[v]);

                var chains = ForearmWeights
                    .Slots(_weights[v])
                    .Where(slot => slot.Weight > 0 && _physBoneTwists[side].ContainsKey(slot.Bone))
                    .ToArray();

                // A vertex mixed with a PhysBone chain takes the twist of the bone the chain hangs
                // on in that chain's share, or the two pull it toward the forearm axis.
                positions[v] =
                    (
                        rawPositions[v] * sourceWeight
                        + chains.Sum(slot => slot.Weight * _physBoneTwists[side][slot.Bone])
                    ) / (sourceWeight + chains.Sum(slot => slot.Weight));
            }

            var rigid = new bool[_vertices.Length];

            // Spread over several nodes, a piece of a wrist accessory, such as a ribbon, bends out
            // of shape and apart from the pieces beside it.
            // A piece of a sleeve stays spread, or it lifts off the sleeve under it.
            foreach (var piece in isSleeve ? Array.Empty<int[]>() : pieces)
            {
                var members = piece.Where(v => sourceWeights[v] > 0).ToArray();

                if (members.Length == 0)
                {
                    continue;
                }

                var span = members.Select(v => rawPositions[v]).ToArray();

                if (span.Max() - span.Min() > MaxPieceSpan)
                {
                    continue;
                }

                var mean = span.Average();

                foreach (var v in members)
                {
                    positions[v] = mean;
                    rigid[v] = true;
                }
            }

            var between = new bool[_vertices.Length];

            foreach (var (piece, twist) in ribbons)
            {
                foreach (var v in piece.Where(v => sourceWeights[v] > 0))
                {
                    positions[v] = twist;
                    rigid[v] = true;
                    between[v] = true;
                }
            }

            for (var v = 0; v < _vertices.Length; v++)
            {
                var sourceWeight = sourceWeights[v];

                if (sourceWeight <= 0)
                {
                    continue;
                }

                var t = positions[v];

                if (
                    rigid[v]
                    || !ForearmWeights.TryRedistribute(
                        _weights[v],
                        sources,
                        sourceWeight,
                        nodes,
                        t,
                        out var weight
                    )
                )
                {
                    weight = between[v]
                        ? ForearmWeights.Between(_weights[v], sources, sourceWeight, nodes, t)
                        : ForearmWeights.Nearest(_weights[v], sources, sourceWeight, nodes, t);
                }

                var hidden = ForearmWeights
                    .Slots(_weights[v])
                    .Where(slot =>
                        slot.Weight > 0
                        && sources.Contains(slot.Bone)
                        && mirrorNodes.ContainsKey(slot.Bone)
                    )
                    .OrderByDescending(slot => slot.Weight)
                    .Select(slot => slot.Bone)
                    .Distinct()
                    .ToArray();

                // Any weight on a hidden bone or its mirror lets Modular Avatar hide the vertex, so
                // every hidden bone the vertex uses keeps some.
                // A small share barely changes the twist, so the unhidden copy beside it stays put.
                // Without a free slot, a node moves to the mirror instead.
                if (hidden.Length > 0)
                {
                    weight = ForearmWeights.KeepHidden(
                        weight,
                        hidden,
                        nodes,
                        hidden.Select(bone => mirrorNodes[bone]).ToArray()
                    );
                }

                _weights[v] = weight;
                RedistributedCount++;
            }
        }

        private BoneWeight MergeIntoHiddenCopies(BoneWeight weight)
        {
            var targets = new Dictionary<int, int>();

            foreach (
                var slot in ForearmWeights
                    .Slots(weight)
                    .Where(slot => slot.Weight > 0 && _hiddenCopies.ContainsKey(slot.Bone))
            )
            {
                targets.TryAdd(_hiddenCopies[slot.Bone], slot.Bone);
            }

            return ForearmWeights.Rebind(weight, targets);
        }

        // A wrist accessory's chain hangs on the twist bone at its root, beside its band, so a cuff
        // ruffle turns with the band instead of with the sleeve chains' node.
        private void HangAccessoryChains(int side, Arm arm, HashSet<Transform> movableRoots)
        {
            var used = _weights
                .SelectMany(ForearmWeights.Slots)
                .Where(slot => slot.Weight > 0)
                .Select(slot => slot.Bone)
                .ToHashSet();

            foreach (var bone in _lowerChains[side].Where(used.Contains))
            {
                var root = arm.HungAncestor(_bones[bone]);

                if (!movableRoots.Contains(root))
                {
                    continue;
                }

                ChainNodes[root] = TwistRig.NodeAt(arm, root.position);
                _physBoneTwists[side][bone] = ChainNodes[root] / (TwistRig.SupportCount - 1);
            }
        }

        // A ribbon hanging on a PhysBone chain apart from the sleeve fabric turns as one with the
        // twist bone its chain moves to, or it bends away from its knot.
        private (int[] Piece, float Twist)[] FindRibbons(
            int side,
            Arm arm,
            int[][] pieces,
            float[] rawPositions,
            HashSet<Transform> movableRoots
        )
        {
            IEnumerable<int> Chains(int v) =>
                ForearmWeights
                    .Slots(_weights[v])
                    .Where(slot => slot.Weight > 0 && _lowerChains[side].Contains(slot.Bone))
                    .Select(slot => slot.Bone);

            Transform Root(int bone) => arm.HungAncestor(_bones[bone]);

            Transform[] Roots(IEnumerable<int> vertices) =>
                vertices.SelectMany(Chains).Select(Root).Distinct().ToArray();

            var hanging = pieces.Where(piece => piece.All(v => Chains(v).Any())).ToArray();
            var singleRootPieces = hanging
                .Select(piece => (Piece: piece, Roots: Roots(piece)))
                .Where(candidate => candidate.Roots.Length == 1)
                .ToArray();
            var fabricRoots = Roots(
                    Enumerable
                        .Range(0, _vertices.Length)
                        .Except(singleRootPieces.SelectMany(candidate => candidate.Piece))
                )
                .ToHashSet();
            var ribbons = singleRootPieces
                .Where(candidate =>
                    !fabricRoots.Contains(candidate.Roots[0])
                    && movableRoots.Contains(candidate.Roots[0])
                )
                .Select(candidate => (candidate.Piece, Root: candidate.Roots[0]))
                .ToArray();
            var gap = MaxRibbonGap * _axes[side].Length;

            // A knot between ribbon loops touches them without sharing a vertex and lies within the
            // bow, unlike a cuff band or button beside it.
            var knots = ribbons
                .GroupBy(ribbon => ribbon.Root)
                .SelectMany(bow =>
                {
                    var bounds = GeometryUtility.CalculateBounds(
                        bow.SelectMany(ribbon => ribbon.Piece).Select(v => _vertices[v]).ToArray(),
                        Matrix4x4.identity
                    );
                    bounds.Expand(2 * gap);

                    return pieces
                        .Except(hanging)
                        .Where(piece =>
                            piece.All(v => bounds.Contains(_vertices[v]))
                            && bow.Any(ribbon =>
                                ribbon.Piece.Any(ribbonVertex =>
                                    piece.Any(v =>
                                        (_vertices[ribbonVertex] - _vertices[v]).magnitude <= gap
                                    )
                                )
                            )
                        )
                        .Select(piece => (Piece: piece, Root: bow.Key));
                })
                .ToArray();
            var last = TwistRig.SupportCount - 1;

            foreach (var root in ribbons.Select(ribbon => ribbon.Root).Distinct())
            {
                var knotVertices = knots
                    .Where(knot => knot.Root == root)
                    .SelectMany(knot => knot.Piece)
                    .ToArray();

                // A bow a whole node off the sleeve under its knot turns ahead of or behind the
                // sleeve and sinks into it on one side.
                ChainNodes[root] =
                    knotVertices.Length > 0
                        ? Mathf.Clamp(
                            Mathf.Round(knotVertices.Average(v => rawPositions[v]) * last * 2) / 2,
                            1,
                            last
                        )
                        : TwistRig.NodeAt(arm, root.position);
            }

            foreach (
                var bone in _lowerChains[side].Where(bone => ChainNodes.ContainsKey(Root(bone)))
            )
            {
                _physBoneTwists[side][bone] = ChainNodes[Root(bone)] / last;
            }

            return ribbons
                .Concat(knots)
                .Select(ribbon => (ribbon.Piece, ChainNodes[ribbon.Root] / last))
                .ToArray();
        }

        private int[][] Pieces()
        {
            var parent = Enumerable.Range(0, _vertices.Length).ToArray();

            int Find(int v)
            {
                while (parent[v] != v)
                {
                    parent[v] = parent[parent[v]];
                    v = parent[v];
                }

                return v;
            }

            // Vertices split along UV or normal seams share a position.
            var byPosition = new Dictionary<Vector3, int>();

            for (var v = 0; v < _vertices.Length; v++)
            {
                if (byPosition.TryGetValue(_vertices[v], out var other))
                {
                    parent[Find(v)] = Find(other);
                }
                else
                {
                    byPosition[_vertices[v]] = v;
                }
            }

            var triangles = Renderer.sharedMesh.triangles;

            for (var i = 0; i < triangles.Length; i += 3)
            {
                parent[Find(triangles[i])] = Find(triangles[i + 1]);
                parent[Find(triangles[i + 1])] = Find(triangles[i + 2]);
            }

            return Enumerable
                .Range(0, _vertices.Length)
                .GroupBy(Find)
                .Select(piece => piece.ToArray())
                .ToArray();
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
            Arm[] arms,
            Func<Transform, bool> isAnimated
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
                    // A plain bone on the LowerArm, such as a support bone missing its constraint,
                    // turns all its vertices by one amount instead of spreading the twist.
                    // An animated one, such as a bone scaled to hide a part, keeps its animation.
                    if (
                        isHiddenCopy
                        || (armBone == null && arm.IsPlainLowerChild(bone) && !isAnimated(bone))
                    )
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
