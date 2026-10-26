using System.Formats.Tar;
using System.IO.Compression;
using R2Cmd;

string root = Path.Combine(Path.GetTempPath(), "R2Cmd-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
int failed = 0;

try
{
    foreach (string format in new[] { ".tar.gz", ".tar", ".zip" })
    {
        string archive = CreateArchive(format,
            ("Config.json", "upper"), ("config.json", "lower"),
            ("Src/a.txt", "AAA"), ("src/b.txt", "BBBBB"));

        Check(format + ": distinct directory names and exact totals", () =>
        {
            var children = ArchiveService.ListChildren(archive, "");
            Equal(2, children.Count(n => n.IsFolder));
            Equal("a.txt", ArchiveService.ListChildren(archive, "Src").Single().Name);
            Equal("b.txt", ArchiveService.ListChildren(archive, "src").Single().Name);
            Equal((1, 3L), ArchiveService.SumTree(archive, "Src"));
            Equal((1, 5L), ArchiveService.SumTree(archive, "src"));
        });

        Check(format + ": selected files use exact archive keys", () =>
        {
            string output = NewDirectory();
            int written = ArchiveService.ExtractFiles(archive, new Dictionary<string, string>
            {
                ["Config.json"] = Path.Combine(output, "first.json"),
                ["config.json"] = Path.Combine(output, "second.json")
            });
            Equal(2, written);
            Equal("upper", File.ReadAllText(Path.Combine(output, "first.json")));
            Equal("lower", File.ReadAllText(Path.Combine(output, "second.json")));
        });

        Check(format + ": extract only the exact folder", () =>
        {
            string output = NewDirectory();
            ArchiveService.ExtractFolder(archive, "src", output);
            Equal("BBBBB", File.ReadAllText(Path.Combine(output, "b.txt")));
            Equal(1, Directory.GetFiles(output, "*", SearchOption.AllDirectories).Length);
        });

        Check(format + ": overwrite skip keeps data and continues", () =>
        {
            string output = NewDirectory();
            string first = Path.Combine(output, "first.json");
            File.WriteAllText(first, "existing");
            int prompts = 0;
            int written = ArchiveService.ExtractFiles(archive, new Dictionary<string, string>
            {
                ["Config.json"] = first,
                ["config.json"] = Path.Combine(output, "second.json")
            }, _ => { prompts++; return false; });
            Equal(1, prompts);
            Equal(1, written);
            Equal("existing", File.ReadAllText(first));
            Equal("lower", File.ReadAllText(Path.Combine(output, "second.json")));
        });

        Check(format + ": pre-canceled work creates no output", () =>
        {
            string output = Path.Combine(root, Guid.NewGuid().ToString("N"));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Throws<OperationCanceledException>(() => ArchiveService.ExtractFolder(archive, "", output, token: cts.Token));
            Throws<OperationCanceledException>(() => ArchiveService.ExtractFile(archive, "Config.json", output, cts.Token));
            Throws<OperationCanceledException>(() => ArchiveService.SumTree(archive, "", cts.Token));
            Throws<OperationCanceledException>(() => ArchiveService.ListChildren(archive, "", cts.Token));
            Equal(false, Directory.Exists(output) || File.Exists(output));
        });

        Check(format + ": cancellation stops within a file", () =>
        {
            string large = CreateArchive(format, ("large.bin", new string('x', 4 * 1024 * 1024)), ("later.txt", "later"));
            string output = NewDirectory();
            using var cts = new CancellationTokenSource();
            Throws<OperationCanceledException>(() => ArchiveService.ExtractFolder(large, "", output,
                onProgress: (_, copied, _) => { if (copied > 0) cts.Cancel(); }, token: cts.Token));
            long length = new FileInfo(Path.Combine(output, "large.bin")).Length;
            Equal(true, length > 0 && length < 4 * 1024 * 1024);
            Equal(false, File.Exists(Path.Combine(output, "later.txt")));
        });

        Check(format + ": archive traversal is rejected", () =>
        {
            string unsafeArchive = CreateArchive(format, ("../escape.txt", "outside"));
            string output = NewDirectory();
            Throws<IOException>(() => ArchiveService.ExtractFolder(unsafeArchive, "", output));
            Equal(false, File.Exists(Path.Combine(root, "escape.txt")));
        });

        Check(format + ": remote entry stream preserves both spellings", () =>
        {
            async Task Verify()
            {
                var contentByKey = new Dictionary<string, string>(StringComparer.Ordinal);
                await foreach (var (node, content) in ArchiveService.ReadEntriesAsync(archive,
                    node => node.FullKey is "Config.json" or "config.json"))
                {
                    using var reader = new StreamReader(content!, leaveOpen: true);
                    contentByKey[node.FullKey] = await reader.ReadToEndAsync();
                }
                Equal("upper", contentByKey["Config.json"]);
                Equal("lower", contentByKey["config.json"]);
            }
            Verify().GetAwaiter().GetResult();
        });

        if (OperatingSystem.IsWindows())
        {
            Check(format + ": conflicting Windows file paths fail before writing", () =>
            {
                string output = NewDirectory();
                string destination = Path.Combine(output, "Config.json");
                File.WriteAllText(destination, "existing");
                Throws<IOException>(() => ArchiveService.ExtractFiles(archive, new Dictionary<string, string>
                {
                    ["Config.json"] = destination,
                    ["config.json"] = Path.Combine(output, "config.json")
                }));
                Equal("existing", File.ReadAllText(destination));
            });

            Check(format + ": conflicting Windows folders cannot merge", () =>
            {
                string folders = CreateArchive(format, ("Src/a.txt", "first"), ("src/b.txt", "second"));
                string output = NewDirectory();
                Throws<IOException>(() => ArchiveService.ExtractFolder(folders, "", output));
                Equal("first", File.ReadAllText(Path.Combine(output, "Src", "a.txt")));
                Equal(false, File.Exists(Path.Combine(output, "src", "b.txt")));
            });
        }
    }

    Check("TAR.GZ: selected and skipped large entries use bounded memory", () =>
    {
        const int size = 64 * 1024 * 1024;
        string dataPath = Path.Combine(root, "payload.bin");
        using (var file = File.Create(dataPath))
        {
            byte[] block = new byte[64 * 1024];
            for (int i = 0; i < size / block.Length; i++) file.Write(block);
        }
        string archive = Path.Combine(root, "large.tar.gz");
        using (var gzip = new GZipStream(File.Create(archive), CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip))
        {
            using var data = File.OpenRead(dataPath);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "large.bin") { DataStream = data });
            using var tail = new MemoryStream("tail"u8.ToArray());
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "tail.txt") { DataStream = tail });
        }

        foreach (string key in new[] { "large.bin", "tail.txt" })
        {
            string output = Path.Combine(NewDirectory(), "result");
            long before = GC.GetAllocatedBytesForCurrentThread();
            ArchiveService.ExtractFile(archive, key, output);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Equal(true, allocated < 8 * 1024 * 1024);
            Equal(key == "large.bin" ? size : 4L, new FileInfo(output).Length);
            Console.WriteLine($"  {key}: allocated {allocated / 1024:N0} KiB for a 64 MiB archive entry");
        }
    });
}
finally
{
    Directory.Delete(root, recursive: true);
}

Console.WriteLine($"{passed} checks passed; {failed} failed.");
return failed == 0 ? 0 : 1;

void Check(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
}

void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}

string NewDirectory()
{
    string path = Path.Combine(root, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

string CreateArchive(string format, params (string Key, string Content)[] files)
{
    string path = Path.Combine(root, Guid.NewGuid().ToString("N") + format);
    if (format == ".zip")
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (key, content) in files)
        {
            using var writer = new StreamWriter(archive.CreateEntry(key).Open());
            writer.Write(content);
        }
    }
    else
    {
        using var file = File.Create(path);
        using Stream output = format == ".tar.gz" ? new GZipStream(file, CompressionLevel.Fastest) : file;
        using var writer = new TarWriter(output);
        foreach (var (key, content) in files)
        {
            using var data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, key) { DataStream = data });
        }
    }
    return path;
}
