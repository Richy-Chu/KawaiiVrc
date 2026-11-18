using VrcCompanion.Models;

namespace VrcCompanion.ViewModels;

/// <summary>A reusable message slot shown in the composer picker.</summary>
public sealed class InviteMessageOption
{
    public InviteMessageOption(InviteMessage message, string typeLabel)
    {
        Slot = message.Slot;
        Message = message.Message ?? string.Empty;
        MessageType = message.MessageType ?? string.Empty;
        RemainingCooldownMinutes = Math.Max(0, message.RemainingCooldownMinutes);
        CanBeUpdated = message.CanBeUpdated;
        TypeLabel = typeLabel;
    }

    public int Slot { get; }

    public string Message { get; }

    public string MessageType { get; }

    public int RemainingCooldownMinutes { get; }

    public bool CanBeUpdated { get; }

    public string TypeLabel { get; }

    public string SlotLabel => $"{TypeLabel} slot {Slot}";

    public string Preview => string.IsNullOrWhiteSpace(Message) ? "(empty)" : Message;

    public bool HasCooldown => RemainingCooldownMinutes > 0;

    public string CooldownText => HasCooldown
        ? $"Ready in {RemainingCooldownMinutes} min"
        : "Ready now";

    public string AccessibilityLabel => $"{TypeLabel} slot {Slot}: {Preview}. {CooldownText}.";
}
