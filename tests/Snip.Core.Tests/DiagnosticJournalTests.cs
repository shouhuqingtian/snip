using System.IO.Compression;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class DiagnosticJournalTests
{
    [Fact]
    public async Task FlushAndExportCompleteWithoutPumpingTheCallingContext() => await Task.Run(CheckNonPumpingCaller);

    // Bounded synchronous waiting is the regression scenario; isolate it from xUnit's own context.
    private static void CheckNonPumpingCaller()
    {
        string folder = Temporary();
        try
        {
            using var journal = new DiagnosticJournal(Path.Combine(folder, "logs"));
            // Keep the background writer busy so that FlushAsync genuinely awaits its barrier.
            for (int i = 0; i < 500; i++) journal.Record("capture", "frame", new { Frame = i });
            var previous = SynchronizationContext.Current;
            var context = new NonPumpingContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var flush = journal.FlushAsync();
                Assert.True(flush.Wait(TimeSpan.FromSeconds(3)), "Log flush waited for a blocked UI context.");
                flush.GetAwaiter().GetResult();
                var export = journal.ExportAsync(Path.Combine(folder, "report.zip"), new { AppVersion = "test" });
                Assert.True(export.Wait(TimeSpan.FromSeconds(3)), "Diagnostic export waited for a blocked UI context.");
                export.GetAwaiter().GetResult();
                Assert.Equal(0, context.PostCount);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            Assert.True(File.Exists(Path.Combine(folder, "report.zip")));
        }
        finally { Directory.Delete(folder, true); }
    }
    private sealed class NonPumpingContext : SynchronizationContext
    {
        private int posts;
        public int PostCount => Volatile.Read(ref posts);
        public override void Post(SendOrPostCallback callback, object? state) => Interlocked.Increment(ref posts);
    }
    [Fact]
    public async Task ExportPreservesOperationTimelineAndRedactsExceptionContent()
    {
        string folder = Temporary();
        try
        {
            await using var journal = new DiagnosticJournal(Path.Combine(folder, "logs"));
            using var operation = journal.Begin("ocr", new { Width = 120 });
            operation.Stage("recognizing");
            try { ThrowPrivateError(); } catch (Exception error) { operation.Fail(error); }
            string path = Path.Combine(folder, "report.zip");
            await journal.ExportAsync(path, new { AppVersion = "test", DisplayCount = 2 });
            using var zip = ZipFile.OpenRead(path);
            using var reader = new StreamReader(zip.GetEntry("recent-session.jsonl")!.Open());
            string text = reader.ReadToEnd();
            Assert.DoesNotContain("PRIVATE", text);
            Assert.Contains(nameof(ThrowPrivateError), text);
            var entries = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Assert.Equal(new[] { "started", "recognizing", "failed" }, entries.Select(e => e.RootElement.GetProperty("Stage").GetString()));
                Assert.All(entries, e => Assert.Equal(operation.Id, e.RootElement.GetProperty("OperationId").GetString()));
                Assert.Equal(typeof(InvalidOperationException).FullName, entries[^1].RootElement.GetProperty("Error").GetProperty("Type").GetString());
            }
            finally { foreach (var entry in entries) entry.Dispose(); }
            Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith(".png"));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task ConcurrentLateProgressCannotAppearAfterCancellation()
    {
        string folder = Temporary();
        try
        {
            await using var journal = new DiagnosticJournal(folder);
            using var operation = journal.Begin("translation");
            await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
            { if (i == 7) operation.Cancel("user"); else operation.Stage("progress", new { Segment = i }); })));
            operation.Complete(); operation.Stage("late-progress");
            await journal.FlushAsync();
            var entries = (await File.ReadAllLinesAsync(Path.Combine(folder, "events.jsonl"))).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Assert.Equal("cancelled", entries[^1].RootElement.GetProperty("Stage").GetString());
                Assert.Single(entries, e => e.RootElement.GetProperty("Stage").GetString() == "cancelled");
                Assert.DoesNotContain(entries, e => e.RootElement.GetProperty("Stage").GetString() is "completed" or "late-progress");
                Assert.All(entries[..^1], e => Assert.True(e.RootElement.GetProperty("Fields").GetProperty("StageElapsedMs").GetInt64() >= 0));
            }
            finally { foreach (var entry in entries) entry.Dispose(); }
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task RotationBoundsDiskUsageAndKeepsLatestRecords()
    {
        string folder = Temporary();
        try
        {
            await using var journal = new DiagnosticJournal(folder, maxBytes: 1024, retainedFiles: 3);
            for (int i = 0; i < 60; i++) journal.Record("capture", "frame", new { Frame = i });
            await journal.FlushAsync();
            var files = Directory.GetFiles(folder, "*.jsonl");
            Assert.Equal(3, files.Length);
            Assert.All(files, f => Assert.True(new FileInfo(f).Length <= 1024));
            Assert.Contains("\"Frame\":59", await File.ReadAllTextAsync(Path.Combine(folder, "events.jsonl")));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task UnwritableLogDirectoryStillAllowsExportingMemoryDiagnostics()
    {
        string folder = Temporary();
        try
        {
            string blocked = Path.Combine(folder, "blocked"); await File.WriteAllTextAsync(blocked, "not a directory");
            await using var journal = new DiagnosticJournal(blocked);
            journal.Record("app", "started");
            string path = Path.Combine(folder, "report.zip"); await journal.ExportAsync(path, new { AppVersion = "test" });
            using var zip = ZipFile.OpenRead(path);
            using var reader = new StreamReader(zip.GetEntry("environment.json")!.Open());
            using var environment = JsonDocument.Parse(await reader.ReadToEndAsync());
            Assert.True(environment.RootElement.GetProperty("FileWriteFailures").GetInt64() > 0);
            using var recent = new StreamReader(zip.GetEntry("recent-session.jsonl")!.Open());
            Assert.Contains("started", await recent.ReadToEndAsync());
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task CancelledExportPreservesExistingDestination()
    {
        string folder = Temporary();
        try
        {
            await using var journal = new DiagnosticJournal(Path.Combine(folder, "logs"));
            string path = Path.Combine(folder, "report.zip"); await File.WriteAllTextAsync(path, "keep-existing");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.ExportAsync(path, new { AppVersion = "test" }, cancelled.Token));
            Assert.Equal("keep-existing", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(folder, "*.partial-*"));
        }
        finally { Directory.Delete(folder, true); }
    }
    private static string Temporary() { string folder = Path.Combine(Path.GetTempPath(), "Snip.Diagnostics.Tests-" + Guid.NewGuid()); Directory.CreateDirectory(folder); return folder; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowPrivateError() => throw new InvalidOperationException("PRIVATE OCR text C:/PRIVATE/account", new IOException("PRIVATE contact"));
}
