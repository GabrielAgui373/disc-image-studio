namespace DiscImageStudio.Burning;

/// <summary>
/// The portable device contract over the IMAPI burner (Task 13.1). It adds nothing to the
/// burner's behaviour: recorders come from <see cref="IOpticalDiscBurner.GetDevicesAsync"/>, every
/// write mode the WPF host offers is reported as available, and the disc is not read here
/// (IMAPI checks it when the burn starts, as before), so <see cref="OpticalDeviceStatus.Media"/>
/// stays unknown.
/// </summary>
public sealed class WindowsOpticalDeviceService(IOpticalDiscBurner burner) : IOpticalDeviceService
{
    private readonly IOpticalDiscBurner _burner = burner ?? throw new ArgumentNullException(nameof(burner));

    public string BackendName => "windows-imapi";

    public OpticalBackendAvailability Availability => OpticalBackendAvailability.Available;

    public async Task<IReadOnlyList<OpticalDeviceStatus>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OpticalBurnDevice> devices = await _burner.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        return [.. devices.Select(Describe)];
    }

    public async Task<OpticalDeviceStatus> GetStatusAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        IReadOnlyList<OpticalBurnDevice> devices = await _burner.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        OpticalBurnDevice device = devices.FirstOrDefault(item => item.Id == deviceId)
                                   ?? throw new OpticalBurnException(OpticalBurnErrorCode.DeviceNotFound, "The recorder is no longer listed.");
        return Describe(device);
    }

    private static OpticalDeviceStatus Describe(OpticalBurnDevice device)
        => new(
            device,
            Vendor: null,
            Model: null,
            Firmware: null,
            OpticalWriteModes.All,
            OpticalDeviceFeatures.None,
            OpticalDeviceAccess.Ready,
            AccessDetail: null,
            Media: null,
            [.. Enum.GetValues<OpticalBurnMediaKind>().Select(mode => new OpticalModeCapability(mode, OpticalBurnUnavailableReason.None, "checked when the burn starts"))]);
}
