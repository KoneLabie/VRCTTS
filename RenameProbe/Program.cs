using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;

const string ExactName = "VRCTTS";
using var enumerator = new MMDeviceEnumerator();
var TargetId = args.FirstOrDefault(argument => argument.StartsWith("--device=", StringComparison.Ordinal))?["--device=".Length..];
if (string.IsNullOrWhiteSpace(TargetId))
{
    using var captures = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
    List<string> matchingIds = [];
    foreach (var capture in captures)
        using (capture)
            if (capture.DeviceFriendlyName.Equals("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase))
                matchingIds.Add(capture.ID);
    if (matchingIds.Count != 1)
        throw new InvalidOperationException("Expected exactly one active basic VB-CABLE input. Use --device=<endpoint-id> to select it explicitly. No write was attempted.");
    TargetId = matchingIds[0];
}
using var target = enumerator.GetDevice(TargetId);
if (target.DataFlow != DataFlow.Capture || target.State != DeviceState.Active ||
    !target.DeviceFriendlyName.Equals("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("The exact authorized basic capture endpoint is unavailable; no write is allowed.");
var originalName = target.FriendlyName;
var originalDesc = Convert.ToString(target.Properties[PropertyKeys.PKEY_Device_DeviceDesc].Value);
var friendlyKey = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
var descriptionKey = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
var policy = (IPolicyConfig)new PolicyConfigClient();
try
{
    var description = ReadPolicy(policy, TargetId, descriptionKey);
    var friendly = ReadPolicy(policy, TargetId, friendlyKey);
    var verified = description.HResult >= 0 && friendly.HResult >= 0 && description.Type == 31 && friendly.Type == 31 &&
        description.Value == originalDesc && friendly.Value == originalName;
    Console.WriteLine(JsonSerializer.Serialize(new { EndpointId = TargetId, CoreName = originalName, CoreDescription = originalDesc,
        PolicyDescription = description, PolicyFriendlyName = friendly, GetterAbiVerified = verified }, new JsonSerializerOptions { WriteIndented = true }));
    var renameLabel = args.Contains("--rename-label");
    if (!args.Contains("--rename") && !renameLabel) return verified ? 0 : 2;
    if (args.Contains("--rename") && renameLabel) throw new ArgumentException("Select exactly one rename operation.");
    if (!verified) throw new InvalidOperationException("PolicyConfig getter ABI did not match MMDevice; no write attempted.");
    var writeKey = renameLabel ? descriptionKey : friendlyKey;
    var value = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(ExactName) };
    int result;
    try { result = policy.SetPropertyValue(TargetId, false, ref writeKey, ref value); }
    finally { Native.PropVariantClear(ref value); }
    Console.WriteLine($"SetPropertyValue({(renameLabel ? "DeviceDesc2" : "FriendlyName14")}) HRESULT=0x{result:X8}");
    if (result < 0) return 3;
    await Task.Delay(250);
    using var refreshedEnumerator = new MMDeviceEnumerator();
    using var refreshed = refreshedEnumerator.GetDevice(TargetId);
    var afterPolicy = ReadPolicy(policy, TargetId, friendlyKey);
    var afterDescription = ReadPolicy(policy, TargetId, descriptionKey);
    var coreDescription = Convert.ToString(refreshed.Properties[PropertyKeys.PKEY_Device_DeviceDesc].Value);
    var waveNames = new List<string>();
    for (uint index = 0; index < Native.waveInGetNumDevs(); index++)
    {
        var caps = new WaveInCaps();
        if (Native.waveInGetDevCaps(new UIntPtr(index), ref caps, (uint)Marshal.SizeOf<WaveInCaps>()) == 0)
            waveNames.Add(caps.Name);
    }
    Console.WriteLine(JsonSerializer.Serialize(new { CoreNameAfter = refreshed.FriendlyName, CoreDescriptionAfter = coreDescription,
        AdapterAfter = refreshed.DeviceFriendlyName, PolicyFriendlyNameAfter = afterPolicy, PolicyDescriptionAfter = afterDescription, WinMMInputNames = waveNames,
        ExactCoreName = refreshed.FriendlyName == ExactName, ExactWinMMName = waveNames.Contains(ExactName) }, new JsonSerializerOptions { WriteIndented = true }));
    return renameLabel ? coreDescription == ExactName ? 0 : 4 : refreshed.FriendlyName == ExactName && waveNames.Contains(ExactName) ? 0 : 4;
}
finally { Marshal.FinalReleaseComObject(policy); }

static PropertyRead ReadPolicy(IPolicyConfig policy, string id, PropertyKey key)
{
    var result = policy.GetPropertyValue(id, false, ref key, out var value);
    try { return new(result, value.Type, result >= 0 && value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null); }
    finally { Native.PropVariantClear(ref value); }
}

record PropertyRead(int HResult, ushort Type, string? Value);
[StructLayout(LayoutKind.Sequential)]
struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }
[StructLayout(LayoutKind.Explicit, Size = 24)]
struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct WaveInCaps
{
    public ushort Manufacturer; public ushort Product; public uint Version;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    public uint Formats; public ushort Channels; public ushort Reserved;
}
static class Native
{
    [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PropVariant value);
    [DllImport("winmm.dll")] public static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll", EntryPoint = "waveInGetDevCapsW", CharSet = CharSet.Unicode)]
    public static extern uint waveInGetDevCaps(UIntPtr id, ref WaveInCaps caps, uint size);
}
[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] class PolicyConfigClient { }
[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPolicyConfig
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
