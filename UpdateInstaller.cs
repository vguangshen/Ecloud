using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EcloudKeepAlive
{
    internal sealed class UpdateInstallRequest
    {
        public int ParentProcessId { get; set; }
        public long ParentStartTimeUtcTicks { get; set; }
        public string OriginalUserSid { get; set; }
        public string TargetPath { get; set; }
        public string ExpectedHash { get; set; }
        public string LogPath { get; set; }
        public bool ResumeKeepAlive { get; set; }
        public bool RefreshStartupTask { get; set; }
    }

    internal static class UpdateInstaller
    {
        internal static string StagingRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EcloudKeepAlive", "updates"); }
        }

        internal static string CreateStagingDirectory()
        {
            Directory.CreateDirectory(StagingRoot);
            foreach (var directory in Directory.GetDirectories(StagingRoot, "update-*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-7)) Directory.Delete(directory, true);
                }
                catch { }
            }
            var result = Path.Combine(StagingRoot, "update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(result);
            return result;
        }

        internal static string WriteRequest(string directory, UpdateInstallRequest request)
        {
            var path = Path.Combine(directory, "install-request.json");
            File.WriteAllText(path, JsonSerializer.Serialize(request));
            return path;
        }

        internal static Process StartHelper(string executable, string requestPath)
        {
            var info = new ProcessStartInfo
            {
                FileName = executable, WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = true, Verb = "runas"
            };
            info.ArgumentList.Add("--apply-update");
            info.ArgumentList.Add(requestPath);
            return Process.Start(info) ?? throw new InvalidOperationException("无法启动更新器。");
        }

        internal static async Task WaitForHandoffAsync(Process helper, string requestPath, CancellationToken cancellationToken)
        {
            var timeout = Stopwatch.StartNew();
            try
            {
                while (!File.Exists(requestPath + ".ready"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (File.Exists(requestPath + ".error")) throw new InvalidOperationException(File.ReadAllText(requestPath + ".error"));
                    if (helper.HasExited) throw new InvalidOperationException("更新器未就绪，保活继续运行。");
                    if (timeout.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("更新器准备超时，保活继续运行。");
                    await Task.Delay(100, cancellationToken);
                }
            }
            catch
            {
                try { File.WriteAllText(requestPath + ".cancel", "cancelled"); } catch { }
                throw;
            }
        }

        internal static void RunHelper(string requestPath, Action<string> refreshStartupTask)
        {
            if (requestPath == null) throw new InvalidOperationException("缺少更新请求。");
            requestPath = Path.GetFullPath(requestPath);
            if (!requestPath.StartsWith(Path.GetFullPath(StagingRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || new FileInfo(requestPath).Length > 65536)
                throw new InvalidOperationException("更新请求路径无效。");
            var request = JsonSerializer.Deserialize<UpdateInstallRequest>(File.ReadAllText(requestPath))
                ?? throw new InvalidOperationException("更新请求无效。");
            using (var identity = WindowsIdentity.GetCurrent())
            {
                if (identity.User?.Value != request.OriginalUserSid)
                    throw new InvalidOperationException("请使用当前 Windows 用户授权更新，不能切换为其他管理员账户。原保活仍在运行。");
            }
            var source = Environment.ProcessPath;
            var directory = Path.GetDirectoryName(requestPath);
            if (!string.Equals(Path.GetFullPath(source), Path.Combine(directory, ReleaseUpdateClient.ExecutableName), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新器路径无效。");
            var target = Path.GetFullPath(request.TargetPath);
            if (!Path.IsPathFullyQualified(request.TargetPath) || !File.Exists(target))
                throw new InvalidOperationException("原程序路径无效。");
            UpdateFileTransaction.VerifyExecutable(source, request.ExpectedHash);
            Process parent = null;
            var parentExited = false;
            try
            {
                try { parent = Process.GetProcessById(request.ParentProcessId); }
                catch (ArgumentException) { parentExited = true; }
                if (parent != null)
                {
                    if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStartTimeUtcTicks
                        || !string.Equals(Path.GetFullPath(parent.MainModule.FileName), target, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("原程序进程信息不匹配。");
                }
                File.WriteAllText(requestPath + ".ready", "ready");
                if (parent != null)
                {
                    if (!parent.WaitForExit(60000)) throw new TimeoutException("原程序尚未退出，未替换文件。");
                    parentExited = true;
                }
                if (File.Exists(requestPath + ".cancel")) throw new OperationCanceledException("更新已取消。");
                UpdateFileTransaction.Install(source, target, request.ExpectedHash, () =>
                {
                    if (request.RefreshStartupTask) refreshStartupTask(target);
                    StartAndConfirm(target, request.ResumeKeepAlive, Path.Combine(directory, "started.ready"));
                });
                AppendLog(request.LogPath, "在线更新完成，已恢复原保活状态。");
            }
            catch (Exception ex)
            {
                AppendLog(request.LogPath, "在线更新失败：" + ex.Message);
                if (parentExited)
                {
                    try { using (var restarted = StartApplication(target, request.ResumeKeepAlive, null)) { } }
                    catch (Exception restartError) { AppendLog(request.LogPath, "恢复旧版启动失败：" + restartError.Message); }
                }
                throw;
            }
            finally { parent?.Dispose(); }
        }

        private static void StartAndConfirm(string executable, bool resumeKeepAlive, string readyPath)
        {
            File.Delete(readyPath);
            using (var process = StartApplication(executable, resumeKeepAlive, readyPath))
            {
                var timeout = Stopwatch.StartNew();
                try
                {
                    while (!File.Exists(readyPath))
                    {
                        if (process.HasExited) throw new InvalidOperationException("新版启动失败，将恢复旧版。");
                        if (timeout.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("新版启动确认超时，将恢复旧版。");
                        Thread.Sleep(100);
                    }
                }
                catch
                {
                    try
                    {
                        if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); }
                    }
                    catch { }
                    throw;
                }
            }
        }

        private static Process StartApplication(string executable, bool resumeKeepAlive, string readyPath)
        {
            var info = new ProcessStartInfo
            {
                FileName = executable, WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false
            };
            if (resumeKeepAlive) info.ArgumentList.Add("--resume-keepalive");
            if (readyPath != null) { info.ArgumentList.Add("--update-ready"); info.ArgumentList.Add(readyPath); }
            return Process.Start(info) ?? throw new InvalidOperationException("无法启动程序。");
        }

        internal static void SignalReady(string path)
        {
            path = Path.GetFullPath(path);
            if (Path.GetFileName(path) != "started.ready"
                || !path.StartsWith(Path.GetFullPath(StagingRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新启动确认路径无效。");
            File.WriteAllText(path, Environment.ProcessId.ToString());
        }

        internal static void ReportHelperError(string requestPath, string message)
        {
            try
            {
                if (requestPath != null && Path.GetFullPath(requestPath).StartsWith(Path.GetFullPath(StagingRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(requestPath + ".error", message);
            }
            catch { }
        }

        private static void AppendLog(string path, string message)
        {
            try { File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine); }
            catch { }
        }
    }
}
