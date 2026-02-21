using System.Security.Cryptography;
using System.Text;

namespace Archimedes.Core;

public static class Crypto
{
    public static (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ExportRSAPublicKey(), rsa.ExportRSAPrivateKey());
    }

    public static byte[] Encrypt(byte[] plaintext, byte[] publicKey)
    {
        using var rsa = RSA.Create();
        rsa.ImportRSAPublicKey(publicKey, out _);
        return rsa.Encrypt(plaintext, RSAEncryptionPadding.OaepSHA256);
    }

    public static byte[] Decrypt(byte[] ciphertext, byte[] privateKey)
    {
        using var rsa = RSA.Create();
        rsa.ImportRSAPrivateKey(privateKey, out _);
        return rsa.Decrypt(ciphertext, RSAEncryptionPadding.OaepSHA256);
    }

    public static string EncryptBase64(string plaintext, byte[] publicKey) =>
        Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(plaintext), publicKey));

    public static string DecryptBase64(string ciphertextBase64, byte[] privateKey) =>
        Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(ciphertextBase64), privateKey));
}
