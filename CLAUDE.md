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

`Game` (src/Game.cs) owns the window, input callbacks, the main shader (GLSL inline as raw strings), and drives the loop: `Player.Update` → `DayCycle.Update` → `VoxelWorld.Update`, then render shadow map → sky → every chunk mesh → crosshair. The window title is the HUD.

**Lighting and sky.** `DayCycle` (src/World/DayCycle.cs) holds the time of day (0 = midnight, 0.25 = sunrise, 0.75 = sunset) and interpolates colour keyframes into an `Atmosphere`. `SkyRenderer` draws the sky as a full-screen triangle; its `Glsl` constant (sky uniforms + `skyColor()`) is pasted into both the sky and the terrain fragment shader, so fog takes the exact sky colour in the view direction. Directional lighting is computed in the terrain shader from a normal rebuilt with `dFdx/dFdy` (faces are flat), so the vertex light is ambient occlusion only. Faces also take "sky light" by sampling `skyColor` around their normal, the per-keyframe `Haze` thickens the haze at dawn/dusk, and both shaders end with the shared `toneMap` (soft shoulder, so bright sunsets saturate instead of clipping). The terrain shader also adds a soft aerial haze (`1 - exp(-uMistDensity * dist)`, slightly denser with `Haze`, with a faint drifting noise variation) that tints distant surfaces with the hue of the sky but keeps their brightness (no whitening), and an edge fog to the sky colour only over the last quarter of the view distance, to hide where the loaded terrain stops; glowing texels cut through both. Block light is dimmed in daylight. Clouds live only in the sky shader (a noise layer at a fixed height, moved by `DayCycle.Elapsed`, i.e. game time).

**Shadows (`ShadowMap`).** Each frame the chunks within `Radius` (+ margin) are drawn into a 2048² depth texture from the light direction, with an orthographic box centred on the player and snapped to texels. The shadow pass stores the faces turned toward the light (back faces culled as usual) with polygon offset; together with a small normal offset and a tiny bias in the shader this avoids acne while keeping shadows attached to their casters. Direct light is plain Lambert, so faces turned away from the light never depend on the (unreliable) self-shadow test. The terrain shader samples it as `sampler2DShadow` (texture unit 1, 3×3 PCF) and applies it to direct light only; ambient occlusion (the vertex light) darkens only the sky/ambient term.

**World streaming (`VoxelWorld`)** runs on the main thread with per-frame budgets:
1. Generate block data for missing chunks within `RenderDistance + 1` (closest first).
2. Mesh dirty chunks within `RenderDistance`, **only once all 4 cardinal neighbours exist**, so border faces and AO are correct.
3. Unload chunks beyond `RenderDistance + 3`, disposing their GPU meshes.

Chunks are always regenerated from the seed, so player edits live in a separate log (`VoxelWorld._edits`: per chunk, local cell index → block) written by `SetBlock` and replayed by `LoadChunk` before lighting; `LightNewChunk` also seeds the replayed emitters. The log is in memory only: nothing is saved to disk yet.

`SetBlock` marks the chunk dirty, plus neighbours when the block is on a chunk border, and re-meshes the affected, already-meshed chunks immediately instead of waiting for the budget.

**Half-block cells.** The world grid is made of cells 1 block wide/deep and `Chunk.CellHeight` = 0.5 tall: every block is a half block and a cube is two stacked cells. All block coordinates (`BlockPos`, `GetBlock`, `SetBlock`, chunk arrays) use cell units for Y; convert with `Chunk.CellY(worldY)` and `cellY * Chunk.CellHeight`. The mesher emits vertex Y as `cell * CellHeight`; side faces always sample the upper half of the tile (rows 0..7), so every half block looks the same wherever it is placed; `TextureAtlas` draws patterned tiles with an 8-row period so that half is a complete design (top faces show it twice). `Raycast` walks the grid in cell space (Y scaled by 1/CellHeight, same ray parameter t) and returns a `RayHit` (cell, face normal, point); placing puts the block in `hit.Adjacent`. Collisions use cell extents, and `Player` climbs one-cell steps (`StepHeight`) while on the ground, easing the camera with `_stepOffset`. Tree trunks and crowns are sized in cells.

**Block light.** `BlockLight` packs RGB light into a ushort (5 bits per channel, 0..31). Light loses 2 per block horizontally and 1 per cell vertically, so it spreads equally far both ways (`MaxReach` ≈ 16 blocks). `Blocks.Emission` defines the emitters. Chunks allocate their light array lazily. `VoxelWorld.Lighting.cs` does a BFS flood fill through air; every `SetBlock` calls `Relight`, which clears the box an edit can influence (±MaxReach), reseeds from emitters inside it and from lit cells just outside it, re-floods, and re-meshes the chunks whose light changed. Freshly loaded chunks pull light in from their neighbours (`LightNewChunk`). The mesher averages light per vertex over the air cells around it (smooth lighting) and the terrain shader adds `pow(light, 2.6)`, modulated by AO, to the ambient term.

**Coordinates.** Chunks are 16×256×16 cells (`Chunk.Size`, `Chunk.Height` in cells). World → chunk conversion uses `>> Chunk.SizeShift` and `& (Size - 1)`, which is correct for negative coordinates. Keep `Size` a power of two. Block index is `(y * Size + z) * Size + x` (y in cells). Missing chunks and out-of-range Y read as `Air`, except in the mesher, where `y < 0` counts as solid so the world bottom is never drawn.

**Meshing (`ChunkMesher`)** takes a 3×3 chunk neighbourhood array (index `(dz+1)*3 + (dx+1)`, centre = 4) to avoid dictionary lookups per block. It emits only faces adjacent to non-solid blocks, as non-indexed triangles, with per-vertex AO and diagonal flipping. The output span is reused between calls: `ChunkMesh.Upload` must consume it before the next `Build`.

Contracts that span multiple files and must stay in sync:
- **Face order `+X, -X, +Y, -Y, +Z, -Z`**: `ChunkMesher.Normals/Corners` and `Blocks.GetTile` (which assumes top = 2, bottom = 3).
- **Vertex layout: position(3), uv(2), ao(1), block light rgb(3) floats**: `ChunkMesher.FloatsPerVertex`, the attribute pointers in `ChunkMesh`, and the `layout(location = …)` inputs in `Game.VertexSource`.
- Corners are wound CCW seen from outside, because back-face culling is enabled.

**Textures (`TextureAtlas`)** are generated procedurally at startup; there are no asset files. The atlas is a 4×4 grid of 16px tiles whose index equals the `Tile` enum value. Its alpha channel is not transparency but a glow mask (`TileGlow`): glowing texels ignore scene lighting, pulse, and resist fog. Adding a block means: add a `Tile` value and a `case` in `TileColor` (and `TileGlow` if it glows), add a `BlockType`, map it in `Blocks.GetTile`, and optionally add it to `Blocks.Hotbar` (keys 1–9 and 0 select the first ten; the mouse wheel cycles through all). With 16px tiles the atlas holds at most 16 tiles. `TextureMaxLevel = 4` is deliberate: deeper mip levels would blend neighbouring tiles.

**Terrain (`TerrainGenerator`)** is a deterministic function of the seed and world coordinates: 2D Perlin fBm heightmap, with sand where height ≤ sea level + 1. Trees are placed only 2+ blocks from the chunk edge, because generation writes into a single chunk and cannot touch its neighbours.

**Math conventions.** `System.Numerics` matrices (row-vector convention) are uploaded untransposed, and the shader computes `uViewProj * vec4(pos, 1)`. So on the C# side the combined matrix is `view * projection`. `Player.Position` is the feet at the centre of the AABB (radius 0.3, height 1.8). Collision resolves one axis at a time in sub-steps of ≤ 0.4 blocks, snapping against block faces.

Namespace gotcha: `Silk.NET.OpenGL` also defines `Shader`. Files outside `Mine.Rendering` that import it need `using Shader = Mine.Rendering.Shader;`.
