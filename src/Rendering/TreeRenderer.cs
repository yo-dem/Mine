using System.Numerics;
using Mine.World;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Draws the trees with instancing: one batch per model variant and level of detail, each a mesh
/// plus a buffer of instances (position, yaw, scale). The instance lists are re-sorted into
/// detail levels when the set of trees changes or the camera has moved a few metres.
/// Each batch's instances are grouped by the direction they lie in from the camera (the near ones
/// first, then <see cref="Sectors"/> slices around it), so <see cref="Draw"/> skips the slices
/// out of view; the shadow pass still draws them all.
/// </summary>
public sealed unsafe class TreeRenderer : IDisposable
{
    private const int FloatsPerInstance = 5;
    private const float ResortDistance = 8f;

    // Detail level by distance: full below the first, medium below the second (these also cast
    // shadows, the shadow map covering about that far), simple below the third, a silhouette beyond.
    private static readonly float[] LodDistances = [50f, 170f, 380f];

    // Direction slices around the camera. Trees nearer than NearRadius are always drawn; farther
    // ones only when their slice is within the view, with SliceMargin to spare for their crowns and
    // for the camera moving up to ResortDistance before the next sort.
    private const int Sectors = 16;
    private const float NearRadius = 40f;
    private const float SliceMargin = 0.5f;

    private sealed class Batch
    {
        public uint Vao, MeshVbo, InstanceVbo;
        public int VertexCount, InstanceCount;
        public readonly List<float> Instances = new();
        // Instances per group while sorting: 0 = near, 1.. = the slices.
        public readonly List<float>[] Groups = Enumerable.Range(0, Sectors + 1).Select(_ => new List<float>()).ToArray();
        // First instance of each group (and the total at the end).
        public readonly int[] GroupStart = new int[Sectors + 2];
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

        foreach (var batch in _batches)
            foreach (var group in batch.Groups) group.Clear();
        foreach (var tree in field.All)
        {
            float dx = tree.Position.X - camera.X, dz = tree.Position.Z - camera.Z;
            float distance = MathF.Sqrt(dx * dx + dz * dz);
            if (distance > TreeField.Radius || distance > TreeModels.MaxDistance(tree.Variant)) continue;
            int lod = 0;
            while (lod < LodDistances.Length && distance >= LodDistances[lod]) lod++;
            int group = distance < NearRadius ? 0 : 1 + Sector(MathF.Atan2(dz, dx));
            var list = _batches[tree.Variant, lod].Groups[group];
            list.Add(tree.Position.X);
            list.Add(tree.Position.Y);
            list.Add(tree.Position.Z);
            list.Add(tree.Yaw);
            list.Add(tree.Scale);
        }

        foreach (var batch in _batches)
        {
            batch.Instances.Clear();
            for (int g = 0; g <= Sectors; g++)
            {
                batch.GroupStart[g] = batch.Instances.Count / FloatsPerInstance;
                batch.Instances.AddRange(batch.Groups[g]);
            }
            batch.GroupStart[Sectors + 1] = batch.Instances.Count / FloatsPerInstance;
            batch.InstanceCount = batch.Instances.Count / FloatsPerInstance;
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, batch.InstanceVbo);
            var data = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(batch.Instances);
            fixed (float* p = data)
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);
        }
    }

    private static int Sector(float angle) =>
        Math.Clamp((int)((angle + MathF.PI) / MathF.Tau * Sectors), 0, Sectors - 1);

    private readonly bool[] _visible = new bool[Sectors];

    /// <summary>
    /// Draws the trees in view: <paramref name="look"/> is the view direction, the field of view
    /// vertical. With <paramref name="translucent"/>, only the variants with glass parts
    /// (<see cref="TreeModels.Translucent"/>), for the see-through pass.
    /// </summary>
    public void Draw(Vector3 look, float verticalFov, float aspect, bool translucent = false)
    {
        // The horizontal half-angle of the view, widened as the view tilts (the frustum then
        // reaches farther to the sides at the ground); looking steeply up or down, everything.
        float pitch = MathF.Asin(Math.Clamp(look.Y, -1f, 1f));
        float halfView = MathF.Atan(MathF.Tan(verticalFov / 2) * aspect) + MathF.Abs(pitch) * 0.6f + SliceMargin;
        bool all = MathF.Abs(pitch) > 0.8f;
        float yaw = MathF.Atan2(look.Z, look.X);
        for (int s = 0; s < Sectors; s++)
        {
            float center = -MathF.PI + (s + 0.5f) * MathF.Tau / Sectors;
            float delta = MathF.Abs(MathF.IEEERemainder(center - yaw, MathF.Tau));
            _visible[s] = all || delta <= halfView + MathF.PI / Sectors;
        }

        for (int v = 0; v < _batches.GetLength(0); v++)
        for (int lod = 0; lod < _batches.GetLength(1); lod++)
        {
            var batch = _batches[v, lod];
            if (batch.InstanceCount == 0 || (translucent && !TreeModels.Translucent(v))) continue;
            // The near group, then each run of visible slices in one draw (the groups are contiguous).
            int runStart = 0, runEnd = batch.GroupStart[1];
            for (int s = 0; s < Sectors; s++)
            {
                if (_visible[s])
                {
                    runEnd = batch.GroupStart[s + 2];
                    continue;
                }
                DrawRange(batch, runStart, runEnd - runStart);
                runStart = runEnd = batch.GroupStart[s + 2];
            }
            DrawRange(batch, runStart, runEnd - runStart);
        }
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

    private void DrawBatch(Batch batch) => DrawRange(batch, 0, batch.InstanceCount);

    // Draws instances first..first+count (GL 3.3 has no base instance: the instance attributes
    // are pointed at the first one).
    private void DrawRange(Batch batch, int first, int count)
    {
        if (count <= 0) return;
        _gl.BindVertexArray(batch.Vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, batch.InstanceVbo);
        uint stride = FloatsPerInstance * sizeof(float);
        Attribute(5, 4, stride, first * FloatsPerInstance);
        Attribute(6, 1, stride, first * FloatsPerInstance + 4);
        _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, (uint)batch.VertexCount, (uint)count);
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
