using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace InstallerVault.Jobs;

[Authorize]
public class ProgressHub : Hub { }

public sealed class JobCancellation
{
    readonly ConcurrentDictionary<string, CancellationTokenSource> active = new();
    public void Register(string id, CancellationTokenSource source) => active[id] = source;
    public void Remove(string id) => active.TryRemove(id, out _);
    public void Cancel(string id) { if (active.TryGetValue(id, out var source)) { try { source.Cancel(); } catch (ObjectDisposedException) { } } }
}

public sealed record DownloadSnapshot(string AppId, string AppName, string SourceId, string Channel,
    string VariantId, string Version, string ReleaseId, string Os, string Architecture, string Format,
    string Url, string OriginalFileName, string? ExpectedSha256, string ArtifactType, string? Requirements);

public class JobQueue(VaultDb db, IHubContext<ProgressHub> hub, JobCancellation cancellation)
{
    static readonly SemaphoreSlim enqueueGate = new(1);
    public async Task<Job> EnqueueAsync(string kind, Library library, string? appId = null, string? variantId = null,
        string? payload = null, CancellationToken ct = default)
    {
        if (kind is not ("scan" or "check" or "download")) throw new ArgumentException("Unknown job kind.");
        await enqueueGate.WaitAsync(ct);
        try
        {
            var existing = await db.Jobs.FirstOrDefaultAsync(x => x.Kind == kind && x.LibraryId == library.Id &&
                x.AppId == appId && x.VariantId == variantId && (x.State == "queued" || x.State == "running"), ct);
            if (existing != null) return existing;
            if (kind == "download")
            {
                var variant = await db.Variants.SingleAsync(x => x.Id == variantId, ct);
                var source = await db.Sources.SingleAsync(x => x.Id == variant.SourceId, ct);
                var app = await db.Apps.SingleAsync(x => x.Id == variant.AppId, ct);
                appId = app.Id;
                payload = JsonSerializer.Serialize(new DownloadSnapshot(app.Id, app.Name, source.Id, source.Channel,
                    variant.Id, variant.Version, variant.ReleaseId, variant.Os, variant.Architecture, variant.Format,
                    variant.Url, variant.FileName, variant.ExpectedSha256, variant.ArtifactType, variant.Requirements));
            }
            var job = new Job { Kind = kind, LibraryId = library.Id, LibraryPath = library.Path,
                LibraryMarker = library.Marker, AppId = appId, VariantId = variantId, Payload = payload ?? "{}" };
            db.Jobs.Add(job); await db.SaveChangesAsync(ct);
            await hub.Clients.All.SendAsync("changed", ct);
            return job;
        }
        finally { enqueueGate.Release(); }
    }

    public async Task CancelAsync(string id, CancellationToken ct = default)
    {
        await db.Jobs.Where(x => x.Id == id && (x.State == "queued" || x.State == "running"))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CancelRequested, true), ct);
        cancellation.Cancel(id);
        await db.Jobs.Where(x => x.Id == id && x.State == "queued")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "cancelled").SetProperty(x => x.FinishedAt, DateTime.UtcNow), ct);
        await hub.Clients.All.SendAsync("changed", ct);
    }

    public async Task RecoverAsync(CancellationToken ct = default)
    {
        await db.Jobs.Where(x => (x.State == "running" || x.State == "queued") && x.CancelRequested)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "cancelled").SetProperty(x => x.FinishedAt, DateTime.UtcNow), ct);
        await db.Jobs.Where(x => x.State == "running")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "queued").SetProperty(x => x.Bytes, 0L)
                .SetProperty(x => x.BytesPerSecond, 0d).SetProperty(x => x.Error, "Interrupted by restart; restarting transfer."), ct);
    }
}

public static class JobSafety
{
    public static string RedactUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "";
        return new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.AbsoluteUri;
    }
    public static string Error(Exception exception) => exception switch {
        ChecksumException => "Expected SHA-256 did not match. The previous archive was preserved.",
        DownloadLimitException => "Download exceeds the configured maximum size.",
        HttpRequestException => "Remote request failed. Check the source, connectivity and provider rate limits.",
        UnauthorizedAccessException => "Library access denied or unsafe path rejected.",
        OperationCanceledException => "Operation cancelled or timed out.",
        IOException => "Storage operation failed. Check library availability, permissions and free space.",
        _ => "Operation failed. Check the source configuration and library availability."
    };
}

public sealed class ChecksumException : IOException { }
public sealed class DownloadLimitException : IOException { }
