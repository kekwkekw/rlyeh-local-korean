using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Rlyeh.Localizer.Core;

public sealed class TranslationService
{
    private readonly record struct Job(string Source, string Scope, string? Context, Action<string?, string?>? Done, string? ObservedSource = null);
    private readonly Channel<Job> queue = Channel.CreateBounded<Job>(256);
    private readonly Channel<Job> uiQueue = Channel.CreateBounded<Job>(256);
    private readonly AutoResetEvent wake = new(false);
    private readonly ConcurrentQueue<Action> completions = new();
    private readonly ConcurrentDictionary<string, string> cache = new();
    private Dictionary<string, string>? readyExact;
    private readonly string root;
    private readonly string snapshot;
    private readonly Action<string> log;
    private readonly int delayMs;
    public TextCatalog Catalog { get; }
    public TranslationService(string root, string snapshot, Action<string> log, int delayMs = 0)
    {
        this.root = root; this.snapshot = snapshot; this.log = log;
        this.delayMs = Math.Clamp(delayMs, 0, 10000);
        try { Catalog = TextCatalog.Load(Path.Combine(root, "glossary", "text-catalog.json")); }
        catch (Exception ex) { log("Text catalog rejected; hdTrans remains available: " + ex.Message); Catalog = new(new TextCatalog.Document()); }
        Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public bool Request(string source, string scope, Action<string?, string?> done, string? context = null)
    {
        if (TryGetReady(source, scope, out string translation, context))
        { completions.Enqueue(() => done(translation, null)); return true; }
        string observedSource = source;
        if (scope == "ui")
        {
            if (Catalog.LookupUi(source, context, out _) < 0)
            { completions.Enqueue(() => done(null, "Ambiguous UI rules; source retained")); return true; }
            if (UiDecoration.TryParse(source, out var decoration))
            {
                if (Catalog.LookupUi(decoration.Body, context, out _) < 0)
                { completions.Enqueue(() => done(null, "Ambiguous decorated UI rules; source retained")); return true; }
                source = decoration.Body;
                var originalDone = done;
                done = (value, error) =>
                {
                    if (error != null || value == null) originalDone(null, error);
                    else if (decoration.TryRestore(value, out string result)) originalDone(result, null);
                    else originalDone(null, "UI decoration controls changed; source retained");
                };
            }
        }
        bool accepted = (scope == "ui" ? uiQueue : queue).Writer.TryWrite(new(source, scope, context, done, observedSource));
        if (accepted) wake.Set();
        return accepted;
    }
    // The worker publishes its revision only after loading the matching files.
    // The diagnostic delay still exercises the asynchronous engine path.
    public bool TryGetReady(string source, string scope, out string translation, string? context = null)
    {
        if (scope == "ui")
        {
            int match = Catalog.LookupUi(source, context, out translation);
            if (match != 0) return match == 1;
        }
        if (TryStored(source, scope, out translation, context)) return true;
        if (scope != "ui" || !UiDecoration.TryParse(source, out var decoration)) return false;
        int innerMatch = Catalog.LookupUi(decoration.Body, context, out string inner);
        if (innerMatch < 0) return false;
        if (innerMatch == 1 || TryStored(decoration.Body, scope, out inner, context))
            return decoration.TryRestore(inner, out translation);
        return false;
    }
    private static string CacheKey(string source, string scope)
        // Only newly supported UI tags need a new namespace. Existing story/plain
        // UI cache entries retain their revision and remain immediately available.
        => (scope == "ui" && source.Contains('<') && new DisplayText(source, ui: true).UsesUiExtensions
            ? "ui-tmp-v1" : scope) + "\u001f" + source;
    private bool TryStored(string source, string scope, out string translation, string? context)
    {
        translation = "";
        var exact = Volatile.Read(ref readyExact);
        if (delayMs != 0 || exact == null) return false;
        string key = CacheKey(source, scope);
        if (!cache.TryGetValue(key, out translation!) && !exact.TryGetValue(key, out translation!) &&
            !exact.TryGetValue(source, out translation!))
        {
            translation = "";
            return false;
        }
        // Preserve UI context evidence. The worker owns disk writes.
        if (scope == "ui" && uiQueue.Writer.TryWrite(new(source, scope, context, null))) wake.Set();
        return true;
    }
    public void Drain() { for (int n = 0; n < 64 && completions.TryDequeue(out var action); n++) action(); }

    private void Run()
    {
        try
        {
            string exports = Path.Combine(root, "glossary");
            string native = Path.Combine(root, "native");
            string user = Path.Combine(native, "hdtrans-data", "user");
            Directory.CreateDirectory(user);
            // Snapshot glossary per launch so hdTrans auto-reload cannot invalidate the cache silently.
            foreach (string name in new[] { "names.txt", "userdict.txt" })
                File.Copy(Path.Combine(exports, name), Path.Combine(user, name), true);
            File.WriteAllText(Path.Combine(user, "hdtrans.config.yaml"), "ehnd: false\nstrict_dict: true\nspeech_levels: auto\n");
            var exact = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(exports, "exact.json"))) ?? new();
            using var engine = new HdTransEngine(Path.Combine(native, "hdTransEngine.dll"), snapshot);
            using var snapshotFile = File.OpenRead(snapshot);
            using var hasher = SHA256.Create();
            string revisionMaterial = engine.Version + DisplayText.PolicyVersion + Convert.ToHexString(hasher.ComputeHash(snapshotFile));
            foreach (string file in new[] { "names.txt", "userdict.txt", "exact.json", "manifest.json" }) revisionMaterial += File.ReadAllText(Path.Combine(exports, file));
            string textCatalog = Path.Combine(exports, "text-catalog.json");
            if (File.Exists(textCatalog)) revisionMaterial += File.ReadAllText(textCatalog);
            string revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revisionMaterial)));
            string cachePath = Path.Combine(root, "cache", revision + ".jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            if (File.Exists(cachePath)) foreach (string line in File.ReadLines(cachePath))
            {
                try { var entry = JsonSerializer.Deserialize<string[]>(line); if (entry?.Length == 2) cache[entry[0]] = entry[1]; } catch (JsonException) { }
            }
            using var writer = new StreamWriter(cachePath, true, new UTF8Encoding(false)) { AutoFlush = true };
            string observationsPath = Path.Combine(root, "cache", "ui-observations.jsonl");
            var observed = new HashSet<string>();
            if (File.Exists(observationsPath)) foreach (string line in File.ReadLines(observationsPath))
            {
                try { using var row = JsonDocument.Parse(line); observed.Add(row.RootElement.GetProperty("id").GetString()!); }
                catch (JsonException) { }
                if (observed.Count >= 10000) break;
            }
            using var observations = new StreamWriter(observationsPath, true, new UTF8Encoding(false)) { AutoFlush = true };
            Volatile.Write(ref readyExact, exact);
            completions.Enqueue(() => log("hdTrans ready " + engine.Version + "; cache=" + cache.Count + "; glossary=" + revision[..12]));
            while (true)
            {
                if (!queue.Reader.TryRead(out var job) && !uiQueue.Reader.TryRead(out job)) { wake.WaitOne(); continue; }
                try
                {
                    string key = CacheKey(job.Source, job.Scope);
                    // UI context is evidence for future reviews; general hdTrans output can share its cache.
                    if (job.Scope == "ui" && observed.Count < 10000)
                    {
                        string observedSource = job.ObservedSource ?? job.Source;
                        string observationId = TextCatalog.Hash((job.Context ?? "") + "\u001f" + observedSource);
                        if (observed.Add(observationId)) observations.WriteLine(JsonSerializer.Serialize(new { id = observationId, source = observedSource, context = job.Context }));
                    }
                    var done = job.Done;
                    if (done == null) continue;
                    if (delayMs > 0) Thread.Sleep(delayMs);
                    if (!cache.TryGetValue(key, out string? value))
                    {
                        if (!exact.TryGetValue(key, out value) && !exact.TryGetValue(job.Source, out value))
                        {
                            var doc = new DisplayText(job.Source, ui: job.Scope == "ui");
                            if (doc.HasUnsupportedTag) throw new InvalidOperationException("Unsupported display tag; source retained");
                            value = string.IsNullOrWhiteSpace(doc.Plain) ? job.Source : doc.Restore(engine.Translate(doc.Plain));
                        }
                        writer.WriteLine(JsonSerializer.Serialize(new[] { key, value }));
                        cache[key] = value;
                    }
                    string translated = value;
                    completions.Enqueue(() => done(translated, null));
                }
                catch (Exception ex)
                {
                    if (job.Done is { } done) completions.Enqueue(() => done(null, ex.Message));
                }
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref readyExact, null);
            string error = ex.ToString(); completions.Enqueue(() => log("LOCAL ENGINE FAILED: " + error));
            queue.Writer.TryComplete(ex);
            uiQueue.Writer.TryComplete(ex);
            while (queue.Reader.TryRead(out var pending))
                if (pending.Done is { } done) completions.Enqueue(() => done(null, error));
            while (uiQueue.Reader.TryRead(out var pending))
                if (pending.Done is { } done) completions.Enqueue(() => done(null, error));
        }
    }
}
