using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

// Proves that an install of an older release updates to the target release using delta
// packages only, resolved from GitHub the same way the app's updater resolves them.
// Exit codes: 0 verified, 1 verification failed, 2 bad arguments, 3 could not reach GitHub
// (rate limit, auth or transport), which says nothing about the release itself.

var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) { continue; }
    options[args[i][2..]] = args[++i];
}

// Diagnostic used by build/test-exit-codes.ps1: reports which exit code an HTTP status maps
// to, without contacting GitHub.
if (options.TryGetValue("classify-status", out var rawStatus) && int.TryParse(rawStatus, out var simulatedStatus))
{
    var mapped = ExitCodeFor(new HttpRequestException("simulated", null, (HttpStatusCode)simulatedStatus));
    Console.WriteLine($"status {simulatedStatus} -> exit {mapped}");
    return mapped;
}

string Required(string name)
{
    if (options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)) { return value; }
    Console.WriteLine($"FAIL: missing --{name}.");
    Environment.Exit(2);
    return string.Empty;
}

var repo = Required("repo");
var baseVersion = Required("from");
var targetVersion = Required("target");
var appId = options.TryGetValue("app-id", out var givenAppId) ? givenAppId : "KawaiiVrc";
var expectedDeltas = options.TryGetValue("expect", out var rawExpect) ? int.Parse(rawExpect) : 0;

// Anonymous GitHub API calls are limited to 60 per hour per IP, which shared CI runners
// exhaust, so authenticate when a token is in the environment. It is never printed.
var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
if (string.IsNullOrWhiteSpace(token)) { token = null; }

var root = Path.Combine(Path.GetTempPath(), "KawaiiVrcReleaseVerify", Guid.NewGuid().ToString("N"));
var packages = Path.Combine(root, "packages");
var content = Path.Combine(root, "current");
Directory.CreateDirectory(packages);
Directory.CreateDirectory(content);

var basePackageName = $"{appId}-{baseVersion}-full.nupkg";
var basePackagePath = Path.Combine(packages, basePackageName);

// Resolve the patcher before downloading: without it nothing can be verified, and there is
// no point fetching the whole base package first.
string updateExeSource;
try
{
    updateExeSource = ResolveUpdateExe(options);
}
catch (FileNotFoundException ex)
{
    // The patcher is part of the tooling, not the release, so nothing was verified.
    Console.WriteLine($"INFRASTRUCTURE: {ex.Message}");
    return 3;
}

try
{
    using (var http = new HttpClient())
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("KawaiiVrc-release-verifier");
        var url = $"https://github.com/{repo}/releases/download/v{baseVersion}/{basePackageName}";
        Console.WriteLine($"base   {url}");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var transport = status is 401 or 403 or 408 or 429 || status >= 500;
            Console.WriteLine(transport
                ? $"FAIL: base package download returned {status}; GitHub refused the request, not release drift."
                : $"FAIL: base package download returned {status}.");
            return transport ? 3 : 1;
        }

        await using var file = File.Create(basePackagePath);
        await response.Content.CopyToAsync(file);
    }

        var updateExe = Path.Combine(root, "Update.exe");
    File.Copy(updateExeSource, updateExe, overwrite: true);
    Console.WriteLine($"update {updateExeSource}");

    var logger = new ProbeLogger();
    var locator = new TestVelopackLocator(appId, baseVersion, packages, content, root, updateExe, "win", logger);
    Console.WriteLine($"auth   {(token is null ? "anonymous" : "token")}");
    var manager = new UpdateManager(new GithubSource($"https://github.com/{repo}", token, false), locator: locator);

    var update = await manager.CheckForUpdatesAsync();
    if (update is null)
    {
        Console.WriteLine($"FAIL: {baseVersion} resolves to itself, so nothing was published for {targetVersion}.");
        return 1;
    }

    if (update.TargetFullRelease.Version.ToString() != targetVersion)
    {
        Console.WriteLine($"FAIL: expected target {targetVersion} but the feed offers {update.TargetFullRelease.Version}.");
        return 1;
    }

    var deltas = update.DeltasToTarget;
    var deltaVersions = deltas.Select(d => d.Version.ToString()).ToArray();
    Console.WriteLine($"deltas {deltas.Length} ({string.Join(", ", deltaVersions)})");

    if (expectedDeltas > 0 && deltas.Length != expectedDeltas)
    {
        Console.WriteLine($"FAIL: expected {expectedDeltas} delta(s) between {baseVersion} and {targetVersion} but got {deltas.Length}.");
        return 1;
    }

    if (deltaVersions.Distinct(StringComparer.Ordinal).Count() != deltaVersions.Length)
    {
        Console.WriteLine("FAIL: the merged feed lists the same delta more than once; the patcher races on one file.");
        return 1;
    }

    if (deltas.Length == 0)
    {
        Console.WriteLine($"FAIL: no delta between {baseVersion} and {targetVersion}; a skipped-version install would download the full package.");
        return 1;
    }

    await manager.DownloadUpdatesAsync(update, _ => { });

    if (logger.SawFullFallback)
    {
        Console.WriteLine("FAIL: the patcher fell back to a full download, so the chain did not apply.");
        return 1;
    }

    var patched = Path.Combine(packages, $"{appId}-{targetVersion}-full.nupkg");
    if (!File.Exists(patched))
    {
        Console.WriteLine($"FAIL: no patched package at {patched}.");
        return 1;
    }

    var packagedVersion = ReadPackagedVersion(patched);
    if (packagedVersion is not null && packagedVersion != targetVersion)
    {
        Console.WriteLine($"FAIL: patched package reports version {packagedVersion}, expected {targetVersion}.");
        return 1;
    }

    Console.WriteLine(
        $"PASS: {baseVersion} -> {targetVersion} via {deltas.Length} delta patch(es), {new FileInfo(patched).Length} bytes rebuilt locally.");
    return 0;
}
catch (Exception ex)
{
    var exitCode = ExitCodeFor(ex);
    Console.WriteLine(exitCode == 3
        ? $"INFRASTRUCTURE: {ex.GetType().Name}: {ex.Message}"
        : $"FAIL: {ex.GetType().Name}: {ex.Message}");
    return exitCode;
}

// An auth, rate-limit, server or transport failure says nothing about the releases, so it
// gets its own exit code instead of looking like drift.
static int ExitCodeFor(Exception ex)
{
    if (ex is TaskCanceledException) { return 3; }
    if (ex is HttpRequestException { StatusCode: null }) { return 3; }

    if (ex is HttpRequestException { StatusCode: { } status })
    {
        var code = (int)status;
        if (code is 401 or 403 or 408 or 429 || code >= 500) { return 3; }
    }

    return 1;
}

// Packages carry only app files; Update.exe ships with the vpk tool (vendor/update.exe)
// and is also installed next to an installed app. Either copy can drive the patcher.
static string ResolveUpdateExe(Dictionary<string, string> options)
{
    if (options.TryGetValue("update-exe", out var given) && File.Exists(given)) { return given; }

    var store = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", ".store", "vpk");
    if (Directory.Exists(store))
    {
        var vendored = Directory
            .GetFiles(store, "update.exe", SearchOption.AllDirectories)
            .OrderByDescending(path => path)
            .FirstOrDefault();
        if (vendored is not null) { return vendored; }
    }

    var installed = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KawaiiVrc", "Update.exe");
    if (File.Exists(installed)) { return installed; }

    throw new FileNotFoundException(
        "No Update.exe found. Install the vpk tool (dotnet tool install -g vpk) or pass --update-exe <path>.");
}

static string? ReadPackagedVersion(string package)
{
    using var zip = ZipFile.OpenRead(package);
    var nuspec = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
    if (nuspec is null) { return null; }

    using var reader = new StreamReader(nuspec.Open());
    var match = Regex.Match(reader.ReadToEnd(), @"<version>\s*([^<\s]+)\s*</version>", RegexOptions.IgnoreCase);
    return match.Success ? match.Groups[1].Value : null;
}

internal sealed class ProbeLogger : IVelopackLogger
{
    public bool SawFullFallback { get; private set; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log(VelopackLogLevel level, string? message, Exception? exception)
    {
        if (message is null) { return; }

        if (message.Contains("falling back to full", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Unable to apply delta", StringComparison.OrdinalIgnoreCase))
        {
            SawFullFallback = true;
        }

        if (level >= VelopackLogLevel.Information)
        {
            Console.WriteLine($"[{level}] {message}");
        }

        if (exception is not null)
        {
            Console.WriteLine($"    {exception.Message}");
        }
    }
}
