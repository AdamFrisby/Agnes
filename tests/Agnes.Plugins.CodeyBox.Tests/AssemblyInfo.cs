using Xunit;

// These tests render real Avalonia controls through process-global headless sessions. Keep their lifecycle
// single-threaded so one test cannot dispose the renderer while another is using it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
