using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PandanciClone
{
    internal static class GoogleTranslationCredentials
    {
        private static string KeyPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PandanciClone", "google-translation-key.bin");
            }
        }

        public static string LoadApiKey()
        {
            if (File.Exists(KeyPath))
            {
                try
                {
                    return Decode(File.ReadAllBytes(KeyPath));
                }
                catch (Exception ex)
                {
                    if (!(ex is CryptographicException) && !(ex is IOException) && !(ex is UnauthorizedAccessException)) throw;
                    throw new InvalidOperationException("无法读取 Google API Key，请在“设置 → Google 官方接口...”重新保存。不要从其他 Windows 用户复制密钥文件。");
                }
            }
            return (Environment.GetEnvironmentVariable("PANDANCI_GOOGLE_TRANSLATE_API_KEY") ?? "").Trim();
        }

        public static void SaveApiKey(string key)
        {
            key = (key ?? "").Trim();
            if (key.Length == 0)
            {
                if (File.Exists(KeyPath)) File.Delete(KeyPath);
                return;
            }
            byte[] encrypted = Encode(key);
            Directory.CreateDirectory(Path.GetDirectoryName(KeyPath));
            File.WriteAllBytes(KeyPath, encrypted);
        }

        internal static byte[] Encode(string key)
        {
            return ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser);
        }

        internal static string Decode(byte[] encrypted)
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser)).Trim();
        }
    }
}
