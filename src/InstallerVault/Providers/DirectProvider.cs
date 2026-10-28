using System.Text.Json;
namespace InstallerVault.Providers;

public sealed class DirectProvider(ProviderHttp http) : IProvider
{
    public string Id=>"direct";
    public Task<List<ProviderHit>> SearchAsync(string query,CancellationToken ct)=>Task.FromResult(new List<ProviderHit>());
    public async Task<ProviderResult> ResolveAsync(ProviderQuery query,CancellationToken ct)
    {
        ProviderUtilities.StableOnly(query);
        ProviderUtilities.Https(query.Identifier);
        if(string.IsNullOrWhiteSpace(query.MetadataUrl))
        {
            var identity=await http.ProbeIdentityAsync(query.Identifier,ct);
            var filename=ProviderUtilities.FileName(query.Identifier);
            var (os,arch)=ProviderUtilities.Infer(filename);
            return new(Path.GetFileNameWithoutExtension(filename),"Direct vendor URL",new Uri(query.Identifier).Host,[new("direct","unknown",identity,os,arch,ProviderUtilities.Format(filename),query.Identifier,filename)],"A fixed URL has no automatic version discovery. Validators can indicate change, but only downloaded content hashes identify content. Offline readiness is unknown.");
        }
        var metadata=await http.JsonAsync(query.MetadataUrl,ct);
        var name=metadata.S("name",new Uri(query.Identifier).Host);
        var items=metadata.Items("artifacts").ToArray();
        if(items.Length==0)items=[metadata];
        if(items.Length>100)throw new ProviderException("Direct metadata may advertise at most 100 artifacts.");
        var artifacts=new List<ProviderArtifact>();
        foreach(var item in items)
        {
            var url=ProviderUtilities.Https(item.S("url",query.Identifier));
            var file=item.S("fileName",ProviderUtilities.FileName(url));
            if(!ProviderUtilities.Matches(file,query.AssetFilter))continue;
            var (suggestedOs,suggestedArch)=ProviderUtilities.Infer(file);
            var version=item.S("version",metadata.S("version","unknown"));
            var os=item.S("os",suggestedOs);var arch=item.S("architecture",suggestedArch);var format=item.S("format",ProviderUtilities.Format(file));
            var key=item.S("key",$"{os}:{arch}:{format}");
            var rawHash=item.S("sha256");var hash=ProviderUtilities.Hash(rawHash);
            if(rawHash.Length>0 && hash is null)throw new ProviderException("The direct metadata SHA-256 must contain exactly 64 hexadecimal characters.");
            artifacts.Add(new(key,version,item.S("releaseId",metadata.S("releaseId",version)),os,arch,format,url,file,hash,item.S("artifactType","unknown"),item.S("requirements",metadata.S("requirements"))));
        }
        if(artifacts.Select(x=>x.Key).Distinct().Count()!=artifacts.Count)throw new ProviderException("Direct metadata artifacts need unique key values.");
        return new(name,metadata.S("description"),metadata.S("publisher",new Uri(query.Identifier).Host),artifacts,"Version discovery uses the explicitly configured JSON metadata endpoint. Publisher authenticity and dependency completeness are not inferred.");
    }
}
