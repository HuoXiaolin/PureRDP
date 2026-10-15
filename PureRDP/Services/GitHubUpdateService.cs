using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PureRDP.Services;

internal enum UpdateCheckStatus
{
    UpdateAvailable, NoNewerVersion, NoPublicRelease, RateLimited, NetworkError, InvalidRelease,
}

internal sealed record UpdateCheckResult(UpdateCheckStatus Status, string Message, string? Tag = null);

internal sealed class GitHubUpdateService : IDisposable
{
    internal const string Author = "HuoXiaolin";
    internal const string ProjectUrl = "https://github.com/HuoXiaolin/PureRDP";
    internal const string ReleasesUrl = ProjectUrl + "/releases";
    internal const string LatestReleaseApi = "https://api.github.com/repos/HuoXiaolin/PureRDP/releases/latest";
    private const int MaximumResponseBytes = 1024 * 1024;
    private readonly HttpClient _client;
    internal static Version CurrentVersion => typeof(GitHubUpdateService).Assembly.GetName().Version ?? new Version(2, 0, 0, 0);
    internal static string VersionText => CurrentVersion.Revision > 0 ? CurrentVersion.ToString(4) : CurrentVersion.ToString(3);

    internal GitHubUpdateService(HttpMessageHandler? handler = null)
    {
        _client = handler == null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = TimeSpan.FromSeconds(12);
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PureRDP", VersionText));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
    }

    internal async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetAsync(LatestReleaseApi,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(UpdateCheckStatus.NoPublicRelease, "暂未找到公开正式版本。项目可能尚未公开，或还没有发布 GitHub Release。");
            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var limits) &&
                string.Join(",", limits) == "0")
                return new(UpdateCheckStatus.RateLimited, "GitHub 请求频率受限，请稍后重试，或直接打开版本页面。");
            if (!response.IsSuccessStatusCode)
                return new(UpdateCheckStatus.NetworkError, $"检查更新失败（HTTP {(int)response.StatusCode}），请稍后重试或查看版本页面。");
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                return new(UpdateCheckStatus.InvalidRelease, "版本信息过大，无法安全读取，请直接查看版本页面。");
            using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await content.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + count > MaximumResponseBytes)
                    return new(UpdateCheckStatus.InvalidRelease, "版本信息过大，无法安全读取，请直接查看版本页面。");
                buffer.Write(chunk, 0, count);
            }
            return ParseRelease(buffer.ToArray(), CurrentVersion);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(UpdateCheckStatus.NetworkError, "检查更新超时，请检查网络或代理后重试。"); }
        catch (HttpRequestException)
        { return new(UpdateCheckStatus.NetworkError, "无法连接 GitHub，请检查网络或代理后重试。"); }
        catch (IOException)
        { return new(UpdateCheckStatus.NetworkError, "读取更新信息失败，请稍后重试。"); }
    }

    internal static UpdateCheckResult ParseRelease(byte[] payload, Version current)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
                root.TryGetProperty("prerelease", out var preview) && preview.GetBoolean())
                return new(UpdateCheckStatus.NoPublicRelease, "仅检查公开正式版本，草稿与预发行版不作为更新。");
            var tag = root.GetProperty("tag_name").GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(tag) || tag.Length > 80)
                return new(UpdateCheckStatus.InvalidRelease, "发布版本号缺失或无效，请直接查看版本页面。");
            var versionText = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
            var metadata = versionText.IndexOf('+');
            if (metadata >= 0) versionText = versionText[..metadata];
            if (!Version.TryParse(versionText, out var latest) || latest.Build < 0)
                return new(UpdateCheckStatus.InvalidRelease, "发布标签无法按版本号比较，请直接查看版本页面。", tag);
            static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
            return Normalize(latest) > Normalize(current)
                ? new(UpdateCheckStatus.UpdateAvailable, $"发现新版本 {tag}。点击“前往更新页面”查看说明并自行下载。", tag)
                : new(UpdateCheckStatus.NoNewerVersion, $"当前没有更高版本（公开正式版 {tag}）。同版本重新编译不会按版本号识别。", tag);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        { return new(UpdateCheckStatus.InvalidRelease, "GitHub 返回的版本信息格式不正确，请直接查看版本页面。"); }
    }

    public void Dispose() => _client.Dispose();
}
