# Working in this repository

KawaiiVrc is a WPF app (`VrcCompanion/`, net8.0-windows10.0.19041.0) with its xunit suite in
`VrcCompanion.Tests/`. From the repository root:

    dotnet build VrcCompanion.Tests/VrcCompanion.Tests.csproj -c Release -v q --nologo
    dotnet test VrcCompanion.Tests/VrcCompanion.Tests.csproj -c Release --no-build --nologo

A full run is 860 tests and takes about 2 minutes 25 seconds. CI builds with SDK 8, while a
newer SDK may be the local default: to check SDK 8 compatibility, write a `global.json` with
`{"sdk":{"version":"8.0.427","rollForward":"latestPatch"}}`, build, then delete it.

## Never write to the real profile

The installed app keeps the user's session in `%LOCALAPPDATA%\VrcCompanion`. A test that
reaches it can clear the real saved session and sign the user out. Test stores go through the
seams in `VrcCompanion/Services` (`UserDataPaths`, `VrcLogPaths`), which the module initializer
points at a scratch folder before any test runs. Do not add a store that builds the profile
path itself: `ProfileIsolationTests` scans every production source file for that.

`RealProfileWatch` records the real profile in the module initializer and compares it when the
suite ends. The verdict is printed from
`%TEMP%\VrcCompanionTests\logs\guard\<run>\profile-guard-note.txt` by MSBuild after VSTest,
because the runner drops a passing test's output. A filtered run hands its baseline to a second
guard pass (`VRCC_PROFILE_GUARD_REPLAY=1`), so a filtered run still goes red on a leak. A run
that changes the profile while the installed app is running is reported as inconclusive rather
than failed: the app writes those files too.

Each run gets its own `<run>` folder, so two suites running at once cannot read each other's
baseline and pass a leak. The project makes the folder when it is evaluated and hands the test
host the path in a generated runsettings file (`VRCC_PROFILE_GUARD_DIR`); a run started without
the project - the test explorer, a bare `dotnet vstest` - names its folder after its test host's
pid instead. The guard deletes run folders older than a day, so the folder does not grow with
every run. A run that leaves no baseline at the path MSBuild gave it fails the run
(`FailOnMissingGuardBaseline`), so a hand-off that silently stops working cannot pass: without
it the replay pass would skip and the note would never print. Listing tests is the one run
exempt, since it never starts the guard.

## The scratch sweep

`TestProfile` (`VrcCompanion.Tests/TestProfile.cs`) gives each run a folder of its own and
points the process's temp at it: `TMP` and `TEMP` are set to `%TEMP%\vrc-companion-tests-<pid>`
in the type's static constructor. `Path.GetTempPath()` reads those variables on every call, so
that one assignment is the seam for the whole suite - store folders, media caches, loose files
named after a GUID, the WebView2 profile, and any path a test builds itself all land inside the
run's folder, with no call site changed. Never build a test path from the system temp: read
`TestProfile.RealTemp`, which was captured before the redirect.

Because everything is in one folder, the sweep has nothing to weigh: it deletes folders whose
process is gone (including a one-hour rule for pids Windows has since reused) and leaves
everything else alone. `SweepFinishedRuns` takes the moment it judges against, so that hour is
decided by cases a minute either side of it, and on it, rather than by how fast the runner is. Two suites at once write in different folders and never touch each
other. The run deletes its own folder at the end of its last collection (`ScratchCleanup`),
not at process exit: a test host that has reported every result is not obliged to run its exit
handlers, and one CI run left its whole scratch behind that way. The exit handler stays only as
a backstop for runs that never reach the last collection.

What survives are Chromium's own temp files: zero-byte `<guid>.tmp` files WebView2's browser
processes write into `%TMP%`, which is now the run's folder. They come and go while the browser
runs, and the last two are still open when the cleanup deletes the folder, because the browser
process lives as long as the host. No test or product call site writes that name, and nothing
can close the browser first: `CoreWebView2Environment` is one process-wide `Lazy` with no
shutdown, and WebView2 has no per-browser temp directory, so the files have to land in `%TMP%`.
A file held that way cannot be deleted from inside the host at all, so the run does not try: it
leaves the folder where it is and writes `cleanup-deferred.txt` inside it, naming its own pid,
the moment it gave up and every file an exclusive open found still held - a read, so a
read-only file is not mistaken for a lock. The next run's sweep takes the folder once that pid
is gone. The post-run check accepts a leftover folder only when that note names the folder's
own dead pid, every file it names was a zero-byte `.tmp`, and the folder holds nothing else it
does not name: cleanup deletes every file it can before writing the note, so a file left out of
it is one the run could not delete and did not explain. A folder with no note - a run whose
cleanup never ran - fails the job, and so does one whose note names a locked store file or whose
folder still holds a store file the note never mentions. The note's own `utc:` line has to be
recent too: the sweep takes a deferred folder on the next run's startup, so one older than two
hours was not swept, and the check takes it itself instead of letting it stand - locally that
usually just means nobody has run the suite since. Age is the one thing forgiven: a stale note
that names a locked store file, or sits beside a file it does not name, still fails and keeps
the folder, because that folder is evidence rather than litter. A stale folder the check cannot
delete fails too, so a take never turns into a silent skip - including a delete that reports no
error and leaves the folder where it was. The check ages a note against `-Now` when it is given,
so the two-hour bound is decided by pinned cases one minute either side of it, and on it, rather
than by whatever the machine's clock says while the cases run.

The WebView2 profile is the one thing that never lives in the run's folder: its browser holds it
open for the host's whole life, so it gets `%TEMP%\vrc-companion-browser-<pid>` and the next
run's sweep reclaims it once that pid is gone. The check judges those folders rather than listing
them: a profile may wait for the sweep only while its name parses to a pid, and that pid is no
longer running. A name no sweep can read, or a pid still alive when the run is over, fails the
job. A run therefore leaves nothing but a browser profile and a deferred folder that says why,
and both are held to the rules above.

`DeferralNoteContractTests` exercises the writer and the reader as a pair by running the real
script over notes this project wrote, so CI decides the accepting path too. It plants both halves
of the stale rule by asking `RecordDeferral` for a note dated four hours back: over files nobody
holds any more, which the check must take, and over a locked store file, which it must refuse and
leave standing.

The guard's files are the exception: they live under `%TEMP%\VrcCompanionTests\logs` (real
temp, via `TestProfile.RealTemp`) because MSBuild reads them after the run from outside this
process.

`TestScratchTests` keeps both rules honest: that `Path.GetTempPath()` resolves inside the run's
folder, and that the sweep takes a finished run's folder while leaving a live one alone. Set
`VRCC_KEEP_TEST_SCRATCH=1` to keep a failing run's files: the whole folder stays at
`%TEMP%\vrc-companion-tests-<pid>`, and the next run sweeps it at startup.

## CI

`lint-workflows.yml` runs on pushes and pull requests that touch `VrcCompanion.Tests/`,
`build/` or `.github/workflows/`: `verifier` runs the release-verifier self-tests
(`build/test-exit-codes.ps1`, `test-feed-parity.ps1`, `test-drift-report.ps1`,
`test-assert-temp-clean.ps1`, `test-retry-failed-tests.ps1`), `actionlint` lints the workflows, the pre-push hook and
`build/*.ps1` with PSScriptAnalyzer, and `profile-guard` on `windows-latest` runs
`build/test-profile-guard-red.ps1`, which requires one filtered run to exit 1 on a fabricated
baseline.

`test-suite.yml` runs on every push and is the only job that runs all 865 tests. When a test
fails, `build/retry-failed-tests.ps1` re-runs only the failed tests once: it plans the filter
from the run's `trx`, reports what passed on retry as a warning and in the step summary, and the
job stays green - a flaky test must not redden an otherwise good commit, but it must not be
hidden either. A test that fails twice fails the job, and a first run that fails more than five
test cases is not retried at all: a broad failure is the commit's, not the runner's. Cases, not
method names, are counted, so a theory that fails most of its rows reads as broken rather than as
one flake. Every run is bounded per test by `--blame-hang-timeout 2m`: a hung test aborts the run
in two minutes and leaves a blame sequence naming it, instead of running out the job's clock with
nothing recorded. An abort keeps no results at all, so for a hang the retry re-runs the whole
suite rather than a filtered set - everything after the hang never ran - and it applies the same
bound, so the re-run cannot hang either. The sequence files ride along in the artifact. The job finishes with
`build/assert-temp-clean.ps1`, which fails it when a `vrc-companion-tests-*` folder is still in
`%TEMP%` without a `cleanup-deferred.txt` naming that folder's own pid - the only litter a run
can leave, and the only folder allowed to wait for the next run's sweep. The step runs even when
the tests fail, so a red run still reports what it left. Run the script by hand after a local run
to get the same verdict.

## Commits

Imperative subject, then a short body that says why. No AI attribution and no trailers.
