using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
namespace InstallerVault;
public sealed class SafeHttp : ISafeHttp, IDisposable {
 readonly HttpClient client; readonly VaultOptions options;
 public SafeHttp(IOptions<VaultOptions> configured) {
  options=configured.Value;
  var handler=new SocketsHttpHandler {AllowAutoRedirect=false,UseProxy=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.All,PooledConnectionLifetime=TimeSpan.FromMinutes(2),ConnectTimeout=TimeSpan.FromSeconds(20)};
  handler.ConnectCallback=async (context,ct)=>{
   var addresses=await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host,ct);
   if(addresses.Length==0 || (!IsAllowedPrivate(context.DnsEndPoint.Host) && addresses.Any(a=>!IsPublic(a))))throw new HttpRequestException("Download host resolves to a forbidden network address.");
   Exception? last=null;
   foreach(var address in addresses){var socket=new Socket(address.AddressFamily,SocketType.Stream,ProtocolType.Tcp);try{await socket.ConnectAsync(new IPEndPoint(address,context.DnsEndPoint.Port),ct);return new NetworkStream(socket,true);}catch(Exception e){socket.Dispose();last=e;}}
   throw new HttpRequestException("Unable to connect to download host.",last);
  };
  client=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
  client.DefaultRequestHeaders.UserAgent.ParseAdd("InstallerVault/1.0");
 }
 bool IsAllowedPrivate(string host)=>options.PrivateSourceHosts.Contains(host,StringComparer.OrdinalIgnoreCase);
 public static bool IsPublic(IPAddress ip){
  if(ip.IsIPv4MappedToIPv6)return IsPublic(ip.MapToIPv4());
  if(IPAddress.IsLoopback(ip))return false;
  var b=ip.GetAddressBytes();
  if(b.Length==4)return !(b[0]==0||b[0]==10||b[0]==127||b[0]>=224||(b[0]==100&&b[1]>=64&&b[1]<=127)||(b[0]==169&&b[1]==254)||(b[0]==172&&b[1]>=16&&b[1]<=31)||(b[0]==192&&(b[1]==168||b[1]==0||b[1]==2))||(b[0]==198&&(b[1]==18||b[1]==19||b[1]==51))||(b[0]==203&&b[1]==0&&b[2]==113));
  // Permit global unicast only; reject transition mechanisms and documentation space.
  return (b[0]&0xe0)==0x20 && !(b[0]==0x20&&b[1]==0x02) && !(b[0]==0x20&&b[1]==0x01&&(b[2]<2 || (b[2]==0x0d&&b[3]==0xb8)));
 }
 public static Uri ValidateUrl(string value){
  if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!string.IsNullOrEmpty(uri.UserInfo)||uri.Port!=443||!string.IsNullOrEmpty(uri.Fragment))throw new InvalidOperationException("Sources must use an HTTPS URL on port 443 without embedded credentials or fragments.");
  return uri;
 }
 public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
  var uri=ValidateUrl(request.RequestUri?.AbsoluteUri??"");
  var originalHost=uri.Host;
  for(var redirects=0;redirects<=5;redirects++){
   if(IPAddress.TryParse(uri.Host.Trim('[',']'),out var literal)&&!IsPublic(literal)&&!IsAllowedPrivate(uri.Host))throw new HttpRequestException("Private network source is not allowed.");
   using var next=new HttpRequestMessage(request.Method,uri);
   foreach(var h in request.Headers) if(!h.Key.Equals("Host",StringComparison.OrdinalIgnoreCase)&& (uri.Host==originalHost||(!h.Key.Equals("Authorization",StringComparison.OrdinalIgnoreCase)&&!h.Key.Equals("Cookie",StringComparison.OrdinalIgnoreCase))))next.Headers.TryAddWithoutValidation(h.Key,h.Value);
   var response=await client.SendAsync(next,HttpCompletionOption.ResponseHeadersRead,ct);
   if((int)response.StatusCode is 301 or 302 or 303 or 307 or 308){var location=response.Headers.Location;response.Dispose();if(location==null)throw new HttpRequestException("Redirect has no destination.");uri=ValidateUrl(new Uri(uri,location).AbsoluteUri);continue;}
   return response;
  }
  throw new HttpRequestException("Too many download redirects.");
 }
 public void Dispose()=>client.Dispose();
}
