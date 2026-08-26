using InfraAdvisor.Mobile.Models;

namespace InfraAdvisor.Mobile.Services.Media;

public interface IMediaInputService
{
    bool IsRecording { get; }

    Task<AttachmentItem?> PickAsync(CancellationToken cancellationToken = default);

    Task StartRecordingAsync(CancellationToken cancellationToken = default);

    Task<AttachmentItem> StopRecordingAsync(CancellationToken cancellationToken = default);

    Task CancelRecordingAsync();

    Task PlayAsync(AttachmentItem item);

    Task RemoveAsync(AttachmentItem item);
}
