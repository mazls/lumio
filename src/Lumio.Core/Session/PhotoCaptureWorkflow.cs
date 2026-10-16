using Lumio.Core.Camera;
using Lumio.Core.Storage;

namespace Lumio.Core.Session;

public enum CaptureStage
{
    Countdown,
    Capturing,
    Saving,
    Completed,
}

public readonly record struct CaptureProgress(CaptureStage Stage, int SecondsRemaining = 0);

public sealed record CaptureResult(string FilePath, CapturedPhoto Photo);

/// <summary>Countdown → capture → save for a single photo.</summary>
public sealed class PhotoCaptureWorkflow
{
    private readonly EventStorage _storage;
    private readonly TimeProvider _time;

    public PhotoCaptureWorkflow(EventStorage storage, TimeProvider time)
    {
        _storage = storage;
        _time = time;
    }

    public async Task<CaptureResult> RunAsync(
        ICameraService camera,
        int countdownSeconds,
        IProgress<CaptureProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentOutOfRangeException.ThrowIfNegative(countdownSeconds);

        for (var remaining = countdownSeconds; remaining > 0; remaining--)
        {
            progress?.Report(new CaptureProgress(CaptureStage.Countdown, remaining));
            await Task.Delay(TimeSpan.FromSeconds(1), _time, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new CaptureProgress(CaptureStage.Capturing));
        var photo = await camera.CapturePhotoAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report(new CaptureProgress(CaptureStage.Saving));
        var path = await _storage.SaveOriginalAsync(photo, cancellationToken).ConfigureAwait(false);

        progress?.Report(new CaptureProgress(CaptureStage.Completed));
        return new CaptureResult(path, photo);
    }
}
