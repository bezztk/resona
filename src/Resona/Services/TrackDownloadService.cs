using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Text.Json;
using Resona.Models;
using System.Threading.Tasks;

namespace Resona.Services;

public class TrackDownloadService
{
    private static readonly HttpClient ImageClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    public async Task<(bool Success, string ErrorOutput)> RunYtDlpAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Values.TracksDirectory);
        var outputTemplate = Path.Combine(Values.TracksDirectory, "%(id)s.%(ext)s");

        var args = YtDlpDownloadArgs(
                "--js-runtimes", "node",
                "--no-playlist",
                "-f", "bestaudio[ext=m4a]/bestaudio/best[height<=360]/18",
                "-x",
                "--audio-format", "m4a",
                "--embed-thumbnail",
                "-o", outputTemplate,
                url);
        var result = await RunProcessAsync(ExternalToolLocator.Resolve("yt-dlp"), args, cancellationToken);
        if (result.ExitCode != 0 && IsForbiddenResponse(result.Error))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(800), cancellationToken);
            result = await RunProcessAsync(ExternalToolLocator.Resolve("yt-dlp"), args, cancellationToken);
        }

        return (result.ExitCode == 0, result.Error);
    }

    public async Task<int?> GetDurationAsync(string filePath)
    {
        try
        {
            var result = await RunProcessAsync(
                ExternalToolLocator.Resolve("ffprobe"),
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                filePath);

            if (result.ExitCode == 0
                && double.TryParse(result.Output.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds))
                return (int)seconds;
        }
        catch { }
        return null;
    }

    public async Task<string?> GetTitleAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunYouTubeProcessAsync(
                YtDlpArgs(
                    "--js-runtimes", "node",
                    "--no-playlist",
                    "--print", "title",
                    "--skip-download",
                    url),
                cancellationToken);

            var title = result.Output
                .Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0);

            return result.ExitCode == 0 ? title : null;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    public async Task<YouTubeTrackMetadata?> GetMetadataAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunYouTubeProcessAsync(
                YtDlpArgs("--js-runtimes", "node", "--no-playlist", "--skip-download", "--dump-single-json", url),
                cancellationToken);
            if (result.ExitCode != 0) return null;

            using var document = JsonDocument.Parse(result.Output);
            var root = document.RootElement;
            string? Value(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            return new YouTubeTrackMetadata(
                Value("title"), Value("channel_id"), Value("channel"), Value("channel_url"), PublishedDate(root),
                EstimatedAudioSize(root), DurationSeconds(root),
                IntegerValue(root, "view_count"), IntegerValue(root, "like_count"),
                IntegerValue(root, "channel_follower_count"), ThumbnailUrl(root));
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public async Task<(IReadOnlyList<YouTubePlaylistEntry> Entries, string? Error)> GetPlaylistEntriesAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        var source = YouTubeUrlNormalizer.ParseImportSource(url);
        if (source is null)
            return ([], "Only individual YouTube videos and explicit playlists are supported. YouTube Radio and Mix links are not supported.");

        try
        {
            var arguments = new List<string>
            {
                "--js-runtimes", "node",
                "--ignore-errors",
                "--flat-playlist",
                "--dump-json",
                "--no-warnings"
            };
            if (source.Value.Kind == YouTubeImportSourceKind.SingleTrack)
                arguments.Add("--no-playlist");
            arguments.Add(source.Value.Url);

            var result = await RunYouTubeProcessAsync(
                YtDlpArgs([.. arguments]),
                cancellationToken);

            var entries = ParsePlaylistEntries(source.Value.Url, result.Output);
            return entries.Count > 0
                ? (entries, null)
                : ([], CleanYtDlpError(result.Error));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { return ([], exception.Message); }
    }

    public async Task<(YouTubeChannelSnapshot? Snapshot, string? Error)> GetChannelSnapshotAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunYouTubeProcessAsync(
                YtDlpArgs(
                    "--js-runtimes", "node",
                    "--ignore-errors",
                    "--flat-playlist",
                    "--dump-single-json",
                    "--no-warnings",
                    NormalizeChannelVideosUrl(url)
                ),
                cancellationToken);

            if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Output))
                return (null, CleanYtDlpError(result.Error));

            using var document = JsonDocument.Parse(result.Output);
            var root = document.RootElement;
            var name = StringValue(root, "channel")
                       ?? StringValue(root, "uploader")
                       ?? StringValue(root, "title")
                       ?? "YouTube channel";
            var channelUrl = StringValue(root, "channel_url") ?? StringValue(root, "uploader_url") ?? url;
            var sourceUrl = StringValue(root, "webpage_url") ?? url;
            var videos = new List<YouTubeChannelVideoEntry>();
            if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                    AddChannelVideoEntry(entry, videos);
            }

            return (new YouTubeChannelSnapshot(
                sourceUrl,
                StringValue(root, "channel_id") ?? StringValue(root, "uploader_id"),
                name,
                channelUrl,
                videos,
                ThumbnailUrl(root),
                FollowerCount: IntegerValue(root, "channel_follower_count")), null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { return (null, exception.Message); }
    }

    public async Task<byte[]?> DownloadImageAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        try
        {
            var bytes = await ImageClient.GetByteArrayAsync(uri, cancellationToken);
            return bytes.Length is > 0 and <= 5_000_000 ? bytes : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch { return null; }
    }

    private static List<YouTubePlaylistEntry> ParsePlaylistEntries(string sourceUrl, string output)
    {
        var entries = new List<YouTubePlaylistEntry>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (document)
            {
                var root = document.RootElement;
                AddPlaylistEntry(sourceUrl, root, entries);
                if (root.TryGetProperty("entries", out var childEntries) && childEntries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var child in childEntries.EnumerateArray())
                        AddPlaylistEntry(sourceUrl, child, entries);
                }
            }
        }
        return entries;
    }

    private static void AddPlaylistEntry(string sourceUrl, JsonElement root, List<YouTubePlaylistEntry> entries)
    {
        if (IsUnavailablePlaylistEntry(root)) return;

        var id = StringValue(root, "id");
        if (string.IsNullOrWhiteSpace(id) || id.Length != 11)
            id = YouTubeUrlNormalizer.ExtractVideoId(StringValue(root, "url") ?? string.Empty);
        if (string.IsNullOrWhiteSpace(id)) return;

        var canonicalUrl = YouTubeUrlNormalizer.GetCanonicalUrl(id);
        if (entries.Any(entry => entry.CanonicalUrl.Equals(canonicalUrl, StringComparison.OrdinalIgnoreCase)))
            return;

        var title = StringValue(root, "title") ?? id;
        var duration = DurationSeconds(root);
        entries.Add(new YouTubePlaylistEntry(sourceUrl, canonicalUrl, title, duration));
    }

    private static void AddChannelVideoEntry(JsonElement root, List<YouTubeChannelVideoEntry> entries)
    {
        if (IsUnavailablePlaylistEntry(root)) return;

        var id = StringValue(root, "id");
        if (string.IsNullOrWhiteSpace(id) || id.Length != 11)
            id = YouTubeUrlNormalizer.ExtractVideoId(StringValue(root, "url") ?? string.Empty);
        if (string.IsNullOrWhiteSpace(id)) return;

        var canonicalUrl = YouTubeUrlNormalizer.GetCanonicalUrl(id);
        if (entries.Any(entry => entry.CanonicalUrl.Equals(canonicalUrl, StringComparison.OrdinalIgnoreCase)))
            return;

        entries.Add(new YouTubeChannelVideoEntry(
            id,
            canonicalUrl,
            StringValue(root, "title") ?? id,
            DurationSeconds(root),
            PublishedDate(root)));
    }

    private static bool IsUnavailablePlaylistEntry(JsonElement root)
    {
        var title = StringValue(root, "title");
        var availability = StringValue(root, "availability");
        var liveStatus = StringValue(root, "live_status");

        return title is "[Deleted video]" or "[Private video]"
               || availability is "private" or "premium_only" or "subscriber_only"
               || liveStatus == "is_upcoming";
    }

    private static string? CleanYtDlpError(string error)
    {
        var line = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            ?? error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

        if (string.IsNullOrWhiteSpace(line)) return null;
        return line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)
            ? line["ERROR:".Length..].Trim()
            : line.Trim();
    }

    private static bool IsForbiddenResponse(string output) =>
        output.Contains("403", StringComparison.OrdinalIgnoreCase)
        || output.Contains("forbidden", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeChannelVideosUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.EndsWith("/videos", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("/playlist", StringComparison.OrdinalIgnoreCase))
            return url;

        return uri.AbsolutePath.TrimEnd('/').Length > 0
            ? $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath.TrimEnd('/')}/videos"
            : url;
    }

    private static string? PublishedDate(JsonElement root)
        => NormalizeDate(StringValue(root, "release_date"))
           ?? NormalizeDate(StringValue(root, "upload_date"))
           ?? DateFromTimestamp(root, "timestamp");

    private static string? NormalizeDate(string? value)
    {
        if (value?.Length == 8 && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            return date.ToString("yyyy-MM-dd");
        return value;
    }

    private static string? DateFromTimestamp(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var timestamp))
            return null;

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd");
        }
        catch
        {
            return null;
        }
    }

    public string? FindDownloadedFile(string videoId)
    {
        return Directory.EnumerateFiles(Values.TracksDirectory)
                   .FirstOrDefault(file => string.Equals(
                       Path.GetFileNameWithoutExtension(file), videoId, StringComparison.Ordinal))
               ?? Directory.EnumerateFiles(Values.TracksDirectory)
                   .FirstOrDefault(file => Path.GetFileNameWithoutExtension(file)
                       .EndsWith($"[{videoId}]", StringComparison.Ordinal));
    }

    public void DeleteDownloadArtifacts(string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId) || !Directory.Exists(Values.TracksDirectory))
            return;

        foreach (var filePath in Directory.EnumerateFiles(Values.TracksDirectory))
        {
            var fileName = Path.GetFileName(filePath);
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            if (!string.Equals(baseName, videoId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(baseName, $"channel-{videoId}", StringComparison.OrdinalIgnoreCase)
                && !baseName.EndsWith($"[{videoId}]", StringComparison.OrdinalIgnoreCase))
                continue;

            try { File.Delete(filePath); }
            catch { }
        }
    }

    public string TitleFromFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var bracket = name.LastIndexOf('[');
        return bracket > 0 ? name[..bracket].Trim() : name;
    }

    private static long? EstimatedAudioSize(JsonElement root)
    {
        if (!root.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array)
            return null;

        var candidates = formats.EnumerateArray()
            .Where(format => Value(format, "acodec") is not "none"
                             && Value(format, "vcodec") == "none")
            .Select(format => new
            {
                Bitrate = Number(format, "abr") ?? 0,
                Size = Integer(format, "filesize") ?? Integer(format, "filesize_approx")
            })
            .Where(format => format.Size is > 0)
            .OrderByDescending(format => format.Bitrate)
            .FirstOrDefault();
        return candidates?.Size;

        static string? Value(JsonElement element, string key) => element.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        static double? Number(JsonElement element, string key) => element.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) ? number : null;
        static long? Integer(JsonElement element, string key) => element.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number) ? number : null;
    }

    private static int? DurationSeconds(JsonElement root) => root.TryGetProperty("duration", out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var seconds) && seconds > 0
            ? (int)Math.Round(seconds)
            : null;

    private static string? StringValue(JsonElement root, string key) => root.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? IntegerValue(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;

    private static string? ThumbnailUrl(JsonElement root)
    {
        if (StringValue(root, "thumbnail") is { Length: > 0 } direct)
            return direct;
        if (!root.TryGetProperty("thumbnails", out var thumbnails)
            || thumbnails.ValueKind != JsonValueKind.Array)
            return null;
        return thumbnails.EnumerateArray()
            .Reverse()
            .Select(item => StringValue(item, "url"))
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
    }

    private static string[] YtDlpArgs(params string[] args)
    {
        if (!Values.UseYtDlpBrowserCookies)
            return args;

        return ["--cookies-from-browser", Values.YtDlpCookiesBrowser, .. args];
    }

    private static string[] YtDlpDownloadArgs(params string[] args)
    {
        if (!ExternalToolLocator.TryResolve("ffmpeg", out var ffmpegPath))
            return YtDlpArgs(args);

        return YtDlpArgs(["--ffmpeg-location", ffmpegPath, .. args]);
    }

    private static Task<ProcessResult> RunYouTubeProcessAsync(
        string[] args,
        CancellationToken cancellationToken) =>
        RunProcessAsync(ExternalToolLocator.Resolve("yt-dlp"), args, cancellationToken);

    private static async Task<ProcessResult> RunProcessAsync(string fileName, params string[] args) =>
        await RunProcessAsync(fileName, args, CancellationToken.None);

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string[] args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception exception)
        {
            return new ProcessResult(-1, "", $"Could not start {Path.GetFileName(fileName)}: {exception.Message}");
        }

        if (process is null)
            return new ProcessResult(-1, "", $"Could not start {Path.GetFileName(fileName)}.");
        using (process)
        {
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync();
                throw;
            }

            return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
        }
    }

    private record ProcessResult(int ExitCode, string Output, string Error);
}
