# Mine

Un clone basilare di Minecraft in C# / .NET 10 con [Silk.NET](https://github.com/dotnet/Silk.NET) (OpenGL 3.3 core).
Gira su Windows, Linux e macOS. Tutte le texture sono generate dal codice: niente file di risorse.

Ha un ciclo giorno/notte (10 minuti di giorno e 10 di notte) con cielo, sole, grande luna, stelle e nuvole
disegnati da shader, ombre proiettate dal sole e dalla luna, e blocchi luminosi (cristalli, funghi, lanterne)
che brillano al buio.

Il mondo è fatto solo di **mezzi blocchi**: ogni cella è alta mezzo blocco e un cubo è semplicemente due mezzi
impilati. Il terreno ha altezze a passi di mezzo blocco e i gradini di mezzo blocco si salgono camminando.
I blocchi luminosi illuminano l'ambiente con luce colorata, che si aggiorna quando piazzi o rompi qualcosa.

## Avvio

```
dotnet run -c Release
```

## Comandi

| Tasto | Azione |
|---|---|
| Mouse | Guarda intorno |
| W A S D | Muoviti |
| W due volte | Corri (finché tieni premuto W) |
| Spazio | Salta (in volo: sali) |
| Shift sinistro | Cammina furtivo: lento, visuale bassa, non cadi dai bordi (in volo: scendi) |
| Ctrl sinistro | Corri |
| T (tenuto) | Fai scorrere il tempo più veloce |
| F | Attiva/disattiva il volo |
| Click sinistro | Rompi blocco |
| Click destro | Piazza blocco |
| 1 – 9, 0 / rotella | Scegli il blocco da piazzare |
| Esc | Libera il mouse (di nuovo Esc: esci) |

## Struttura

```
src/
  Program.cs              punto di ingresso
  Game.cs                 finestra, input, loop, rendering
  Player.cs               movimento, gravità, collisioni
  World/
    Blocks.cs             tipi di blocco e texture per faccia
    Chunk.cs              16 x 256 x 16 celle da mezzo blocco (128 blocchi di altezza)
    BlockLight.cs         luce colorata dei blocchi (5 bit per canale)
    VoxelWorld.Lighting.cs  propagazione della luce
    Noise.cs              Perlin noise 2D
    TerrainGenerator.cs   colline, spiagge, alberi
    DayCycle.cs           orologio e colori di cielo e luce per ogni ora
    VoxelWorld.cs         caricamento chunk attorno al giocatore, raycast
  Rendering/
    ChunkMesher.cs        facce visibili + ambient occlusion
    ChunkMesh.cs          buffer GPU di un chunk
    TextureAtlas.cs       texture procedurali (alpha = maschera di luminosità)
    SkyRenderer.cs        cielo, sole, luna, stelle, nuvole
    ShadowMap.cs          mappa delle ombre vista dal sole
    Shader.cs, Crosshair.cs
```

## Pubblicare un eseguibile

Eseguibile singolo con il runtime incluso (va compilato su ciascun sistema):

```
dotnet publish -c Release -r win-x64   --self-contained -p:PublishSingleFile=true
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
dotnet publish -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true
```
