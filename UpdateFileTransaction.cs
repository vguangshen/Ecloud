using System;
using System.IO;
using System.Security.Cryptography;

namespace EcloudKeepAlive
{
    internal static class UpdateFileTransaction
    {
        internal static void VerifyExecutable(string path, string expectedHash)
        {
            using (var stream = File.OpenRead(path))
            {
                if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
                    throw new InvalidDataException("下载文件不是 Windows 可执行文件。");
                stream.Position = 0;
                var actualHash = Convert.ToHexString(SHA256.HashData(stream));
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 校验失败，已取消更新。");
            }
        }

        internal static void Install(string source, string target, string expectedHash, Action activate)
        {
            source = Path.GetFullPath(source);
            target = Path.GetFullPath(target);
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
                throw new InvalidOperationException("更新目标无效。");
            VerifyExecutable(source, expectedHash);
            var incoming = target + ".incoming-" + Guid.NewGuid().ToString("N");
            var backup = target + ".previous";
            var replaced = false;
            try
            {
                File.Copy(source, incoming, false);
                VerifyExecutable(incoming, expectedHash);
                File.Replace(incoming, target, backup, true);
                replaced = true;
                activate();
            }
            catch
            {
                if (replaced)
                {
                    File.Copy(backup, incoming, true);
                    File.Replace(incoming, target, null, true);
                }
                throw;
            }
            finally
            {
                try { File.Delete(incoming); } catch { }
            }
        }
    }
}
