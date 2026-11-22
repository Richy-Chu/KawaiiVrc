using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The launch path must rebuild the signed-in state from the saved cookie
/// without the user typing anything, and it must keep that cookie.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public sealed class SessionRestoreTests
{
    [Fact]
    public async Task ASavedSessionIsRestoredForTheSignedInUser()
    {
        using var api = new VrcApiClient(CreateStore(), new StubVrcHandler());
        Assert.True(api.HasSavedSession);

        var user = await api.RestoreSessionAsync();

        Assert.NotNull(user);
        Assert.Equal("usr_saved", user!.Id);
        // Restoring must not throw the saved cookie away.
        Assert.True(api.HasSavedSession);
    }

    [Fact]
    public void LaunchRestoresTheSessionAndSignsIn()
    {
        // The real launch runs on the WPF dispatcher, and ApplyCurrentUser
        // updates views bound to CollectionViews. Running this on a thread-pool
        // thread would fail for that reason instead of testing restore.
        WpfTestHost.RunAsync(async () =>
        {
            var api = new VrcApiClient(CreateStore(), new StubVrcHandler());
            using var shell = new MainViewModel(api);
            TestHooks.SkipStartupRestore(false);

            await shell.InitializeAsync();

            Assert.True(
                shell.IsLoggedIn,
                $"error={shell.ErrorMessage} saved={api.HasSavedSession} " +
                $"apiUser={api.CurrentUser?.Id} shellUser={shell.CurrentUser?.Id} busy={shell.IsBusy}");
            Assert.True(shell.HasLoggedInSession);
            Assert.Equal("usr_saved", shell.CurrentUser?.Id);
        });
    }

    [Fact]
    public void TheSuiteNeverUsesTheRealSessionFile()
    {
        var realPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VrcCompanion",
            "session.dat");

        Assert.NotEqual(realPath, new SessionStore().FilePath);
        Assert.StartsWith(Path.GetTempPath(), new SessionStore().FilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnEmptyAccountResponseKeepsTheSavedSession()
    {
        var store = CreateStore();
        using var api = new VrcApiClient(store, new StubVrcHandler(emptyUser: true));

        Assert.Null(await api.RestoreSessionAsync());
        Assert.True(api.HasSavedSession);
    }

    [Fact]
    public void FriendHistoryFromABackgroundThreadIsMarshalledToTheDispatcher()
    {
        WpfTestHost.Run(() =>
        {
            using var api = new VrcApiClient(CreateStore(), new StubVrcHandler());
            using var shell = new MainViewModel(api);
            var window = new MainWindow(shell, initializeOnLoad: false);
            window.Show();
            try
            {
                var entries = new[]
                {
                    new ActivityEntry
                    {
                        Id = "evt_thread",
                        Type = ActivityType.FriendAdded,
                        UserId = "usr_thread",
                        DisplayName = "Thread Friend",
                        Timestamp = DateTimeOffset.UtcNow
                    }
                };
                var load = typeof(MainViewModel).GetMethod(
                    "LoadFriendChangesFromHistory",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!;

                // The history warm-up can deliver this off the dispatcher.
                Task.Run(() => load.Invoke(shell, new object[] { entries }))
                    .GetAwaiter()
                    .GetResult();

                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (shell.FriendChanges.Count == 0 && DateTime.UtcNow < deadline)
                {
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                        () => { },
                        System.Windows.Threading.DispatcherPriority.Background);
                    Thread.Sleep(10);
                }

                Assert.Single(shell.FriendChanges);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static SessionStore CreateStore()
    {
        var store = new SessionStore(Path.Combine(
            Path.GetTempPath(),
            "vrc-restore-" + Guid.NewGuid().ToString("N")));
        store.Save("test-cookie");
        return store;
    }

    private sealed class StubVrcHandler : HttpMessageHandler
    {
        private readonly bool _emptyUser;

        public StubVrcHandler(bool emptyUser = false) => _emptyUser = emptyUser;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var isUser = path.EndsWith("/auth/user", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(path, "/auth/user", StringComparison.OrdinalIgnoreCase);
            var body = isUser
                ? _emptyUser
                    ? "{}"
                    : "{\"id\":\"usr_saved\",\"displayName\":\"Saved User\",\"state\":\"active\"}"
                : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
