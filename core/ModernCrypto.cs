using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sodium;

namespace Archimedes.Core;

public class VersionedEnvelope
{
    public int Version { get; set; } = 2;
    public string DeviceId { get; set; } = "";
    public string OperationId { get; set; } = "";
    public long Timestamp { get; set; }
    public string Nonce { get; set; } = "";
    public string Ciphertext { get; set; } = "";
    public string EphemeralPublicKey { get; set; } = "";
}

public class KeyPairInfo
{
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();
    public byte[] PrivateKey { get; set; } = Array.Empty<byte>();
}

public static class ModernCrypto
{
    public static KeyPairInfo GenerateKeyPair()
    {
        var keyPair = PublicKeyBox.GenerateKeyPair();
        return new KeyPairInfo
        {
            PublicKey = keyPair.PublicKey,
            PrivateKey = keyPair.PrivateKey
        };
    }

    public static VersionedEnvelope Encrypt(
        string plaintext, 
        byte[] recipientPublicKey, 
        string deviceId, 
        string operationId)
    {
        var ephemeralKeyPair = PublicKeyBox.GenerateKeyPair();
        var nonce = SecretBox.GenerateNonce();
        var sharedSecret = ScalarMult.Mult(ephemeralKeyPair.PrivateKey, recipientPublicKey);
        var key = GenericHash.Hash(sharedSecret, null, 32);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = SecretBox.Create(plaintextBytes, nonce, key);

        return new VersionedEnvelope
        {
            Version = 2,
            DeviceId = deviceId,
            OperationId = operationId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Nonce = Convert.ToBase64String(nonce),
            Ciphertext = Convert.ToBase64String(ciphertext),
            EphemeralPublicKey = Convert.ToBase64String(ephemeralKeyPair.PublicKey)
        };
    }

    public static string Decrypt(VersionedEnvelope envelope, byte[] recipientPrivateKey)
    {
        if (envelope.Version != 2)
            throw new InvalidOperationException($"Unsupported envelope version: {envelope.Version}");

        var ephemeralPublicKey = Convert.FromBase64String(envelope.EphemeralPublicKey);
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var sharedSecret = ScalarMult.Mult(recipientPrivateKey, ephemeralPublicKey);
        var key = GenericHash.Hash(sharedSecret, null, 32);
        var plaintextBytes = SecretBox.Open(ciphertext, nonce, key);

        return Encoding.UTF8.GetString(plaintextBytes);
    }

    public static string EncryptToJson(
        string plaintext, 
        byte[] recipientPublicKey, 
        string deviceId, 
        string operationId)
    {
        var envelope = Encrypt(plaintext, recipientPublicKey, deviceId, operationId);
        return JsonSerializer.Serialize(envelope);
    }

    public static string DecryptFromJson(string envelopeJson, byte[] recipientPrivateKey)
    {
        var envelope = JsonSerializer.Deserialize<VersionedEnvelope>(envelopeJson);
        if (envelope == null)
            throw new InvalidOperationException("Invalid envelope JSON");
        return Decrypt(envelope, recipientPrivateKey);
    }

    public static bool VerifyEnvelope(VersionedEnvelope envelope, TimeSpan maxAge)
    {
        if (envelope.Version != 2)
            return false;

        if (string.IsNullOrEmpty(envelope.DeviceId))
            return false;

        if (string.IsNullOrEmpty(envelope.OperationId))
            return false;

        var envelopeTime = DateTimeOffset.FromUnixTimeMilliseconds(envelope.Timestamp);
        var age = DateTimeOffset.UtcNow - envelopeTime;
        if (age > maxAge || age < TimeSpan.Zero)
            return false;

        return true;
    }
}

public class DeviceKeyManager
{
    private readonly string _keyPath;
    private KeyPairInfo? _keyPair;

    public DeviceKeyManager(string? basePath = null)
    {
        var dir = basePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Archimedes"
        );
        Directory.CreateDirectory(dir);
        _keyPath = Path.Combine(dir, "device_keys.enc");
    }

    public KeyPairInfo GetOrCreateKeyPair()
    {
        if (_keyPair != null)
            return _keyPair;

        if (File.Exists(_keyPath))
        {
            _keyPair = LoadKeys();
            ArchLogger.LogInfo("Loaded device keys from DPAPI-protected storage");
        }
        else
        {
            _keyPair = ModernCrypto.GenerateKeyPair();
            SaveKeys(_keyPair);
            ArchLogger.LogInfo("Generated new device keys (X25519), protected with DPAPI");
        }

        return _keyPair;
    }

    private void SaveKeys(KeyPairInfo keys)
    {
        var combined = new byte[64];
        Buffer.BlockCopy(keys.PublicKey, 0, combined, 0, 32);
        Buffer.BlockCopy(keys.PrivateKey, 0, combined, 32, 32);

        // Phase 23: cross-platform key protection
        var protected_ = OsProtect(combined);
        File.WriteAllBytes(_keyPath, protected_);
        OsRestrictFilePermissions(_keyPath);
    }

    private KeyPairInfo LoadKeys()
    {
        // Phase 23: cross-platform key protection
        var protected_ = File.ReadAllBytes(_keyPath);
        var combined   = OsUnprotect(protected_);

        return new KeyPairInfo
        {
            PublicKey = combined[..32],
            PrivateKey = combined[32..]
        };
    }

    // ── Phase 23: cross-platform key protection ───────────────────────────────
    // Windows: DPAPI — OS-managed, user-scoped encryption
    // Linux:   raw bytes + chmod 600 — file permission is the security layer
    private static byte[] OsProtect(byte[] data) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser)
            : data;

    private static byte[] OsUnprotect(byte[] data) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser)
            : data;

    private static void OsRestrictFilePermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName  = "chmod",
                Arguments = $"600 \"{path}\"",
                UseShellExecute = false
            })?.WaitForExit();
        }
        catch { /* best-effort */ }
    }

    public string GetPublicKeyBase64()
    {
        var keys = GetOrCreateKeyPair();
        return Convert.ToBase64String(keys.PublicKey);
    }
}
