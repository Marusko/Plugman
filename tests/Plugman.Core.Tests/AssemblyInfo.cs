// Unload assertions are process-global: they force a GC and then ask whether one specific
// collectible load context died. A test running in parallel that holds a plugin instance,
// a plugin Type or a plugin-built view alive makes those assertions non-deterministic, so
// this assembly runs its tests one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
