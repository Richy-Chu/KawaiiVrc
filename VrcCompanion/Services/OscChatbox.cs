using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VrcCompanion.Services;

/// <summary>
/// Builds OSC 1.0 packets for VRChat's local chatbox endpoints.
/// </summary>
public static class OscMessage
{
    public const int ChatboxLimit = 144;


    public static byte[] ChatboxInput(string text, bool sendImmediately = true, bool playSound = false)
    {
        var trimmed = text.Length > ChatboxLimit ? text[..ChatboxLimit] : text;
        return Encode("/chatbox/input", trimmed, sendImmediately, playSound);
    }

    /// <summary>
    /// Shortens a chatbox line near a word boundary instead of cutting a word
    /// in half. The ellipsis counts toward VRChat's 144-character limit.
    /// </summary>
    public static string FitChatboxText(string text)
        => FitChatboxText(text, ChatboxLimit);

    public static string FitChatboxText(string text, int limit)
    {
        if (limit <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= limit)
        {
            return text;
        }

        var available = limit - 1;
        var slice = text[..available];
        var boundary = slice.LastIndexOf(' ');
        if (boundary < available / 2)
        {
            boundary = available;
        }

        return slice[..boundary].TrimEnd() + "…";
    }

    public static byte[] ChatboxTyping(bool typing)
        => Encode("/chatbox/typing", typing);

    /// <summary>
    /// Asks the running VRChat client to load an avatar immediately. VRChat
    /// applies this locally without waiting for the REST avatar change to reach
    /// the game session.
    /// </summary>
    public static byte[] AvatarChange(string avatarId)
        => Encode("/avatar/change", avatarId);

    public static byte[] Encode(string address, params object[] arguments)
    {
        using var stream = new MemoryStream();
        WriteOscString(stream, address);

        var tags = new StringBuilder(",");
        foreach (var argument in arguments)
        {
            tags.Append(argument switch
            {
                string => 's',
                bool value => value ? 'T' : 'F',
                _ => throw new ArgumentException("Only string and bool OSC arguments are supported.")
            });
        }

        WriteOscString(stream, tags.ToString());

        foreach (var argument in arguments)
        {
            if (argument is string value)
            {
                WriteOscString(stream, value);
            }
        }

        return stream.ToArray();
    }

    private static void WriteOscString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
        stream.WriteByte(0);

        var padding = (4 - ((bytes.Length + 1) % 4)) % 4;
        for (var i = 0; i < padding; i++)
        {
            stream.WriteByte(0);
        }
    }
}

/// <summary>
/// Sends a one-shot avatar switch to the local VRChat client. OSC must be
/// enabled in VRChat (Action Menu → Options → OSC).
/// </summary>
public static class OscAvatarSwitch
{
    public static bool TrySend(string avatarId, string? host, int port)
    {
        if (string.IsNullOrWhiteSpace(avatarId))
        {
            return false;
        }

        var packet = OscMessage.AvatarChange(avatarId);
        var configuredPort = Math.Clamp(port, 1, 65535);
        var sent = false;

        // VRChat listens for OSC on IPv4 127.0.0.1:9000 by default. Send there
        // explicitly even if the saved chatbox setting points elsewhere, so a
        // stale port or a hostname that resolves to ::1 cannot lose the switch.
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, 9000));
            sent = true;

            var customHost = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
            var isLocalHost = customHost is null ||
                              customHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                              customHost == "127.0.0.1";

            if (!isLocalHost)
            {
                udp.Send(packet, packet.Length, customHost!, configuredPort);
                sent = true;
            }
            else if (configuredPort != 9000)
            {
                udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, configuredPort));
                sent = true;
            }
        }
        catch
        {
            // UDP has no acknowledgement; a failed custom target must not break Wear.
        }

        return sent;
    }
}

public interface IChatboxSender
{
    string Host { get; set; }

    int Port { get; set; }

    void SendChatbox(string text, bool sendImmediately = true, bool playSound = false);

    void SendTyping(bool typing);
}

/// <summary>
/// Sends chatbox messages to the local VRChat client over UDP OSC.
/// OSC must be enabled in VRChat (Action Menu → Options → OSC).
/// </summary>
public sealed class OscChatboxService : IChatboxSender, IDisposable
{
    private readonly UdpClient _udp = new();
    private readonly object _sync = new();

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 9000;

    public void SendChatbox(string text, bool sendImmediately = true, bool playSound = false)
        => Send(OscMessage.ChatboxInput(text, sendImmediately, playSound));

    public void SendTyping(bool typing)
        => Send(OscMessage.ChatboxTyping(typing));

    private void Send(byte[] packet)
    {
        lock (_sync)
        {
            _udp.Send(packet, packet.Length, Host, Port);
        }
    }

    public void Dispose() => _udp.Dispose();
}
