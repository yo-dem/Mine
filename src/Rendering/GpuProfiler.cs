using System.Diagnostics;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Debug aid (MINE_GPU_PROFILE=1): measures the GPU time of each render pass with timer queries
/// and prints the averages every two seconds. Sections follow each other (timer queries cannot
/// nest): <see cref="Section"/> ends the previous one. Results are read a few frames later, so
/// the measurement never stalls the pipeline.
/// </summary>
public sealed class GpuProfiler
{
    private const int Latency = 4; // frames in flight before a result is read

    private readonly GL _gl;
    private readonly List<string> _names = [];
    private readonly Dictionary<string, (double Total, int Count)> _sums = [];
    private readonly (uint Query, string Name)[][] _frames = new (uint, string)[Latency][];
    private readonly List<(uint, string)> _current = [];
    private readonly Stack<uint> _free = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Stopwatch _cpu = new();
    private double _cpuTotal;
    private int _frameIndex, _cpuFrames;
    private bool _open;

    public static readonly bool Enabled = Environment.GetEnvironmentVariable("MINE_GPU_PROFILE") == "1";

    public GpuProfiler(GL gl) => _gl = gl;

    public void BeginFrame()
    {
        if (!Enabled) return;
        _cpu.Restart();
        // Collect the frame issued Latency frames ago.
        var old = _frames[_frameIndex % Latency];
        if (old != null)
        {
            foreach (var (query, name) in old)
            {
                _gl.GetQueryObject(query, QueryObjectParameterName.Result, out ulong ns);
                var (total, count) = _sums.GetValueOrDefault(name);
                _sums[name] = (total + ns / 1e6, count + 1);
                _free.Push(query);
            }
        }
        _current.Clear();
    }

    public void Section(string name)
    {
        if (!Enabled) return;
        if (_open) _gl.EndQuery(QueryTarget.TimeElapsed);
        uint query = _free.Count > 0 ? _free.Pop() : _gl.GenQuery();
        _gl.BeginQuery(QueryTarget.TimeElapsed, query);
        _current.Add((query, name));
        if (!_names.Contains(name)) _names.Add(name);
        _open = true;
    }

    public void EndFrame()
    {
        if (!Enabled) return;
        if (_open) _gl.EndQuery(QueryTarget.TimeElapsed);
        _open = false;
        _cpuTotal += _cpu.Elapsed.TotalMilliseconds;
        _cpuFrames++;
        _frames[_frameIndex % Latency] = [.. _current];
        _frameIndex++;

        if (_clock.Elapsed.TotalSeconds < 2) return;
        _clock.Restart();
        double sum = 0;
        var line = new System.Text.StringBuilder("GPU ms:");
        foreach (var name in _names)
        {
            if (!_sums.TryGetValue(name, out var s) || s.Count == 0) continue;
            double avg = s.Total / s.Count;
            sum += avg;
            line.Append($" {name} {avg:0.00} |");
        }
        line.Append($" totale {sum:0.00} | CPU render {_cpuTotal / Math.Max(_cpuFrames, 1):0.00}");
        Console.WriteLine(line.ToString());
        _sums.Clear();
        _cpuTotal = 0;
        _cpuFrames = 0;
    }
}
