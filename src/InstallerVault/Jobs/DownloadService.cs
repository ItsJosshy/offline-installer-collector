using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InstallerVault.Jobs;

public class DownloadService(VaultDb db, PathGuard paths, ISafeHttp http, IOptions<VaultOptions> options,
    IHubContext<ProgressHub> hub, RetentionService retention)
{
    static string Component(string value)
    {
        var safe = new string(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').Take(100).ToArray()).Trim('.');
        return string.IsNullOrEmpty(safe) ? "unknown" : safe;
    }

    public async Task RunAsync(Job job, CancellationToken ct)
    {
        var snapshot = JsonSerializer.Deserialize<DownloadSnapshot>(job.Payload) ?? throw new InvalidDataException("Invalid download snapshot.");
        var library = new Library { Id = job.LibraryId, Path = job.LibraryPath, Marker = job.LibraryMarker };
        paths.AssertLibrary(library);
        var stageDirectory = paths.ForLibrary(library, ".installervault-staging", false);
        Directory.CreateDirectory(stageDirectory);
        var stageRelative = Path.Combine(".installervault-staging", Component(job.Id) + ".part");
        var stage = paths.ForLibrary(library, stageRelative, false);
        if (File.Exists(stage)) File.Delete(stage); // Only this persistent job's interrupted transfer.
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromHours(2));
            var token = timeout.Token;
            using var request = new HttpRequestMessage(HttpMethod.Get, snapshot.Url);
            using var response = await http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            if (total > options.Value.MaxDownloadBytes) throw new DownloadLimitException();
            job.Bytes = 0; job.TotalBytes = total;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                var timer = Stopwatch.StartNew();
                var lastProgress = TimeSpan.Zero;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var read = await source.ReadAsync(buffer, token);
                    if (read == 0) break;
                    if (job.Bytes > options.Value.MaxDownloadBytes - read) throw new DownloadLimitException();
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    hash.AppendData(buffer, 0, read); job.Bytes += read;
                    if (timer.Elapsed - lastProgress >= TimeSpan.FromMilliseconds(500))
                    {
                        job.BytesPerSecond = job.Bytes / Math.Max(timer.Elapsed.TotalSeconds, .001);
                        await SaveProgress(job, token); lastProgress = timer.Elapsed;
                    }
                }
                await output.FlushAsync(token);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (total.HasValue && job.Bytes != total) throw new IOException("Incomplete response body.");
            var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.IsNullOrWhiteSpace(snapshot.ExpectedSha256) &&
                !string.Equals(snapshot.ExpectedSha256.Trim(), sha, StringComparison.OrdinalIgnoreCase)) throw new ChecksumException();
            var filename = Component(snapshot.OriginalFileName);
            var relative = Path.Combine("applications", Component(snapshot.AppId), Component(snapshot.SourceId), Component(snapshot.Channel),
                Component(snapshot.Version), Component(snapshot.Os + "-" + snapshot.Architecture), sha, filename);
            paths.AssertLibrary(library);
            var destination = paths.ForLibrary(library, relative, false);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            destination = paths.ForLibrary(library, relative, false);
            if (File.Exists(destination))
            {
                await using var existing = File.OpenRead(paths.ForLibrary(library, relative));
                var existingHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(existing, token));
                if (existingHash != sha) throw new IOException("An existing archive has unexpected content.");
                File.Delete(stage);
            }
            else File.Move(stage, destination); // Same filesystem, immutable unique content path; never replace.

            var now = DateTime.UtcNow;
            var verification = string.IsNullOrWhiteSpace(snapshot.ExpectedSha256) ? "sha256-calculated" : "expected-sha256-verified";
            var originalUrl = JobSafety.RedactUrl(snapshot.Url);
            var resolvedUrl = JobSafety.RedactUrl(response.RequestMessage?.RequestUri?.AbsoluteUri ?? snapshot.Url);
            var metadata = new { schemaVersion = 1, snapshot.AppId, snapshot.SourceId, snapshot.Channel, snapshot.VariantId,
                snapshot.AppName, snapshot.Version, snapshot.ReleaseId, snapshot.Os, snapshot.Architecture, snapshot.Format,
                snapshot.Requirements, snapshot.OriginalFileName, snapshot.ArtifactType, originalUrl, resolvedUrl,
                size = job.Bytes, sha256 = sha, verification, downloadedAt = now, dependencyStatus = "unknown", offlineTested = false,
                entityTag = response.Headers.ETag?.ToString(), lastModified = response.Content.Headers.LastModified };
            var sidecar = paths.ForLibrary(library, relative + ".metadata.json", false);
            var sidecarStage = paths.ForLibrary(library, stageRelative + ".json", false);
            await File.WriteAllTextAsync(sidecarStage, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
            File.Move(sidecarStage, sidecar, true);
            var archive = await db.Files.FirstOrDefaultAsync(f => f.LibraryId == library.Id && f.RelativePath == relative, CancellationToken.None);
            if (archive == null)
            {
                archive = new ArchiveFile { LibraryId = library.Id, RelativePath = relative, AppId = snapshot.AppId, VariantId = snapshot.VariantId,
                    FileName = filename, Name = snapshot.AppName, Version = snapshot.Version, Os = snapshot.Os, Architecture = snapshot.Architecture,
                    ArtifactType = snapshot.ArtifactType, Managed = true, MetadataOrigin = "provider", Confidence = "source-supplied" };
                db.Files.Add(archive);
            }
            archive.Size = job.Bytes; archive.Sha256 = sha; archive.Missing = false;
            archive.ModifiedAt = File.GetLastWriteTimeUtc(destination); archive.DownloadedAt ??= now;
            archive.Verification = verification; archive.OriginalUrl = originalUrl; archive.ResolvedUrl = resolvedUrl; archive.ReleaseId = snapshot.ReleaseId;
            archive.EntityTag = response.Headers.ETag?.ToString(); archive.LastModified = response.Content.Headers.LastModified?.UtcDateTime;
            await db.SaveChangesAsync(CancellationToken.None);
            await SaveProgress(job, CancellationToken.None);
            var keep = await db.Apps.Where(a => a.Id == snapshot.AppId).Select(a => a.KeepVersions).FirstOrDefaultAsync(ct);
            await retention.ApplyAsync(library, snapshot.VariantId, keep, ct);
            await hub.Clients.All.SendAsync("changed", CancellationToken.None);
        }
        finally
        {
            try { paths.AssertLibrary(library); var cleanup = paths.ForLibrary(library, stageRelative, false); if (File.Exists(cleanup)) File.Delete(cleanup); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    async Task SaveProgress(Job job, CancellationToken ct)
    {
        await db.Jobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Bytes, job.Bytes)
            .SetProperty(x => x.TotalBytes, job.TotalBytes).SetProperty(x => x.BytesPerSecond, job.BytesPerSecond), ct);
        // Do not broadcast Payload: it contains the private immutable source URL.
        await hub.Clients.All.SendAsync("progress", new { job.Id, job.Kind, job.State, job.Bytes, job.TotalBytes, job.BytesPerSecond }, ct);
    }
}
