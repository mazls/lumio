using System.Net.Http.Headers;
using System.Text.Json;

namespace Lumio.Core.Updates;

/// <summary>A published Lumio release that is newer than the running version.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Url)
{
    public string DisplayVersion => Version.ToString(3);
}

/// <summary>
/// Looks for a newer Lumio release on GitHub. Only real releases count: the "latest release" endpoint ignores
/// drafts and pre-releases. Without internet (or when GitHub is unreachable) nothing happens.
/// </summary>
public static class UpdateChecker
{
    public const string ReleasesPage = "https://github.com/mazls/lumio/releases/latest";
    private const string LatestReleaseApi = "https://api.github.com/repos/mazls/lumio/releases/latest";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static int _started;

    public static Version CurrentVersion { get; } = Normalize(typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>The newer release found by the last check, or null.</summary>
    public static ReleaseInfo? AvailableUpdate { get; private set; }

    /// <summary>Raised (on a background thread) when a newer release was found.</summary>
    public static event Action<ReleaseInfo>? UpdateAvailable;

    /// <summary>Checks shortly after the start and then every few hours (a booth often runs for days).</summary>
    public static void StartPeriodicCheck()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(FirstCheckDelay).ConfigureAwait(false);
            while (true)
            {
                await CheckAsync().ConfigureAwait(false);
                await Task.Delay(CheckInterval).ConfigureAwait(false);
            }
        });
    }

    public static async Task<ReleaseInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Lumio", CurrentVersion.ToString(3)));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            // 404 = no release published yet, 403 = rate limit: both simply mean "no update"
            using var response = await http.GetAsync(LatestReleaseApi, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = json.RootElement;

            if (IsTrue(root, "draft") || IsTrue(root, "prerelease"))
                return null;
            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            if (tag is null || !TryParseVersion(tag, out var version) || version <= CurrentVersion)
                return null;

            var url = root.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() : null;
            var release = new ReleaseInfo(version, tag, url ?? ReleasesPage);
            AvailableUpdate = release;
            UpdateAvailable?.Invoke(release);
            return release;
        }
        catch
        {
            // Offline or GitHub unreachable: try again at the next interval
            return null;
        }
    }

    // Kept apart from the settings: saving settings restarts the camera on some platforms
    private static readonly string DismissedFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumio", "dismissed-update.txt");

    /// <summary>True if the start screen banner for this release was closed (it returns for the next release).</summary>
    public static bool IsDismissed(ReleaseInfo release)
    {
        try
        {
            return File.Exists(DismissedFile)
                   && string.Equals(File.ReadAllText(DismissedFile).Trim(), release.Tag, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void Dismiss(ReleaseInfo release)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DismissedFile)!);
            File.WriteAllText(DismissedFile, release.Tag);
        }
        catch
        {
            // Then the banner simply comes back after a restart
        }
    }

    /// <summary>Parses tags like "v1.3.4" or "1.4.0-beta" (the suffix is ignored).</summary>
    public static bool TryParseVersion(string tag, out Version version)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        var end = text.IndexOfAny(['-', '+', ' ']);
        if (end >= 0)
            text = text[..end];

        if (Version.TryParse(text, out var parsed) || (int.TryParse(text, out var major) && (parsed = new Version(major, 0)) is not null))
        {
            version = Normalize(parsed);
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    private static bool IsTrue(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}
