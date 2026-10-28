using InstallerVault;
using InstallerVault.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Text;

namespace InstallerVault.Tests;

public class JobTests
{
    [Fact]
    public async Task Verified_download_publishes_hash_named_file_and_sidecar()
    {
        using var f = new Fixture(); await f.Seed();
        f.Variant.ExpectedSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        await f.Db.SaveChangesAsync();
        var job = await f.Queue.EnqueueAsync("download", f.Library, "app", "variant");
        await f.Downloader(new ResponseHttp("abc")).RunAsync(job, CancellationToken.None);
        var file = await f.Db.Files.SingleAsync();
        Assert.Equal("expected-sha256-verified", file.Verification);
        Assert.Equal("abc", await File.ReadAllTextAsync(Path.Combine(f.Root, file.RelativePath)));
        Assert.Contains("ba7816bf", file.RelativePath);
        Assert.True(File.Exists(Path.Combine(f.Root, file.RelativePath + ".metadata.json")));
    }

    [Fact]
    public async Task Checksum_failure_never_publishes_or_deletes_previous_copy()
    {
        using var f = new Fixture(); await f.Seed();
        await File.WriteAllTextAsync(Path.Combine(f.Root, "previous.exe"), "previous");
        f.Variant.ExpectedSha256 = new string('0', 64); await f.Db.SaveChangesAsync();
        var job = await f.Queue.EnqueueAsync("download", f.Library, "app", "variant");
        await Assert.ThrowsAsync<ChecksumException>(() => f.Downloader(new ResponseHttp("abc")).RunAsync(job, CancellationToken.None));
        Assert.Empty(await f.Db.Files.ToListAsync());
        Assert.Equal("previous", await File.ReadAllTextAsync(Path.Combine(f.Root, "previous.exe")));
        Assert.Empty(Directory.GetFiles(f.Root, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Failed_network_transfer_leaves_no_partial_or_archive()
    {
        using var f = new Fixture(); await f.Seed();
        var job = await f.Queue.EnqueueAsync("download", f.Library, "app", "variant");
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Downloader(new FailedHttp()).RunAsync(job, CancellationToken.None));
        Assert.Empty(await f.Db.Files.ToListAsync());
        Assert.Empty(Directory.GetFiles(f.Root, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Cancellation_during_transfer_removes_staging_and_keeps_archive_empty()
    {
        using var f = new Fixture(); await f.Seed();
        var job = await f.Queue.EnqueueAsync("download", f.Library, "app", "variant");
        using var cts = new CancellationTokenSource();
        var http = new ResponseHttp("abc", () => cts.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Downloader(http).RunAsync(job, cts.Token));
        Assert.Empty(await f.Db.Files.ToListAsync());
        Assert.Empty(Directory.GetFiles(f.Root, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Queue_deduplicates_active_variant_and_freezes_destination_and_artifact()
    {
        using var f = new Fixture();
        await f.Seed();
        var first = await f.Queue.EnqueueAsync("download", f.Library, "app", "variant");
        f.Variant.Url = "https://example.com/replaced.exe";
        f.Library.Path = "changed";
        await f.Db.SaveChangesAsync();
        var second = await f.Queue.EnqueueAsync("download", f.Library, "app", "variant");
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(f.Root, first.LibraryPath);
        Assert.Contains("installer.exe", first.Payload);
        Assert.DoesNotContain("replaced.exe", first.Payload);
    }

    [Fact]
    public async Task Cancelling_queued_job_persists_terminal_state()
    {
        using var f = new Fixture(); await f.Seed();
        var job = await f.Queue.EnqueueAsync("scan", f.Library);
        await f.Queue.CancelAsync(job.Id);
        await f.Db.Entry(job).ReloadAsync();
        Assert.True(job.CancelRequested);
        Assert.Equal("cancelled", job.State);
        Assert.NotNull(job.FinishedAt);
    }

    [Fact]
    public async Task Recovery_requeues_interrupted_jobs_but_keeps_requested_cancellation()
    {
        using var f = new Fixture(); await f.Seed();
        f.Db.Jobs.AddRange(new Job { State = "running", Bytes = 17 }, new Job { State = "running", CancelRequested = true });
        await f.Db.SaveChangesAsync();
        await f.Queue.RecoverAsync();
        var jobs = await f.Db.Jobs.AsNoTracking().ToListAsync();
        Assert.Contains(jobs, j => j.State == "queued" && j.Bytes == 0);
        Assert.Contains(jobs, j => j.State == "cancelled" && j.FinishedAt != null);
    }

    [Fact]
    public void Retention_preserves_pinned_unmanaged_and_last_two_distinct_versions()
    {
        var files = new[] {
            Archived("new", "2", 5), Archived("same-version-changed", "2", 4),
            Archived("previous", "1", 3), Archived("old", "0", 2),
            Archived("pin", "p", 1, pinned: true), Archived("unmanaged", "u", 0, managed: false)
        };
        Assert.Equal(new[] { "old" }, RetentionService.Candidates(files, 2).Select(x => x.Id));
        Assert.DoesNotContain(RetentionService.Candidates(files, 0), x => x.Id == "new");
    }

    static ArchiveFile Archived(string id, string version, int day, bool pinned = false, bool managed = true) => new()
    { Id = id, Version = version, Managed = managed, Pinned = pinned, DownloadedAt = new DateTime(2026, 1, 1).AddDays(day) };

    sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vault-jobs-" + Guid.NewGuid().ToString("N"));
        public ServiceProvider Services { get; }
        public VaultDb Db { get; }
        public JobQueue Queue { get; }
        public Library Library { get; }
        public Variant Variant { get; } = new() { Id = "variant", AppId = "app", SourceId = "source", Url = "https://example.com/installer.exe", FileName = "installer.exe", Version = "1", Tracked = true };
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var services = new ServiceCollection();
            services.AddLogging(); services.AddSignalR(); services.AddSingleton<JobCancellation>();
            services.AddDbContext<VaultDb>(o => o.UseSqlite("Data Source=" + Path.Combine(Root, "test.db")));
            services.AddTransient<JobQueue>();
            Services = services.BuildServiceProvider();
            Db = Services.GetRequiredService<VaultDb>(); Db.Database.EnsureCreated();
            Queue = Services.GetRequiredService<JobQueue>();
            Library = new Library { Path = Root, Active = true };
        }
        public async Task Seed()
        {
            await File.WriteAllTextAsync(Path.Combine(Root, ".installervault-library"), Library.Marker);
            Db.Libraries.Add(Library); Db.Apps.Add(new TrackedApp { Id = "app", Name = "Application" });
            Db.Sources.Add(new Source { Id = "source", AppId = "app", Provider = "direct", Identifier = Variant.Url });
            Db.Variants.Add(Variant); await Db.SaveChangesAsync();
        }
        public DownloadService Downloader(ISafeHttp http)
        {
            var options = Options.Create(new VaultOptions { AllowedRoots = [Root] });
            var guard = new PathGuard(options);
            return new DownloadService(Db, guard, http, options, Services.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<ProgressHub>>(), new RetentionService(Db, guard));
        }
        public void Dispose() { Services.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
    }
    sealed class ResponseHttp(string body, Action? onRead = null) : ISafeHttp
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            onRead?.Invoke();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)), RequestMessage = request });
        }
    }
    sealed class FailedHttp : ISafeHttp
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new HttpRequestException("private details https://example.com/?token=secret");
    }
}
