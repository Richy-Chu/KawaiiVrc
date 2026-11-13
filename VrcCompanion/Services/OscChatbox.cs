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
    {
        if (text.Length <= ChatboxLimit)
        {
            return text;
        }

        var available = ChatboxLimit - 1;
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

public interface IChatboxSender
{
    string Host { get; set; }

    int Port { get; set; }

    void SendChatbox(string text, bool sendImmediately = true);

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

    public void SendChatbox(string text, bool sendImmediately = true)
        => Send(OscMessage.ChatboxInput(text, sendImmediately));

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
