using PureRDP.Services;
using System.Net;
using System.Text;

int passed = 0;
void Assert(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
UpdateCheckResult Parse(string tag) => GitHubUpdateService.ParseRelease(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { tag_name = tag, draft = false, prerelease = false })), new Version(2, 0, 0, 0));
Assert(Parse("v2.0.1").Status == UpdateCheckStatus.UpdateAvailable, "newer version");
Assert(Parse("2.0.0").Status == UpdateCheckStatus.NoNewerVersion, "same version normalized");
Assert(Parse("v1.9.9").Status == UpdateCheckStatus.NoNewerVersion, "older version");
Assert(Parse("v2.0.1+build").Status == UpdateCheckStatus.UpdateAvailable, "build metadata");
foreach (var tag in new[] { "v2.0.1-beta", "vv2.0.1", "latest", "", "2.0" }) Assert(Parse(tag).Status == UpdateCheckStatus.InvalidRelease, "invalid tag " + tag);
foreach (var json in new[] { "{}", "[]", "broken", "{\"tag_name\":123}" }) Assert(GitHubUpdateService.ParseRelease(Encoding.UTF8.GetBytes(json), new Version(2, 0, 0)).Status == UpdateCheckStatus.InvalidRelease, "malformed payload " + json);
Assert(GitHubUpdateService.ParseRelease(Encoding.UTF8.GetBytes("{\"draft\":true}"), new Version(2, 0, 0)).Status == UpdateCheckStatus.NoPublicRelease, "draft excluded");
Assert(GitHubUpdateService.ParseRelease(Encoding.UTF8.GetBytes("{\"prerelease\":true}"), new Version(2, 0, 0)).Status == UpdateCheckStatus.NoPublicRelease, "preview excluded");
foreach (var pair in new[] { (404, UpdateCheckStatus.NoPublicRelease), (429, UpdateCheckStatus.RateLimited), (500, UpdateCheckStatus.NetworkError), (403, UpdateCheckStatus.RateLimited), (200, UpdateCheckStatus.UpdateAvailable) })
{
    using var service = new GitHubUpdateService(new FakeHandler(request => {
        Assert(request.RequestUri!.AbsoluteUri == GitHubUpdateService.LatestReleaseApi, "fixed API endpoint");
        Assert(request.Headers.UserAgent.ToString().StartsWith("PureRDP/"), "user agent");
        Assert(request.Headers.Authorization == null, "no credentials sent");
        var response = new HttpResponseMessage((HttpStatusCode)pair.Item1) { Content = new StringContent("{\"tag_name\":\"v2.0.1\"}") };
        if (pair.Item1 == 403) response.Headers.Add("X-RateLimit-Remaining", "0");
        return response;
    }));
    Assert((await service.CheckAsync(default)).Status == pair.Item2, "HTTP " + pair.Item1);
}
using (var service = new GitHubUpdateService(new FakeHandler(_ => throw new HttpRequestException()))) Assert((await service.CheckAsync(default)).Status == UpdateCheckStatus.NetworkError, "network failure");
using (var service = new GitHubUpdateService(new FakeHandler(_ => throw new OperationCanceledException()))) Assert((await service.CheckAsync(default)).Status == UpdateCheckStatus.NetworkError, "timeout");
using (var service = new GitHubUpdateService(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 1024 * 1024 + 1)) }))) Assert((await service.CheckAsync(default)).Status == UpdateCheckStatus.InvalidRelease, "bounded response");
Console.WriteLine($"{passed} checks passed");

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
}
