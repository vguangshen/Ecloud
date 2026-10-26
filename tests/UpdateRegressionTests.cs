using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EcloudKeepAlive;

internal static class UpdateRegressionTests
{
    private static int checks;
    private static readonly byte[] OldExe = Encoding.ASCII.GetBytes("MZ previous executable fixture");
    private static readonly byte[] NewExe = Encoding.ASCII.GetBytes("MZ new executable fixture");

    internal static async Task<int> RunAsync()
    {
        Check(ReleaseUpdateClient.ParseVersion("v1.0.2") == new Version(1, 0, 2, 0), "Release versions must compare equal to assembly versions.");
        Check(ReleaseUpdateClient.ParseVersion("v1.0.3") > new Version(1, 0, 2, 0), "Newer releases were not recognized.");
        Throws(() => ReleaseUpdateClient.ParseVersion("v1.0.3-preview"));
        Throws(() => ReleaseUpdateClient.ParseRelease(ReleaseJson(draft: true)));
        Throws(() => ReleaseUpdateClient.ParseRelease(ReleaseJson(prerelease: true)));
        Throws(() => ReleaseUpdateClient.ParseRelease(ReleaseJson(includeChecksum: false)));
        Throws(() => ReleaseUpdateClient.ParseRelease(ReleaseJson(size: ReleaseUpdateClient.MaximumDownloadBytes + 1)));
        Throws(() => ReleaseUpdateClient.ParseChecksum(new string('0', 64) + " other.exe"));

        var directory = Path.Combine(Path.GetTempPath(), "ecloud update tests " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (var handler = new FixtureHandler())
            using (var client = new ReleaseUpdateClient(handler))
            {
                var release = await client.CheckAsync(CancellationToken.None);
                var downloaded = await client.DownloadAsync(release, Path.Combine(directory, "download"), null, CancellationToken.None);
                Check(File.ReadAllBytes(downloaded.ExecutablePath).AsSpan().SequenceEqual(NewExe), "Downloaded bytes changed.");
                Check(handler.Requests.Count == 3, "Unexpected update requests.");
                Check(handler.Requests[1].EndsWith("/releases/assets/12") && handler.Requests[2].EndsWith("/releases/assets/11"), "Assets were not downloaded from the repository API.");
                Check(!handler.AuthorizationSeen, "Public updates must not require a token.");
                var target = Path.Combine(directory, "installed tool.exe");
                File.WriteAllBytes(target, OldExe);
                var activated = false;
                UpdateFileTransaction.Install(downloaded.ExecutablePath, target, downloaded.Sha256, () => activated = true);
                Check(activated && File.ReadAllBytes(target).AsSpan().SequenceEqual(NewExe), "Verified update did not install.");
                Check(File.ReadAllBytes(target + ".previous").AsSpan().SequenceEqual(OldExe), "Previous version backup was not retained.");
                File.WriteAllBytes(target, OldExe);
                Throws(() => UpdateFileTransaction.Install(downloaded.ExecutablePath, target, downloaded.Sha256, () => throw new Exception("startup failed")));
                Check(File.ReadAllBytes(target).AsSpan().SequenceEqual(OldExe), "Failed activation did not restore the old executable.");
                Throws(() => UpdateFileTransaction.Install(downloaded.ExecutablePath, target, new string('0', 64), () => throw new Exception("must not run")));
                Check(File.ReadAllBytes(target).AsSpan().SequenceEqual(OldExe), "Failed verification touched the installed executable.");
                Check(Directory.GetFiles(directory, "*.incoming-*").Length == 0, "An incomplete replacement file was left behind.");
            }

            await FailedDownload(directory, "bad hash", new FixtureHandler { Checksum = new string('0', 64) + "  EcloudKeepAlive.exe" });
            await FailedDownload(directory, "truncated", new FixtureHandler { Body = Encoding.ASCII.GetBytes("MZ") });
            await FailedDownload(directory, "oversized", new FixtureHandler { Body = new byte[1000] });
            await FailedDownload(directory, "invalid checksum", new FixtureHandler { Checksum = "invalid" });
            using (var client = new ReleaseUpdateClient(new FixtureHandler { Status = HttpStatusCode.NotFound }))
                await ThrowsAsync(async () => { await client.CheckAsync(CancellationToken.None); });
            using (var client = new ReleaseUpdateClient(new FixtureHandler()))
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await ThrowsAsync(async () => { await client.CheckAsync(cancellation.Token); });
            }
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS: public release download, checksum/size/cancellation rejection, replacement, backup and rollback.");
        return checks;
    }

    private static async Task FailedDownload(string root, string name, FixtureHandler handler)
    {
        var path = Path.Combine(root, name);
        using (handler)
        using (var client = new ReleaseUpdateClient(handler))
        {
            var release = await client.CheckAsync(CancellationToken.None);
            await ThrowsAsync(async () => { await client.DownloadAsync(release, path, null, CancellationToken.None); });
            Check(!File.Exists(Path.Combine(path, "EcloudKeepAlive.exe")), "A rejected package was retained: " + name);
        }
    }

    private static string ReleaseJson(bool draft = false, bool prerelease = false, bool includeChecksum = true, long? size = null)
    {
        var assets = new List<object>
        {
            new { name = "EcloudKeepAlive.exe", id = 11L, size = size ?? NewExe.Length, url = "https://untrusted.example/file" }
        };
        if (includeChecksum) assets.Add(new { name = "EcloudKeepAlive.exe.sha256", id = 12L, size = 88L, url = "https://untrusted.example/hash" });
        return JsonSerializer.Serialize(new { tag_name = "v1.0.3", draft, prerelease, body = "Release notes", assets });
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        internal readonly List<string> Requests = new List<string>();
        internal bool AuthorizationSeen;
        internal byte[] Body = NewExe;
        internal string Checksum = Convert.ToHexString(SHA256.HashData(NewExe)) + "  EcloudKeepAlive.exe";
        internal HttpStatusCode Status = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = request.RequestUri.AbsoluteUri;
            Requests.Add(url);
            AuthorizationSeen |= request.Headers.Authorization != null;
            if (!url.StartsWith("https://api.github.com/repos/vguangshen/Ecloud/releases/", StringComparison.Ordinal))
                throw new Exception("Update attempted a non-repository URL.");
            var response = new HttpResponseMessage(Status);
            if (url.EndsWith("/latest")) response.Content = new StringContent(ReleaseJson());
            else if (url.EndsWith("/assets/12")) response.Content = new StringContent(Checksum);
            else if (url.EndsWith("/assets/11")) response.Content = new ByteArrayContent(Body);
            else throw new Exception("Unexpected update URL.");
            return Task.FromResult(response);
        }
    }

    private static void Throws(Action action)
    {
        try { action(); }
        catch { checks++; return; }
        throw new Exception("Expected update failure did not occur.");
    }

    private static async Task ThrowsAsync(Func<Task> action)
    {
        try { await action(); }
        catch { checks++; return; }
        throw new Exception("Expected async update failure did not occur.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }
}
