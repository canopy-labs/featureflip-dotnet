using Featureflip.Client.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;
using static Featureflip.Client.Tests.Snapshot.SnapshotFixtures;

namespace Featureflip.Client.Tests.Snapshot;

public sealed class FileSnapshotStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ff-snapshot-" + Guid.NewGuid().ToString("N"));
    private string PathIn(string name) => Path.Combine(_dir, name);

    public FileSnapshotStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PutThenGet_RoundTripsTheExactText()
    {
        var store = new FileSnapshotStore(PathIn("s.json"));
        const string text = "{\"a\":\"héllo ✓\"}";

        await store.PutAsync(text, Ct);

        Assert.Equal(text, await store.GetAsync(Ct));
    }

    [Fact]
    public async Task MissingFile_IsASilentMiss()
    {
        var logger = new LevelCapturingLogger();
        var store = new FileSnapshotStore(PathIn("nope.json"), logger);

        Assert.Null(await store.GetAsync(Ct));
        Assert.Empty(logger.Snapshot());
    }

    [Fact]
    public async Task DirectoryInPlaceOfTheFile_IsAWarnedMiss()
    {
        var logger = new LevelCapturingLogger();
        var store = new FileSnapshotStore(_dir, logger);

        Assert.Null(await store.GetAsync(Ct));
        Assert.Contains(logger.Snapshot(), e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task MissingParentDirectory_DropsTheWriteWithAWarning_AndCreatesNothing()
    {
        var logger = new LevelCapturingLogger();
        var parent = PathIn("missing");
        var store = new FileSnapshotStore(Path.Combine(parent, "s.json"), logger);

        await store.PutAsync("x", Ct);

        Assert.False(Directory.Exists(parent));
        Assert.Contains(logger.Snapshot(), e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Write_IsOwnerOnly_EvenWhenTheTargetExistedWithBroaderPermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = PathIn("s.json");
        await File.WriteAllTextAsync(path, "old", Ct);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        await new FileSnapshotStore(path).PutAsync("new", Ct);

        Assert.Equal("new", await File.ReadAllTextAsync(path, Ct));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task FreshWrite_IsOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = PathIn("fresh.json");

        await new FileSnapshotStore(path).PutAsync("x", Ct);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task ConcurrentPuts_LeaveOneCompleteFileAndNoTempFiles()
    {
        var path = PathIn("s.json");
        var store = new FileSnapshotStore(path);
        var texts = Enumerable.Range(0, 24).Select(i => new string((char)('a' + i % 26), 2000 + i)).ToArray();

        await Task.WhenAll(texts.Select(t => Task.Run(() => store.PutAsync(t, Ct), Ct)));

        Assert.Contains(await store.GetAsync(Ct), texts);
        Assert.Equal(new[] { path }, Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task FailedWrite_LeavesNoTempFile()
    {
        // The target is a non-empty directory, so the rename fails after the temp file exists.
        var path = PathIn("target");
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "inner"), "x", Ct);
        var logger = new LevelCapturingLogger();

        await new FileSnapshotStore(path, logger).PutAsync("x", Ct);

        Assert.Contains(logger.Snapshot(), e => e.Level == LogLevel.Warning);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void EmptyPath_IsRejected() => Assert.Throws<ArgumentException>(() => new FileSnapshotStore(" "));

    [Fact]
    public async Task ClientLogger_IsAttachedWhenTheStoreHasNone_AndTheFirstLoggerWins()
    {
        var first = new LevelCapturingLogger();
        var second = new LevelCapturingLogger();
        var store = new FileSnapshotStore(_dir);
        store.AttachLogger(first);
        store.AttachLogger(second);

        await store.GetAsync(Ct); // a directory: warns

        Assert.NotEmpty(first.Snapshot());
        Assert.Empty(second.Snapshot());
    }
}
