using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EcloudKeepAlive
{
    internal sealed class ReleaseUpdateInfo
    {
        internal Version Version { get; set; }
        internal string Tag { get; set; }
        internal string Notes { get; set; }
        internal long ExecutableAssetId { get; set; }
        internal long ChecksumAssetId { get; set; }
        internal long ExecutableSize { get; set; }
    }

    internal sealed class DownloadedUpdate
    {
        internal string ExecutablePath { get; set; }
        internal string Sha256 { get; set; }
    }

    internal sealed class ReleaseUpdateClient : IDisposable
    {
        internal const string Repository = "vguangshen/Ecloud";
        internal const string ExecutableName = "EcloudKeepAlive.exe";
        internal const long MaximumDownloadBytes = 256L * 1024 * 1024;
        private const string ApiRoot = "https://api.github.com/repos/" + Repository;
        private readonly HttpClient client;

        internal ReleaseUpdateClient(HttpMessageHandler handler = null)
        {
            client = handler == null ? new HttpClient() : new HttpClient(handler);
            client.Timeout = TimeSpan.FromMinutes(15);
        }

        internal async Task<ReleaseUpdateInfo> CheckAsync(CancellationToken cancellationToken)
        {
            using (var request = CreateRequest(ApiRoot + "/releases/latest", false))
            using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                EnsureSuccess(response);
                var bytes = await ReadBoundedAsync(response, 2 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                return ParseRelease(Encoding.UTF8.GetString(bytes));
            }
        }

        internal static ReleaseUpdateInfo ParseRelease(string json)
        {
            using (var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 }))
            {
                var root = document.RootElement;
                if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
                    throw new InvalidDataException("更新接口返回了未正式发布的版本。");
                var tag = root.GetProperty("tag_name").GetString();
                var assets = root.GetProperty("assets").EnumerateArray().ToArray();
                var executables = assets.Where(a => a.GetProperty("name").GetString() == ExecutableName).ToArray();
                var checksums = assets.Where(a => a.GetProperty("name").GetString() == ExecutableName + ".sha256").ToArray();
                if (executables.Length != 1 || checksums.Length != 1)
                    throw new InvalidDataException("Release 缺少唯一的 EXE 或 SHA-256 校验文件。");
                var size = executables[0].GetProperty("size").GetInt64();
                var executableId = executables[0].GetProperty("id").GetInt64();
                var checksumId = checksums[0].GetProperty("id").GetInt64();
                if (size < 2 || size > MaximumDownloadBytes || executableId <= 0 || checksumId <= 0)
                    throw new InvalidDataException("Release 文件信息无效。");
                return new ReleaseUpdateInfo
                {
                    Version = ParseVersion(tag), Tag = tag,
                    Notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                    ExecutableAssetId = executableId, ChecksumAssetId = checksumId, ExecutableSize = size
                };
            }
        }

        internal static Version ParseVersion(string text)
        {
            if (text == null || !Regex.IsMatch(text, @"^[vV]?\d+\.\d+\.\d+(\.\d+)?$"))
                throw new InvalidDataException("Release 版本号格式无效。");
            if (!Version.TryParse(text.TrimStart('v', 'V'), out var version))
                throw new InvalidDataException("Release 版本号格式无效。");
            return NormalizeVersion(version);
        }

        internal static Version NormalizeVersion(Version version)
        {
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        }

        internal async Task<DownloadedUpdate> DownloadAsync(ReleaseUpdateInfo release, string stagingDirectory,
            IProgress<int> progress, CancellationToken cancellationToken)
        {
            byte[] checksum;
            using (var request = CreateRequest(AssetUrl(release.ChecksumAssetId), true))
            using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                EnsureSuccess(response);
                checksum = await ReadBoundedAsync(response, 4096, cancellationToken).ConfigureAwait(false);
            }
            var expectedHash = ParseChecksum(Encoding.ASCII.GetString(checksum));
            Directory.CreateDirectory(stagingDirectory);
            var destination = Path.Combine(stagingDirectory, ExecutableName);
            try
            {
                using (var request = CreateRequest(AssetUrl(release.ExecutableAssetId), true))
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    EnsureSuccess(response);
                    using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        long total = 0;
                        int count;
                        int lastPercent = -1;
                        while ((count = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
                        {
                            total += count;
                            if (total > release.ExecutableSize || total > MaximumDownloadBytes)
                                throw new InvalidDataException("下载文件超过 Release 声明的大小。");
                            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                            var percent = (int)(total * 100 / release.ExecutableSize);
                            if (percent != lastPercent) { progress?.Report(percent); lastPercent = percent; }
                        }
                        if (total != release.ExecutableSize) throw new InvalidDataException("下载不完整，请重试。");
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                UpdateFileTransaction.VerifyExecutable(destination, expectedHash);
                return new DownloadedUpdate { ExecutablePath = destination, Sha256 = expectedHash };
            }
            catch
            {
                try { File.Delete(destination); } catch { }
                throw;
            }
        }

        internal static string ParseChecksum(string text)
        {
            var parts = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !Regex.IsMatch(parts[0], "^[0-9a-fA-F]{64}$") || parts[1].TrimStart('*') != ExecutableName)
                throw new InvalidDataException("SHA-256 校验文件格式或文件名无效。");
            return parts[0].ToLowerInvariant();
        }

        private static string AssetUrl(long id)
        {
            // Download only assets belonging to the configured repository.
            if (id <= 0) throw new InvalidDataException("Release 文件编号无效。");
            return ApiRoot + "/releases/assets/" + id;
        }

        private static HttpRequestMessage CreateRequest(string url, bool binary)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("EcloudKeepAlive-Updater/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(binary ? "application/octet-stream" : "application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            return request;
        }

        private static void EnsureSuccess(HttpResponseMessage response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("更新接口要求登录，请确认发布仓库保持公开。");
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException("尚未找到公开 Release，请稍后重试。");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new InvalidOperationException("GitHub 暂时限制访问，请稍后重试。");
            response.EnsureSuccessStatusCode();
        }

        private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken cancellationToken)
        {
            using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192];
                int count;
                while ((count = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
                {
                    if (output.Length + count > maximum) throw new InvalidDataException("更新接口响应过大。");
                    output.Write(buffer, 0, count);
                }
                return output.ToArray();
            }
        }

        public void Dispose() { client.Dispose(); }
    }
}
