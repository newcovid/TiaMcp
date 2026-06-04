using System;
using System.IO;
using System.Text;

namespace TiaMcp
{
    /// <summary>
    /// 极简文件日志：带时间戳，写到 exe 同目录的 logs\ 下。
    /// 现在阶段1还能往 Console 打印；但到阶段5(MCP/stdio)时 stdout 被协议独占，
    /// 那时日志只能走文件——所以现在就把文件日志建好，养成习惯。
    /// </summary>
    internal static class Logger
    {
        private static readonly object _lock = new object();
        public static string LogFilePath { get; private set; }

        public static void Init()
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            LogFilePath = Path.Combine(dir, $"tia-mcp-{DateTime.Now:yyyyMMdd}.log");
        }

        public static void Info(string msg) => Write("INFO", msg);

        public static void Error(string msg, Exception ex)
        {
            string full = ex == null ? msg : msg + Environment.NewLine + ex;
            Write("ERROR", full);
        }

        private static void Write(string level, string msg)
        {
            try
            {
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
                lock (_lock)
                {
                    File.AppendAllText(
                        LogFilePath ?? "tia-mcp.log",
                        line + Environment.NewLine,
                        new UTF8Encoding(false)); // 无 BOM
                }
            }
            catch
            {
                // 日志失败绝不影响主流程
            }
        }
    }
}
