using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Resona.Models;

namespace Resona.Services;

public sealed class TrackAnalysisService : IDisposable
{
    private static readonly TimeSpan DefaultAnalysisTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultHealthTimeout = TimeSpan.FromSeconds(10);
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".wav", ".flac"
    };
    private static readonly HashSet<string> ExcludedExperimentalModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "mtg_" + "jamen" + "do_" + "mood" + "theme"
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly Func<string?> _serverUrlProvider;
    private readonly TimeSpan _analysisTimeout;
    private readonly TimeSpan _healthTimeout;
    private readonly bool _ownsHttpClient;

    public TrackAnalysisService(
        HttpClient? httpClient = null,
        Func<string?>? serverUrlProvider = null,
        TimeSpan? analysisTimeout = null,
        TimeSpan? healthTimeout = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _serverUrlProvider = serverUrlProvider ?? (() => AppSettingsStore.Load().MusicAnalysisServerUrl);
        _analysisTimeout = analysisTimeout ?? DefaultAnalysisTimeout;
        _healthTimeout = healthTimeout ?? DefaultHealthTimeout;
        _ownsHttpClient = httpClient is null;

        // Cancellation is controlled explicitly so user cancellation and timeout can be distinguished.
        if (_ownsHttpClient)
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint("health");
        using var timeout = new CancellationTokenSource(_healthTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = await _httpClient.SendAsync(request, linked.Token);
            if (!response.IsSuccessStatusCode)
                throw ServerError(response);

            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: linked.Token);
            return HasHealthyStatus(document.RootElement);
        }
        catch (OperationCanceledException exception)
        {
            throw CancellationError(cancellationToken, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new MusicAnalysisException(
                MusicAnalysisErrorKind.ConnectionError,
                "The analysis server could not be reached.", exception);
        }
        catch (JsonException exception)
        {
            throw new MusicAnalysisException(
                MusicAnalysisErrorKind.InvalidResponse,
                "The analysis server returned an invalid health response.", exception);
        }
    }

    public async Task<MusicAnalysisResult> AnalyzeTrackAsync(
        string trackPath,
        CancellationToken cancellationToken = default)
    {
        var file = ValidateTrackFile(trackPath);
        var endpoint = BuildEndpoint("analyze");
        using var timeout = new CancellationTokenSource(_analysisTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await using var stream = file.OpenRead();
            using var form = new MultipartFormDataContent();
            using var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(ContentTypeFor(file.Extension));
            form.Add(fileContent, "file", file.Name);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
            using var response = await _httpClient.SendAsync(request, linked.Token);
            if (!response.IsSuccessStatusCode)
                throw ServerError(response);

            await using var responseStream = await response.Content.ReadAsStreamAsync(linked.Token);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: linked.Token);
            if (!TryReadSuccess(document.RootElement, out var success))
                throw new MusicAnalysisException(
                    MusicAnalysisErrorKind.InvalidResponse,
                    "The analysis server response is missing a valid success value.");

            var result = document.RootElement.Deserialize<MusicAnalysisResult>(JsonOptions);
            if (result is null)
                throw new MusicAnalysisException(
                    MusicAnalysisErrorKind.InvalidResponse,
                    "The analysis server returned an empty response.");
            result.PredictionShape ??= [];
            result.Predictions ??= [];
            result.ExperimentalPredictions ??= [];
            result.ExperimentalErrors ??= [];
            if (!success)
                throw new MusicAnalysisException(
                    MusicAnalysisErrorKind.ServerError,
                    string.IsNullOrWhiteSpace(result.Error)
                        ? "The analysis server reported that the analysis failed."
                        : $"The analysis server reported an error: {result.Error}");

            return result;
        }
        catch (MusicAnalysisException)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw CancellationError(cancellationToken, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new MusicAnalysisException(
                MusicAnalysisErrorKind.ConnectionError,
                "The analysis server could not be reached.", exception);
        }
        catch (JsonException exception)
        {
            throw new MusicAnalysisException(
                MusicAnalysisErrorKind.InvalidResponse,
                "The analysis server returned invalid JSON.", exception);
        }
        catch (IOException exception)
        {
            throw new MusicAnalysisException(
                MusicAnalysisErrorKind.FileError,
                "The audio file could not be read.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new MusicAnalysisException(
                MusicAnalysisErrorKind.FileError,
                "The audio file cannot be accessed.", exception);
        }
    }

    public static bool TryNormalizeServerUrl(string? value, out string normalizedUrl)
        => ServerUrlNormalizer.TryNormalize(value, out normalizedUrl);

    public static TrackAnalysisResult ToTrackAnalysisResult(MusicAnalysisResult result)
    {
        var predictions = result.Predictions
            .Select(prediction => TryCreatePrediction(prediction.Label, prediction.Score))
            .Where(prediction => prediction is not null)
            .Cast<TrackGenrePrediction>()
            .ToList();
        if (predictions.Count == 0)
            throw new MusicAnalysisException(
                MusicAnalysisErrorKind.InvalidResponse,
                "Analysis completed but returned no valid genre predictions.");

        var experimentalModels = result.ExperimentalPredictions
            .Where(model => !string.IsNullOrWhiteSpace(model.Model))
            .Where(model => !ExcludedExperimentalModels.Contains(model.Model!))
            .Select(model => new ExperimentalAnalysisModel(
                model.Family ?? "unknown",
                model.Category ?? "Other",
                model.Model!,
                model.Type ?? "classifier",
                model.Description ?? string.Empty,
                model.Values
                    .Where(value => !string.IsNullOrWhiteSpace(value.Label))
                    .Select(value => new ExperimentalAnalysisValue(value.Label!, value.Score))
                    .ToList()))
            .ToList();

        return new TrackAnalysisResult(
            result.Model ?? "unknown",
            predictions,
            result.Bpm,
            result.IntegratedLoudness,
            result.LoudnessRange,
            experimentalModels);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private Uri BuildEndpoint(string relativePath)
    {
        if (!TryNormalizeServerUrl(_serverUrlProvider(), out var normalizedUrl))
            throw new MusicAnalysisException(
                MusicAnalysisErrorKind.ConnectionError,
                "The analysis server address is missing or invalid.");

        return new Uri($"{normalizedUrl}/{relativePath}", UriKind.Absolute);
    }

    private static FileInfo ValidateTrackFile(string trackPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(trackPath))
                throw new MusicAnalysisException(MusicAnalysisErrorKind.FileError, "No audio file was selected.");

            var file = new FileInfo(trackPath);
            if (!file.Exists)
                throw new MusicAnalysisException(MusicAnalysisErrorKind.FileError, "The audio file does not exist.");
            if (!SupportedExtensions.Contains(file.Extension))
                throw new MusicAnalysisException(
                    MusicAnalysisErrorKind.FileError,
                    $"The audio format '{file.Extension}' is not supported.");
            return file;
        }
        catch (MusicAnalysisException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new MusicAnalysisException(MusicAnalysisErrorKind.FileError, "The audio file path is invalid.", exception);
        }
    }

    private static TrackGenrePrediction? TryCreatePrediction(string? label, double score)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var separator = label.IndexOf("---", StringComparison.Ordinal);
        if (separator <= 0 || separator == label.Length - 3) return null;
        return new TrackGenrePrediction(label[..separator], label[(separator + 3)..], score);
    }

    private static bool TryReadSuccess(JsonElement root, out bool success)
    {
        success = false;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, "success", StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                continue;

            success = property.Value.GetBoolean();
            return true;
        }

        return false;
    }

    private static bool HasHealthyStatus(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "status", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
                return string.Equals(property.Value.GetString(), "ok", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string ContentTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        ".flac" => "audio/flac",
        _ => "application/octet-stream"
    };

    private static MusicAnalysisException ServerError(HttpResponseMessage response) =>
        new(MusicAnalysisErrorKind.ServerError,
            $"The analysis server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "error"}).",
            statusCode: response.StatusCode);

    private static MusicAnalysisException CancellationError(
        CancellationToken userCancellationToken,
        OperationCanceledException exception) => userCancellationToken.IsCancellationRequested
        ? new MusicAnalysisException(MusicAnalysisErrorKind.Cancelled, "The analysis was cancelled.", exception)
        : new MusicAnalysisException(MusicAnalysisErrorKind.Timeout, "The analysis server request timed out.", exception);
}
