using System.Runtime.InteropServices;
using System.Text.Json;
using SnowFixer.Core.Configuration;
using SnowFixer.Core.Pipeline;
using SnowFixer.Core.Scanning;

namespace SnowFixer.NativeExport;

/// <summary>
/// C-callable entry points DNNE wraps into SnowFixer.NativeExportNE.{dll,h,lib} so the
/// native wxWidgets shell (native/SnowFixer) can drive the existing Mutagen/niflysharp
/// extraction logic without ever running as a .NET apphost itself - the actual extraction happens
/// here, in-process, on a background Task; the native side polls <see cref="GetProgress"/> instead
/// of receiving a callback, since a callback crossing the native boundary while a background .NET
/// Task is mid-flight is far more fragile than polling a tiny status blob every ~150ms. Mirrors
/// AutoBlend.NativeExport.Exports 1:1. In addition to the extraction bridge, the native launcher
/// asks this assembly to discover valid MO2 profiles so the UI and backend use the same
/// base_directory/profile parsing rules.
///
/// Settings load/save is deliberately NOT exposed here - the native shell's SFConfig reads/writes
/// %APPDATA%\SnowFixer\settings.json directly (mirroring
/// SnowFixer.Core.Configuration.ExtractSettings' JSON shape).
/// </summary>
public static class Exports
{
    private static volatile RunState? _currentRun;

    private sealed class RunState
    {
        public string Status = "Starting...";
        public int Current;
        public int Total;
        public bool IsIndeterminate = true;
        public bool IsDone;
        public bool IsFailed;
        public string? ResultJson;
        public string? ErrorMessage;
    }

    [UnmanagedCallersOnly(EntryPoint = "start_extract_run")]
    public static void StartExtractRun(IntPtr settingsJsonPtr)
    {
        if (_currentRun is { IsDone: false })
        {
            return; // a run is already in flight
        }

        // DeserializeSettings can throw (malformed/empty JSON from the native side) - unlike the
        // Task.Run body below, this runs synchronously on the native caller's own thread, so an
        // unhandled exception here would cross the unmanaged boundary directly and crash the
        // process instead of being observable via get_progress like every other failure mode.
        ExtractSettings settings;
        try
        {
            settings = DeserializeSettings(settingsJsonPtr);
        }
        catch (Exception ex)
        {
            _currentRun = new RunState
            {
                Status = "Failed to start",
                IsDone = true,
                IsFailed = true,
                ErrorMessage = ex.Message,
            };
            return;
        }

        var state = new RunState();
        _currentRun = state;

        Task.Run(() =>
        {
            try
            {
                var orchestrator = new ExtractOrchestrator(settings);
                var result = orchestrator.Run(progress =>
                {
                    state.Status = progress.Message;
                    state.Current = progress.Current;
                    state.Total = progress.Total;
                    state.IsIndeterminate = progress.IsIndeterminate;
                });

                state.ResultJson = JsonSerializer.Serialize(result);
            }
            catch (Exception ex)
            {
                state.ErrorMessage = ex.Message;
                state.IsFailed = true;
            }
            finally
            {
                state.IsDone = true;
            }
        });
    }

    [UnmanagedCallersOnly(EntryPoint = "get_mo2_profiles")]
    public static IntPtr GetMo2Profiles(IntPtr instancePathPtr)
    {
        var instancePath = Marshal.PtrToStringUTF8(instancePathPtr) ?? string.Empty;
        try
        {
            var discovery = Mo2InstanceReader.DiscoverProfiles(instancePath);
            return ToNativeUtf8(JsonSerializer.Serialize(new
            {
                Profiles = discovery.Profiles,
                discovery.SelectedProfile,
                ErrorMessage = (string?)null,
            }));
        }
        catch (Exception ex)
        {
            return ToNativeUtf8(JsonSerializer.Serialize(new
            {
                Profiles = Array.Empty<string>(),
                SelectedProfile = (string?)null,
                ErrorMessage = ex.Message,
            }));
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "get_progress")]
    public static IntPtr GetProgress()
    {
        var state = _currentRun;
        var payload = new ProgressSnapshot(
            Status: state?.Status ?? string.Empty,
            Current: state?.Current ?? 0,
            Total: state?.Total ?? 0,
            IsIndeterminate: state?.IsIndeterminate ?? true,
            IsRunning: state is { IsDone: false },
            IsDone: state?.IsDone ?? false,
            IsFailed: state?.IsFailed ?? false,
            ResultJson: state?.ResultJson,
            ErrorMessage: state?.ErrorMessage);

        return ToNativeUtf8(JsonSerializer.Serialize(payload));
    }

    [UnmanagedCallersOnly(EntryPoint = "free_string")]
    public static void FreeString(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    private static ExtractSettings DeserializeSettings(IntPtr jsonPtr)
    {
        var json = Marshal.PtrToStringUTF8(jsonPtr) ?? "{}";
        return JsonSerializer.Deserialize<ExtractSettings>(json) ?? new ExtractSettings();
    }

    private static IntPtr ToNativeUtf8(string value) => Marshal.StringToCoTaskMemUTF8(value);

    private sealed record ProgressSnapshot(
        string Status,
        int Current,
        int Total,
        bool IsIndeterminate,
        bool IsRunning,
        bool IsDone,
        bool IsFailed,
        string? ResultJson,
        string? ErrorMessage);
}
