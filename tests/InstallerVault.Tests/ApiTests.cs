using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InstallerVault;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
namespace InstallerVault.Tests;
public class ApiTests {
 sealed class Host : WebApplicationFactory<Program> {
  public string Root {get;}=Path.Combine(Path.GetTempPath(),"vault-api-"+Guid.NewGuid().ToString("N"));
  public Host(){Directory.CreateDirectory(Path.Combine(Root,"library"));}
  protected override void ConfigureWebHost(IWebHostBuilder b)=>b.UseEnvironment("Testing").ConfigureAppConfiguration((_,c)=>c.AddInMemoryCollection(new Dictionary<string,string?>{["Vault:DataPath"]=Path.Combine(Root,"data"),["Vault:AllowedRoots:0"]=Path.Combine(Root,"library"),["Vault:SetupSecret"]="test-only-setup-secret-long-enough"}));
  protected override void Dispose(bool disposing){base.Dispose(disposing);Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(disposing)Directory.Delete(Root,true);}
 }
 static async Task Token(HttpClient c){var j=await c.GetFromJsonAsync<JsonElement>("/api/auth");c.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");c.DefaultRequestHeaders.Add("X-CSRF-TOKEN",j.GetProperty("csrfToken").GetString());}
 [Fact] public async Task ProtectsSetupCsrfAndLibraryAndServesOfflineArchive(){
  using var host=new Host(); using var c=host.CreateClient();
  Assert.Equal(HttpStatusCode.Unauthorized,(await c.GetAsync("/api/directories")).StatusCode);
  Assert.Equal(HttpStatusCode.BadRequest,(await c.PostAsJsonAsync("/api/auth/setup",new {username="admin",password="a secure test password",setupSecret="test-only-setup-secret-long-enough"})).StatusCode);
  await Token(c);
  Assert.Equal(HttpStatusCode.Forbidden,(await c.PostAsJsonAsync("/api/auth/setup",new {username="admin",password="a secure test password",setupSecret="wrong"})).StatusCode);
  (await c.PostAsJsonAsync("/api/auth/setup",new {username="admin",password="a secure test password",setupSecret="test-only-setup-secret-long-enough"})).EnsureSuccessStatusCode();
  await Token(c);
  (await c.PostAsJsonAsync("/api/libraries",new {path=Path.Combine(host.Root,"library")})).EnsureSuccessStatusCode();
  Assert.Equal(HttpStatusCode.Forbidden,(await c.GetAsync("/api/directories?path="+Uri.EscapeDataString(host.Root))).StatusCode);
  await File.WriteAllTextAsync(Path.Combine(host.Root,"library","test.zip"),"archived bytes");
  (await c.PostAsJsonAsync("/api/scan",new {})).EnsureSuccessStatusCode();
  JsonElement state=default; for(var i=0;i<100;i++){state=await c.GetFromJsonAsync<JsonElement>("/api/state");if(state.GetProperty("files").GetArrayLength()>0)break;await Task.Delay(100);}
  var file=state.GetProperty("files")[0]; Assert.Equal("archived bytes",await c.GetStringAsync("/api/files/"+file.GetProperty("id").GetString()+"/download"));
  Assert.Equal("unknown",file.GetProperty("os").GetString());
  Assert.Equal(HttpStatusCode.Conflict,(await c.PostAsJsonAsync("/api/auth/setup",new {username="hijack",password="a secure test password",setupSecret="test-only-setup-secret-long-enough"})).StatusCode);
 }
}
