using Mine;

// Prefer the discrete GPU (Windows); the first time, the game restarts itself to pick it up.
if (GpuPreference.EnsureHighPerformance(args)) return;

using var game = new Game();
game.Run();
