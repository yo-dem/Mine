using System.Numerics;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

public sealed unsafe class Shader : IDisposable
{
    private readonly GL _gl;
    private readonly uint _program;
    private readonly Dictionary<string, int> _locations = new();

    public Shader(GL gl, string vertexSource, string fragmentSource)
    {
        _gl = gl;
        uint vs = Compile(ShaderType.VertexShader, vertexSource);
        uint fs = Compile(ShaderType.FragmentShader, fragmentSource);

        _program = gl.CreateProgram();
        gl.AttachShader(_program, vs);
        gl.AttachShader(_program, fs);
        gl.LinkProgram(_program);
        gl.GetProgram(_program, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
            throw new InvalidOperationException($"Shader link failed: {gl.GetProgramInfoLog(_program)}");

        gl.DetachShader(_program, vs);
        gl.DetachShader(_program, fs);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
    }

    private uint Compile(ShaderType type, string source)
    {
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compiled);
        if (compiled == 0)
            throw new InvalidOperationException($"{type} compile failed: {_gl.GetShaderInfoLog(shader)}");
        return shader;
    }

    public void Use() => _gl.UseProgram(_program);

    private int Location(string name)
    {
        if (!_locations.TryGetValue(name, out int location))
            _locations[name] = location = _gl.GetUniformLocation(_program, name);
        return location;
    }

    public void Set(string name, int value) => _gl.Uniform1(Location(name), value);
    public void Set(string name, int x, int y) => _gl.Uniform2(Location(name), x, y);
    public void Set(string name, float value) => _gl.Uniform1(Location(name), value);
    public void Set(string name, Vector2 value) => _gl.Uniform2(Location(name), value.X, value.Y);
    public void Set(string name, Vector3 value) => _gl.Uniform3(Location(name), value.X, value.Y, value.Z);
    public void Set(string name, Vector4 value) => _gl.Uniform4(Location(name), value.X, value.Y, value.Z, value.W);

    // System.Numerics matrices are row-major with row vectors; uploading them
    // untransposed gives GLSL the equivalent column-vector matrix.
    public void Set(string name, Matrix4x4 value) => _gl.UniformMatrix4(Location(name), 1, false, (float*)&value);

    public void Dispose() => _gl.DeleteProgram(_program);
}
