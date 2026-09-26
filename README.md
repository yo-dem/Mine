# Mine

Un gioco di esplorazione in paesaggi morbidi e sognanti, in C# / .NET 10 con
[Silk.NET](https://github.com/dotnet/Silk.NET) (OpenGL 3.3 core). Gira su Windows, Linux e macOS.
Tutto è generato dal codice: niente file di risorse.

Il mondo è fatto di **strati**: piastrelle piatte di 2×2 m ad altezze multiple di mezzo metro, con pareti
tra un livello e l'altro (un gradino si sale camminando, fino a un metro saltando). La forma è generata
proceduralmente: colline, dune, altipiani a gradoni morbidi
e guglie di roccia surreali, visibili fino a oltre un chilometro, in una palette cosmica viola, indaco
e turchese. Il ciclo giorno/notte (20 minuti) respira tra un giorno dorato, con il sole sempre basso sull'orizzonte,
e una notte cosmica dominata da un'enorme galassia a spirale, una grande luna, una seconda luna con gli
anelli, nebulose, stelle fittissime e stelle cadenti. La sabbia e i prati scintillano di granelli luminosi.

Nelle zone basse ci sono mari e laghi che riflettono il cielo, galassia compresa, con spiagge di sabbia viola:
nell'acqua bassa pulsano venature di luce ciano bioluminescente, la riva è segnata da una linea luminosa
e sulla superficie brillano scintille come stelle cadute. Vicino alla riva si cammina nell'acqua; al largo è profonda e si **nuota**: ci si muove come in volo ma lentamente, Spazio per salire, Shift per immergersi, e da fermi si torna a galla.
Sulle rive crescono grappoli di cristalli viola e ciano che illuminano ciò che hanno intorno, tra scogli
scuri; nell'acqua bassa galleggiano fiori di loto luminosi e sui prati spuntano campanule che brillano.
Sulle spiagge svettano palme scure in controluce, farfalle
luminose svolazzano sui prati, pesci che brillano nuotano nell'acqua bassa e stormi di uccelli
volteggiano in cielo, sagome scure al crepuscolo e luminosi di notte.

Nel mondo si trovano piccoli cristalli luminosi da raccogliere, e puoi piazzarli dove vuoi: ognuno emette
una luce viola che illumina il paesaggio attorno, con un alone nell'aria.

I prati sono coperti di erba a chiazze di colore (verde acqua, viola, magenta, oro-lilla, azzurro) che ondeggia al vento e brilla in controluce; le rive sono bordate di canneti; in certe zone cresce alta fino alle cosce e, al centro, più alta del personaggio, e il paesaggio è punteggiato
di boschetti, ognuno di una sola specie, e alberi isolati in stile fiabesco: querce, cipressi, alberi dorati, turchesi, fioriti di lilla,
ad ombrello, e alcuni con sfere luminose appese che si accendono di notte.

Nel cielo galleggiano isole fluttuanti con alberi in cima e cristalli luminosi appesi sotto: in volo
ci puoi atterrare e camminarci sopra. Le nuvole sono volumetriche, si colorano all'alba e al tramonto
e proiettano ombre in movimento sul paesaggio. Il bloom fa brillare sole, luci e cristalli,
e l'aria è piena di particelle: granelli dorati di giorno,
spore lilla al tramonto, lucciole di notte.

> Nato come clone di Minecraft: la versione a blocchi è sul branch `main`.
> Questo branch (`terreno-realistico`) la sostituisce con un terreno continuo.

## Avvio

```
dotnet run -c Release
```

Su Windows il gioco imposta da solo la preferenza "Prestazioni elevate" per la scheda video, così sui
portatili con due schede usa quella dedicata; la prima volta si riavvia da solo per applicarla.

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
| Click sinistro | Raccogli il cristallo che guardi |
| Click destro | Piazza un cristallo sul terreno |
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
    WorldObjects.cs       cristalli da raccogliere e piazzare, e le loro luci
    IslandField.cs        isole fluttuanti: dove sono, forma, alberi in cima
    Ground.cs             dove si può camminare: terreno e cime delle isole
    Creatures.cs          farfalle, pesci e stormi di uccelli (simulazione)
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
    PostProcess.cs        buffer HDR, bloom, color grading
    MoteRenderer.cs       particelle magiche: granelli, spore, lucciole
    WaterRenderer.cs      mari e laghi (lo shader è in TerrainShaders)
    CreatureRenderer.cs   modelli e animazione delle creature
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
