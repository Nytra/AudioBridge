using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.NET.Common;
using BepInExResoniteShim;
using CSCore.CoreAudioAPI;
using CSCore.SoundOut;
using Elements.Core;
using FrooxEngine;
using HarmonyLib;
using InterprocessLib;
using Renderite.Shared;

namespace AudioBridge;

// Enum for mute target selection
public enum MuteTarget
{
    None,
    Host,
    Renderer
}

// BepInEx entry
[ResonitePlugin(PluginMetadata.GUID, PluginMetadata.NAME, PluginMetadata.VERSION, PluginMetadata.AUTHORS, PluginMetadata.REPOSITORY_URL)]
[BepInDependency(BepInExResoniteShim.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
public class AudioBridge : BasePlugin
{
    private static ConfigEntry<bool>? _enabled;
    private static ConfigEntry<MuteTarget>? _muteTarget;
    private static ConfigEntry<bool>? _debugLogging;

    internal static new ManualLogSource? Log;

    internal static bool ShouldMute => Enabled && MuteTarget == MuteTarget.Host;

    internal static MuteTarget MuteTarget => _muteTarget!.Value;
    internal static bool Enabled => _enabled!.Value;
    internal static bool DebugLogging => _debugLogging!.Value;

    internal static bool Ready
    {
        get
        {
            if (Engine.Current?.IsReady != true) return false;

            var renderSystem = Engine.Current.RenderSystem;
            if (renderSystem is null) return false;

            return Engine.Current.RenderSystem.FrameIndex >= 120;
        }
    }

    public override void Load()
    {
        try
        {
            Log = base.Log;

            _enabled = Config.Bind("General", "Enabled", true, "Enable audio sharing to renderer process?");
            _muteTarget = Config.Bind("General", "MuteTarget", MuteTarget.Host, "Which process to mute (prevents double audio)?");
            _debugLogging = Config.Bind("General", "DebugLogging", false, "Enable debug/verbose logging?");
            
            Log.LogInfo("[AudioBridge] Plugin loading!");
            
            // Subscribe to configuration changes (inline events, crazy concept i know)
            _enabled.SettingChanged += (sender, args) => OnEnabledChanged();
            _muteTarget.SettingChanged += (sender, args) => OnMuteTargetChanged();

            Log.LogInfo($"[AudioBridge] On load mute target: {_muteTarget.Value}");
            Log.LogInfo($"[AudioBridge] On load enabled: {_enabled.Value}");
            Log.LogInfo($"[AudioBridge] On load debug logging: {_debugLogging.Value}");

            // Time to patch everything manually, yay!
            Log.LogInfo("[AudioBridge] Applying audio driver patches");
            var harmony = HarmonyInstance;
            
            try
            {
                // **Try** to patch CSCoreAudioOutputDriver methods
                var driverType = typeof(CSCoreAudioOutputDriver);
                var baseType = typeof(AudioOutputDriver);
                
                // Get all methods including declared only (not inherited)
                var methods = driverType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                int patchedCount = 0;
                
                if (DebugLogging)
                    Log.LogInfo($"[AudioBridge] Found {methods.Length} audio driver methods");
                
                foreach (var method in methods)
                {
                    // Log all Read-related methods
                    if (method.Name.Contains("Read") || method.Name.Contains("read"))
                    {
                        var parameters = method.GetParameters();
                        var paramInfo = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));

                        if (DebugLogging)
                            Log.LogInfo($"[AudioBridge] Discovered audio method: {method.Name}({paramInfo})");
                        
                        // Try to patch each Read method with the appropriate postfix
                        if (method.DeclaringType == driverType)
                        {
                            try
                            {
                                string? postfixName = null;
                                
                                // Choose the right postfix based on method signature
                                if (method.Name == "Read" && parameters.Length == 3)
                                {
                                    if (parameters[0].ParameterType == typeof(float[]))
                                    {
                                        postfixName = "Read_Float_Postfix";
                                    }
                                    else if (parameters[0].ParameterType == typeof(byte[]))
                                    {
                                        postfixName = "Read_Byte_Postfix";
                                    }
                                }
                                else if (method.Name == "ReadAuto")
                                {
                                    postfixName = "ReadAuto_Span_Postfix";
                                }
                                
                                if (postfixName != null)
                                {
                                    var postfix = typeof(ShadowWriterPatch).GetMethod(postfixName,
                                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                                    if (postfix != null)
                                    {
                                        harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                                        if (DebugLogging)
                                            Log.LogInfo($"[AudioBridge] Successfully patched {method.Name}");
                                        patchedCount++;
                                    }
                                    else
                                    {
                                        if (DebugLogging)
                                            Log.LogInfo($"[AudioBridge] Patch method {postfixName} not found");
                                    }
                                }
                            }
                            catch (Exception patchEx)
                            {
                                if (DebugLogging)
                                    Log.LogError($"[AudioBridge] Failed to patch {method.Name}: {patchEx.Message}");
                            }
                        }
                    }
                }
                
                // Also check base class methods
                var baseMethods = baseType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (DebugLogging)
                    Log.LogInfo($"[AudioBridge] Base driver has {baseMethods.Length} methods");
                
                foreach (var method in baseMethods)
                {
                    if (method.Name.Contains("Read") || method.Name.Contains("read") || method.Name == "Start")
                    {
                        var parameters = method.GetParameters();
                        var paramInfo = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));

                        if (DebugLogging)
                            Log.LogInfo($"[AudioBridge] Found base method: {method.Name}({paramInfo})");
                        
                        // Patch Start method from base class
                        if (method.Name == "Start" && method.DeclaringType == baseType)
                        {
                            try
                            {
                                var startPostfix = typeof(ShadowWriterPatch).GetMethod("Start_Base_Postfix",
                                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                                harmony.Patch(method, postfix: new HarmonyMethod(startPostfix));
                                Log.LogInfo("[AudioBridge] Patched base Start method");
                            }
                            catch (Exception patchEx)
                            {
                                Log.LogError($"[AudioBridge] Failed to patch base Start: {patchEx.Message}");
                            }
                        }
                    }
                }
                
                if (DebugLogging)
                    Log.LogInfo($"[AudioBridge] Successfully patched {patchedCount} audio methods");
            }
            catch (Exception ex)
            {
                Log.LogError($"[AudioBridge] Patching failed: {ex.Message}");
            }

            Log.LogInfo("[AudioBridge] Audio driver patching completed");

            ShadowBus.CreateMessenger();
        }
        catch (Exception ex)
        {
            Log!.LogError($"[AudioBridge] Initialization failed: {ex.Message}");
            Log.LogError($"[AudioBridge] Stack trace: {ex.StackTrace}");
        }
    }
    
    private void OnEnabledChanged()
    {
        UniLog.Log($"[AudioBridge] Enabled changed to: {_enabled!.Value}");

        if (!Ready)
        {
            return;
        }

        if (_enabled.Value)
        {
            UniLog.Log("[AudioBridge] Enabling audio sharing");
            ShadowWriterPatch.ResetState();
        }
        else
        {
            UniLog.Log("[AudioBridge] Disabling audio sharing");

            ShadowBus.RunWithLock(() => 
            {
                if (ShadowBus.Initialized)
                    ShadowBus.Shutdown();
            });
        }
    }
    
    private void OnMuteTargetChanged()
    {
        UniLog.Log($"[AudioBridge] Mute target changed to {MuteTarget}");

        if (!Ready) return;
        
        // Only process if enabled
        if (Enabled)
        {
            ShadowBus.RunWithLock(() => 
            {
                if (ShadowBus.Initialized)
                    ShadowBus.SendMuteTarget(MuteTarget);
            });

            ShadowWriterPatch.UpdateSessionMuting();
        }
    }
}

internal class ShadowBusFloatsData : IMemoryPackable
{
    public float[]? data;

    public void Pack(ref MemoryPacker packer)
    {
        packer.Write(data!.Length);
        foreach (var flt in data!)
        {
            packer.Write(flt);
        }
    }

    public void Unpack(ref MemoryUnpacker unpacker)
    {
        int len = 0;
        unpacker.Read(ref len);
        data = new float[len];
        for (int i = 0; i < len; i++)
        {
            float flt = 0f;
            unpacker.Read(ref flt);
            data[i] = flt;
        }
    }
}

internal class ShadowBusInitData : IMemoryPackable
{
    public int sampleRate;
    public int channels;
    public int muteTarget;
    public string? sessionId;
    public void Pack(ref MemoryPacker packer)
    {
        packer.Write(sampleRate);
        packer.Write(channels);
        packer.Write(muteTarget);
        packer.Write(sessionId!);
    }

    public void Unpack(ref MemoryUnpacker unpacker)
    {
        unpacker.Read(ref sampleRate);
        unpacker.Read(ref channels);
        unpacker.Read(ref muteTarget);
        unpacker.Read(ref sessionId!);
    }
}

// ===== Shared bus (host<->renderer) =====
internal static class ShadowBus
{
    private const string MESSENGER_NAME = "AudioBridge";

    private static Messenger? _messenger = null;

    public static bool Initialized { get; private set; } = false;

    private static object _lockObj = new();

    public static void CreateMessenger()
    {
        if (_messenger is not null)
            throw new InvalidOperationException("Messenger has already been created!");

        _messenger = new Messenger(MESSENGER_NAME, [typeof(ShadowBusInitData), typeof(ShadowBusFloatsData)], []);

        if (AudioBridge.DebugLogging)
            AudioBridge.Log!.LogInfo($"[AudioBridge] Messenger created: {MESSENGER_NAME}");
    }

    public static bool Init(int sampleRate = 48000, int channels = 2, string? sessionId = null)
    {
        if (_messenger is null)
            throw new InvalidOperationException("The messenger must be created first!");

        if (Initialized)
            throw new InvalidOperationException("Already initialized!");

        try
        {
            var initData = new ShadowBusInitData();
            initData.sampleRate = sampleRate;
            initData.channels = channels;
            initData.muteTarget = (int)AudioBridge.MuteTarget;
            initData.sessionId = sessionId;
            _messenger!.SendObject("initData", initData);

            Initialized = true;

            if (AudioBridge.DebugLogging)
                UniLog.Log($"[AudioBridge] Sent init data: {initData.sampleRate}Hz, {initData.channels} channels, muteTarget: {initData.muteTarget}, SessionID: {initData.sessionId ?? "none"}");

            return true;
        }
        catch (Exception ex)
        {
            UniLog.Error($"[AudioBridge] Audio initialization failed: {ex.Message}");
            Initialized = false;
            return false;
        }
    }

    public static void Shutdown()
    {
        if (_messenger is null)
            throw new InvalidOperationException("The messenger must be created first!");

        if (!Initialized)
            throw new InvalidOperationException("Cannot shutdown when not initialized!");

        _messenger!.SendEmptyCommand("stop");

        Initialized = false;
    }

    public static void RunWithLock(Action act)
    {
        lock (_lockObj)
        {
            try
            {
                act();
            }
            catch (Exception ex)
            {
                UniLog.Error($"Exception in ShadowBus RunWithLock!\n{ex}");
            }
        }
    }

    public static void WriteFloats(ReadOnlySpan<float> src)
    {
        if (_messenger is null)
            throw new InvalidOperationException("The messenger must be created first!");

        if (!Initialized)
            throw new InvalidOperationException("Cannot write floats when not initialized!");

        if (src.IsEmpty) return;

        var floatsData = new ShadowBusFloatsData();
        floatsData.data = src.ToArray(); // Is it possible to avoid allocating an array here?
        _messenger!.SendObject("floats", floatsData);
    }

    public static void SendMuteTarget(MuteTarget muteTarget)
    {
        if (_messenger is null)
            throw new InvalidOperationException("The messenger must be created first!");

        if (!Initialized)
            throw new InvalidOperationException("Cannot send mute target when not initialized!");

        _messenger!.SendValue("muteTarget", (int)muteTarget);
    }
}

// ===== Writer patch (Host process) =====
internal static class ShadowWriterPatch
{
    private static readonly AccessTools.FieldRef<CSCoreAudioOutputDriver, WasapiOut>
        _fOut = AccessTools.FieldRefAccess<CSCoreAudioOutputDriver, WasapiOut>("_out");
    
    private static WasapiOut? _currentAudioOutput = null;

    private static bool _isSessionMuted = false;

    private static bool _firstRun = true;
    private static bool _initFailed = false;

    private static string? _capturedSessionId = null;

    private static bool _loggedBufferMuted = false;
    private static bool _loggedUnsupported = false;
    private static bool _loggedByte = false;
    private static bool _loggedAuto = false;
    private static bool _loggedFormat = false;
    private static int _writeCounter = 0;

    private static bool? _patchesEnabled = null;

    private static DateTime? _shadowBusDisabledTime = null;

    private const float DEACTIVATE_TIME_SECONDS = 0.5f;

    // Postfix for Read(float[], int, int)
    private static void Read_Float_Postfix(CSCoreAudioOutputDriver __instance, float[] buffer, int offset, int count, ref int __result)
    {
        try
        {
            if (!AudioBridge.Ready) return;

            if (_patchesEnabled is null)
                _patchesEnabled = AudioBridge.Enabled;

            if (_patchesEnabled != true) return;
            if (_initFailed) return;
            
            var outp = _fOut(__instance);
            var fmt = outp?.ActualOutputFormat;
            
            // Log format info once
            if (!_loggedFormat && fmt != null)
            {
                if (AudioBridge.DebugLogging)
                    UniLog.Log($"[AudioBridge] Audio output detected: {fmt.SampleRate}Hz, {fmt.Channels}ch, {fmt.BitsPerSample}bit");
                _loggedFormat = true;
            }
            
            if (fmt == null || fmt.BitsPerSample != 32) return;

            // __result is BYTES read; convert to float count
            int floatsRead = Math.Max(0, __result / sizeof(float));
            if (floatsRead == 0) return;

            ShadowBus.RunWithLock(() => 
            {
                if (_firstRun)
                {
                    _firstRun = false;
                    if (!ShadowBus.Initialized && !ShadowBus.Init(fmt.SampleRate, fmt.Channels, _capturedSessionId))
                    {
                        _initFailed = true;
                        return;
                    }
                    ShadowWriterPatch.UpdateSessionMuting();
                }

                if (!ShadowBus.Initialized)
                {
                    if (_shadowBusDisabledTime is null)
                        _shadowBusDisabledTime = DateTime.Now;
                    else if ((DateTime.Now - _shadowBusDisabledTime).Value.TotalSeconds > DEACTIVATE_TIME_SECONDS)
                    {
                        _patchesEnabled = false;
                        ShadowWriterPatch.UpdateSessionMuting();
                    }
                    return;
                }

                // Write audio data BEFORE muting (so renderer gets unmuted audio)
                ShadowBus.WriteFloats(buffer.AsSpan(offset, floatsRead));

                // If host should be muted, zero out the buffer AFTER sharing it
                if (AudioBridge.ShouldMute)
                {
                    Array.Clear(buffer, offset, floatsRead);
                    if (!_loggedBufferMuted && AudioBridge.DebugLogging)
                    {
                        _loggedBufferMuted = true;
                        UniLog.Log($"[AudioBridge] Buffer muted");
                    }
                }

                // Log periodically
                _writeCounter++;
                if (_writeCounter % 1000 == 0 && AudioBridge.DebugLogging)
                {
                    UniLog.Log($"[AudioBridge] Processed {_writeCounter} audio chunks");
                }
            });
        }
        catch (Exception ex) { UniLog.Error($"Audio float processing error: {ex.Message}"); }
    }
    
    // Postfix for Read(byte[], int, int)
    private static void Read_Byte_Postfix(CSCoreAudioOutputDriver __instance, byte[] buffer, int offset, int count, ref int __result)
    {
        try
        {
            if (!AudioBridge.Ready) return;

            if (_patchesEnabled is null)
                _patchesEnabled = AudioBridge.Enabled;

            if (_patchesEnabled != true) return;
            if (_initFailed) return;
            if (__result <= 0) return;
            
            var outp = _fOut(__instance);
            var fmt = outp?.ActualOutputFormat;
            
            if (fmt == null) return;
            
            // Log format info once
            if (!_loggedByte)
            {
                if (AudioBridge.DebugLogging)
                    UniLog.Log($"[AudioBridge] Audio output detected (byte mode): {fmt.SampleRate}Hz, {fmt.Channels}ch, {fmt.BitsPerSample}bit");
                _loggedByte = true;
            }
            
            // Convert byte data to float based on format
            int bytesRead = __result;
            int bitsPerSample = fmt.BitsPerSample;

            ShadowBus.RunWithLock(() => 
            {
                if (_firstRun)
                {
                    _firstRun = false;
                    if (!ShadowBus.Initialized && !ShadowBus.Init(fmt.SampleRate, fmt.Channels, _capturedSessionId))
                    {
                        _initFailed = true;
                        return;
                    }
                    UpdateSessionMuting();
                }

                if (!ShadowBus.Initialized)
                {
                    if (_shadowBusDisabledTime is null)
                        _shadowBusDisabledTime = DateTime.Now;
                    else if ((DateTime.Now - _shadowBusDisabledTime).Value.TotalSeconds > DEACTIVATE_TIME_SECONDS)
                    {
                        _patchesEnabled = false;
                        ShadowWriterPatch.UpdateSessionMuting();
                    }
                    return;
                }

                bool supported = true;
                float[]? floatBuffer = null;

                // Convert bytes to float based on bit depth
                if (bitsPerSample == 32)
                {
                    // It's already float data in byte form
                    floatBuffer = new float[bytesRead / sizeof(float)];
                    Buffer.BlockCopy(buffer, offset, floatBuffer, 0, bytesRead);
                }
                else if (bitsPerSample == 16)
                {
                    // Convert 16-bit PCM to float
                    int sampleCount = bytesRead / 2; // 2 bytes per sample
                    floatBuffer = new float[sampleCount];

                    for (int i = 0; i < sampleCount; i++)
                    {
                        short sample = BitConverter.ToInt16(buffer, offset + i * 2);
                        floatBuffer[i] = sample / 32768.0f; // Convert to -1.0 to 1.0 range
                    }
                }
                else if (bitsPerSample == 24)
                {
                    // Convert 24-bit PCM to float
                    int sampleCount = bytesRead / 3; // 3 bytes per sample
                    floatBuffer = new float[sampleCount];

                    for (int i = 0; i < sampleCount; i++)
                    {
                        int sample = (buffer[offset + i * 3] << 8) |
                                     (buffer[offset + i * 3 + 1] << 16) |
                                     (buffer[offset + i * 3 + 2] << 24);
                        sample >>= 8; // Sign extend
                        floatBuffer[i] = sample / 8388608.0f; // Convert to -1.0 to 1.0 range
                    }
                }
                else
                {
                    supported = false;

                    // Unsupported format
                    if (!_loggedUnsupported && AudioBridge.DebugLogging)
                    {
                        UniLog.Log($"[AudioBridge] Unsupported audio format: {bitsPerSample}-bit");
                        _loggedUnsupported = true;
                    }
                }

                if (supported)
                {
                    // Write to shared memory BEFORE muting
                    if (ShadowBus.Initialized)
                        ShadowBus.WriteFloats(floatBuffer.AsSpan());

                    // If host should be muted, zero out the original buffer AFTER sharing
                    if (AudioBridge.ShouldMute)
                    {
                        Array.Clear(buffer, offset, bytesRead);
                        if (!_loggedBufferMuted && AudioBridge.DebugLogging)
                        {
                            _loggedBufferMuted = true;
                            UniLog.Log($"[AudioBridge] Buffer muted");
                        }
                    }

                    _writeCounter++;
                    if (_writeCounter % 1000 == 0 && AudioBridge.DebugLogging)
                    {
                        UniLog.Log($"[AudioBridge] Processed {_writeCounter} audio chunks ({bitsPerSample}-bit {(bitsPerSample == 32 ? "float" : "pcm")})");
                    }
                }
            });
        }
        catch (Exception ex) { UniLog.Error($"Audio byte processing error: {ex.Message}"); }
    }

    // Postfix for ReadAuto(Span<byte>, WaveFormat)
    private static void ReadAuto_Span_Postfix(CSCoreAudioOutputDriver __instance, ref int __result)
    {
        try
        {
            // Log that ReadAuto was called
            if (!_loggedAuto && AudioBridge.DebugLogging)
            {
                UniLog.Log("[AudioBridge] Auto-read method detected");
                _loggedAuto = true;
            }
        }
        catch (Exception ex) { UniLog.Error($"Auto-read processing error: {ex.Message}"); }
    }
    
    // Postfix for base class Start method
    private static void Start_Base_Postfix(AudioOutputDriver __instance, string context)
    {
        try
        {
            
            if (AudioBridge.DebugLogging)
                UniLog.Log($"[AudioBridge] Audio driver started with context: {context}");
            
            // Try to capture the Engine's SessionID through reflection
            // AudioOutputDriver should have a reference to AudioSystem which has Engine
            try
            {
                var audioSystemField = AccessTools.Field(typeof(AudioOutputDriver), "AudioSystem");
                if (audioSystemField != null)
                {
                    var audioSystem = audioSystemField.GetValue(__instance);
                    if (audioSystem != null)
                    {
                        var engineField = AccessTools.Field(audioSystem.GetType(), "Engine");
                        if (engineField != null)
                        {
                            var engine = engineField.GetValue(audioSystem);
                            if (engine != null)
                            {
                                var sessionIdProperty = AccessTools.Property(engine.GetType(), "UniqueSessionID");
                                if (sessionIdProperty != null)
                                {
                                    var sessionId = sessionIdProperty.GetValue(engine);
                                    if (sessionId != null)
                                    {
                                        _capturedSessionId = sessionId.ToString();
                                        if (AudioBridge.DebugLogging)
                                            UniLog.Log($"[AudioBridge] Captured Engine SessionID: {_capturedSessionId}");
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception sessionEx)
            {
                if (AudioBridge.DebugLogging)
                    UniLog.Log($"[AudioBridge] Could not capture SessionID: {sessionEx.Message}");
            }
            
            // Check if it's actually a CSCoreAudioOutputDriver
            if (__instance is CSCoreAudioOutputDriver csDriver)
            {
                var outp = _fOut(csDriver);
                if (outp != null)
                {
                    var fmt = outp.ActualOutputFormat;
                    if (fmt != null)
                    {
                        if (AudioBridge.DebugLogging)
                            UniLog.Log($"[AudioBridge] Audio device format: {fmt.SampleRate}Hz, {fmt.Channels}ch, {fmt.BitsPerSample}bit");
                    }
                    
                    var device = outp.Device;
                    if (device != null)
                    {
                        if (AudioBridge.DebugLogging)
                            UniLog.Log($"[AudioBridge] Audio device: {device.FriendlyName}");
                        
                        // Store reference and apply muting if needed
                        _currentAudioOutput = outp;
                        UpdateSessionMuting();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            UniLog.Error($"Audio start error: {ex.Message}");
        }
    }
    
    internal static void UpdateSessionMuting()
    {
        var shouldMute = AudioBridge.ShouldMute;
        UniLog.Log($"[AudioBridge] Updating session muting. Should mute: {shouldMute}");

        try
        {
            if (_currentAudioOutput == null || _currentAudioOutput.Device == null)
            {
                UniLog.Log("[AudioBridge] No audio device available for session muting (buffer muting will still work)");
                _isSessionMuted = false;
                return;
            }

            using var sessionManager = AudioSessionManager2.FromMMDevice(_currentAudioOutput.Device);
            using var sessionEnumerator = sessionManager.GetSessionEnumerator();
            var currentProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            
            bool sessionFound = false;
            foreach (var session in sessionEnumerator)
            {
                using var sessionControl = session.QueryInterface<AudioSessionControl2>();
                if (sessionControl.ProcessID == currentProcessId)
                {
                    sessionFound = true;
                    using var simpleVolume = session.QueryInterface<SimpleAudioVolume>();
                    simpleVolume.MasterVolume = shouldMute ? 0.0f : 1.0f;
                    if (AudioBridge.DebugLogging)
                        UniLog.Log($"[AudioBridge] Host audio session found for PID {currentProcessId}, {(shouldMute ? "muted" : "unmuted")} at session level");
                    
                    // Also log session details for debugging
                    if (AudioBridge.DebugLogging)
                    {
                        try
                        {
                            var sessionId = sessionControl.SessionIdentifier;
                            var displayName = sessionControl.DisplayName;
                            UniLog.Log($"[AudioBridge] Session details - ID: {sessionId}, Name: {displayName}");
                        }
                        catch { }
                    }
                    
                    break;
                }
            }
            
            if (!sessionFound)
            {
                UniLog.Log($"[AudioBridge] No audio session found for PID {currentProcessId}, using buffer-level muting only");
                _isSessionMuted = false;
                return;
            }

            _isSessionMuted = shouldMute;
            return;
        }
        catch (Exception ex)
        {
            UniLog.Error($"Session muting failed (buffer muting still active): {ex.Message}");
            _isSessionMuted = false;
            return;
        }
    }
    
    internal static void ResetState()
    {
        if (AudioBridge.DebugLogging)
            UniLog.Log("[AudioBridge] Resetting shadow writer state");

        _firstRun = true;
        _shadowBusDisabledTime = null;
        _patchesEnabled = null;
        _initFailed = false;
        _loggedFormat = false;
        _loggedByte = false;
        _loggedAuto = false;
        _writeCounter = 0;
        _loggedBufferMuted = false;
    }
}
