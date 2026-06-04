using System;
using System.IO;
using System.Text;

namespace TiaMcp
{
    /// <summary>
    /// 跟本机 E-SafeNet 透明加密 + 定期全盘扫描打交道的文件辅助。
    /// 铁律：一律走 %TEMP%(加密豁免区)，写后验证、读后校验不是密文、即用即删。
    /// 供 Commands / CrossRef 共用，避免重复。
    /// </summary>
    internal static class IoUtil
    {
        /// <summary>
        /// 在 %TEMP% 根目录下生成唯一文件路径（不创建文件）。
        /// 关键：直接用 %TEMP% 根（E-SafeNet 豁免区），不用固定子目录——
        /// 实测固定子目录(%TEMP%\TiaMcp)持久存在后会被本机 E-SafeNet 加访问保护(连本进程都拒写)。
        /// 每个临时文件独立、即用即删，避免共享目录被锁。
        /// </summary>
        public static string NewTempFile(string ext)
        {
            return Path.Combine(Path.GetTempPath(), "TiaMcp_" + Guid.NewGuid().ToString("N") + ext);
        }

        /// <summary>
        /// 判断字节是否像密文（被 E-SafeNet 加密）。
        /// 主信号：实测加密信封头前若干字节含 ASCII "E-SafeNet"/"LOCK"。
        /// 次信号：明文 .scl/.awl/.xml(UTF-8) 不会含 NUL 字节；密文常含 NUL。
        /// </summary>
        public static bool LooksEncrypted(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return false;
            int n = Math.Min(64, bytes.Length);
            if (Encoding.ASCII.GetString(bytes, 0, n).IndexOf("SafeNet", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            int probe = Math.Min(512, bytes.Length);
            for (int i = 0; i < probe; i++) if (bytes[i] == 0) return true;
            return false;
        }

        /// <summary>按 UTF-8 解码并去掉可能的 BOM。</summary>
        public static string DecodeUtf8StripBom(byte[] b)
        {
            int start = (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) ? 3 : 0;
            return new UTF8Encoding(false).GetString(b, start, b.Length - start);
        }

        /// <summary>读临时文件文本；若读到密文则抛异常。</summary>
        public static string ReadPlaintext(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (LooksEncrypted(b))
                throw new IOException("临时文件是密文（疑似全盘扫描命中 %TEMP%）: " + path);
            return DecodeUtf8StripBom(b);
        }

        /// <summary>
        /// 把文本写到 %TEMP% 并立刻读回校验"仍是我们写的明文"，最多重试 3 次，返回路径。
        /// 用 UTF-8 带 BOM，与 TIA 自己导出的格式一致。
        /// </summary>
        public static string WriteTempPlaintextVerified(string text, string ext)
        {
            byte[] bytes = new UTF8Encoding(true).GetBytes(text);
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                string path = NewTempFile(ext);
                File.WriteAllBytes(path, bytes);
                byte[] back = File.ReadAllBytes(path);
                if (!LooksEncrypted(back) && BytesEqual(back, bytes)) return path;
                try { File.Delete(path); } catch { }
                System.Threading.Thread.Sleep(50);
            }
            throw new IOException("临时文件写出后被加密/篡改（疑似全盘扫描），重试多次仍失败。");
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
