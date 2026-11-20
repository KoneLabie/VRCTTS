using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace VRCTTS.Services;

public sealed record DefaultAudioEndpoint(DataFlow Flow, Role Role, string? Id);
public sealed record AudioEndpointDiagnostic(string Id, string Name, string Adapter, DataFlow Flow,
    DeviceState State, int FormFactor, int? MixChannels, int? SampleRate, string? ReadError = null, string? Label = null);

public static class WindowsAudioEndpoints
{
    public const string MicrophoneName = "VRCTTS";
    private static bool IsBasicCable(MMDevice device) => device.DeviceFriendlyName.Equals("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase);
    private static string? GetEndpointLabel(MMDevice device) =>
        device.Properties.Contains(PropertyKeys.PKEY_Device_DeviceDesc)
            ? Convert.ToString(device.Properties[PropertyKeys.PKEY_Device_DeviceDesc].Value, System.Globalization.CultureInfo.InvariantCulture)
            : null;
    internal static bool IsAppMicrophoneLabel(string adapter, string? label) =>
        adapter.Equals("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase) && label == MicrophoneName;
    public static bool HasAppMicrophone() => FindMicrophoneCaptureName() is not null && FindBasicCableRenderId() is not null;
    public static string? FindMicrophoneCaptureName()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        foreach (var endpoint in endpoints)
        {
            using (endpoint)
                if (IsAppMicrophoneLabel(endpoint.DeviceFriendlyName, GetEndpointLabel(endpoint))) return endpoint.FriendlyName;
        }
        return null;
    }
    public static string? FindBasicCableRenderId()
    {
        return SelectBasicCableRenderId(GetEndpointInventory());
    }

    internal static string? SelectBasicCableRenderId(IEnumerable<AudioEndpointDiagnostic> endpoints) =>
        endpoints.Where(IsStandardBasicCableRender).OrderBy(d => d.Id, StringComparer.Ordinal).Select(d => d.Id).FirstOrDefault();

    internal static bool IsStandardBasicCableRender(AudioEndpointDiagnostic endpoint) =>
        endpoint.Adapter.Equals("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase) &&
        endpoint.Flow == DataFlow.Render && endpoint.State == DeviceState.Active &&
        // Pack45 exposes both a speaker pin and a separate LineLevel 16-channel pin. The line
        // pin can default to stereo too; channel count or the translated display name is not identity.
        endpoint.FormFactor == 1 && (endpoint.MixChannels is null or > 0 and <= 8);

    public static IReadOnlyList<AudioEndpointDiagnostic> GetEndpointInventory(bool includeInactive = false)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.All, includeInactive ? DeviceState.All : DeviceState.Active);
        var result = new List<AudioEndpointDiagnostic>();
        foreach (var endpoint in endpoints)
        {
            using (endpoint)
            {
                var formFactor = -1;
                int? channels = null;
                int? sampleRate = null;
                string? error = null;
                try
                {
                    if (endpoint.Properties.Contains(PropertyKeys.PKEY_AudioEndpoint_FormFactor))
                        formFactor = Convert.ToInt32(endpoint.Properties[PropertyKeys.PKEY_AudioEndpoint_FormFactor].Value,
                            System.Globalization.CultureInfo.InvariantCulture);
                    if (endpoint.State == DeviceState.Active)
                    {
                        using var client = endpoint.CreateAudioClient();
                        var format = client.MixFormat; // Read-only; no Initialize/Start, volume, or device-setting calls.
                        channels = format.Channels;
                        sampleRate = format.SampleRate;
                    }
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or FormatException or OverflowException)
                {
                    error = $"ReadEndpoint:0x{ex.HResult:X8}";
                }
                result.Add(new(endpoint.ID, endpoint.FriendlyName, endpoint.DeviceFriendlyName, endpoint.DataFlow,
                    endpoint.State, formFactor, channels, sampleRate, error, GetEndpointLabel(endpoint)));
            }
        }
        return result;
    }
    internal static bool HasActiveBasicCablePair()
    {
        if (FindBasicCableRenderId() is null) return false;
        using var enumerator = new MMDeviceEnumerator();
        using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        foreach (var endpoint in endpoints) { using (endpoint) if (IsBasicCable(endpoint)) return true; }
        return false;
    }

    /// <summary>
    /// Finishes the app-name change on an already active basic cable. This only
    /// writes the capture endpoint's editable label; it never installs a driver,
    /// opens a playback stream, or changes any default audio device.
    /// </summary>
    public static bool TryNameExistingMicrophone() => TryNameExistingMicrophone(
        HasActiveBasicCablePair, NameBasicCableMicrophone, HasAppMicrophone);

    internal static bool TryNameExistingMicrophone(Func<bool> hasActivePair, Action rename, Func<bool> verify)
    {
        try
        {
            // A standard render pin plus an active matching capture endpoint is
            // required before the existing-device path can change a label.
            if (!hasActivePair()) return false;
            rename();
            return verify();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException or UnauthorizedAccessException)
        {
            // Repair can use the normal elevated setup path if Windows denies a
            // per-user label change. Never report success without readback.
            return false;
        }
    }

    internal static void NameBasicCableMicrophone()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        foreach (var endpoint in endpoints)
        {
            using (endpoint)
            {
                if (!IsBasicCable(endpoint)) continue;
                if (GetEndpointLabel(endpoint) == MicrophoneName) return;
                SetEndpointLabel(endpoint.ID, GetEndpointLabel(endpoint), MicrophoneName);
                return;
            }
        }
        throw new InvalidOperationException("Windows has not activated the microphone endpoint yet.");
    }
    public static IReadOnlyList<DefaultAudioEndpoint> SnapshotDefaultEndpoints()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<DefaultAudioEndpoint>();
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            try { using var endpoint = enumerator.GetDefaultAudioEndpoint(flow, role); result.Add(new(flow, role, endpoint.ID)); }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490)) { result.Add(new(flow, role, null)); }
        }
        return result;
    }
    internal static List<string> RestoreDefaultEndpoints(IReadOnlyList<DefaultAudioEndpoint> original)
    {
        var failures = new List<string>();
        IPolicyConfig? policy = null;
        try
        {
            var current = SnapshotDefaultEndpoints();
            foreach (var before in original)
            {
                var after = current.First(d => d.Flow == before.Flow && d.Role == before.Role);
                if (before.Id == after.Id) continue;
                if (before.Id is null) { failures.Add($"{before.Flow}/{before.Role}"); continue; }
                try
                {
                    // Windows has no public default setter. Use the established desktop COM
                    // interface only to undo a driver-install change, then verify restoration.
                    policy ??= (IPolicyConfig)new PolicyConfigClient();
                    Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(before.Id, (int)before.Role));
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException) { failures.Add($"{before.Flow}/{before.Role}"); }
            }
            var verified = SnapshotDefaultEndpoints();
            foreach (var before in original)
                if (verified.First(d => d.Flow == before.Flow && d.Role == before.Role).Id != before.Id && !failures.Contains($"{before.Flow}/{before.Role}"))
                    failures.Add($"{before.Flow}/{before.Role}");
        }
        catch (COMException) { failures.Add("audio defaults"); }
        finally { if (policy is not null) Marshal.FinalReleaseComObject(policy); }
        return failures;
    }
    private static void SetEndpointLabel(string endpointId, string? expectedCurrentLabel, string name)
    {
        IPolicyConfig? policy = null;
        var value = new PropVariant { VariantType = 31, Pointer = Marshal.StringToCoTaskMemUni(name) };
        try
        {
            policy = (IPolicyConfig)new PolicyConfigClient();
            // DeviceDesc is the user-editable pin label. FriendlyName14 is a read-only composite;
            // Windows appends the unchanged adapter name to the label for some device lists.
            // The Windows policy-service ABI includes bFxStore; false selects endpoint metadata.
            var key = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
            Marshal.ThrowExceptionForHR(policy.GetPropertyValue(endpointId, false, ref key, out var previous));
            try
            {
                if (previous.VariantType != 31 || Marshal.PtrToStringUni(previous.Pointer) != expectedCurrentLabel)
                    throw new InvalidOperationException("The microphone label changed during setup. Refresh devices before repairing it.");
            }
            finally { PropVariantClear(ref previous); }
            Marshal.ThrowExceptionForHR(policy.SetPropertyValue(endpointId, false, ref key, ref value));
            Marshal.ThrowExceptionForHR(policy.GetPropertyValue(endpointId, false, ref key, out var verified));
            try
            {
                if (verified.VariantType != 31 || Marshal.PtrToStringUni(verified.Pointer) != name)
                    throw new InvalidOperationException("Windows did not retain the requested microphone label.");
            }
            finally { PropVariantClear(ref verified); }
        }
        finally
        {
            PropVariantClear(ref value);
            if (policy is not null) Marshal.FinalReleaseComObject(policy);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }
    [StructLayout(LayoutKind.Explicit, Size=24)]
    private struct PropVariant { [FieldOffset(0)] public ushort VariantType; [FieldOffset(8)] public IntPtr Pointer; }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] private class PolicyConfigClient { }
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int current, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpoint, IntPtr mix);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int current, out long period, out long minimum);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, ref long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool fxStore, ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool fxStore, ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
}
