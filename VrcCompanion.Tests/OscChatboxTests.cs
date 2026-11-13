using System;
using System.Text;
using VrcCompanion.Services;
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
