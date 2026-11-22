using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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
    public async Task LaunchRestoresTheSessionAndSignsIn()
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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var isUser = path.EndsWith("/auth/user", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(path, "/auth/user", StringComparison.OrdinalIgnoreCase);
            var body = isUser
                ? "{\"id\":\"usr_saved\",\"displayName\":\"Saved User\",\"state\":\"active\"}"
                : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
