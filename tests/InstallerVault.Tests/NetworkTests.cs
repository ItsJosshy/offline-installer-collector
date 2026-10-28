using System.Net;
using InstallerVault;
namespace InstallerVault.Tests;
public class NetworkTests {
 [Theory][InlineData("127.0.0.1")][InlineData("10.2.3.4")][InlineData("192.168.0.1")][InlineData("169.254.169.254")][InlineData("100.100.100.200")][InlineData("::1")][InlineData("::ffff:127.0.0.1")][InlineData("fc00::1")][InlineData("fe80::1")][InlineData("2002:7f00:1::")]
 public void RejectsNonPublicAddresses(string ip)=>Assert.False(SafeHttp.IsPublic(IPAddress.Parse(ip)));
 [Theory][InlineData("1.1.1.1")][InlineData("2606:4700:4700::1111")]
 public void AcceptsPublicAddresses(string ip)=>Assert.True(SafeHttp.IsPublic(IPAddress.Parse(ip)));
}
