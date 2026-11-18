using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class OscChatboxTests
{
    [Fact]
    public void ChatboxInputEncodesAddressTagsAndText()
    {
        var packet = OscMessage.ChatboxInput("Hello VRChat", sendImmediately: true, playSound: false);

        var (address, offset) = ReadString(packet, 0);
        var (tags, textOffset) = ReadString(packet, offset);
        var (text, _) = ReadString(packet, textOffset);

        Assert.Equal("/chatbox/input", address);
        Assert.Equal(",sTF", tags);
        Assert.Equal("Hello VRChat", text);
        Assert.Equal(0, packet.Length % 4);
    }

    [Fact]
    public void ChatboxTypingEncodesABooleanTag()
    {
        var packet = OscMessage.ChatboxTyping(typing: true);

        var (address, offset) = ReadString(packet, 0);
        var (tags, _) = ReadString(packet, offset);

        Assert.Equal("/chatbox/typing", address);
        Assert.Equal(",T", tags);
    }

    [Fact]
    public void AvatarChangeEncodesTheAvatarId()
    {
        var packet = OscMessage.AvatarChange("avtr_912d66a4-4714-43b8-8407-7de2cafbf55b");

        var (address, offset) = ReadString(packet, 0);
        var (tags, textOffset) = ReadString(packet, offset);
        var (avatarId, _) = ReadString(packet, textOffset);

        Assert.Equal("/avatar/change", address);
        Assert.Equal(",s", tags);
        Assert.Equal("avtr_912d66a4-4714-43b8-8407-7de2cafbf55b", avatarId);
    }

    [Fact]
    public async Task AvatarSwitchIsSentToVrchatsLocalOscPort()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        var receive = listener.ReceiveAsync();

        Assert.True(OscAvatarSwitch.TrySend("avtr_fast", "127.0.0.1", port));
        var result = await receive.WaitAsync(TimeSpan.FromSeconds(2));
        var (address, offset) = ReadString(result.Buffer, 0);
        var (_, textOffset) = ReadString(result.Buffer, offset);
        var (avatarId, _) = ReadString(result.Buffer, textOffset);

        Assert.Equal("/avatar/change", address);
        Assert.Equal("avtr_fast", avatarId);
    }

    [Fact]
    public async Task AutomaticStatusIsSentToVrChatWithDisplayNowEnabled()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        var settingsPath = Path.Combine(Path.GetTempPath(), $"VrcCompanion-osc-{Guid.NewGuid():N}.json");

        try
        {
            using var sender = new OscChatboxService { Host = "127.0.0.1", Port = port };
            using var vm = new ChatboxViewModel(new SettingsStore(settingsPath), sender, startTimers: false);
            vm.Port = port;
            vm.NewStatusText = "visible in VRChat";
            vm.AddStatusCommand.Execute(null);

            var receive = listener.ReceiveAsync();
            vm.RefreshForTest();
            var result = await receive.WaitAsync(TimeSpan.FromSeconds(2));
            var packet = result.Buffer;
            var (address, offset) = ReadString(packet, 0);
            var (tags, textOffset) = ReadString(packet, offset);
            var (text, _) = ReadString(packet, textOffset);

            Assert.Equal("/chatbox/input", address);
            Assert.Equal(",sTF", tags);
            Assert.Equal("💭 visible in VRChat", text);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    [Fact]
    public void ChatboxTextIsShortenedAtAWordBoundary()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 60));

        var fitted = OscMessage.FitChatboxText(text);

        Assert.True(fitted.Length <= OscMessage.ChatboxLimit);
        Assert.EndsWith("…", fitted);
        Assert.DoesNotContain("wor…", fitted);
    }

    [Fact]
    public void ChatboxTextIsTruncatedToVrchatsLimit()
    {
        var packet = OscMessage.ChatboxInput(new string('x', 200));

        var (_, offset) = ReadString(packet, 0);
        var (_, textOffset) = ReadString(packet, offset);
        var (text, _) = ReadString(packet, textOffset);

        Assert.Equal(OscMessage.ChatboxLimit, text.Length);
    }

    private static (string Value, int NextOffset) ReadString(byte[] packet, int offset)
    {
        var end = Array.IndexOf(packet, (byte)0, offset);
        var value = Encoding.UTF8.GetString(packet, offset, end - offset);
        var next = ((end + 4) / 4) * 4;
        return (value, next);
    }
}
