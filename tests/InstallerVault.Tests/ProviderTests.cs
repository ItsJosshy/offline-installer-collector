using System.Net;
using System.Text.Json;
using InstallerVault;
using InstallerVault.Providers;

namespace InstallerVault.Tests;

public class ProviderTests
{
    [Fact]
    public async Task Github_filters_prereleases_and_source_archives_and_keeps_variant_identity()
    {
        var http = new FixtureHttp(req => FixtureHttp.Json("""
        [{"id":3,"tag_name":"v3.0","prerelease":true,"assets":[{"id":30,"name":"Tool-3.0-win-x64.exe","browser_download_url":"https://example.org/3.exe"}]},
         {"id":2,"tag_name":"v2.0","prerelease":false,"zipball_url":"https://example.org/source.zip","assets":[{"id":20,"name":"Tool-2.0-win-x64.exe","browser_download_url":"https://example.org/2.exe"},{"id":21,"name":"Tool-2.0-linux-arm64.AppImage","browser_download_url":"https://example.org/2.AppImage"}]},
         {"id":1,"tag_name":"v1.0","prerelease":false,"assets":[{"id":10,"name":"Tool-1.0-win-x64.exe","browser_download_url":"https://example.org/1.exe"}]}]
        """));
        var provider = new GitHubProvider(new ProviderHttp(http, new VaultOptions()));
        var result = await provider.ResolveAsync(new("https://github.com/acme/tool", AssetFilter:"*win*.exe"), default);
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal("v2.0", artifact.Version);
        Assert.Equal("windows", artifact.Os);
        Assert.Equal("x64", artifact.Architecture);
        var preview = await provider.ResolveAsync(new("acme/tool", "prerelease", "*win*.exe"), default);
        Assert.Equal(artifact.Key, Assert.Single(preview.Artifacts).Key);
        Assert.Equal("v3.0", preview.Artifacts[0].Version);
    }

    [Fact]
    public async Task Github_follows_bounded_pagination_and_cache_revalidates_with_etag()
    {
        var requests = 0;
        var http = new FixtureHttp(req => {
            requests++;
            if (req.Headers.IfNoneMatch.Count > 0) return new(HttpStatusCode.NotModified);
            var response = FixtureHttp.Json(req.RequestUri!.Query.Contains("page=2") ? """[{"id":1,"tag_name":"1.0","assets":[{"id":1,"name":"app.msi","browser_download_url":"https://example.org/app.msi"}]}]""" : "[]");
            response.Headers.ETag = new("\"fixture\"");
            if (!req.RequestUri.Query.Contains("page=2")) response.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/repos/a/b/releases?per_page=100&page=2>; rel=\"next\"");
            return response;
        });
        var provider = new GitHubProvider(new ProviderHttp(http, new VaultOptions(), TimeSpan.Zero));
        Assert.Single((await provider.ResolveAsync(new("a/b"), default)).Artifacts);
        Assert.Single((await provider.ResolveAsync(new("a/b"), default)).Artifacts);
        Assert.Equal(4, requests);
    }

    [Fact]
    public async Task Rate_limit_is_actionable_and_not_served_as_fresh_cache()
    {
        var http = new FixtureHttp(_ => { var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests); r.Headers.TryAddWithoutValidation("Retry-After", "60"); return r; });
        var error = await Assert.ThrowsAsync<ProviderException>(() => new GitHubProvider(new ProviderHttp(http,new())).ResolveAsync(new("a/b"), default));
        Assert.Contains("rate limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Direct_fixed_url_does_not_invent_a_version()
    {
        var http = new FixtureHttp(req => { Assert.Equal(HttpMethod.Head, req.Method); var r = new HttpResponseMessage(HttpStatusCode.OK); r.Headers.ETag = new("\"build-a\""); return r; });
        var result = await new DirectProvider(new ProviderHttp(http,new())).ResolveAsync(new("https://example.org/Tool.exe"), default);
        Assert.Equal("unknown", Assert.Single(result.Artifacts).Version);
        Assert.Contains("version", result.Warning!);
    }

    [Fact]
    public async Task Direct_metadata_preserves_explicit_variant_version_and_checksum()
    {
        var http = new FixtureHttp(_ => FixtureHttp.Json("""{"name":"Tool","version":"1.2","artifacts":[{"key":"windows","os":"windows","architecture":"arm64","url":"https://example.org/tool.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}"""));
        var result = await new DirectProvider(new ProviderHttp(http,new())).ResolveAsync(new("https://example.org/tool.exe", MetadataUrl:"https://example.org/version.json"),default);
        var a = Assert.Single(result.Artifacts); Assert.Equal("1.2",a.Version); Assert.Equal("arm64",a.Architecture); Assert.Equal(64,a.Sha256!.Length);
    }

    [Fact]
    public async Task Homebrew_merges_platform_overrides_and_excludes_linux()
    {
        var http = new FixtureHttp(_ => FixtureHttp.Json("""
        {"token":"tool","name":["Tool"],"version":"2.0","url":"https://example.org/arm.dmg","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
         "supported_platforms":["arm64_sonoma","sonoma","arm64_big_sur"],"variations":{"sonoma":{"url":"https://example.org/intel.dmg"},"arm64_big_sur":{"version":"1.0","url":"https://example.org/old.dmg"},"x86_64_linux":{"url":"https://example.org/bad.zip"}}}
        """));
        var result = await new HomebrewProvider(new ProviderHttp(http,new())).ResolveAsync(new("tool"),default);
        Assert.Equal(3,result.Artifacts.Count);
        Assert.Equal("https://example.org/intel.dmg",result.Artifacts.Single(x=>x.Key=="cask:sonoma").Url);
        Assert.Equal("x64",result.Artifacts.Single(x=>x.Key=="cask:sonoma").Architecture);
        Assert.Equal("1.0",result.Artifacts.Single(x=>x.Key=="cask:arm64_big_sur").Version);
        Assert.All(result.Artifacts,x=>Assert.Equal("macos",x.Os));
    }

    [Fact]
    public async Task Winget_uses_component_versions_and_multifile_inheritance()
    {
        var http = new FixtureHttp(req => {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/Acme/Tool")) return FixtureHttp.Json("""[{"name":"1.9","type":"dir"},{"name":"1.10","type":"dir"}]""");
            if (path.EndsWith("/1.10")) return FixtureHttp.Json("""[{"name":"Acme.Tool.yaml","type":"file"},{"name":"Acme.Tool.installer.yaml","type":"file"},{"name":"Acme.Tool.locale.en-US.yaml","type":"file"}]""");
            if (path.EndsWith(".installer.yaml")) return FixtureHttp.Text("PackageIdentifier: Acme.Tool\nPackageVersion: 1.10\nManifestType: installer\nInstallerType: msi\nMinimumOSVersion: 10.0.0.0\nInstallers:\n- Architecture: x64\n  InstallerUrl: https://example.org/tool.msi\n  InstallerSha256: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n- Architecture: arm64\n  InstallerType: exe\n  InstallerUrl: https://example.org/tool.exe\n  InstallerSha256: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n");
            if (path.EndsWith(".locale.en-US.yaml")) return FixtureHttp.Text("PackageIdentifier: Acme.Tool\nPackageVersion: 1.10\nManifestType: defaultLocale\nPackageName: Real Tool\nPublisher: Acme\nShortDescription: Fixture\n");
            return FixtureHttp.Text("PackageIdentifier: Acme.Tool\nPackageVersion: 1.10\nDefaultLocale: en-US\nManifestType: version\n");
        });
        var result = await new WinGetProvider(new ProviderHttp(http,new())).ResolveAsync(new("Acme.Tool"),default);
        Assert.Equal("Real Tool",result.Name); Assert.Equal(2,result.Artifacts.Count);
        Assert.Equal("msi",result.Artifacts.Single(x=>x.Architecture=="x64").Format);
        Assert.Equal("exe",result.Artifacts.Single(x=>x.Architecture=="arm64").Format);
        Assert.All(result.Artifacts,x=> { Assert.Equal("1.10",x.Version); Assert.Contains("10.0.0.0",x.Requirements); Assert.NotNull(x.Sha256); });
    }

    private sealed class FixtureHttp(Func<HttpRequestMessage,HttpResponseMessage> respond) : ISafeHttp
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
        public static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body,System.Text.Encoding.UTF8,"application/json") };
        public static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }
}
