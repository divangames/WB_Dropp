using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WBDropp.Models;

namespace WBDropp.Services;

public sealed class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/divangames/WB_Dropp/releases/latest";
    private static readonly HttpClient Http = CreateHttpClient();

    public Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken) =>
        CheckAsync(CurrentVersion, cancellationToken);

    public async Task<AppUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!TryParseVersion(tag, out var releaseVersion) || releaseVersion <= currentVersion) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? string.Empty;
            if (!name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;
            var url = asset.GetProperty("browser_download_url").GetString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var downloadUri) ||
                !string.Equals(downloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                continue;

            string? digest = null;
            if (asset.TryGetProperty("digest", out var digestElement))
            {
                var value = digestElement.GetString();
                if (value?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true)
                    digest = value[7..];
            }

            return new AppUpdate(releaseVersion, tag, name, downloadUri, digest);
        }

        return null;
    }

    public async Task<DownloadedAppUpdate> DownloadAsync(
        AppUpdate update,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WBDropp",
            "updates",
            update.Version.ToString(3));
        var safeAssetName = Path.GetFileName(update.AssetName);
        if (!safeAssetName.Equals(update.AssetName, StringComparison.Ordinal))
            throw new InvalidDataException("Имя файла обновления содержит небезопасный путь.");
        var zipPath = Path.Combine(updateRoot, safeAssetName);
        var payloadPath = Path.Combine(updateRoot, "payload");
        var expectedExe = Path.Combine(payloadPath, "WB Dropp.exe");

        if (File.Exists(expectedExe)) return new DownloadedAppUpdate(update, payloadPath);

        Directory.CreateDirectory(updateRoot);
        var partialPath = zipPath + ".download";
        if (File.Exists(partialPath)) File.Delete(partialPath);

        using (var response = await Http.GetAsync(update.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;
                if (length > 0) progress?.Report((int)Math.Clamp(total * 100 / length.Value, 0, 100));
            }
        }

        if (!string.IsNullOrWhiteSpace(update.Sha256))
        {
            await using var checksumStream = File.OpenRead(partialPath);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(checksumStream, cancellationToken));
            if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partialPath);
                throw new InvalidDataException("Контрольная сумма обновления не совпала.");
            }
        }

        File.Move(partialPath, zipPath, true);
        if (Directory.Exists(payloadPath)) Directory.Delete(payloadPath, true);
        Directory.CreateDirectory(payloadPath);
        ExtractSafely(zipPath, payloadPath);
        if (!File.Exists(expectedExe))
            throw new InvalidDataException("В архиве обновления не найден WB Dropp.exe.");

        progress?.Report(100);
        return new DownloadedAppUpdate(update, payloadPath);
    }

    public void InstallAndRestart(DownloadedAppUpdate update)
    {
        var targetDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь запущенного приложения.");
        var executableName = Path.GetFileName(processPath);
        var stagedExecutable = Path.Combine(update.PayloadDirectory, executableName);
        if (!File.Exists(stagedExecutable))
            throw new FileNotFoundException("В обновлении отсутствует исполняемый файл приложения.", stagedExecutable);

        var scriptPath = Path.Combine(Path.GetTempPath(), $"WBDropp-updater-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, UpdaterScript, new UTF8Encoding(false));

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-AppProcessId");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add("-Source");
        startInfo.ArgumentList.Add(update.PayloadDirectory);
        startInfo.ArgumentList.Add("-Target");
        startInfo.ArgumentList.Add(targetDirectory);
        startInfo.ArgumentList.Add("-ExecutableName");
        startInfo.ArgumentList.Add(executableName);

        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить установщик обновления.");
    }

    public static bool TryParseVersion(string tag, out Version version) =>
        Version.TryParse(tag.Trim().TrimStart('v', 'V'), out version!);

    private static void ExtractSafely(string zipPath, string destination)
    {
        var destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Архив обновления содержит небезопасный путь.");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WB-Dropp-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
        return client;
    }

    private const string UpdaterScript = """
param(
    [Parameter(Mandatory=$true)][int]$AppProcessId,
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Target,
    [Parameter(Mandatory=$true)][string]$ExecutableName
)
$ErrorActionPreference = 'Stop'
$log = Join-Path $env:LOCALAPPDATA 'WBDropp\logs\updater.log'
try {
    Wait-Process -Id $AppProcessId -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path (Split-Path $log) -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $Target $_.Name) -Recurse -Force
    }
    Start-Process -FilePath (Join-Path $Target $ExecutableName) -WorkingDirectory $Target
    "$(Get-Date -Format s) Update installed from $Source" | Add-Content -LiteralPath $log -Encoding UTF8
} catch {
    "$(Get-Date -Format s) Update failed: $($_.Exception.Message)" | Add-Content -LiteralPath $log -Encoding UTF8
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show("Не удалось установить обновление: $($_.Exception.Message)", 'WB Dropp') | Out-Null
    exit 1
} finally {
    Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
}
""";
}
