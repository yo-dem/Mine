# Mine

Un gioco di esplorazione in paesaggi morbidi e sognanti, in C# / .NET 10 con
[Silk.NET](https://github.com/dotnet/Silk.NET) (OpenGL 3.3 core). Gira su Windows, Linux e macOS.
Tutto è generato dal codice: niente file di risorse.

Il mondo è una superficie continua generata proceduralmente: colline, dune, altipiani a gradoni morbidi
e guglie di roccia surreali, visibili fino a oltre un chilometro. Ha un ciclo giorno/notte
(10 minuti di giorno e 10 di notte) con cielo, sole, grande luna, stelle e nuvole disegnati da shader,
ombre proiettate dal sole e dalla luna e una foschia leggera che tinge la distanza.

> Nato come clone di Minecraft: la versione a blocchi è sul branch `main`.
> Questo branch (`terreno-realistico`) la sostituisce con un terreno continuo.

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
| Shift sinistro | Cammina furtivo: lento, visuale bassa (in volo: scendi) |
| Ctrl sinistro | Corri |
| T (tenuto) | Fai scorrere il tempo più veloce |
| F | Attiva/disattiva il volo |
| Esc | Libera il mouse (di nuovo Esc: esci) |

## Struttura

```
src/
  Program.cs              punto di ingresso
  Game.cs                 finestra, input, loop, rendering
  Player.cs               movimento sul terreno, pendii, inerzia
  World/
    Noise.cs              Perlin noise 2D
    TerrainField.cs       forma del terreno: altezza in ogni punto
    DayCycle.cs           orologio e colori di cielo e luce per ogni ora
  Rendering/
    TerrainRenderer.cs    tessere del terreno con livelli di dettaglio, costruite in background
    TerrainShaders.cs     materiali, luce, ombre e foschia del terreno
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
