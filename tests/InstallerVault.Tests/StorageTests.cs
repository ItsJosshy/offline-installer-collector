using InstallerVault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InstallerVault.Tests;
public sealed class StorageTests : IDisposable {
 readonly string root = Path.Combine(Path.GetTempPath(), "vault-test-"+Guid.NewGuid().ToString("N"));
 readonly PathGuard guard;
 public StorageTests() { Directory.CreateDirectory(root); guard = new(Options.Create(new VaultOptions{AllowedRoots=[root]})); }
 [Fact] public void RejectsSiblingTraversal() { Assert.ThrowsAny<Exception>(()=>guard.Validate(Path.Combine(root,"..","escape"),false)); }
 [Fact] public void AcceptsExistingChild() { var p=Directory.CreateDirectory(Path.Combine(root,"child")).FullName; Assert.Equal(p,guard.Validate(p)); }
 [Fact] public void RejectsSymbolicDirectoryEscape() { var link=Path.Combine(root,"escape"); Directory.CreateSymbolicLink(link,Path.GetTempPath()); Assert.ThrowsAny<Exception>(()=>guard.Validate(Path.Combine(link,"nested"),false)); }
 [Fact] public void MissingLibraryIsNeverRecreated() { var library=new Library{Path=Path.Combine(root,"gone")}; Assert.ThrowsAny<Exception>(()=>guard.AssertLibrary(library)); Assert.False(Directory.Exists(library.Path)); }
 [Fact] public async Task ScanIsNonDestructiveIdempotentAndMarksMissing() {
  var path=Path.Combine(root,"something.zip"); await File.WriteAllTextAsync(path,"not really an installer");
  var library=new Library{Path=root}; await File.WriteAllTextAsync(Path.Combine(root,".installervault-library"),library.Marker);
  await using var db=new VaultDb(new DbContextOptionsBuilder<VaultDb>().UseSqlite("Data Source=:memory:").Options);
  await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync(); db.Libraries.Add(library); await db.SaveChangesAsync();
  var scanner=new Scanner(db,guard); await scanner.ScanAsync(library,default); await scanner.ScanAsync(library,default);
  var file=Assert.Single(await db.Files.ToListAsync()); Assert.False(file.Managed); Assert.Equal("unknown",file.Os); Assert.Equal("unknown",file.ArtifactType); Assert.Equal("not really an installer",await File.ReadAllTextAsync(path));
  File.Delete(path); await scanner.ScanAsync(library,default); Assert.True(file.Missing); Assert.Single(await db.Files.ToListAsync());
 }
 public void Dispose(){ Directory.Delete(root,true); }
}
