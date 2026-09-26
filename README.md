# Mine

Un gioco di esplorazione in paesaggi morbidi e sognanti, in C# / .NET 10 con
[Silk.NET](https://github.com/dotnet/Silk.NET) (OpenGL 3.3 core). Gira su Windows, Linux e macOS.
Tutto è generato dal codice: niente file di risorse.

Il mondo è una superficie continua generata proceduralmente: colline, dune, altipiani a gradoni morbidi
e guglie di roccia surreali, visibili fino a oltre un chilometro. Ha un ciclo giorno/notte
(10 minuti di giorno e 10 di notte) con cielo, sole, grande luna, stelle e nuvole disegnati da shader,
ombre proiettate dal sole e dalla luna e una foschia leggera che tinge la distanza.

Nel mondo si trovano cristalli e lanterne da raccogliere, e puoi piazzare lanterne, torce e cristalli
dove vuoi: ognuno emette una luce colorata che illumina il paesaggio attorno, con un alone nell'aria.

I prati sono coperti di erba che ondeggia al vento e brilla in controluce, e il paesaggio è punteggiato
di boschi e alberi isolati in stile fiabesco: querce, cipressi, alberi dorati, turchesi, fioriti di lilla,
ad ombrello, e alcuni con sfere luminose appese che si accendono di notte.

Nel cielo galleggiano isole fluttuanti con alberi in cima e cristalli luminosi appesi sotto: in volo
ci puoi atterrare e camminarci sopra. Le nuvole sono volumetriche, si colorano all'alba e al tramonto
e proiettano ombre in movimento sul paesaggio. Il bloom fa brillare sole, luci e cristalli, i raggi
di luce filtrano tra nuvole e alberi, e l'aria è piena di particelle: granelli dorati di giorno,
spore lilla al tramonto, lucciole di notte.

> Nato come clone di Minecraft: la versione a blocchi è sul branch `main`.
> Questo branch (`terreno-realistico`) la sostituisce con un terreno continuo.

## Avvio

```
dotnet run -c Release
```

Sui portatili con due schede video Windows può avviare il gioco su quella integrata, molto più lenta.
In quel caso: Impostazioni → Sistema → Schermo → Grafica → aggiungi `Mine.exe` → "Prestazioni elevate".

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
| Click sinistro | Raccogli l'oggetto che guardi |
| Click destro | Piazza l'oggetto selezionato sul terreno |
| 1 – 3 / rotella | Scegli l'oggetto: lanterna, torcia, cristallo |
| Q | Qualità alta / bassa (bassa: automatica sulle schede integrate) |
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
    WorldObjects.cs       lanterne, torce e cristalli: generati, raccolti, piazzati; le loro luci
    IslandField.cs        isole fluttuanti: dove sono, forma, alberi in cima
    Ground.cs             dove si può camminare: terreno e cime delle isole
    TreeField.cs          dove crescono gli alberi (boschi, alberi isolati), collisione con i tronchi
    GroundMaterials.cs    colore e copertura dell'erba (copia in C# delle funzioni dello shader)
  Rendering/
    TerrainRenderer.cs    tessere del terreno con livelli di dettaglio, costruite in background
    TerrainShaders.cs     shader di terreno e oggetti: materiali, luce, ombre, luci, foschia
    ObjectRenderer.cs     modelli procedurali degli oggetti
    GrassRenderer.cs      fili d'erba attorno al giocatore (instancing, costruiti in background)
    TreeModels.cs         modelli procedurali degli alberi, 8 stili e 4 livelli di dettaglio
    TreeRenderer.cs       disegno degli alberi (instancing per stile e livello di dettaglio)
    SkyRenderer.cs        cielo, sole, luna, stelle, nuvole volumetriche
    CloudNoise.cs         texture di rumore 3D per le nuvole
    PostProcess.cs        buffer HDR, bloom, raggi di luce, color grading
    MoteRenderer.cs       particelle magiche: granelli, spore, lucciole
    IslandRenderer.cs     modelli delle isole fluttuanti
    MeshBuilder.cs        costruzione di modelli (oggetti, isole)
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
