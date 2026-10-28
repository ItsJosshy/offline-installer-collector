using Microsoft.EntityFrameworkCore;
namespace InstallerVault;
public class Scanner(VaultDb db, PathGuard guard) {
 static readonly string[] Extensions=[".exe",".msi",".msix",".msixbundle",".appx",".appxbundle",".dmg",".pkg",".deb",".rpm",".appimage",".zip",".7z",".tar.gz",".tar.xz"];
 public async Task ScanAsync(Library library,CancellationToken ct) {
  guard.AssertLibrary(library);
  var files=await db.Files.Where(f=>f.LibraryId==library.Id).ToListAsync(ct);
  var comparer=OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal;
  var existing=files.ToDictionary(f=>f.RelativePath,comparer);
  var seen=new HashSet<string>(comparer); var inaccessible=new List<string>(); var pending=new Stack<string>(); pending.Push(library.Path);
  while(pending.TryPop(out var directory)) {
   ct.ThrowIfCancellationRequested();
   try {
    guard.Validate(directory);
    foreach(var path in Directory.GetFileSystemEntries(directory)) {
     ct.ThrowIfCancellationRequested(); var relative=Path.GetRelativePath(library.Path,path);
     if(relative.StartsWith(".staging",StringComparison.OrdinalIgnoreCase)) continue;
     try {
      guard.ForLibrary(library,relative);
      var info=new FileInfo(path);
      if((info.Attributes&FileAttributes.Directory)!=0){pending.Push(path);continue;}
      if(!Extensions.Any(e=>path.EndsWith(e,StringComparison.OrdinalIgnoreCase)))continue;
      seen.Add(relative);
      if(!existing.TryGetValue(relative,out var record)){
       record=new ArchiveFile{LibraryId=library.Id,RelativePath=relative,FileName=info.Name,Name=Path.GetFileNameWithoutExtension(info.Name)};
       var ext=info.Extension.ToLowerInvariant(); record.Os=ext switch {".exe" or ".msi" or ".msix" or ".msixbundle" or ".appx" or ".appxbundle"=>"windows",".dmg" or ".pkg"=>"macos",".deb" or ".rpm" or ".appimage"=>"linux",_=>"unknown"};
       db.Files.Add(record);existing[relative]=record;
      }
      if(record.Size!=info.Length||record.ModifiedAt!=info.LastWriteTimeUtc){record.Sha256=null;record.Verification="not-calculated";}
      record.Size=info.Length;record.ModifiedAt=info.LastWriteTimeUtc;record.Missing=false;
     }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){inaccessible.Add(relative);}
    }
   }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){inaccessible.Add(Path.GetRelativePath(library.Path,directory));}
  }
  // Preserve state beneath inaccessible subtrees. Absence has not been established there.
  foreach(var file in files) if(!seen.Contains(file.RelativePath) && !inaccessible.Any(p=>p=="."||file.RelativePath.Equals(p,comparer==StringComparer.OrdinalIgnoreCase?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)||file.RelativePath.StartsWith(p+Path.DirectorySeparatorChar,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)))file.Missing=true;
  await db.SaveChangesAsync(ct);
 }
}
