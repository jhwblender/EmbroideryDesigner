using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace EmbroideryDesigner;

public static class UpdateService
{
    private const string Owner = "jhwblender";
    private const string Repo = "EmbroideryDesigner";
    private const string AssetName = "EmbroideryDesigner.exe";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EmbroideryDesigner-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private record ReleaseInfo(Version Version, string TagName, string Body, string? AssetUrl);

    /// <summary>
    /// Looks at every non-draft, non-prerelease GitHub release newer than <paramref name="current"/>.
    /// Returns the newest version found, a changelog combining all skipped-over releases' notes
    /// (oldest first), and the download URL for the newest release's asset. Returns nulls if
    /// already up to date.
    /// </summary>
    public static async Task<(Version? latest, string changelog, string? assetUrl)> CheckForUpdateAsync(Version current)
    {
        var json = await Http.GetStringAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases");
        using var doc = JsonDocument.Parse(json);

        var newer = new List<ReleaseInfo>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            if (el.TryGetProperty("prerelease", out var p) && p.GetBoolean()) continue;

            var tag = el.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) continue;
            if (ver <= current) continue;

            string? assetUrl = null;
            if (el.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    if (a.GetProperty("name").GetString() == AssetName)
                    {
                        assetUrl = a.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }

            var body = el.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            newer.Add(new ReleaseInfo(ver, tag, body, assetUrl));
        }

        if (newer.Count == 0) return (null, "", null);

        newer.Sort((a, b) => a.Version.CompareTo(b.Version));
        var latest = newer[^1];
        if (latest.AssetUrl == null) return (null, "", null);

        var changelog = string.Join("\n\n", newer.Select(r => $"{r.TagName}\n{r.Body}".Trim()));
        return (latest.Version, changelog, latest.AssetUrl);
    }

    public static async Task<string> DownloadUpdateAsync(string assetUrl)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "EmbroideryDesignerUpdate");
        Directory.CreateDirectory(tempDir);
        var dest = Path.Combine(tempDir, AssetName);
        var bytes = await Http.GetByteArrayAsync(assetUrl);
        await File.WriteAllBytesAsync(dest, bytes);
        return dest;
    }

    /// <summary>
    /// Spawns a helper script that waits for this process to exit, replaces the running exe
    /// with the downloaded one, and relaunches it (passing the changelog along so the new
    /// instance can show a "what's new" notice). Then shuts this instance down.
    /// </summary>
    public static void ApplyUpdateAndRestart(string newExePath, string changelog, Version newVersion)
    {
        var currentExe = Process.GetCurrentProcess().MainModule!.FileName!;
        var pid = Environment.ProcessId;

        var tempDir = Path.Combine(Path.GetTempPath(), "EmbroideryDesignerUpdate");
        Directory.CreateDirectory(tempDir);
        var changelogPath = Path.Combine(tempDir, "changelog.txt");
        File.WriteAllText(changelogPath, changelog);

        var scriptPath = Path.Combine(tempDir, "apply-update.ps1");
        var script = $@"
$ErrorActionPreference = 'SilentlyContinue'
try {{ Wait-Process -Id {pid} -Timeout 30 }} catch {{}}
for ($i = 0; $i -lt 10; $i++) {{
    try {{
        Copy-Item -Path '{newExePath}' -Destination '{currentExe}' -Force
        break
    }} catch {{
        Start-Sleep -Milliseconds 500
    }}
}}
Start-Process -FilePath '{currentExe}' -ArgumentList '--updated-to={newVersion}', '--changelog=""{changelogPath}""'
";
        File.WriteAllText(scriptPath, script);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process.Start(psi);

        Environment.Exit(0);
    }
}
