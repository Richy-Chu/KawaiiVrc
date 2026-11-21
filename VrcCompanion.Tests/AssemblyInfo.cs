using Xunit;

// Every test here shares one process: the WPF dispatcher and application, the
// WebView2 profile, the decoded-image cache and the settings singletons. Running
// collections beside each other was what made the same suite fail a different
// set of tests on every run, so the whole assembly runs one test at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
