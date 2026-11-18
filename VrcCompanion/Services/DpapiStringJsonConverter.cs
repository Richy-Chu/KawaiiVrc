using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VrcCompanion.Services;

/// <summary>
/// Encrypts selected settings strings for the current Windows user. Plaintext
/// values from older settings files are still readable so they can be migrated
/// and rewritten in encrypted form.
/// </summary>
internal sealed class DpapiStringJsonConverter : JsonConverter<string>
{
    internal const string Prefix = "dpapi:v1:";

    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("VrcCompanion.ChatboxTokens.v1");

    public override string Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var value = reader.GetString() ?? string.Empty;
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            var encrypted = Convert.FromBase64String(value[Prefix.Length..]);
            var payload = ProtectedData.Unprotect(
                encrypted,
                Entropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(payload);
        }
        catch
        {
            return string.Empty;
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        string value,
        JsonSerializerOptions options)
    {
        if (string.IsNullOrEmpty(value))
        {
            writer.WriteStringValue(string.Empty);
            return;
        }

        try
        {
            var payload = Encoding.UTF8.GetBytes(value);
            var encrypted = ProtectedData.Protect(
                payload,
                Entropy,
                DataProtectionScope.CurrentUser);
            writer.WriteStringValue(Prefix + Convert.ToBase64String(encrypted));
        }
        catch
        {
            // Never fall back to writing a token in plain text.
            writer.WriteStringValue(string.Empty);
        }
    }
}