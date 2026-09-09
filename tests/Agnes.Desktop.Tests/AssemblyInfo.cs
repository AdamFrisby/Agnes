using Xunit;

// Avalonia's headless application and renderer are process-global. Desktop UI tests create and dispose
// real sessions, so running classes concurrently can cross their dispatcher and renderer lifetimes.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
