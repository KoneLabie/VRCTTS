using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace VRCTTS.Services;

public sealed record MicrophoneSetupResult(bool Success, string Message, bool NeedsRestart = false);

/// <summary>Installs the embedded unmodified signed basic VB-CABLE package through Windows SetupAPI.</summary>
public static class DriverSetup
{
    internal const string PackageHash = "B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB";
    private const string BasicHardwareId = "VBAudioVACWDM";
    private static readonly Guid MediaClass = new("4d36e96c-e325-11ce-bfc1-08002be10318");
    private static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCTTS");
    private static string StagingRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VRCTTS", "DriverStaging");
    public static string DiagnosticsPath => Path.Combine(DataDirectory, "microphone-setup-diagnostics.json");

    public static async Task<MicrophoneSetupResult> LaunchAsync()
    {
        if (WindowsAudioEndpoints.HasAppMicrophone()) return new(true, "VRCTTS is ready as a microphone.");
        // Renaming the previous Justin Bridge endpoint does not require a
        // driver reinstall or administrator approval on supported Windows builds.
        if (WindowsAudioEndpoints.TryNameExistingMicrophone()) return new(true, "VRCTTS is ready as a microphone.");
        var nonce = Guid.NewGuid().ToString("N");
        var resultPath = GetResultPath(nonce);
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable) || Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return new(false, "Run VRCTTS.exe to create the microphone. Driver setup cannot run through the dotnet host.");
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--setup-microphone");
            start.ArgumentList.Add(nonce);
            using var process = Process.Start(start);
            if (process is null) return new(false, "Windows could not start microphone setup.");
            await process.WaitForExitAsync().ConfigureAwait(false);
            await PreserveDiagnosticsAsync(nonce).ConfigureAwait(false);
            if (!File.Exists(resultPath))
                return new(false, "Windows microphone setup did not return a result. Run the app again using the same Windows account.");
            var json = await File.ReadAllTextAsync(resultPath).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<MicrophoneSetupResult>(json);
            if (result is null) return new(false, "Windows microphone setup returned an unreadable result.");
            return new(result.Success, SanitizeMessage(result.Message), result.NeedsRestart);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new(false, "Windows approval was canceled. The microphone needs administrator approval.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or Win32Exception)
        {
            return new(false, "Windows could not complete microphone setup. Close other audio setup programs and reopen VRCTTS.");
        }
        finally
        {
            try { if (File.Exists(resultPath)) File.Delete(resultPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try { if (File.Exists(GetDiagnosticResultPath(nonce))) File.Delete(GetDiagnosticResultPath(nonce)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static async Task<int> RunElevatedAsync(string nonce)
    {
        if (!IsValidNonce(nonce)) return 2;
        var diagnostics = new SetupDiagnostics();
        MicrophoneSetupResult result;
        try
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                result = new(false, "Microphone setup requires Windows administrator approval.");
            else result = await ProvisionAsync(diagnostics).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            result = new(false, "Windows could not initialize microphone setup. Close other audio setup programs and reopen VRCTTS.");
            diagnostics.ErrorCode = ex.HResult;
        }
        diagnostics.Result = result;
        try { diagnostics.EndpointsAfter = WindowsAudioEndpoints.GetEndpointInventory(includeInactive: true); }
        catch (COMException ex) { diagnostics.EndpointReadError = $"QueryDevices:0x{ex.HResult:X8}"; }
        try
        {
            var path = GetResultPath(nonce);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RejectReparseDirectory(DataDirectory);
            RejectReparseDirectory(Path.GetDirectoryName(path)!);
            try
            {
                await using var diagnosticStream = new FileStream(GetDiagnosticResultPath(nonce), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                await JsonSerializer.SerializeAsync(diagnosticStream, diagnostics).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Result still reaches the parent. */ }
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await JsonSerializer.SerializeAsync(stream, new MicrophoneSetupResult(result.Success, SanitizeMessage(result.Message), result.NeedsRestart)).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 3; }
        return result.Success ? 0 : 1;
    }

    private static async Task<MicrophoneSetupResult> ProvisionAsync(SetupDiagnostics diagnostics)
    {
        MicrophoneSetupResult result;
        diagnostics.SetStage("QueryDevices");
        var defaults = WindowsAudioEndpoints.SnapshotDefaultEndpoints();
        try
        {
            diagnostics.EndpointsBefore = WindowsAudioEndpoints.GetEndpointInventory(includeInactive: true);
            diagnostics.BoundDeviceBefore = QueryBasicCableDevice();
            if (WindowsAudioEndpoints.HasAppMicrophone()) return new(true, "VRCTTS is ready as a microphone.");
            var reboot = false;
            if (WindowsAudioEndpoints.HasActiveBasicCablePair())
                diagnostics.Events.Add("InstallDriver:SkippedActivePair");
            else
            {
                diagnostics.SetStage("InstallDriver");
                var package = await LoadVerifiedPackageAsync().ConfigureAwait(false);
                var folder = CreateSecureStagingDirectory();
                try
                {
                    using (var archive = new ZipArchive(new MemoryStream(package, writable: false))) archive.ExtractToDirectory(folder);
                    reboot = InstallBasicCable(Path.Combine(folder, "vbMmeCable64_win10.inf"));
                }
                finally
                {
                    var full = Path.GetFullPath(folder);
                    var parent = Path.GetFullPath(StagingRoot).TrimEnd(Path.DirectorySeparatorChar);
                    if (string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileName(full).StartsWith("VRCTTSDriver-", StringComparison.Ordinal) &&
                        !new DirectoryInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        try { Directory.Delete(full, recursive: true); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
            diagnostics.SetStage("QueryDevices");
            for (var attempt = 0; attempt < 40 && !WindowsAudioEndpoints.HasActiveBasicCablePair(); attempt++)
                await Task.Delay(250).ConfigureAwait(false);
            diagnostics.BoundDeviceAfter = QueryBasicCableDevice();
            if (!WindowsAudioEndpoints.HasActiveBasicCablePair())
                result = new(false, "[QueryDevices] Windows accepted the microphone driver, but its audio endpoints are not active yet. Restart your PC and reopen VRCTTS to finish setup.", true);
            else
            {
                diagnostics.SetStage("RenameMicrophone");
                WindowsAudioEndpoints.NameBasicCableMicrophone();
                result = WindowsAudioEndpoints.HasAppMicrophone()
                    ? new(true, reboot ? "VRCTTS is ready as a microphone. Windows also requested a restart to finalize the driver." : "VRCTTS is ready as a microphone.", reboot)
                    : new(false, "[RenameMicrophone] Windows did not retain the microphone name VRCTTS. Setup diagnostics were saved.");
            }
        }
        catch (Win32Exception ex)
        {
            diagnostics.ErrorCode = ex.NativeErrorCode;
            result = new(false, ex.NativeErrorCode == 259
                ? $"[{diagnostics.Stage}] Windows already has the current matching driver, but a healthy active microphone pair could not be verified. Setup diagnostics were saved; use Repair microphone after checking device availability."
                : $"[{diagnostics.Stage}] Windows could not complete microphone setup (error {ex.NativeErrorCode}). No existing audio driver was removed. Setup diagnostics were saved.");
        }
        catch (COMException ex)
        {
            diagnostics.ErrorCode = ex.HResult;
            result = new(false, $"[{diagnostics.Stage}] Windows audio returned error 0x{ex.HResult:X8}. The driver remains identified as VB-Audio Virtual Cable. Setup diagnostics were saved.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException)
        {
            result = new(false, ex is CryptographicException
                ? "[InstallDriver] The embedded microphone driver failed its integrity check. Setup was stopped."
                : $"[{diagnostics.Stage}] Microphone setup could not complete. No existing audio driver was removed. Setup diagnostics were saved.");
            diagnostics.ErrorCode = ex.HResult;
        }
        var restoreErrors = WindowsAudioEndpoints.RestoreDefaultEndpoints(defaults);
        if (restoreErrors.Count != 0)
            return new(false, result.Message + " Windows changed an audio default and automatic restoration failed. Restore your previous default playback or recording device in Windows Sound settings.", result.NeedsRestart);
        return result;
    }

    private sealed class SetupDiagnostics
    {
        public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;
        public string Stage { get; private set; } = "Authorize";
        public int? ErrorCode { get; set; }
        public List<string> Events { get; } = [];
        public IReadOnlyList<AudioEndpointDiagnostic>? EndpointsBefore { get; set; }
        public IReadOnlyList<AudioEndpointDiagnostic>? EndpointsAfter { get; set; }
        public BasicCableDeviceState? BoundDeviceBefore { get; set; }
        public BasicCableDeviceState? BoundDeviceAfter { get; set; }
        public string? EndpointReadError { get; set; }
        public MicrophoneSetupResult? Result { get; set; }
        public void SetStage(string stage) { Stage = stage; Events.Add(stage); }
    }

    private static string GetDiagnosticResultPath(string nonce) => Path.ChangeExtension(GetResultPath(nonce), ".diagnostics.json");
    private static async Task PreserveDiagnosticsAsync(string nonce)
    {
        try
        {
            var source = GetDiagnosticResultPath(nonce);
            if (!File.Exists(source) || new FileInfo(source).Length > 1024 * 1024) return;
            // The non-elevated parent saves the latest diagnostic file. No credentials or typed
            // messages are collected; only endpoint/driver metadata and setup stages are present.
            var text = await File.ReadAllTextAsync(source).ConfigureAwait(false);
            using var parsed = JsonDocument.Parse(text);
            await File.WriteAllTextAsync(DiagnosticsPath, parsed.RootElement.GetRawText()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    internal static bool IsValidNonce(string? nonce) => nonce is not null && nonce.Length == 32 && Guid.TryParseExact(nonce, "N", out _);
    internal static string GetResultPath(string nonce)
    {
        if (!IsValidNonce(nonce)) throw new ArgumentException("Invalid setup request identifier.", nameof(nonce));
        return Path.Combine(DataDirectory, "SetupResults", Guid.ParseExact(nonce, "N").ToString("N") + ".json");
    }
    internal static bool VerifyPackage(byte[] package) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(package), Convert.FromHexString(PackageHash));
    internal static async Task<byte[]> LoadVerifiedPackageAsync()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("VRCTTS.Resources.VBCABLE_Driver_Pack45.zip")
            ?? throw new InvalidOperationException("The virtual microphone driver is missing from this build.");
        using var buffer = new MemoryStream();
        await resource.CopyToAsync(buffer).ConfigureAwait(false);
        var package = buffer.ToArray();
        if (!VerifyPackage(package)) throw new CryptographicException("Microphone driver integrity check failed.");
        return package;
    }
    internal static string SanitizeMessage(string? message) => new((message ?? "Microphone setup returned no status.").Where(c => !char.IsControl(c)).Take(650).ToArray());
    private static void RejectReparseDirectory(string path)
    {
        if (new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Setup directories cannot be symbolic links.");
    }

    private static string CreateSecureStagingDirectory()
    {
        // Driver inputs must never be writable by a medium-integrity process between extraction
        // and SetupAPI consumption. Use protected administrator-owned directories, not user TEMP.
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        foreach (var sid in new[] { administrators, system })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));

        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        for (var ancestor = new DirectoryInfo(commonData); ancestor is not null; ancestor = ancestor.Parent)
            RejectReparseDirectory(ancestor.FullName);

        void EnsureProtected(string path)
        {
            var directory = new DirectoryInfo(path);
            // Create applies the security descriptor during directory creation. If another process
            // pre-created this path, reject an unsafe owner/DACL rather than changing its ACL.
            directory.Create(security);
            RejectReparseDirectory(path);
            var actual = directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            var owner = (SecurityIdentifier)actual.GetOwner(typeof(SecurityIdentifier))!;
            if (!actual.AreAccessRulesProtected || (!owner.Equals(administrators) && !owner.Equals(system)))
                throw new UnauthorizedAccessException("Driver staging has an unsafe owner or inherited permissions.");
            var rules = actual.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
                if (rule.AccessControlType == AccessControlType.Allow &&
                    !rule.IdentityReference.Equals(administrators) && !rule.IdentityReference.Equals(system))
                    throw new UnauthorizedAccessException("Driver staging permits access outside administrators and SYSTEM.");
        }

        EnsureProtected(Path.Combine(commonData, "VRCTTS"));
        EnsureProtected(StagingRoot);
        var folder = Path.Combine(StagingRoot, "VRCTTSDriver-" + Guid.NewGuid().ToString("N"));
        EnsureProtected(folder);
        return folder;
    }

    internal sealed record BasicCableDeviceState(bool Exists, bool HealthyBound, string? InstanceId,
        uint? ProblemCode, string? DriverKey, uint? ConfigQueryStatus);

    internal static bool CanContinueAfterNoUpdate(int errorCode, bool healthyBoundRootDevice, bool activeBasicPair) =>
        errorCode == 259 && healthyBoundRootDevice && activeBasicPair;

    internal static BasicCableDeviceState QueryBasicCableDevice()
    {
        var classGuid = MediaClass;
        var set = Native.SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, 2);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        BasicCableDeviceState result = new(false, false, null, null, null, null);
        try
        {
            for (uint index = 0; ; index++)
            {
                var info = DeviceInfo.Create();
                if (!Native.SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) return result;
                    throw new Win32Exception(error);
                }
                var bytes = new byte[8192];
                if (Native.SetupDiGetDeviceRegistryProperty(set, ref info, 1, out _, bytes, (uint)bytes.Length, out _))
                {
                    var ids = Encoding.Unicode.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    if (!ids.Any(id => id.Equals(BasicHardwareId, StringComparison.OrdinalIgnoreCase))) continue;
                    var instance = new StringBuilder(1024);
                    if (!Native.SetupDiGetDeviceInstanceId(set, ref info, instance, (uint)instance.Capacity, out _))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    var driver = new byte[4096];
                    string? driverKey = null;
                    if (Native.SetupDiGetDeviceRegistryProperty(set, ref info, 9 /* SPDRP_DRIVER */, out _, driver, (uint)driver.Length, out _))
                        driverKey = Encoding.Unicode.GetString(driver).TrimEnd('\0');
                    else if (Marshal.GetLastWin32Error() != 13)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    var statusResult = Native.CM_Get_DevNode_Status(out var status, out var problem, info.DevInst, 0);
                    var healthy = statusResult == 0 && problem == 0 && (status & 8 /* DN_STARTED */) != 0 &&
                        !string.IsNullOrWhiteSpace(driverKey) && instance.ToString().StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase);
                    result = new(true, healthy, instance.ToString(), statusResult == 0 ? problem : null, driverKey, statusResult);
                    if (healthy) return result;
                }
                else if (Marshal.GetLastWin32Error() != 13) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
    }

    private static bool InstallBasicCable(string infPath)
    {
        if (!Environment.Is64BitProcess) throw new InvalidOperationException("The microphone driver requires the 64-bit VRCTTS build.");
        var existing = QueryBasicCableDevice();
        var classGuid = MediaClass;
        IntPtr createdSet = IntPtr.Zero;
        var created = DeviceInfo.Create();
        try
        {
            if (!existing.Exists)
            {
                createdSet = Native.SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
                if (createdSet == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!Native.SetupDiCreateDeviceInfo(createdSet, "MEDIA", ref classGuid, "VB-Audio Virtual Cable", IntPtr.Zero, 1, ref created))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var ids = Encoding.Unicode.GetBytes(BasicHardwareId + "\0\0");
                if (!Native.SetupDiSetDeviceRegistryProperty(createdSet, ref created, 1, ids, (uint)ids.Length) ||
                    !Native.SetupDiCallClassInstaller(0x19, createdSet, ref created)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            // Flags=0 respects driver ranking and updates older matching devices in place.
            // No uninstall, forced downgrade, INF edit, or certificate-store modification.
            if (!Native.DiInstallDriver(IntPtr.Zero, Path.GetFullPath(infPath), 0, out var reboot))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 259)
                {
                    var bound = QueryBasicCableDevice();
                    if (CanContinueAfterNoUpdate(error, bound.HealthyBound, WindowsAudioEndpoints.HasActiveBasicCablePair()))
                        return false;
                }
                // ERROR_NO_MORE_ITEMS can mean an already-current package. It is not success
                // without a healthy bound ROOT device and verified active speaker/capture pair.
                throw new Win32Exception(error);
            }
            return reboot;
        }
        finally
        {
            if (createdSet != IntPtr.Zero && createdSet != new IntPtr(-1)) Native.SetupDiDestroyDeviceInfoList(createdSet);
        }
    }
    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo
    {
        public uint Size; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved;
        public static DeviceInfo Create() => new() { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
    }
    private static class Native
    {
        [DllImport("setupapi.dll", EntryPoint="SetupDiGetClassDevsW", CharSet=CharSet.Unicode, SetLastError=true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
        [DllImport("setupapi.dll", EntryPoint="SetupDiGetDeviceRegistryPropertyW", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref DeviceInfo info, uint property, out uint type, byte[] buffer, uint size, out uint required);
        [DllImport("setupapi.dll", EntryPoint="SetupDiGetDeviceInstanceIdW", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceInfo info, StringBuilder id, uint capacity, out uint required);
        [DllImport("cfgmgr32.dll")]
        public static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
        [DllImport("setupapi.dll", SetLastError=true)] public static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid guid, IntPtr parent);
        [DllImport("setupapi.dll", EntryPoint="SetupDiCreateDeviceInfoW", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiCreateDeviceInfo(IntPtr set, string name, ref Guid guid, string description, IntPtr parent, uint flags, ref DeviceInfo info);
        [DllImport("setupapi.dll", EntryPoint="SetupDiSetDeviceRegistryPropertyW", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref DeviceInfo info, uint property, byte[] buffer, uint size);
        [DllImport("setupapi.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref DeviceInfo info);
        [DllImport("setupapi.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("newdev.dll", EntryPoint="DiInstallDriverW", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
        public static extern bool DiInstallDriver(IntPtr parent, string inf, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool reboot);
    }
}
