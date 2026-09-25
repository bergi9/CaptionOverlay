using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CaptionOverlay.Core.Settings;

/// <summary>
/// Stores secrets (API keys) encrypted with DPAPI for the current Windows user, in a file separate
/// from settings.json. Values are never logged.
/// </summary>
public sealed class SecretStore
{
    private static readonly byte[] Entropy = "CaptionOverlay.v1"u8.ToArray();
    private readonly string _path;
    private readonly Lock _gate = new();

    public SecretStore(string? path = null)
    {
        _path = path ?? AppPaths.SecretsFile;
    }

    public static string ApiKeyName(string provider) => $"api-key:{provider}";

    public string? Get(string name)
    {
        lock (_gate)
        {
            return ReadAll().GetValueOrDefault(name);
        }
    }

    public void Set(string name, string? value)
    {
        lock (_gate)
        {
            var all = ReadAll();
            if (string.IsNullOrEmpty(value))
            {
                all.Remove(name);
            }
            else
            {
                all[name] = value;
            }
            WriteAll(all);
        }
    }

    private Dictionary<string, string> ReadAll()
    {
        if (!File.Exists(_path))
        {
            return [];
        }
        try
        {
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(plain)) ?? [];
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            // Encrypted by another user/machine or corrupted: behave as empty.
            return [];
        }
    }

    private void WriteAll(Dictionary<string, string> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values));
        byte[] cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        string tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, cipher);
        File.Move(tmp, _path, overwrite: true);
        CryptographicOperations.ZeroMemory(plain);
    }
}
