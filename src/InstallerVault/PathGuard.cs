using Microsoft.Extensions.Options;
namespace InstallerVault;
public class PathGuard(IOptions<VaultOptions> options) {
 public string[] Roots => options.Value.AllowedRoots.Select(Path.GetFullPath).ToArray();
 static StringComparison Comparison => OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
 public static bool IsWithin(string path,string root) => path.Equals(root,Comparison) || path.StartsWith(Path.TrimEndingDirectorySeparator(root)+Path.DirectorySeparatorChar,Comparison);
 public string Validate(string path,bool mustExist=true) {
  if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path)||path.IndexOf('\0')>=0) throw new InvalidOperationException("An absolute server path is required.");
  if(OperatingSystem.IsWindows() && (path.StartsWith(@"\\") || path.IndexOf(':',2)>=0)) throw new InvalidOperationException("UNC, device paths and alternate data streams are not allowed. Mount storage beneath an allowed local path.");
  var full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
  if(!Roots.Any(r=>IsWithin(full,Path.TrimEndingDirectorySeparator(r)))) throw new UnauthorizedAccessException("Path is outside the configured allowed directories.");
  // Check all ancestors, including the configured root itself. Reject links rather than resolving them.
  for(string? p=full;p!=null;p=Path.GetDirectoryName(p)) {
   try { if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0) throw new UnauthorizedAccessException("Symbolic links and junctions are not permitted."); }
   catch(FileNotFoundException){} catch(DirectoryNotFoundException){}
  }
  if(mustExist && !Directory.Exists(full) && !File.Exists(full)) throw new DirectoryNotFoundException("The configured path is unavailable; check the drive or mount.");
  return full;
 }
 public string ForLibrary(Library library,string relativePath,bool mustExist=true) {
  AssertLibrary(library);
  if(Path.IsPathRooted(relativePath)) throw new UnauthorizedAccessException("Library paths must be relative.");
  var full=Validate(Path.Combine(library.Path,relativePath),mustExist);
  if(!IsWithin(full,Path.TrimEndingDirectorySeparator(Path.GetFullPath(library.Path)))) throw new UnauthorizedAccessException("Path escapes this library.");
  return full;
 }
 public void AssertLibrary(Library library) {
  var root=Validate(library.Path);
  if(!Directory.Exists(root)) throw new DirectoryNotFoundException("Library directory is unavailable.");
  var marker=Validate(Path.Combine(root,".installervault-library"));
  if(File.ReadAllText(marker)!=library.Marker) throw new IOException("Library identity does not match. Check the mounted drive before continuing.");
 }
}
