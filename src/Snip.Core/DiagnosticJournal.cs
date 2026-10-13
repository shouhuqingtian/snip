using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Snip.Core;

/// <summary>Bounded background logging. Callers supply technical metadata only, never user content.</summary>
public sealed class DiagnosticJournal : IDisposable, IAsyncDisposable
{
    private sealed record Pending(string? Line, TaskCompletionSource? Barrier = null);
    private readonly Channel<Pending> queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(512)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task writer;
    private readonly object sync = new();
    private readonly Queue<string> recent = new();
    private readonly long maxBytes;
    private readonly int retainedFiles;
    private long dropped, writeFailures;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string DirectoryPath { get; }
    public DiagnosticJournal(string directory, long maxBytes = 2 * 1024 * 1024, int retainedFiles = 5)
    {
        if (maxBytes < 1024 || retainedFiles < 1 || retainedFiles > 10) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        DirectoryPath = Path.GetFullPath(directory); this.maxBytes = maxBytes; this.retainedFiles = retainedFiles;
        writer = Task.Run(ConsumeAsync);
    }
    public DiagnosticOperation Begin(string kind, object? fields = null) => new(this, kind, fields);
    public void Record(string kind, string stage, object? fields = null, Exception? error = null, string? operationId = null)
    {
        try
        {
            var line = JsonSerializer.Serialize(new { TimestampUtc = DateTimeOffset.UtcNow, SessionId, OperationId = operationId,
                Kind = kind, Stage = stage, Fields = fields, Error = error is null ? null : Describe(error, 0) });
            if (Encoding.UTF8.GetByteCount(line) > Math.Min(16 * 1024, maxBytes / 2) || !queue.Writer.TryWrite(new(line)))
                Interlocked.Increment(ref dropped);
        }
        catch (Exception) { Interlocked.Increment(ref dropped); } // Diagnostics must never break the operation being diagnosed.
    }
    private static object Describe(Exception error, int depth) => new
    {
        Type = error.GetType().FullName, error.HResult,
        // Exception messages, Data, source filenames and local values can contain private content.
        Stack = new StackTrace(error, false).GetFrames()?.Take(48).Select(f =>
            { var method = f.GetMethod(); return method?.DeclaringType?.FullName + "." + method?.Name; }).ToArray(),
        Inner = depth < 7 && error.InnerException is { } inner ? Describe(inner, depth + 1) : null
    };
    private string FilePath(int index) => Path.Combine(DirectoryPath, index == 0 ? "events.jsonl" : $"events.{index}.jsonl");
    private async Task ConsumeAsync()
    {
        await foreach (var item in queue.Reader.ReadAllAsync())
        {
            if (item.Barrier is { } barrier) { barrier.TrySetResult(); continue; }
            lock (sync)
            {
                recent.Enqueue(item.Line!); while (recent.Count > 200) recent.Dequeue();
                try
                {
                    Directory.CreateDirectory(DirectoryPath);
                    var path = FilePath(0);
                    if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(item.Line!) + 1 > maxBytes)
                    {
                        File.Delete(FilePath(retainedFiles - 1));
                        for (int i = retainedFiles - 2; i >= 0; i--) if (File.Exists(FilePath(i))) File.Move(FilePath(i), FilePath(i + 1), true);
                    }
                    File.AppendAllText(path, item.Line + "\n", new UTF8Encoding(false));
                }
                catch (Exception) { Interlocked.Increment(ref writeFailures); }
            }
        }
    }
    public async Task FlushAsync(CancellationToken token = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(new(null, completion), token).ConfigureAwait(false);
        await completion.Task.WaitAsync(token).ConfigureAwait(false);
    }
    public async Task ExportAsync(string destination, object environment, CancellationToken token = default)
    {
        await FlushAsync(token).ConfigureAwait(false);
        await Task.Run(() =>
        {
            var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    lock (sync)
                    {
                        void Text(string name, string text)
                        { using var entry = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); entry.Write(text); }
                        Text("environment.json", JsonSerializer.Serialize(new { SchemaVersion = 1, ExportedUtc = DateTimeOffset.UtcNow,
                            SessionId, DroppedEntries = Interlocked.Read(ref dropped), FileWriteFailures = Interlocked.Read(ref writeFailures), Environment = environment }, new JsonSerializerOptions { WriteIndented = true }));
                        Text("README.txt", "自用截图诊断包：环境信息、阶段/耗时和脱敏异常堆栈。\n不包含截图、识别原文、译文、网页地址、窗口标题、联系人、密钥或用户名。\nrecent-session.jsonl 是内存保留的最新记录，可能与轮转日志重复。\n");
                        Text("recent-session.jsonl", string.Join('\n', recent) + "\n");
                        for (int i = 0; i < retainedFiles; i++)
                        {
                            token.ThrowIfCancellationRequested();
                            var path = FilePath(i);
                            if (File.Exists(path)) zip.CreateEntryFromFile(path, "logs/" + Path.GetFileName(path));
                        }
                    }
                }
                token.ThrowIfCancellationRequested(); File.Move(temporary, destination, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, token).ConfigureAwait(false);
    }
    public void Dispose() { queue.Writer.TryComplete(); writer.Wait(TimeSpan.FromSeconds(1)); }
    public async ValueTask DisposeAsync() { queue.Writer.TryComplete(); await writer.ConfigureAwait(false); }
}

public sealed class DiagnosticOperation : IDisposable
{
    private readonly DiagnosticJournal journal;
    private readonly string kind;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly object sync = new();
    private long previous;
    private bool ended;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    internal DiagnosticOperation(DiagnosticJournal journal, string kind, object? fields)
    { this.journal = journal; this.kind = kind; Stage("started", fields); }
    public void Stage(string stage, object? fields = null)
    {
        lock (sync)
        {
            if (ended) return;
            long now = elapsed.ElapsedMilliseconds, before = previous; previous = now;
            journal.Record(kind, stage, new { ElapsedMs = now, StageElapsedMs = now - before, Metadata = fields }, operationId: Id);
        }
    }
    private void End(string stage, object? fields = null, Exception? error = null)
    {
        lock (sync)
        {
            if (ended) return; ended = true;
            journal.Record(kind, stage, new { ElapsedMs = elapsed.ElapsedMilliseconds, Metadata = fields }, error, Id);
        }
    }
    public void Complete(object? fields = null) => End("completed", fields);
    public void Cancel(string reason) => End("cancelled", new { Reason = reason });
    public void Fail(Exception error) => End("failed", error: error);
    public void Dispose() => End("abandoned");
}
