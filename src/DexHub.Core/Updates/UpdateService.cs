using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DexHub.Core.Updates;

/// <summary>Eine neuere Version auf GitHub.</summary>
public sealed record UpdateInfo(string Tag, string Name, DateTimeOffset Date, string DownloadUrl, long Size);

/// <summary>
/// Sucht auf GitHub nach neueren Releases, lädt das passende Paket und ersetzt das Programm.
/// Weil laufende Programmdateien (vor allem unter Windows) nicht überschrieben werden können,
/// übernimmt ein kleines Skript das Kopieren, sobald DexHub beendet ist, und startet es danach neu.
/// </summary>
public static class UpdateService
{
    private const string Repository = "Blauerbarsch/Pokedex";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DexHub-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Git-Commit, aus dem dieses Programm gebaut wurde (beim Bauen eingetragen).</summary>
    public static string? CurrentCommit
    {
        get
        {
            var version = typeof(UpdateService).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            int plus = version.IndexOf('+');
            return plus >= 0 && version.Length > plus + 7 ? version[(plus + 1)..] : null;
        }
    }

    /// <summary>Entwicklerversion (mit "dotnet run" aus dem Git-Ordner gestartet) – Update dann per "git pull".</summary>
    public static bool IsDevelopmentBuild
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return true;
            return false;
        }
    }

    /// <summary>Paketname für dieses System, z. B. "DexHub-linux-x64.zip".</summary>
    public static string AssetName => $"DexHub-{RuntimeInformation.RuntimeIdentifier}.zip";

    public static string CurrentVersionText =>
        IsDevelopmentBuild ? "Entwicklerversion" : CurrentCommit is { } c ? $"Build {c[..7]}" : "unbekannt";

    /// <summary>Gibt die neueste Release zurück, wenn sie neuer ist als dieses Programm; sonst null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var releases = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases", ct));

        foreach (var release in releases.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;

            string? url = null;
            long size = 0;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
                if (asset.GetProperty("name").GetString() == AssetName)
                {
                    url = asset.GetProperty("browser_download_url").GetString();
                    size = asset.GetProperty("size").GetInt64();
                }
            if (url is null) continue; // für dieses System noch kein Paket → nächste Release

            var tag = release.GetProperty("tag_name").GetString() ?? "";
            var name = release.GetProperty("name").GetString() is { Length: > 0 } n ? n : tag;

            // Welcher Commit steckt in der Release, und ist er neuer als unserer?
            var (releaseSha, releaseDate) = await CommitAsync(tag, ct);
            if (CurrentCommit is { } current)
            {
                if (releaseSha.StartsWith(current, StringComparison.OrdinalIgnoreCase) ||
                    current.StartsWith(releaseSha, StringComparison.OrdinalIgnoreCase))
                    return null; // schon aktuell
                var (_, currentDate) = await CommitAsync(current, ct);
                if (currentDate >= releaseDate) return null;
            }
            return new UpdateInfo(tag, name.Trim(), releaseDate, url, size);
        }
        return null;
    }

    private static async Task<(string Sha, DateTimeOffset Date)> CommitAsync(string reference, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repository}/commits/{reference}", ct));
        var root = doc.RootElement;
        var date = root.GetProperty("commit").GetProperty("committer").GetProperty("date").GetDateTimeOffset();
        return (root.GetProperty("sha").GetString() ?? "", date);
    }

    /// <summary>
    /// Lädt und entpackt das Update und startet das Austausch-Skript. Danach muss DexHub sich beenden.
    /// </summary>
    public static async Task PrepareAndLaunchAsync(UpdateInfo update, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var work = Path.Combine(Path.GetTempPath(), "DexHub-Update-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        Directory.CreateDirectory(work);
        var zip = Path.Combine(work, AssetName);

        progress?.Report($"Lade Update ({update.Size / 1024 / 1024} MB)…");
        await using (var source = await Http.GetStreamAsync(update.DownloadUrl, ct))
        await using (var target = File.Create(zip))
            await source.CopyToAsync(target, ct);

        progress?.Report("Entpacke…");
        var extracted = Path.Combine(work, "neu");
        ZipFile.ExtractToDirectory(zip, extracted);
        // Das Paket enthält einen Ordner "DexHub"
        var newFiles = Directory.Exists(Path.Combine(extracted, "DexHub")) ? Path.Combine(extracted, "DexHub") : extracted;

        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int pid = Environment.ProcessId;
        progress?.Report("Starte Austausch – DexHub startet gleich neu…");

        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(work, "update.ps1");
            await File.WriteAllTextAsync(script, $$"""
                Wait-Process -Id {{pid}} -ErrorAction SilentlyContinue
                Start-Sleep -Milliseconds 500
                Copy-Item -Path "{{newFiles}}\*" -Destination "{{installDir}}" -Recurse -Force
                Start-Process "{{Path.Combine(installDir, "DexHub.exe")}}"
                """, ct);
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        else
        {
            var script = Path.Combine(work, "update.sh");
            await File.WriteAllTextAsync(script, $$"""
                #!/bin/sh
                while kill -0 {{pid}} 2>/dev/null; do sleep 0.5; done
                cp -R "{{newFiles}}/." "{{installDir}}/"
                chmod +x "{{installDir}}/DexHub"
                nohup "{{installDir}}/DexHub" >/dev/null 2>&1 &
                """, ct);
            Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { script }, UseShellExecute = false });
        }
    }
}
