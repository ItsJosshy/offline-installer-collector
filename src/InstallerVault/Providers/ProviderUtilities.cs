using System.Text.Json;
using System.Text.RegularExpressions;
namespace InstallerVault.Providers;

internal static class ProviderUtilities
{
    public static string S(this JsonElement item,string key,string fallback="") => item.ValueKind==JsonValueKind.Object && item.TryGetProperty(key,out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? (value.ValueKind==JsonValueKind.String ? value.GetString()! : value.ToString()) : fallback;
    public static bool B(this JsonElement item,string key) => item.ValueKind==JsonValueKind.Object && item.TryGetProperty(key,out var value) && value.ValueKind==JsonValueKind.True;
    public static IEnumerable<JsonElement> Items(this JsonElement item,string key) => item.ValueKind==JsonValueKind.Object && item.TryGetProperty(key,out var value) && value.ValueKind==JsonValueKind.Array ? value.EnumerateArray() : [];
    public static string Format(string name) => new[]{"tar.gz","tar.xz","msixbundle","appxbundle","appimage","msix","appx","exe","msi","dmg","pkg","deb","rpm","zip","7z"}.FirstOrDefault(x=>name.EndsWith("."+x,StringComparison.OrdinalIgnoreCase)) ?? "unknown";
    public static (string Os,string Architecture) Infer(string name)
    {
        var lower=name.ToLowerInvariant(); var format=Format(name);
        var os= format is "exe" or "msi" or "msix" or "msixbundle" or "appx" or "appxbundle" ? "windows" : format is "dmg" or "pkg" ? "macos" : format is "deb" or "rpm" or "appimage" ? "linux" : Regex.IsMatch(lower,@"(^|[-_.])(windows|win32|win64|win)([-_.]|$)") ? "windows" : Regex.IsMatch(lower,@"(^|[-_.])(darwin|macos|osx|mac)([-_.]|$)") ? "macos" : lower.Contains("linux") ? "linux" : "unknown";
        var arch=Regex.IsMatch(lower,@"(^|[-_.])(arm64|aarch64)([-_.]|$)") ? "arm64" : Regex.IsMatch(lower,@"(^|[-_.])(x64|x86_64|amd64|win64)([-_.]|$)") ? "x64" : Regex.IsMatch(lower,@"(^|[-_.])(x86|i386|i686|win32)([-_.]|$)") ? "x86" : lower.Contains("universal") ? "universal" : "unknown";
        return(os,arch);
    }
    public static bool Matches(string name,string filter) => string.IsNullOrWhiteSpace(filter) || filter.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Any(pattern=>Regex.IsMatch(name,"^"+Regex.Escape(pattern).Replace("\\*",".*").Replace("\\?",".")+"$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)));
    public static string FileName(string url,string fallback="download.bin") => Uri.TryCreate(url,UriKind.Absolute,out var uri) && !string.IsNullOrWhiteSpace(Path.GetFileName(uri.AbsolutePath)) ? Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)) : fallback;
    public static string? Hash(string? value) => value is not null && Regex.IsMatch(value,@"\A[0-9a-fA-F]{64}\z") ? value.ToLowerInvariant() : null;
    public static void StableOnly(ProviderQuery query) { if(query.Channel!="stable") throw new ProviderException("This provider does not expose release channels. Use stable and select a source identifier for the desired channel."); }
    public static string Https(string url) { if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length>0) throw new ProviderException("Provider advertised an unsupported URL; HTTPS without embedded credentials is required."); return url; }
}

/// <summary>Natural comparison of numeric components for WinGet's heterogeneous version labels.</summary>
public sealed class PackageVersionComparer : IComparer<string>
{
    public static PackageVersionComparer Instance {get;}=new();
    public int Compare(string? x,string? y)
    {
        var left=Regex.Matches((x??"").TrimStart('v','V'),@"\d+|[^\d]+"); var right=Regex.Matches((y??"").TrimStart('v','V'),@"\d+|[^\d]+");
        for(var i=0;i<Math.Min(left.Count,right.Count);i++)
        {
            var a=left[i].Value;var b=right[i].Value;int compare;
            if(char.IsDigit(a[0])&&char.IsDigit(b[0])) { a=a.TrimStart('0'); b=b.TrimStart('0'); compare=a.Length.CompareTo(b.Length); if(compare==0) compare=string.CompareOrdinal(a,b); }
            else compare=string.Compare(a,b,StringComparison.OrdinalIgnoreCase);
            if(compare!=0)return compare;
        }
        return left.Count.CompareTo(right.Count);
    }
}
