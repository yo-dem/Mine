# Mine

Un gioco di esplorazione in paesaggi morbidi e sognanti, in C# / .NET 10 con
[Silk.NET](https://github.com/dotnet/Silk.NET) (OpenGL 3.3 core). Gira su Windows, Linux e macOS.
Tutto è generato dal codice: niente file di risorse.

Il mondo è fatto di **strati**: piastrelle piatte di 2×2 m ad altezze multiple di mezzo metro, con pareti
tra un livello e l'altro (un gradino si sale camminando, fino a un metro saltando). La forma è generata
proceduralmente: colline dolci e dune a strati di mezzo blocco (i salti più alti sono rarissimi)
e guglie di roccia surreali, visibili fino a oltre un chilometro, in una palette cosmica viola, indaco
e turchese. Il ciclo giorno/notte (20 minuti) respira tra lunghi crepuscoli dorati, con il sole basso sull'orizzonte, un mezzogiorno breve e luminoso in cui il sole sale alto nel cielo,
e una notte cosmica dominata da un'enorme galassia a spirale dai cinque bracci sfumati, irregolare, con scie di polvere scura, una grande luna argento-lilla con crateri, mari viola e un alone luminoso, nuvole cupe dai bordi argentati, nebulose, stelle fittissime e, ogni tanto, stelle cadenti che solcano lentamente gran parte del cielo con una lunga scia. La sabbia e i prati scintillano di granelli luminosi.

Nelle zone basse ci sono mari e laghi che riflettono il cielo, galassia compresa, con spiagge di sabbia viola:
nell'acqua bassa pulsano venature di luce ciano bioluminescente, la riva è segnata da una linea luminosa
e sulla superficie brillano scintille come stelle cadute. Vicino alla riva si cammina nell'acqua; al largo è profonda e si **nuota**: ci si muove come in volo ma lentamente, Spazio per salire, Shift per immergersi, e da fermi si torna a galla.
Sulle rive crescono grappoli di cristalli viola e ciano che illuminano ciò che hanno intorno; nell'acqua bassa galleggiano fiori di loto luminosi e tra l'erba spuntano fiori di ogni colore: campanule luminose, margherite pastello, tulipani, lupini dalle punte che brillano, piccoli fiori a stella luminosi e, raccolti in giardini sotto i boschi fitti, fiori nati dalla luce: steli chiari che brillano verso l'alto e si ramificano in grappoli di 3–4 fiori, foglie colorate con venature di luce, petali al neon, semi di luce che fluttuano sopra la corolla e una luce colorata che tinge l'erba intorno (iris viola dalle foglie a spada, papaveri magenta su rosette di foglie, gigli ciano dai petali ripiegati); stelle luminose, campanule e gigli crescono anche sotto i boschi.
Sulle spiagge e lungo le rive dei laghi crescono boschetti di palme scure in controluce, alte e slanciate, medie o basse e tozze, mai uguali tra loro; farfalle
luminose svolazzano sui prati (a sciami di notte), pesci che brillano nuotano nell'acqua più profonda e, tuffandosi, si incontrano banchi di grossi pesci luminosi che nuotano vicino al fondale e stormi di uccelli,
ogni volta diversi per numero e forma, mai regolari (V sbilenche con ritardatari, nuvole che si allungano e si stringono, gruppetti che si separano e si riuniscono, vortici), volteggiano in cielo: tanti di giorno, arrivano da lontano al mattino e se ne vanno al calare della sera, e di sera e di notte se ne vede solo qualcuno.

Nel mondo si trovano piccoli cristalli luminosi da raccogliere, e puoi piazzarli dove vuoi: ognuno emette
una luce viola che illumina il paesaggio attorno, con un alone nell'aria.

I prati sono coperti da un tappeto fitto di ciuffi d'erba (più fili da una base comune, aperti a ventaglio, raccolti in gruppetti, con ciuffi bassi che coprono il terreno tra quelli alti) a chiazze di colore (verde acqua, viola, magenta, oro-lilla, azzurro) che ondeggia al vento e brilla in controluce; le rive e l'acqua bassa sono punteggiate di canneti di altezze diverse, dai ciuffi bassi alle canne altissime, con le spighe scure delle tife; in certe zone cresce alta fino alle cosce e, al centro, più alta del personaggio, e il paesaggio è punteggiato
di boschi fitti, con arbusti e alberelli nel sottobosco, fatti di boschetti ognuno di una sola specie, e di alberi isolati in stile fiabesco: querce, cipressi, pini, giganti, salici, alberi rosa, turchesi, fioriti di lilla,
ad ombrello, e alcuni con sfere luminose appese che si accendono di notte.

Il mondo è diviso in biomi, ognuno con i suoi alberi, i suoi fiori e il suo colore d'erba (le chiazze di colore continuano a mescolarsi, ma tendono al tono del bioma), che sfumano l'uno nell'altro ai confini: boschi indaco di pini, cipressi e giganti con iris e lupini, boschi rosa di alberi fioriti con papaveri e tulipani, boschi turchesi di salici e sfere azzurre con gigli e fiori a stella, e deserti di sabbia calda color pesca, con dune alte, alberi secchi e contorti, cespugli spogli e grappoli di cristalli che spuntano dalla sabbia. Tra le colline si aprono tanti laghi, spesso profondi.

Nel cielo galleggiano isole fluttuanti con alberi in cima e cristalli luminosi appesi sotto: in volo
ci puoi atterrare e camminarci sopra. Da poche, rare isole un filo di acqua luminescente scorre fino
al bordo e precipita in sottili rivoli argentati che, a terra, riempiono un piccolo laghetto luminoso,
con cerchi di luce che si allargano dal punto in cui cade l'acqua e schizzi di goccioline che saltano
e ricadono. Lungo i rivoli si vede l'acqua scorrere, a impulsi di luce che scendono veloci. Le nuvole sono volumetriche, si colorano all'alba e al tramonto
e proiettano ombre in movimento sul paesaggio. Il bloom fa brillare sole, luci e cristalli,
e l'aria è piena di particelle: granelli dorati di giorno,
spore lilla al tramonto, lucciole di notte. Col tasto 2 arriva la pioggia: il cielo si chiude in un indaco profondo, cade una pioggia fitta di fili sottilissimi e argentei, e sull'acqua si aprono cerchi luminosi.

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
| 1 | Fai partire una stella cadente davanti a te (debug) |
| 2 | Fai iniziare o smettere la pioggia |
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
    Weather.cs            pioggia: accesa/spenta col tasto 2, intensità che sale e scende piano
    WorldObjects.cs       cristalli da raccogliere e piazzare, e le loro luci
    IslandField.cs        isole fluttuanti: dove sono, forma, alberi in cima
    Ground.cs             dove si può camminare: terreno e cime delle isole
    Creatures.cs          farfalle, pesci e stormi di uccelli (simulazione)
    TreeField.cs          dove crescono alberi e fiori, per bioma (boschi, alberi isolati, deserti), collisione con i tronchi
    GroundMaterials.cs    biomi, colore e copertura dell'erba (copia in C# delle funzioni dello shader)
  Rendering/
    TerrainRenderer.cs    tessere del terreno con livelli di dettaglio, costruite in background
    TerrainShaders.cs     shader di terreno e oggetti: materiali, luce, ombre, luci, foschia
    ObjectRenderer.cs     modelli procedurali degli oggetti
    GrassRenderer.cs      ciuffi d'erba attorno al giocatore (instancing, costruiti in background)
    TreeModels.cs         modelli procedurali degli alberi, 16 stili (anche secchi) e 4 livelli di dettaglio
    TreeRenderer.cs       disegno degli alberi (instancing per stile e livello di dettaglio)
    SkyRenderer.cs        cielo, sole, luna, stelle, nuvole volumetriche
    CloudNoise.cs         texture di rumore 3D per le nuvole
    PostProcess.cs        buffer HDR, bloom, color grading
    MoteRenderer.cs       particelle magiche: granelli, spore, lucciole
    RainRenderer.cs       pioggia di luce (gocce generate nello shader)
    WaterRenderer.cs      mari e laghi (lo shader è in TerrainShaders)
    CreatureRenderer.cs   modelli e animazione delle creature
    IslandRenderer.cs     modelli delle isole fluttuanti
    WaterfallRenderer.cs  cascate luminose delle isole e i loro laghetti
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
