using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace KOZ39.ElbowFixer
{
    internal sealed class ForearmSubdivision
    {
        private const int UVChannelCount = 8;

        private readonly List<Vector3> _positions = new();
        private readonly List<Vector3> _normals = new();
        private readonly List<Vector4> _tangents = new();
        private readonly List<Color> _colors = new();
        private readonly List<Vector4>[] _uvs = new List<Vector4>[UVChannelCount];
        private readonly int[] _uvDimensions = new int[UVChannelCount];
        private readonly List<BoneWeight> _weights;
        private readonly List<int>[] _indices;
        private readonly MeshTopology[] _topologies;
        private readonly Mesh _source;
        private readonly List<(int First, int Second)> _midpointEdges = new();
        private readonly Dictionary<(int, int), int> _midpoints = new();

        internal Vector3[] Positions => _positions.ToArray();
        internal BoneWeight[] Weights => _weights.ToArray();

        internal ForearmSubdivision(Mesh mesh, BoneWeight[] weights)
        {
            _source = mesh;
            mesh.GetVertices(_positions);
            mesh.GetNormals(_normals);
            mesh.GetTangents(_tangents);
            mesh.GetColors(_colors);

            for (var channel = 0; channel < UVChannelCount; channel++)
            {
                _uvs[channel] = new();
                _uvDimensions[channel] = mesh.GetVertexAttributeDimension(
                    VertexAttribute.TexCoord0 + channel
                );
                mesh.GetUVs(channel, _uvs[channel]);
            }

            _weights = new(weights);
            _indices = Enumerable
                .Range(0, mesh.subMeshCount)
                .Select(subMesh => new List<int>(mesh.GetIndices(subMesh)))
                .ToArray();
            _topologies = Enumerable.Range(0, mesh.subMeshCount).Select(mesh.GetTopology).ToArray();
        }

        internal void Subdivide(
            int maxLevels,
            Func<Vector3, BoneWeight, Vector3, BoneWeight, bool> splits
        )
        {
            for (var level = 0; level < maxLevels; level++)
            {
                var count = _positions.Count;
                _midpoints.Clear();

                for (var subMesh = 0; subMesh < _indices.Length; subMesh++)
                {
                    if (_topologies[subMesh] != MeshTopology.Triangles)
                    {
                        continue;
                    }

                    var source = _indices[subMesh];
                    var result = new List<int>(source.Count * 2);

                    for (var i = 0; i < source.Count; i += 3)
                    {
                        SplitTriangle(result, source[i], source[i + 1], source[i + 2], splits);
                    }

                    _indices[subMesh] = result;
                }

                if (_positions.Count == count)
                {
                    break;
                }
            }
        }

        internal void Write(Mesh target, BoneWeight[] weights)
        {
            target.Clear();
            target.ClearBlendShapes();

            if (_positions.Count > ushort.MaxValue)
            {
                target.indexFormat = IndexFormat.UInt32;
            }

            target.SetVertices(_positions);

            if (_normals.Count > 0)
            {
                target.SetNormals(_normals);
            }

            if (_tangents.Count > 0)
            {
                target.SetTangents(_tangents);
            }

            if (_colors.Count > 0)
            {
                target.SetColors(_colors);
            }

            for (var channel = 0; channel < UVChannelCount; channel++)
            {
                SetUVs(target, channel);
            }

            target.subMeshCount = _indices.Length;

            for (var subMesh = 0; subMesh < _indices.Length; subMesh++)
            {
                target.SetIndices(_indices[subMesh], _topologies[subMesh], subMesh);
            }

            target.boneWeights = weights;
            WriteBlendShapes(target);
        }

        private void WriteBlendShapes(Mesh target)
        {
            var sourceVertices = new Vector3[_source.vertexCount];
            var sourceNormals = new Vector3[_source.vertexCount];
            var sourceTangents = new Vector3[_source.vertexCount];
            var vertices = new Vector3[_positions.Count];
            var normals = new Vector3[_positions.Count];
            var tangents = new Vector3[_positions.Count];

            for (var shape = 0; shape < _source.blendShapeCount; shape++)
            {
                var name = _source.GetBlendShapeName(shape);

                for (var frame = 0; frame < _source.GetBlendShapeFrameCount(shape); frame++)
                {
                    _source.GetBlendShapeFrameVertices(
                        shape,
                        frame,
                        sourceVertices,
                        sourceNormals,
                        sourceTangents
                    );
                    FillMidpoints(sourceVertices, vertices);
                    FillMidpoints(sourceNormals, normals);
                    FillMidpoints(sourceTangents, tangents);
                    target.AddBlendShapeFrame(
                        name,
                        _source.GetBlendShapeFrameWeight(shape, frame),
                        vertices,
                        normals,
                        tangents
                    );
                }
            }
        }

        private void FillMidpoints(Vector3[] source, Vector3[] result)
        {
            source.CopyTo(result, 0);

            for (var i = 0; i < _midpointEdges.Count; i++)
            {
                var (first, second) = _midpointEdges[i];
                result[source.Length + i] = (result[first] + result[second]) * 0.5f;
            }
        }

        private void SetUVs(Mesh target, int channel)
        {
            var uvs = _uvs[channel];

            switch (_uvDimensions[channel])
            {
                case 2:
                    target.SetUVs(channel, uvs.Select(uv => (Vector2)uv).ToList());
                    break;
                case 3:
                    target.SetUVs(channel, uvs.Select(uv => (Vector3)uv).ToList());
                    break;
                case 4:
                    target.SetUVs(channel, uvs);
                    break;
            }
        }

        private void SplitTriangle(
            List<int> result,
            int a,
            int b,
            int c,
            Func<Vector3, BoneWeight, Vector3, BoneWeight, bool> splits
        )
        {
            var corners = new[] { a, b, c };
            var split = Enumerable
                .Range(0, 3)
                .Select(edge =>
                {
                    var (first, second) = (corners[edge], corners[(edge + 1) % 3]);

                    return splits(
                        _positions[first],
                        _weights[first],
                        _positions[second],
                        _weights[second]
                    );
                })
                .ToArray();

            switch (split.Count(isSplit => isSplit))
            {
                case 0:
                    result.AddRange(corners);
                    break;
                case 1:
                {
                    var edge = Array.IndexOf(split, true);
                    var (first, second, third) = Rotate(corners, edge);
                    var midpoint = Midpoint(first, second);
                    result.AddRange(new[] { first, midpoint, third, midpoint, second, third });
                    break;
                }
                case 2:
                {
                    var edge = split[2] ? (split[0] ? 2 : 1) : 0;
                    var (first, second, third) = Rotate(corners, edge);
                    var firstMidpoint = Midpoint(first, second);
                    var secondMidpoint = Midpoint(second, third);
                    result.AddRange(
                        new[]
                        {
                            firstMidpoint,
                            second,
                            secondMidpoint,
                            first,
                            firstMidpoint,
                            secondMidpoint,
                            first,
                            secondMidpoint,
                            third,
                        }
                    );
                    break;
                }
                default:
                {
                    var ab = Midpoint(a, b);
                    var bc = Midpoint(b, c);
                    var ca = Midpoint(c, a);
                    result.AddRange(new[] { a, ab, ca, ab, b, bc, ca, bc, c, ab, bc, ca });
                    break;
                }
            }
        }

        private static (int First, int Second, int Third) Rotate(int[] corners, int start) =>
            (corners[start], corners[(start + 1) % 3], corners[(start + 2) % 3]);

        private int Midpoint(int a, int b)
        {
            var key = (Math.Min(a, b), Math.Max(a, b));

            if (_midpoints.TryGetValue(key, out var found))
            {
                return found;
            }

            var index = _positions.Count;
            _midpoints[key] = index;
            _positions.Add((_positions[a] + _positions[b]) * 0.5f);

            if (_normals.Count > 0)
            {
                _normals.Add((_normals[a] + _normals[b]).normalized);
            }

            if (_tangents.Count > 0)
            {
                var direction = ((Vector3)(_tangents[a] + _tangents[b])).normalized;
                _tangents.Add(new Vector4(direction.x, direction.y, direction.z, _tangents[a].w));
            }

            if (_colors.Count > 0)
            {
                _colors.Add(Color.Lerp(_colors[a], _colors[b], 0.5f));
            }

            foreach (var uvs in _uvs.Where(uvs => uvs.Count > 0))
            {
                uvs.Add((uvs[a] + uvs[b]) * 0.5f);
            }

            _weights.Add(ForearmWeights.Mix(_weights[a], _weights[b]));
            _midpointEdges.Add((a, b));

            return index;
        }
    }
}
