# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

An exploration game with smooth, dreamlike landscapes (inspired by Journey, Stray, Genshin Impact), in C# / .NET 10 using Silk.NET 2.23 (windowing, input, OpenGL). It started as a Minecraft clone: the block-based version lives on `main` (and `vegetazione-blocchi` adds swaying grass to it); this branch (`terreno-realistico`) replaces the blocks with a continuous terrain. Targets Windows, Linux and macOS, so portability matters: keep the GL context at **3.3 core + forward-compatible** (macOS caps at 4.1) and avoid platform-specific APIs. The user communicates in Italian; in-game UI strings (window title HUD) and README are in Italian.

Roadmap on this branch: 1) smooth terrain (done), 2) objects the player can pick up and place (lanterns, torches) with point lights, 3) instanced grass and procedural trees swaying in the wind, 4) post-processing (bloom, light shafts, grading), volumetric clouds, surreal elements.

## Commands

```
dotnet build                 # debug build
dotnet run -c Release        # play
dotnet publish -c Release -r <win-x64|linux-x64|osx-arm64> --self-contained -p:PublishSingleFile=true
```

There are no tests and no linter. The SDK lives at `C:\Program Files\dotnet\dotnet.exe`; if `dotnet` is not on PATH in the current shell, call it by full path. To verify rendering changes, launch `bin\Debug\net10.0\Mine.exe`, screenshot its window and kill the process (the game captures the mouse in raw mode while focused).

## Architecture

`Game` (src/Game.cs) owns the window, input callbacks and the terrain shader, and drives the loop: `Player.Update` → `DayCycle.Update` → `TerrainRenderer.Update`, then render shadow map → sky → terrain → crosshair. The window title is the HUD. Units are metres; Y is up.

**Terrain shape (`TerrainField`).** A pure, thread-safe function `Height(x, z)` built from the 2D `PerlinNoise` in src/World/Noise.cs: slow continental swells, domain-warped hills, dune ridges in the lowlands, soft terraces on high ground, small detail, and rare rock spires (one per 180 m cell at most, with a ragged outline from noise sampled on a circle). `Normal(x, z)` uses central differences. The player and the renderer both read it, so there is no stored terrain data.

**Terrain rendering (`TerrainRenderer`).** Square tiles of `TileSize` (64 m) out to `ViewDistance` (1300 m). Vertex spacing grows with distance (`LodSteps` 1/2/4/8/16 m at `LodRanges`). Tile vertices (position + normal, normals from central differences over the grid with one extra ring of samples) are built on background worker threads fed by a `BlockingCollection`, nearest first, and uploaded on the main thread at most `UploadsPerFrame` per frame; a tile keeps drawing its previous mesh until the new level of detail arrives, and results that no longer match the wanted level are dropped. Index buffers are shared per detail level (element buffer binding is VAO state: re-bound on upload). Every tile has a double-sided vertical skirt to hide cracks between detail levels. `Draw` skips tiles well behind the camera; `DrawNear` draws only the tiles around a point for the shadow map.

**Terrain shading (`TerrainShaders`).** Materials are procedural, from slope and height: grass (drifting between green, golden and a teal accent), warm sandstone with faint wavy strata on steep slopes, pale sand in the lowlands, plus three octaves of grain that fade with distance. Lighting: Lambert sunlight × shadow map, plus a generous ambient mixing the keyframe ambient with the sky colour around the normal.

**Lighting and sky.** `DayCycle` (src/World/DayCycle.cs) holds the time of day (0 = midnight, 0.25 = sunrise, 0.75 = sunset; 20-minute days) and interpolates colour keyframes into an `Atmosphere`. `SkyRenderer` draws the sky as a full-screen triangle (gradient with a warm sun-side horizon, square sun and moon, twinkling stars, a cloud layer moved by `DayCycle.Elapsed`). Its `Glsl` constant (sky uniforms, `skyColor()`, `toneMap()`, `hash13()`) is pasted into both the sky and the terrain fragment shader, so fog takes the exact sky colour in the view direction. The terrain adds a soft aerial haze that tints distance with the hue of the sky while keeping brightness (no whitening), and an edge fog to the sky colour over the last quarter of the view distance.

**Shadows (`ShadowMap`).** Each frame the caller's `drawCasters` draws the geometry around the player into a 2048² depth texture from the light direction, with an orthographic box of half-width `Radius` (150 m) centred on the player and snapped to texels. The pass stores the faces turned toward the light (back faces culled as usual) with polygon offset; together with a normal offset and a tiny bias in the shader this avoids acne while keeping shadows attached. The terrain shader samples it as `sampler2DShadow` (texture unit 0, 3×3 PCF) for direct light only.

**Player.** `Player.Position` is the feet. It walks on `TerrainField.Height`, stays glued to the ground when walking downhill (`GroundSnap`), and cannot climb slopes whose normal is below `MaxWalkableNormalY` (about 50°): the uphill part of its velocity is removed and it slides down. Movement has inertia (exponential approach), double-tap W to sprint, Shift to sneak (slower, lower camera), F to fly, T to speed up time.

**Math conventions.** `System.Numerics` matrices (row-vector convention) are uploaded untransposed, and shaders compute `uViewProj * vec4(pos, 1)`. So on the C# side the combined matrix is `view * projection`. Its projections map depth to [0, 1] in NDC, which GL then maps to window depth [0.5, 1]; `ShadowMap.DepthBias` accounts for that.

Namespace gotcha: `Silk.NET.OpenGL` also defines `Shader`. Files outside `Mine.Rendering` that import it need `using Shader = Mine.Rendering.Shader;`.
