using Microsoft.EntityFrameworkCore;
namespace InstallerVault.Jobs;

public class RetentionService(VaultDb db, PathGuard paths)
{
    // Retention counts successful distinct versions by acquisition time, never lexical version strings.
    // Unknown mutable releases count by content identity so changed fixed URLs retain prior content.
    static string Identity(ArchiveFile f) => f.Version is "unknown" or "" ? f.Sha256 ?? f.Id : f.Version;
    public static IEnumerable<ArchiveFile> Candidates(IEnumerable<ArchiveFile> files, int keep)
    {
        var successful = files.Where(f => !f.Missing && f.Managed && f.DownloadedAt != null)
            .OrderByDescending(f => f.DownloadedAt).ToList();
        var preserved = successful.Select(Identity).Distinct().Take(Math.Max(1, keep)).ToHashSet();
        return successful.Where(f => !f.Pinned && !preserved.Contains(Identity(f))).ToList();
    }

    public async Task ApplyAsync(Library library, string variantId, int keep, CancellationToken ct)
    {
        var files = await db.Files.Where(f => f.LibraryId == library.Id && f.VariantId == variantId).ToListAsync(ct);
        foreach (var file in Candidates(files, keep))
        {
            ct.ThrowIfCancellationRequested();
            paths.AssertLibrary(library);
            try
            {
                var path = paths.ForLibrary(library, file.RelativePath);
                File.Delete(path);
                var metadata = paths.ForLibrary(library, file.RelativePath + ".metadata.json", false);
                if (File.Exists(metadata)) File.Delete(metadata);
                file.Missing = true;
                await db.SaveChangesAsync(ct);
            }
            catch (IOException) { /* Retention is best effort; a successful replacement remains valid. */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
