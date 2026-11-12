namespace VrcCompanion.Models;

/// <summary>
/// Result of GET /auth/user. VRChat returns either the signed-in user or a
/// list of available second-factor methods.
/// </summary>
public sealed class LoginResult
{
    public VrcUser? User { get; init; }

    public IReadOnlyList<string> TwoFactorMethods { get; init; } = Array.Empty<string>();

    public bool RequiresTwoFactor => User is null && TwoFactorMethods.Count > 0;

    public static LoginResult SignedIn(VrcUser user) => new() { User = user };

    public static LoginResult NeedsTwoFactor(IReadOnlyList<string> methods) =>
        new() { TwoFactorMethods = methods };
}
