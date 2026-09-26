# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

A basic Minecraft clone in C# / .NET 10 using Silk.NET 2.23 (windowing, input, OpenGL). Targets Windows, Linux and macOS, so portability matters: keep the GL context at **3.3 core + forward-compatible** (macOS caps at 4.1) and avoid platform-specific APIs. The user communicates in Italian; in-game UI strings (window title HUD) and README are in Italian.

## Commands

```
dotnet build                 # debug build
dotnet run -c Release        # play
dotnet publish -c Release -r <win-x64|linux-x64|osx-arm64> --self-contained -p:PublishSingleFile=true
```

There are no tests and no linter. The SDK lives at `C:\Program Files\dotnet\dotnet.exe`; if `dotnet` is not on PATH in the current shell, call it by full path. To verify rendering changes, launch `bin\Debug\net10.0\Mine.exe`, screenshot its window and kill the process (the game captures the mouse in raw mode while focused).

## Architecture

`Game` (src/Game.cs) owns the window, input callbacks, the main shader (GLSL inline as raw strings), and drives the loop: `Player.Update` → `VoxelWorld.Update` → render every chunk mesh → crosshair. The window title is the HUD.

**World streaming (`VoxelWorld`)** runs on the main thread with per-frame budgets:
1. Generate block data for missing chunks within `RenderDistance + 1` (closest first).
2. Mesh dirty chunks within `RenderDistance`, **only once all 4 cardinal neighbours exist**, so border faces and AO are correct.
3. Unload chunks beyond `RenderDistance + 3`, disposing their GPU meshes.

`SetBlock` marks the chunk dirty, plus neighbours when the block is on a chunk border, and re-meshes the affected, already-meshed chunks immediately instead of waiting for the budget.

**Coordinates.** Chunks are 16×128×16 (`Chunk.Size`, `Chunk.Height`). World → chunk conversion uses `>> Chunk.SizeShift` and `& (Size - 1)`, which is correct for negative coordinates. Keep `Size` a power of two. Block index is `(y * Size + z) * Size + x`. Missing chunks and out-of-range Y read as `Air`, except in the mesher, where `y < 0` counts as solid so the world bottom is never drawn.

**Meshing (`ChunkMesher`)** takes a 3×3 chunk neighbourhood array (index `(dz+1)*3 + (dx+1)`, centre = 4) to avoid dictionary lookups per block. It emits only faces adjacent to non-solid blocks, as non-indexed triangles, with per-vertex AO and diagonal flipping. The output span is reused between calls: `ChunkMesh.Upload` must consume it before the next `Build`.

Contracts that span multiple files and must stay in sync:
- **Face order `+X, -X, +Y, -Y, +Z, -Z`**: `ChunkMesher.Normals/Corners/FaceShade` and `Blocks.GetTile` (which assumes top = 2, bottom = 3).
- **Vertex layout: position(3), uv(2), light(1) floats**: `ChunkMesher.FloatsPerVertex`, the attribute pointers in `ChunkMesh`, and the `layout(location = …)` inputs in `Game.VertexSource`.
- Corners are wound CCW seen from outside, because back-face culling is enabled.

**Textures (`TextureAtlas`)** are generated procedurally at startup; there are no asset files. The atlas is a 4×4 grid of 16px tiles whose index equals the `Tile` enum value. Adding a block means: add a `Tile` value and a `case` in `TileColor`, add a `BlockType`, map it in `Blocks.GetTile`, and optionally add it to `Blocks.Hotbar` (keys 1–9). With 16px tiles the atlas holds at most 16 tiles. `TextureMaxLevel = 4` is deliberate: deeper mip levels would blend neighbouring tiles.

**Terrain (`TerrainGenerator`)** is a deterministic function of the seed and world coordinates: 2D Perlin fBm heightmap, with sand where height ≤ sea level + 1. Trees are placed only 2+ blocks from the chunk edge, because generation writes into a single chunk and cannot touch its neighbours.

**Math conventions.** `System.Numerics` matrices (row-vector convention) are uploaded untransposed, and the shader computes `uViewProj * vec4(pos, 1)`. So on the C# side the combined matrix is `view * projection`. `Player.Position` is the feet at the centre of the AABB (radius 0.3, height 1.8). Collision resolves one axis at a time in sub-steps of ≤ 0.4 blocks, snapping against block faces.

Namespace gotcha: `Silk.NET.OpenGL` also defines `Shader`. Files outside `Mine.Rendering` that import it need `using Shader = Mine.Rendering.Shader;`.
