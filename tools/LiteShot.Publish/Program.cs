// LiteShot release publisher: signing key management, upload + signed channel manifest, verification.
// See Usage() below. Signing contract: ECDSA P-256, SHA-256, IEEE P1363 signature over the UTF-8 bytes of
// the payload string; the envelope { payload, signature } is stored and served verbatim by the update server.
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiteShot.Publish;

record ReleaseFile(string Kind, string Name, long Size, string Sha256);
record Payload(string Product, string Version, string Channel, string Published, string Notes, List<ReleaseFile> Files);
record Envelope(string Payload, string Signature);

sealed class UsageException(string message) : Exception(message);
sealed class FailException(string message) : Exception(message);

static class Program
{
    const string Product = "LiteShot";
    const string P256Oid = "1.2.840.10045.3.1.7";
    static readonly string DefaultPrivateKey = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Devgm", "LiteShot Publishing", "update-signing-key.pem");
    static readonly string DefaultTokenFile = Path.Combine(Path.GetDirectoryName(DefaultPrivateKey)!, "publish-token.txt");
    const string DefaultServer = "https://liteshot-updates.tight-dawn-4128.workers.dev";
    static readonly string[] PublicKeyInRepo = ["src", "LiteShot", "update-public-key.pem"];
    static readonly Regex VersionRe = new(@"^\d+\.\d+\.\d+$");
    static readonly Regex ChannelRe = new("^[a-z0-9-]{1,20}$");
    static readonly Regex FileRe = new("^[A-Za-z0-9._-]{1,100}$");
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keeps the stored JSON readable; still valid JSON
    };

    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help") { Usage(); return args.Length == 0 ? 2 : 0; }
        try
        {
            var o = Options.Parse(args.Skip(1), "force", "download");
            return args[0] switch
            {
                "keygen" => Keygen(o),
                "publish" => await Publish(o),
                "verify" => await Verify(o),
                _ => throw new UsageException($"unknown command '{args[0]}'"),
            };
        }
        catch (UsageException e) { Console.Error.WriteLine($"error: {e.Message}\n"); Usage(); return 2; }
        catch (Exception e) { Console.Error.WriteLine($"error: {e.Message}"); return 1; }
    }

    static void Usage() => Console.WriteLine("""
        LiteShot.Publish <command> [options]

          keygen [--force] [--public-key <pem>] [--key <pem>]
              Creates the ECDSA P-256 update-signing key (or, if it already exists, keeps it and rewrites the
              public key from it). --force replaces an existing key; the old file is renamed, not deleted.

          publish --server <url> [--token <t>] [--channel stable] [--notes "<text>"] [--dist <folder>]
                  [--version x.y.z] [--key <pem>] [--public-key <pem>] [--mac <LiteShot-mac-x.y.z.zip>]
              Uploads dist\LiteShot.exe and dist\LiteShot-Setup-<version>.msi, then signs and publishes the
              channel manifest. --mac publishes the Mac app's zip instead, to channel "mac", versioned by its
              file name. Token defaults to %LITESHOT_PUBLISH_TOKEN%, then publish-token.txt next to the key.
              --server defaults to the live update server. --version (for testing) skips reading
              the exe version and publishes whichever of the two files exist.

          verify --server <url> [--channel stable] [--public-key <pem>] [--download]
              Fetches /v1/latest, checks the signature, prints the payload; --download also checks every file's
              size and SHA-256. Exits non-zero on any failure.

        Defaults: private key %APPDATA%\Devgm\LiteShot Publishing\update-signing-key.pem,
                  public key <repo>\src\LiteShot\update-public-key.pem, dist <repo>\dist.
        """);

    // ---------------------------------------------------------------- keygen

    static int Keygen(Options o)
    {
        o.Allow("force", "public-key", "key");
        var keyPath = Path.GetFullPath(o.Get("key") ?? DefaultPrivateKey);
        var publicPath = Path.GetFullPath(o.Get("public-key") ?? RepoPath("public-key", PublicKeyInRepo));

        ECDsa key;
        if (File.Exists(keyPath) && !o.Has("force"))
        {
            key = LoadKey(keyPath);
            Console.WriteLine("A signing key already exists; keeping it and rewriting the public key from it (--force replaces it).");
        }
        else
        {
            if (File.Exists(keyPath))
            {
                var backup = $"{keyPath}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
                File.Move(keyPath, backup);
                Console.WriteLine($"Old private key moved to {backup}");
            }
            key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            using var w = new StreamWriter(new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write));
            w.Write(key.ExportPkcs8PrivateKeyPem() + "\n");
            Console.WriteLine("Generated a new ECDSA P-256 signing key.");
        }
        using (key)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(publicPath)!);
            File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem() + "\n");
            Console.WriteLine($"""

                Private key: {keyPath}
                Public key:  {publicPath}
                Fingerprint: {Fingerprint(key)}

                WARNING: back up the private key somewhere safe (password manager / offline media).
                Without it, existing installs can never receive another update: they only accept
                manifests signed by this key. Never commit it or share it.
                """);
        }
        return 0;
    }

    // ---------------------------------------------------------------- publish

    static async Task<int> Publish(Options o)
    {
        o.Allow("server", "token", "channel", "notes", "dist", "version", "key", "public-key", "mac");
        var server = ServerUrl(o);
        var token = o.Get("token") ?? Environment.GetEnvironmentVariable("LITESHOT_PUBLISH_TOKEN")
            ?? (File.Exists(DefaultTokenFile) ? File.ReadAllText(DefaultTokenFile).Trim() : null);
        if (string.IsNullOrWhiteSpace(token)) throw new UsageException($"no token: pass --token, set LITESHOT_PUBLISH_TOKEN or put it in {DefaultTokenFile}");
        var mac = o.Get("mac");
        var channel = mac is not null && !o.Has("channel") ? "mac" : Channel(o);
        var notes = o.Get("notes") ?? "";
        var dist = mac is not null ? Path.GetDirectoryName(Path.GetFullPath(mac))! : Path.GetFullPath(o.Get("dist") ?? RepoPath("dist", "dist"));
        using var key = LoadKey(o.Get("key") ?? DefaultPrivateKey);
        try { key.ExportParameters(true); }
        catch (CryptographicException) { throw new FailException("the signing key file has no private key"); }

        // Refuse to publish something installed copies would reject.
        var publicPath = o.Get("public-key") ?? TryRepoPath(PublicKeyInRepo);
        if (publicPath is not null && (File.Exists(publicPath) || o.Has("public-key")))
        {
            using var pub = LoadKey(publicPath);
            if (!pub.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()))
                throw new FailException($"the signing key does not match {publicPath}; the app would reject this release");
        }

        string version;
        var testVersion = o.Get("version");
        // The Mac app is built on a Mac and versioned on its own; its zip's name carries the version.
        var macMatch = mac is null ? null : Regex.Match(Path.GetFileName(mac), @"^LiteShot-mac-(\d+\.\d+\.\d+)\.zip$");
        if (macMatch is { Success: false }) throw new UsageException("--mac must be a LiteShot-mac-<x.y.z>.zip from mac/scripts/bundle.sh");
        if (macMatch is not null) version = macMatch.Groups[1].Value;
        else if (testVersion is not null)
        {
            if (!VersionRe.IsMatch(testVersion)) throw new UsageException("--version must look like 1.2.3");
            version = testVersion;
        }
        else version = ExeVersion(Path.Combine(dist, "LiteShot.exe"));

        var wanted = mac is not null
            ? new[] { (Kind: "mac", Name: Path.GetFileName(mac)) }
            : new[] { (Kind: "portable", Name: "LiteShot.exe"), (Kind: "msi", Name: $"LiteShot-Setup-{version}.msi") };
        var missing = wanted.Where(f => !File.Exists(Path.Combine(dist, f.Name))).Select(f => f.Name).ToList();
        if ((testVersion is null || mac is not null) && missing.Count > 0) throw new FailException($"missing in {dist}: {string.Join(", ", missing)}");
        if (missing.Count == wanted.Length) throw new FailException($"neither {string.Join(" nor ", missing)} found in {dist}");

        using var http = Http(token);
        var files = new List<ReleaseFile>();
        foreach (var (kind, name) in wanted.Where(f => !missing.Contains(f.Name)))
        {
            var path = Path.Combine(dist, name);
            Console.Write($"Hashing {name}... ");
            var (size, sha256) = await HashFile(path);
            Console.Write($"uploading {Mb(size)}... ");
            var watch = Stopwatch.StartNew();
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            using var request = new HttpRequestMessage(HttpMethod.Put, $"{server}/v1/releases/{version}/{name}")
            {
                Content = new StreamContent(stream, 1 << 20),
            };
            request.Content.Headers.ContentLength = size;
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Headers.Add("X-Content-SHA256", sha256); // lets the server verify the upload
            var result = await SendJson(http, request);
            if (result.GetProperty("size").GetInt64() != size) throw new FailException($"server stored a different size for {name}");
            Console.WriteLine($"done ({watch.Elapsed.TotalSeconds:0.0}s)");
            files.Add(new ReleaseFile(kind, name, size, sha256));
        }

        var published = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var payload = JsonSerializer.Serialize(new Payload(Product, version, channel, published, notes, files), Json);
        var signature = key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256); // IEEE P1363, 64 bytes
        var envelope = JsonSerializer.Serialize(new Envelope(payload, Convert.ToBase64String(signature)), Json);

        using (var request = new HttpRequestMessage(HttpMethod.Put, $"{server}/v1/channels/{channel}"))
        {
            request.Content = new StringContent(envelope, new UTF8Encoding(false), "application/json");
            await SendJson(http, request);
        }

        Console.WriteLine($"\nPublished {Product} {version} to channel '{channel}' on {server}");
        foreach (var f in files)
            Console.WriteLine($"  {f.Kind,-8} {f.Name}  {Mb(f.Size)}  sha256 {f.Sha256}\n           {server}/v1/download/{version}/{f.Name}");
        return 0;
    }

    static string ExeVersion(string exe)
    {
        if (!File.Exists(exe)) throw new FailException($"{exe} not found");
        var text = FileVersionInfo.GetVersionInfo(exe).FileVersion;
        if (!Version.TryParse(text, out var v)) throw new FailException($"cannot read a file version from {exe} ('{text}')");
        return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
    }

    // ---------------------------------------------------------------- verify

    static async Task<int> Verify(Options o)
    {
        o.Allow("server", "channel", "public-key", "download");
        var server = ServerUrl(o);
        var channel = Channel(o);
        using var key = LoadKey(o.Get("public-key") ?? RepoPath("public-key", PublicKeyInRepo));
        using var http = Http(null);

        var url = $"{server}/v1/latest?channel={channel}";
        using var response = await http.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new FailException($"GET {url}: {(int)response.StatusCode} {body}");

        var envelope = JsonSerializer.Deserialize<Envelope>(body, Json);
        if (envelope?.Payload is null || envelope.Signature is null) throw new FailException("response is not a { payload, signature } envelope");
        if (!key.VerifyData(Encoding.UTF8.GetBytes(envelope.Payload), Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256))
            throw new FailException("SIGNATURE INVALID: the manifest was not signed by this public key (or was altered)");
        Console.WriteLine($"OK    signature valid (key {Fingerprint(key)})");

        var payload = JsonSerializer.Deserialize<Payload>(envelope.Payload, Json) ?? throw new FailException("payload is empty");
        using (var doc = JsonDocument.Parse(envelope.Payload))
            Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions(Json) { WriteIndented = true }));

        var problems = new List<string>();
        if (payload.Product != Product) problems.Add($"product is '{payload.Product}'");
        if (payload.Channel != channel) problems.Add($"channel is '{payload.Channel}', expected '{channel}'");
        if (payload.Version is null || !VersionRe.IsMatch(payload.Version)) problems.Add($"bad version '{payload.Version}'");
        if (payload.Files is not { Count: > 0 }) problems.Add("no files");
        foreach (var f in payload.Files ?? [])
            if (f?.Name is null || !FileRe.IsMatch(f.Name) || f.Name.StartsWith('.') || f.Sha256 is null || f.Size < 0)
                problems.Add($"malformed file entry '{f?.Name}'");
        problems.ForEach(p => Console.Error.WriteLine($"FAIL  {p}"));

        if (o.Has("download") && problems.Count == 0)
        {
            foreach (var f in payload.Files!)
            {
                var fileUrl = $"{server}/v1/download/{payload.Version}/{f.Name}";
                string? problem = null;
                try
                {
                    var (size, sha256) = await HashDownload(http, fileUrl);
                    if (size != f.Size) problem = $"{f.Name}: downloaded {size} bytes, manifest says {f.Size}";
                    else if (sha256 != f.Sha256) problem = $"{f.Name}: SHA-256 {sha256} does not match manifest {f.Sha256}";
                    else Console.WriteLine($"OK    {f.Name}  {Mb(size)}  sha256 matches");
                }
                catch (Exception e) when (e is HttpRequestException or FailException or IOException or TaskCanceledException)
                {
                    problem = $"{f.Name}: {e.Message}";
                }
                if (problem is null) continue;
                problems.Add(problem);
                Console.Error.WriteLine($"FAIL  {problem}");
            }
        }
        Console.WriteLine(problems.Count == 0 ? "\nVerified." : $"\n{problems.Count} problem(s).");
        return problems.Count == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- helpers

    static ECDsa LoadKey(string path)
    {
        if (!File.Exists(path)) throw new FailException($"key file not found: {path} (run keygen)");
        var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(path));
        if (key.ExportParameters(false).Curve.Oid?.Value != P256Oid) throw new FailException($"{path} is not a P-256 key");
        return key;
    }

    static string Fingerprint(ECDsa key) => Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();

    static async Task<(long Size, string Sha256)> HashFile(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream);
        return (stream.Length, Convert.ToHexString(hash).ToLowerInvariant());
    }

    static async Task<(long Size, string Sha256)> HashDownload(HttpClient http, string url)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode) throw new FailException($"GET {url}: {(int)response.StatusCode}");
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long size = 0;
        for (int n; (n = await stream.ReadAsync(buffer)) > 0; size += n) sha.AppendData(buffer, 0, n);
        return (size, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
    }

    static HttpClient Http(string? token)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LiteShot.Publish/1.0");
        if (token is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    static async Task<JsonElement> SendJson(HttpClient http, HttpRequestMessage request)
    {
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new FailException($"{request.Method} {request.RequestUri}: {(int)response.StatusCode} {response.ReasonPhrase} {text}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    static string ServerUrl(Options o)
    {
        var raw = o.Get("server") ?? DefaultServer;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new UsageException($"--server must be an http(s) URL, got '{raw}'");
        if (uri.Scheme == "http" && !uri.IsLoopback) Console.Error.WriteLine("warning: --server is plain http; the publish token is sent unencrypted");
        return raw.TrimEnd('/');
    }

    static string Channel(Options o)
    {
        var channel = o.Get("channel") ?? "stable";
        return ChannelRe.IsMatch(channel) ? channel : throw new UsageException("--channel must match [a-z0-9-]{1,20}");
    }

    static string? TryRepoPath(params string[] parts)
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "LiteShot.sln"))) return Path.Combine([dir.FullName, .. parts]);
        return null;
    }

    static string RepoPath(string option, params string[] parts) =>
        TryRepoPath(parts) ?? throw new UsageException($"repo root (LiteShot.sln) not found; pass --{option}");

    static string Mb(long bytes) => $"{bytes / 1048576.0:0.0} MB";
}

sealed class Options
{
    readonly Dictionary<string, string?> values = new(StringComparer.Ordinal);

    public static Options Parse(IEnumerable<string> args, params string[] flags)
    {
        var o = new Options();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (!arg.StartsWith("--") || arg.Length < 3) throw new UsageException($"unexpected argument '{arg}'");
            var name = arg[2..];
            if (flags.Contains(name)) o.values[name] = null;
            else if (i + 1 < list.Count) o.values[name] = list[++i];
            else throw new UsageException($"--{name} needs a value");
        }
        return o;
    }

    public void Allow(params string[] names)
    {
        foreach (var name in values.Keys)
            if (!names.Contains(name)) throw new UsageException($"unknown option --{name}");
    }

    public bool Has(string name) => values.ContainsKey(name);
    public string? Get(string name) => values.GetValueOrDefault(name);
}
