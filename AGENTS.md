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
suite ends. The verdict is printed from `%TEMP%\VrcCompanionTests\logs\profile-guard-note.txt`
by MSBuild after VSTest, because the runner drops a passing test's output. A filtered run hands
its baseline to a second guard pass (`VRCC_PROFILE_GUARD_REPLAY=1`), so a filtered run still
goes red on a leak. A run that changes the profile while the installed app is running is
reported as inconclusive rather than failed: the app writes those files too.

## The scratch sweep

`TestProfile` (`VrcCompanion.Tests/TestProfile.cs`) deletes what runs leave in `%TEMP%`: at
startup for runs that crashed, at exit for its own. It clears the roots (`VrcCompanionTests`
except `logs`, `shared` and `browser`; `VrcMediaMeta`; `VrcCompanion.Tests`; `VrcMediaTests`),
single files named with a test prefix **and** a GUID (a name without one belongs to another
tool, so its `vrc-*.log` stays), and per-run folders `vrc-companion-tests-<pid>` only once that
pid is gone, including a one-hour rule for pids Windows has reused.

`TestScratchTests` sweeps a sandbox to keep those rules honest. Set
`VRCC_KEEP_TEST_SCRATCH=1` to skip the exit sweep and keep a failing run's files: the run's own
folder stays at `%TEMP%\vrc-companion-tests-<pid>` (the test host's pid, and the newest such
folder after a run), along with the cache folders under `%TEMP%\VrcMediaMeta`,
`%TEMP%\VrcCompanion.Tests` and `%TEMP%\VrcMediaTests` and any loose `%TEMP%` files the tests
wrote. The next run sweeps them at startup.

## Commits

Imperative subject, then a short body that says why. No AI attribution and no trailers.
