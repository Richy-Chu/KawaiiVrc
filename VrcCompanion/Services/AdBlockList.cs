using System;
using System.Collections.Generic;

namespace VrcCompanion.Services;

/// <summary>
/// Emergency fallback host list used only when the bundled Brave
/// <c>adblock-rust</c> engine cannot load. Normal filtering goes through the
/// official Brave filter lists in <see cref="BraveAdBlockEngine"/>.
/// </summary>
public static class AdBlockList
{
    private static readonly HashSet<string> BlockedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        // Ad networks and exchanges.
        "3lift.com",
        "adform.net",
        "adition.com",
        "adnxs.com",
        "adroll.com",
        "adsrvr.org",
        "adsymptotic.com",
        "advertising.com",
        "agkn.com",
        "amazon-adsystem.com",
        "bidswitch.net",
        "bluekai.com",
        "casalemedia.com",
        "contextweb.com",
        "criteo.com",
        "criteo.net",
        "crwdcntrl.net",
        "demdex.net",
        "doubleclick.net",
        "doubleverify.com",
        "emetriq.com",
        "everesttech.net",
        "exelator.com",
        "googleadservices.com",
        "googlesyndication.com",
        "googletagservices.com",
        "gumgum.com",
        "indexww.com",
        "innovid.com",
        "krxd.net",
        "lijit.com",
        "mathtag.com",
        "mgid.com",
        "moatads.com",
        "nativo.com",
        "openx.net",
        "outbrain.com",
        "pubmatic.com",
        "quantserve.com",
        "revcontent.com",
        "rlcdn.com",
        "rubiconproject.com",
        "sharethrough.com",
        "smartadserver.com",
        "smartclip.net",
        "sovrn.com",
        "spotxchange.com",
        "springserve.com",
        "stickyadstv.com",
        "taboola.com",
        "tapad.com",
        "teads.tv",
        "tremorhub.com",
        "tribalfusion.com",
        "undertone.com",
        "yieldlab.net",
        "yieldmo.com",
        "zedo.com",

        // Tracking and audience measurement that ad blockers commonly stop.
        "adsafeprotected.com",
        "amplitude.com",
        "clarity.ms",
        "comscore.com",
        "fullstory.com",
        "google-analytics.com",
        "hotjar.com",
        "mixpanel.com",
        "scorecardresearch.com",
        "segment.io"
    };

    public static bool ShouldBlock(Uri uri)
    {
        if (uri is null ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var host = uri.Host.TrimEnd('.');
        foreach (var blocked in BlockedHosts)
        {
            if (host.Equals(blocked, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + blocked, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
