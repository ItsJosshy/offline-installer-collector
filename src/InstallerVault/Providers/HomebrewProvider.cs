using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace InstallerVault.Providers;

public sealed class HomebrewProvider(ProviderHttp http) : IProvider
{
    public string Id=>"homebrew";
    public async Task<List<ProviderHit>> SearchAsync(string query,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(query))return [];
        var catalogue=await http.JsonAsync("https://formulae.brew.sh/api/cask.json",ct,64*1024*1024);
        return catalogue.EnumerateArray().Where(x=>x.S("token").Contains(query,StringComparison.OrdinalIgnoreCase) || string.Join(" ",x.Items("name")).Contains(query,StringComparison.OrdinalIgnoreCase)).Take(30).Select(x=>new ProviderHit(x.S("token"),x.Items("name").FirstOrDefault().ToString(),x.S("desc"),x.S("homepage"))).ToList();
    }
    public async Task<ProviderResult> ResolveAsync(ProviderQuery query,CancellationToken ct)
    {
        ProviderUtilities.StableOnly(query);
        var token=query.Identifier.Trim();
        if(!Regex.IsMatch(token,@"\A[a-zA-Z0-9][a-zA-Z0-9+@._-]*\z"))throw new ProviderException("Enter a Homebrew cask token, for example visual-studio-code. Formulae and Ruby recipes are not supported.");
        var cask=await http.JsonAsync($"https://formulae.brew.sh/api/cask/{Uri.EscapeDataString(token)}.json",ct);
        if(cask.B("disabled"))return new(token,cask.S("desc"),"",[],"This cask is disabled by Homebrew and is not offered for automatic archiving.");
        var platforms=cask.Items("supported_platforms").Select(x=>x.GetString()!).Where(x=>!x.EndsWith("_linux",StringComparison.OrdinalIgnoreCase)).ToList();
        var hasVariations=cask.TryGetProperty("variations",out var variations) && variations.ValueKind==JsonValueKind.Object;
        if(platforms.Count==0)
        {
            if(hasVariations) platforms.AddRange(variations.EnumerateObject().Select(x=>x.Name).Where(x=>!x.EndsWith("_linux")));
            // Historical API has no supported_platforms: the root is the API's default macOS build, architecture uncertain.
            platforms.Add("default");
        }
        var artifacts=new List<ProviderArtifact>();
        foreach(var platform in platforms.Distinct())
        {
            var merged=JsonNode.Parse(cask.GetRawText())!.AsObject();
            if(hasVariations && variations.TryGetProperty(platform,out var variation)) foreach(var property in variation.EnumerateObject())merged[property.Name]=JsonNode.Parse(property.Value.GetRawText());
            var value=JsonSerializer.SerializeToElement(merged);
            var url=value.S("url");var version=value.S("version");
            if(url.Length==0||version.Length==0)continue;
            var file=ProviderUtilities.FileName(url,token+".bin");
            if(!ProviderUtilities.Matches(file,query.AssetFilter))continue;
            var arch=platform.StartsWith("arm64_")?"arm64":platform=="default"?"unknown":"x64";
            var macos=platform.Replace("arm64_","");
            var format=ProviderUtilities.Format(file);
            var requirements="macOS platform: "+macos;
            if(value.TryGetProperty("depends_on",out var dependencies))requirements+="; cask dependencies: "+dependencies.GetRawText();
            var hash=ProviderUtilities.Hash(value.S("sha256"));
            artifacts.Add(new("cask:"+platform,version=="latest"?"unknown":version,version+":"+(hash??"unverified"),"macos",arch,format,ProviderUtilities.Https(url),file,hash,Requirements:requirements));
        }
        var names=cask.Items("name").Select(x=>x.GetString()).Where(x=>x is not null);
        return new(names.FirstOrDefault()??token,cask.S("desc"),"",artifacts,"Cask vendor distribution files only. Platform variations are preserved; Homebrew dependencies, install scripts, language selection and offline installation are not executed or verified. Extensionless vendor URLs may have unknown format.");
    }
}
