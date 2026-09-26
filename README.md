# Mine

Un clone basilare di Minecraft in C# / .NET 10 con [Silk.NET](https://github.com/dotnet/Silk.NET) (OpenGL 3.3 core).
Gira su Windows, Linux e macOS. Tutte le texture sono generate dal codice: niente file di risorse.

## Avvio

```
dotnet run -c Release
```

## Comandi

| Tasto | Azione |
|---|---|
| Mouse | Guarda intorno |
| W A S D | Muoviti |
| Spazio | Salta (in volo: sali) |
| Shift sinistro | In volo: scendi |
| Ctrl sinistro | Corri |
| F | Attiva/disattiva il volo |
| Click sinistro | Rompi blocco |
| Click destro | Piazza blocco |
| 1 – 8 | Scegli il blocco da piazzare |
| Esc | Libera il mouse (di nuovo Esc: esci) |

## Struttura

```
src/
  Program.cs              punto di ingresso
  Game.cs                 finestra, input, loop, rendering
  Player.cs               movimento, gravità, collisioni
  World/
    Blocks.cs             tipi di blocco e texture per faccia
    Chunk.cs              16 x 128 x 16 blocchi
    Noise.cs              Perlin noise 2D
    TerrainGenerator.cs   colline, spiagge, alberi
    VoxelWorld.cs         caricamento chunk attorno al giocatore, raycast
  Rendering/
    ChunkMesher.cs        facce visibili + ambient occlusion
    ChunkMesh.cs          buffer GPU di un chunk
    TextureAtlas.cs       texture procedurali
    Shader.cs, Crosshair.cs
```

## Pubblicare un eseguibile

Eseguibile singolo con il runtime incluso (va compilato su ciascun sistema):

```
dotnet publish -c Release -r win-x64   --self-contained -p:PublishSingleFile=true
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
dotnet publish -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true
```
