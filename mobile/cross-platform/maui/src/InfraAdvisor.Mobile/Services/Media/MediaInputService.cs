using InfraAdvisor.Mobile.Models;
using Plugin.Maui.Audio;

namespace InfraAdvisor.Mobile.Services.Media;

/// <summary>
/// Uses platform pickers and microphone APIs only after a user action. Temporary recordings live in the OS cache and are never included in telemetry.
/// </summary>
public sealed class MediaInputService(IAudioManager audioManager) : IMediaInputService
{
    private static readonly FilePickerFileType SupportedFileTypes = new(new Dictionary<DevicePlatform, IEnumerable<string>>
    {
        [DevicePlatform.Android] = ["image/jpeg", "image/png", "image/webp", "audio/wav", "audio/mpeg", "audio/ogg", "audio/webm"],
        [DevicePlatform.iOS] = ["public.jpeg", "public.png", "org.webmproject.webp", "com.microsoft.waveform-audio", "public.mp3", "org.xiph.ogg-audio", "org.webmproject.webm"],
    });

    private IAudioRecorder? recorder;
    private IAudioPlayer? player;
    private Stream? playbackStream;
    private string? recordingPath;

    public bool IsRecording => recorder?.IsRecording == true;

    public async Task<AttachmentItem?> PickAsync(CancellationToken cancellationToken = default)
    {
        var result = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Choose an image or audio file", FileTypes = SupportedFileTypes });
        cancellationToken.ThrowIfCancellationRequested();
        if (result is null)
        {
            return null;
        }

        await using var stream = await result.OpenReadAsync();
        var mimeType = NormalizeMimeType(result.ContentType, result.FileName);
        var kind = MediaValidator.Validate(mimeType, stream.Length);
        return new AttachmentItem
        {
            DisplayName = result.FileName,
            Kind = kind,
            MimeType = mimeType,
            SizeBytes = stream.Length,
            OpenReadAsync = result.OpenReadAsync,
        };
    }

    public async Task StartRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (IsRecording)
        {
            return;
        }

        var permission = await Permissions.RequestAsync<Permissions.Microphone>();
        cancellationToken.ThrowIfCancellationRequested();
        if (permission != PermissionStatus.Granted)
        {
            throw new ApiException("Microphone access is required to record audio. Enable it in system settings and try again.", category: "microphone_denied");
        }

        recordingPath = Path.Combine(FileSystem.CacheDirectory, $"infra-advisor-{Guid.NewGuid():N}.wav");
        var options = new AudioRecorderOptions { SampleRate = 44100, Channels = ChannelType.Mono, BitDepth = BitDepth.Pcm16bit, Encoding = Plugin.Maui.Audio.Encoding.Wav, ThrowIfNotSupported = true };
        recorder = audioManager.CreateRecorder(options);
        await recorder.StartAsync(recordingPath, options);
    }

    public async Task<AttachmentItem> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (recorder is null || recordingPath is null || !recorder.IsRecording)
        {
            throw new ApiException("No audio recording is active.", category: "recording_state");
        }

        await recorder.StopAsync();
        cancellationToken.ThrowIfCancellationRequested();
        var path = recordingPath;
        var size = new FileInfo(path).Length;
        recorder = null;
        recordingPath = null;
        try
        {
            MediaValidator.Validate("audio/wav", size);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
        return new AttachmentItem { DisplayName = "Recorded question.wav", Kind = "audio", MimeType = "audio/wav", SizeBytes = size, TemporaryPath = path, OpenReadAsync = () => Task.FromResult<Stream>(File.OpenRead(path)) };
    }

    public async Task CancelRecordingAsync()
    {
        if (recorder?.IsRecording == true)
        {
            await recorder.StopAsync();
        }

        if (recordingPath is { } path && File.Exists(path))
        {
            File.Delete(path);
        }

        recorder = null;
        recordingPath = null;
    }

    public async Task PlayAsync(AttachmentItem item)
    {
        player?.Stop();
        player?.Dispose();
        playbackStream?.Dispose();
        playbackStream = await item.OpenReadAsync();
        player = audioManager.CreatePlayer(playbackStream);
        player.Play();
    }

    public Task RemoveAsync(AttachmentItem item)
    {
        if (item.TemporaryPath is { } path && File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private static string NormalizeMimeType(string? value, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(value) && value != "application/octet-stream")
        {
            return value;
        }

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".ogg" => "audio/ogg",
            ".webm" => "audio/webm",
            _ => "application/octet-stream",
        };
    }
}
