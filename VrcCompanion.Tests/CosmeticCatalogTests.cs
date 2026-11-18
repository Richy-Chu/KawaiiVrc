using System.Net;
using System.Net.Http;
using System.Text;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

[Collection("CommunityHandler")]
public class CosmeticCatalogTests
{
    [Fact]
    public async Task FailedIndexCoolsDownThenRetriesWithoutRefetchingSuccessfulTypes()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var handler = new CosmeticRetryHandler();
        CosmeticCatalog.ResetForTests();
        CosmeticCatalog.UtcNow = () => now;
        CosmeticCatalog.RetryDelay = TimeSpan.FromSeconds(30);

        try
        {
            using var api = new VrcApiClient(null, handler);

            await CosmeticCatalog.EnsureLoadedAsync(api);
            Assert.False(CosmeticCatalog.IsLoaded);
            Assert.False(handler.IconFrameSucceeds);
            Assert.Equal(1, handler.IconFrameRequests);
            Assert.Equal(1, handler.ProfileEffectRequests);

            // The failed index must not be requested again during its cool-down.
            await CosmeticCatalog.EnsureLoadedAsync(api);
            Assert.Equal(1, handler.IconFrameRequests);

            handler.IconFrameSucceeds = true;
            now = now.AddSeconds(31);
            await CosmeticCatalog.EnsureLoadedAsync(api);

            Assert.True(CosmeticCatalog.IsLoaded);
            Assert.Equal(2, handler.IconFrameRequests);
            Assert.Equal(1, handler.ProfileEffectRequests);
            Assert.Equal(1, handler.NameplateEffectRequests);
            Assert.Equal(
                "https://files.example/icon-main.webp",
                CosmeticCatalog.DisplayUrl(CosmeticCatalog.IconFrame, "icon_1"));
            Assert.Equal(
                "https://files.example/icon-intro.webp",
                CosmeticCatalog.IntroUrl(CosmeticCatalog.IconFrame, "icon_1"));
        }
        finally
        {
            CosmeticCatalog.ResetForTests();
        }
    }

    private sealed class CosmeticRetryHandler : HttpMessageHandler
    {
        public bool IconFrameSucceeds { get; set; }

        public int IconFrameRequests { get; private set; }

        public int ProfileEffectRequests { get; private set; }

        public int NameplateEffectRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.EndsWith("/cosmetics/index/iconFrame", StringComparison.Ordinal))
            {
                IconFrameRequests++;
                if (!IconFrameSucceeds)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }

                return Json(
                    """
                    [
                      {
                        "id": "icon_1",
                        "name": "Frame",
                        "itemType": "iconFrame",
                        "metadata": {
                          "assets": [
                            { "type": "base", "url": "https://files.example/icon-base.png" },
                            { "type": "mainAnimation", "url": "https://files.example/icon-main.webp" },
                            { "type": "introAnimation", "url": "https://files.example/icon-intro.webp" }
                          ]
                        }
                      }
                    ]
                    """);
            }

            if (path.EndsWith("/cosmetics/index/profileEffect", StringComparison.Ordinal))
            {
                ProfileEffectRequests++;
                return Json("[]");
            }

            if (path.EndsWith("/cosmetics/index/nameplateEffect", StringComparison.Ordinal))
            {
                NameplateEffectRequests++;
                return Json("[]");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
