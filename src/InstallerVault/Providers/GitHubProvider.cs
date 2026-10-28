using System.Text.Json;
using System.Text.RegularExpressions;
namespace InstallerVault.Providers;

public sealed class GitHubProvider(ProviderHttp http) : IProvider
{
    public string Id=>"github";
    public async Task<List<ProviderHit>> SearchAsync(string query,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(query))return [];
        if(query.Contains('/') && !query.Contains(' '))
        {
            var id=Repository(query);
            var item=await http.JsonAsync($"https://api.github.com/repos/{id}",ct);
            return [new(id,item.S("name",id),item.S("description"),item.S("html_url"))];
        }
        var result=await http.JsonAsync("https://api.github.com/search/repositories?per_page=20&q="+Uri.EscapeDataString(query),ct);
        return result.Items("items").Select(x=>new ProviderHit(x.S("full_name"),x.S("name"),x.S("description"),x.S("html_url"))).ToList();
    }
    public async Task<ProviderResult> ResolveAsync(ProviderQuery query,CancellationToken ct)
    {
        var repository=Repository(query.Identifier);
        var exact=query.Channel.StartsWith("tag:",StringComparison.Ordinal) ? query.Channel[4..] : null;
        if(exact is null && query.Channel is not "stable" and not "prerelease") throw new ProviderException("GitHub supports stable, prerelease (includes stable), or tag:<exact-tag>. LTS must be an explicitly selected repository/tag.");
        var releases=new List<JsonElement>();
        var truncated=false;
        if(exact is not null) releases.Add(await http.JsonAsync($"https://api.github.com/repos/{repository}/releases/tags/{Uri.EscapeDataString(exact)}",ct));
        else for(var page=1;page<=5;page++)
        {
            var response=await http.GetAsync($"https://api.github.com/repos/{repository}/releases?per_page=100&page={page}",ct);
            using var document=JsonDocument.Parse(response.Body);
            releases.AddRange(document.RootElement.EnumerateArray().Select(x=>x.Clone()));
            var hasNext=response.Link?.Contains("rel=\"next\"",StringComparison.Ordinal)==true;
            if(!hasNext)break;
            truncated=page==5;
        }
        var artifacts=new Dictionary<string,ProviderArtifact>(StringComparer.OrdinalIgnoreCase);
        foreach(var release in releases)
        {
            if(release.B("draft") || query.Channel=="stable" && release.B("prerelease"))continue;
            var version=release.S("tag_name","unknown");
            // Only uploaded release assets. GitHub's generated tarball/zipball are never considered installers.
            foreach(var asset in release.Items("assets"))
            {
                var name=asset.S("name"); var format=ProviderUtilities.Format(name);
                if(format=="unknown" || !ProviderUtilities.Matches(name,query.AssetFilter) || asset.S("state","uploaded")!="uploaded")continue;
                var (os,arch)=ProviderUtilities.Infer(name);
                var normalized=name.Replace(version,"{version}",StringComparison.OrdinalIgnoreCase);
                if(version.StartsWith('v') || version.StartsWith('V')) normalized=normalized.Replace(version[1..],"{version}",StringComparison.OrdinalIgnoreCase);
                var key=$"{os}:{arch}:{format}:{normalized.ToLowerInvariant()}";
                var digest=asset.S("digest");
                artifacts.TryAdd(key,new(key,version,release.S("id")+":"+asset.S("id")+":"+asset.S("updated_at"),os,arch,format,ProviderUtilities.Https(asset.S("browser_download_url")),name,ProviderUtilities.Hash(digest.StartsWith("sha256:")?digest[7..]:null)));
            }
        }
        var warning="OS and architecture inferred from asset names are suggestions. Preview and select exact assets; generic archives may contain source or dependencies. Offline readiness is unknown.";
        if(truncated)warning+=" Results are bounded to the newest 500 releases; use tag:<tag> for older releases.";
        if(artifacts.Count==0)warning+=" No matching uploaded installer/archive assets were found.";
        return new(repository.Split('/')[1],"GitHub Releases: "+repository,repository.Split('/')[0],artifacts.Values.ToList(),warning);
    }
    private static string Repository(string value)
    {
        value=value.Trim().TrimEnd('/');
        if(Uri.TryCreate(value,UriKind.Absolute,out var uri))
        {
            if(uri.Scheme!="https" || !uri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase))throw new ProviderException("Enter owner/repository or an HTTPS github.com repository URL.");
            value=uri.AbsolutePath.Trim('/');
        }
        if(value.EndsWith(".git",StringComparison.OrdinalIgnoreCase))value=value[..^4];
        if(!Regex.IsMatch(value,@"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z") || value.Split('/').Any(x=>x is "." or ".."))throw new ProviderException("GitHub identifier must be owner/repository.");
        return value;
    }
}
