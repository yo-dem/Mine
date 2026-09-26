using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Draws the trees with instancing: one batch per model variant and level of detail, each a mesh
/// plus a buffer of instances (position, yaw, scale). The instance lists are re-sorted into
/// detail levels when the set of trees changes or the camera has moved a few metres.
/// </summary>
public sealed unsafe class TreeRenderer : IDisposable
{
    private const int FloatsPerInstance = 5;
    private const float ResortDistance = 8f;

    // Detail level by distance: full below the first, medium below the second (these also cast
    // shadows, the shadow map covering about that far), simple below the third, a silhouette beyond.
    private static readonly float[] LodDistances = [50f, 170f, 380f];

    private sealed class Batch
    {
        public uint Vao, MeshVbo, InstanceVbo;
        public int VertexCount, InstanceCount;
        public readonly List<float> Instances = new();
    }

    private readonly GL _gl;
    private readonly Batch[,] _batches;
    private int _version = -1;
    private Vector3 _sortedAt = new(float.MaxValue);

    public TreeRenderer(GL gl)
    {
        _gl = gl;
        _batches = new Batch[TreeModels.VariantCount, TreeModels.LodCount];
        for (int v = 0; v < TreeModels.VariantCount; v++)
        for (int lod = 0; lod < TreeModels.LodCount; lod++)
            _batches[v, lod] = CreateBatch(TreeModels.Build(v, lod));
    }

    public void Update(TreeField field, Vector3 camera)
    {
        var moved = new Vector2(camera.X - _sortedAt.X, camera.Z - _sortedAt.Z);
        if (field.Version == _version && moved.LengthSquared() < ResortDistance * ResortDistance) return;
        _version = field.Version;
        _sortedAt = camera;

        foreach (var batch in _batches) batch.Instances.Clear();
        foreach (var tree in field.All)
        {
            float distance = new Vector2(tree.Position.X - camera.X, tree.Position.Z - camera.Z).Length();
            if (distance > TreeField.Radius || distance > TreeModels.MaxDistance(tree.Variant)) continue;
            int lod = 0;
            while (lod < LodDistances.Length && distance >= LodDistances[lod]) lod++;
            var list = _batches[tree.Variant, lod].Instances;
            list.Add(tree.Position.X);
            list.Add(tree.Position.Y);
            list.Add(tree.Position.Z);
            list.Add(tree.Yaw);
            list.Add(tree.Scale);
        }

        foreach (var batch in _batches)
        {
            batch.InstanceCount = batch.Instances.Count / FloatsPerInstance;
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, batch.InstanceVbo);
            var data = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(batch.Instances);
            fixed (float* p = data)
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);
        }
    }

    public void Draw()
    {
        foreach (var batch in _batches) DrawBatch(batch);
    }

    /// <summary>The trees near the camera, for the shadow map (same vertex layout, read by its instanced path).</summary>
    public void DrawShadowCasters()
    {
        for (int v = 0; v < TreeModels.VariantCount; v++)
        {
            if (!TreeModels.CastsShadow(v)) continue;
            DrawBatch(_batches[v, 0]);
            DrawBatch(_batches[v, 1]);
        }
    }

    private void DrawBatch(Batch batch)
    {
        if (batch.InstanceCount == 0) return;
        _gl.BindVertexArray(batch.Vao);
        _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, (uint)batch.VertexCount, (uint)batch.InstanceCount);
    }

    private Batch CreateBatch(float[] mesh)
    {
        var batch = new Batch
        {
            Vao = _gl.GenVertexArray(),
            MeshVbo = _gl.GenBuffer(),
            InstanceVbo = _gl.GenBuffer(),
            VertexCount = mesh.Length / TreeModels.FloatsPerVertex,
        };
        _gl.BindVertexArray(batch.Vao);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, batch.MeshVbo);
        fixed (float* p = mesh)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(mesh.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        uint stride = TreeModels.FloatsPerVertex * sizeof(float);
        Attribute(0, 3, stride, 0);  // position
        Attribute(1, 3, stride, 3);  // normal
        Attribute(2, 3, stride, 6);  // colour
        Attribute(3, 1, stride, 9);  // emissive
        Attribute(4, 1, stride, 10); // sway

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, batch.InstanceVbo);
        uint instanceStride = FloatsPerInstance * sizeof(float);
        Attribute(5, 4, instanceStride, 0); // position, yaw
        Attribute(6, 1, instanceStride, 4); // scale
        _gl.VertexAttribDivisor(5, 1);
        _gl.VertexAttribDivisor(6, 1);

        _gl.BindVertexArray(0);
        return batch;
    }

    private void Attribute(uint index, int size, uint stride, int offsetFloats)
    {
        _gl.VertexAttribPointer(index, size, VertexAttribPointerType.Float, false, stride, (void*)(offsetFloats * sizeof(float)));
        _gl.EnableVertexAttribArray(index);
    }

    public void Dispose()
    {
        foreach (var batch in _batches)
        {
            _gl.DeleteBuffer(batch.MeshVbo);
            _gl.DeleteBuffer(batch.InstanceVbo);
            _gl.DeleteVertexArray(batch.Vao);
        }
    }
}
