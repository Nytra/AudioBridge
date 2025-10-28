using BepInEx;
using BepInEx.Configuration;
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
    private static ConfigEntry<bool> ENABLED;
    private static ConfigEntry<MuteTarget> MUTE_TARGET;
    private static ConfigEntry<bool> DEBUG_LOGGING;

    private static MuteTarget _currentMuteTarget = MuteTarget.Host;
    private static bool _isEnabled = false;
    private static bool _debugLogging = false;

    public override void Load()
    {
        try
        {
            ENABLED = Config.Bind("General", "Enabled", true, "Enable audio sharing to renderer process?");
            MUTE_TARGET = Config.Bind("General", "MuteTarget", MuteTarget.Host, "Which process to mute (prevents double audio)?");
            DEBUG_LOGGING = Config.Bind("General", "DebugLogging", false, "Enable debug/verbose logging?");
            
            UniLog.Log("[AudioBridge] Initializing audio sharing module");
            
            _currentMuteTarget = MUTE_TARGET.Value;
            _isEnabled = ENABLED.Value;
            _debugLogging = DEBUG_LOGGING.Value;
            
            // Subscribe to configuration changes (inline events, crazy concept i know)
            ENABLED.SettingChanged += (sender, args) => OnEnabledChanged();
            MUTE_TARGET.SettingChanged += (sender, args) => OnMuteTargetChanged();
            DEBUG_LOGGING.SettingChanged += (sender, args) => _debugLogging = DEBUG_LOGGING.Value;
            UniLog.Log($"[AudioBridge] Mute target set to: {_currentMuteTarget}");
            
            // Time to patch everything manually, yay!
            UniLog.Log("[AudioBridge] Applying audio driver patches");
            var harmony = HarmonyInstance;
            
            try
            {
                // **Try** to patch CSCoreAudioOutputDriver methods
                var driverType = typeof(CSCoreAudioOutputDriver);
                var baseType = typeof(AudioOutputDriver);
                
                // Get all methods including declared only (not inherited)
                var methods = driverType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                int patchedCount = 0;
                
                if (_debugLogging)
                    UniLog.Log($"[AudioBridge] Found {methods.Length} audio driver methods");
                
                foreach (var method in methods)
                {
                    // Log all Read-related methods
                    if (method.Name.Contains("Read") || method.Name.Contains("read"))
                    {
                        var parameters = method.GetParameters();
                        var paramInfo = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        if (_debugLogging)
                            UniLog.Log($"[AudioBridge] Discovered audio method: {method.Name}({paramInfo})");
                        
                        // Try to patch each Read method with the appropriate postfix
                        if (method.DeclaringType == driverType)
                        {
                            try
                            {
                                string postfixName = null;
                                
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
                                        if (_debugLogging)
                                            UniLog.Log($"[AudioBridge] Successfully patched {method.Name}");
                                        patchedCount++;
                                    }
                                    else
                                    {
                                        if (_debugLogging)
                                            UniLog.Log($"[AudioBridge] Patch method {postfixName} not found");
                                    }
                                }
                            }
                            catch (Exception patchEx)
                            {
                                if (_debugLogging)
                                    UniLog.Log($"[AudioBridge] Failed to patch {method.Name}: {patchEx.Message}");
                            }
                        }
                    }
                }
                
                // Also check base class methods
                var baseMethods = baseType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (_debugLogging)
                    UniLog.Log($"[AudioBridge] Base driver has {baseMethods.Length} methods");
                
                foreach (var method in baseMethods)
                {
                    if (method.Name.Contains("Read") || method.Name.Contains("read") || method.Name == "Start")
                    {
                        var parameters = method.GetParameters();
                        var paramInfo = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        if (_debugLogging)
                            UniLog.Log($"[AudioBridge] Found base method: {method.Name}({paramInfo})");
                        
                        // Patch Start method from base class
                        if (method.Name == "Start" && method.DeclaringType == baseType)
                        {
                            try
                            {
                                var startPostfix = typeof(ShadowWriterPatch).GetMethod("Start_Base_Postfix",
                                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                                harmony.Patch(method, postfix: new HarmonyMethod(startPostfix));
                                UniLog.Log("[AudioBridge] Patched base Start method");
                            }
                            catch (Exception patchEx)
                            {
                                UniLog.Log($"[AudioBridge] Failed to patch base Start: {patchEx.Message}");
                            }
                        }
                    }
                }
                
                if (_debugLogging)
                    UniLog.Log($"[AudioBridge] Successfully patched {patchedCount} audio methods");
            }
            catch (Exception ex)
            {
                UniLog.Error($"[AudioBridge] Patching failed: {ex.Message}");
            }
            
            UniLog.Log("[AudioBridge] Audio driver patching completed");

            // Check if enabled in config
            UniLog.Log($"[AudioBridge] Audio sharing enabled: {_isEnabled}");
            
            if (_isEnabled)
            {
                UniLog.Log("[AudioBridge] Initializing audio writer for host process");
                
                // Initialize the bus as writer immediately
                UniLog.Log("[AudioBridge] Initializing shared memory audio buffer");
                if (ShadowBus.EnsureInit(writer: true))
                {
                    UniLog.Log("[AudioBridge] Shared memory audio buffer initialized");
                    // Note: SessionID will be captured and written when the audio driver starts
                }
                else
                {
                    UniLog.Error("[AudioBridge] Failed to initialize shared memory buffer");
                }
            }
            else
            {
                UniLog.Log("[AudioBridge] Audio sharing is disabled");
            }
        }
        catch (Exception ex)
        {
            UniLog.Error($"[AudioBridge] Initialization failed: {ex.Message}");
            UniLog.Error($"[AudioBridge] Stack trace: {ex.StackTrace}");
        }
    }


    internal static void Msg(string s) => UniLog.Log($"[AudioBridge] {s}");
    internal static void Err(string s) => UniLog.Error($"[AudioBridge] {s}", stackTrace: false);
    
    private void OnEnabledChanged()
    {
        var previousEnabled = _isEnabled;
        _isEnabled = ENABLED.Value;
        UniLog.Log($"[AudioBridge] Audio sharing enabled changed from {previousEnabled} to {_isEnabled}");
        
        if (_isEnabled && !previousEnabled)
        {
            // Enabling audio sharing
            UniLog.Log("[AudioBridge] Enabling audio sharing...");
            Task.Run(async () =>
            {
                await Task.Delay(100); // Small delay
                if (ShadowBus.EnsureInit(writer: true))
                {
                    UniLog.Log("[AudioBridge] Audio sharing enabled successfully");
                    
                    // Reset the writer state
                    ShadowWriterPatch.ResetState();
                    
                    // Apply mute configuration if needed
                    if (_currentMuteTarget == MuteTarget.Host)
                    {
                        // Try to apply mute configuration with a slight delay if audio device isn't ready
                        Task.Run(async () =>
                        {
                            for (int i = 0; i < 10; i++)
                            {
                                if (ShadowWriterPatch.TryApplyMuteConfiguration(true))
                                {
                                    break;
                                }
                                await Task.Delay(500);
                            }
                        });
                    }
                }
                else
                {
                    UniLog.Error("[AudioBridge] Failed to enable audio sharing");
                }
            });
        }
        else if (!_isEnabled && previousEnabled)
        {
            // Disabling audio sharing
            UniLog.Log("[AudioBridge] Disabling audio sharing...");
            
            // Unmute host if it was muted
            if (_currentMuteTarget == MuteTarget.Host)
            {
                ShadowWriterPatch.ApplyMuteConfiguration(false);
            }

            ShadowBus.Messenger!.SendValue("enabled", false);
            
            // Wait a bit for renderer to see the change
            Task.Run(async () =>
            {
                await Task.Delay(500);
                ShadowBus.Shutdown();
                ShadowWriterPatch.ResetState();
                UniLog.Log("[AudioBridge] Audio sharing disabled");
            });
        }
    }
    
    private void OnMuteTargetChanged()
    {
        var previousTarget = _currentMuteTarget;
        _currentMuteTarget = MUTE_TARGET.Value;
        UniLog.Log($"[AudioBridge] Mute target changed from {previousTarget} to {_currentMuteTarget}");
        
        // Only process if enabled
        if (_isEnabled)
        {
            ShadowBus.Messenger!.SendValue("muteTarget", (int)_currentMuteTarget);
            
            // Update host muting based on the new target
            // Mute host if target is Host, unmute for Renderer or None
            bool shouldMuteHost = (_currentMuteTarget == MuteTarget.Host);
            
            // Only apply if there's an actual change in host muting state
            if (previousTarget == MuteTarget.Host || _currentMuteTarget == MuteTarget.Host)
            {
                ShadowWriterPatch.ApplyMuteConfiguration(shouldMuteHost);
            }
        }
    }
    
    internal static MuteTarget GetCurrentMuteTarget() => _currentMuteTarget;
    internal static bool IsEnabled() => _isEnabled;
    internal static bool IsDebugLogging() => _debugLogging;
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
    public int enabled;
    public string? sessionId;
    public void Pack(ref MemoryPacker packer)
    {
        packer.Write(sampleRate);
        packer.Write(channels);
        packer.Write(muteTarget);
        packer.Write(enabled);
        packer.Write(sessionId!);
    }

    public void Unpack(ref MemoryUnpacker unpacker)
    {
        unpacker.Read(ref sampleRate);
        unpacker.Read(ref channels);
        unpacker.Read(ref muteTarget);
        unpacker.Read(ref enabled);
        unpacker.Read(ref sessionId!);
    }
}

// ===== Shared bus (host<->renderer) =====
internal static class ShadowBus
{
    private const string MESSENGER_NAME = "AudioBridge";

    internal static Messenger? Messenger;

    public static bool EnsureInit(bool writer, int sampleRate = 48000, int channels = 2, string? sessionId = null)
    {
        if (Messenger is not null)
        {
            // Already initialized
            SendInitData(sampleRate, channels, sessionId);
            return true;
        }
        
        if (AudioBridge.IsDebugLogging())
            UniLog.Log($"[AudioBridge] Initializing shared memory as {(writer ? "writer" : "reader")}");
        
        try
        {
            Messenger = new Messenger(MESSENGER_NAME, [typeof(ShadowBusInitData), typeof(ShadowBusFloatsData)], []);

            if (AudioBridge.IsDebugLogging())
                UniLog.Log($"[AudioBridge] Messenger created: {MESSENGER_NAME}");

            if (writer)
            {
                SendInitData(sampleRate, channels, sessionId);
            }
            
            UniLog.Log("[AudioBridge] Initialization complete");
            return true;
        }
        catch (Exception ex)
        {
            UniLog.Error($"[AudioBridge] Shared memory initialization failed: {ex.Message}");
            UniLog.Error($"[AudioBridge] Stack trace: {ex.StackTrace}");
            
            Messenger = null;
            
            return false;
        }
    }

    private static void SendInitData(int sampleRate = 48000, int channels = 2, string? sessionId = null)
    {
        var muteTarget = AudioBridge.GetCurrentMuteTarget();

        var initData = new ShadowBusInitData();
        initData.sampleRate = sampleRate;
        initData.channels = channels;
        initData.muteTarget = (int)muteTarget;
        initData.enabled = AudioBridge.IsEnabled() ? 1 : 0;
        initData.sessionId = sessionId;
        Messenger!.SendObject("initData", initData);

        if (AudioBridge.IsDebugLogging())
            UniLog.Log($"[AudioBridge] Audio format: {sampleRate}Hz, {channels} channels, muteTarget: {muteTarget}, enabled: {initData.enabled}, SessionID: {sessionId ?? "none"}");
    }
    
    public static void Shutdown()
    {
        if (Messenger is null) return;
        
        UniLog.Log("[AudioBridge] Shutting down shared memory");
        
        Messenger = null;
    }

    // Writer: float32 interleaved -> ring
    public static void WriteFloats(ReadOnlySpan<float> src)
    {
        if (src.IsEmpty) return;

        var floatsData = new ShadowBusFloatsData();
        floatsData.data = src.ToArray();
		Messenger?.SendObject<ShadowBusFloatsData>("floats", floatsData); // ToDo: optimize this, allocating new arrays and lists constantly is bad
    }
}

// ===== Writer patch (Host process) =====
internal static class ShadowWriterPatch
{
    private static readonly AccessTools.FieldRef<CSCoreAudioOutputDriver, WasapiOut>
        _fOut = AccessTools.FieldRefAccess<CSCoreAudioOutputDriver, WasapiOut>("_out");
    
    private static bool _busInitialized = false;
    private static WasapiOut _currentAudioOutput = null;
    private static bool _isMuted = false;
    private static string _capturedSessionId = null;
    private static bool _shouldMuteHostAudio = false;
    
    // Postfix for Read(float[], int, int)
    private static void Read_Float_Postfix(CSCoreAudioOutputDriver __instance, float[] buffer, int offset, int count, ref int __result)
    {
        try
        {
            // Check if audio sharing is enabled
            if (!AudioBridge.IsEnabled()) return;
            
            var outp = _fOut(__instance);
            var fmt = outp?.ActualOutputFormat;
            
            // Log format info once
            if (!_loggedFormat && fmt != null)
            {
                if (AudioBridge.IsDebugLogging())
                    UniLog.Log($"[AudioBridge] Audio output detected: {fmt.SampleRate}Hz, {fmt.Channels}ch, {fmt.BitsPerSample}bit");
                _loggedFormat = true;
            }
            
            if (fmt == null || fmt.BitsPerSample != 32) return;

            // __result is BYTES read; convert to float count
            int floatsRead = Math.Max(0, __result / sizeof(float));
            if (floatsRead == 0) return;

            // Initialize shared memory only once
            if (!_busInitialized)
            {
                if (ShadowBus.EnsureInit(writer: true, sampleRate: fmt.SampleRate, channels: fmt.Channels, sessionId: _capturedSessionId))
                {
                    _busInitialized = true;
                    UniLog.Log("[AudioBridge] Shared memory initialized for audio streaming");
                }
                else
                {
                    return;
                }
            }
            
            // Write audio data BEFORE muting (so renderer gets unmuted audio)
            ShadowBus.WriteFloats(buffer.AsSpan(offset, floatsRead));
            
            // If host should be muted, zero out the buffer AFTER sharing it
            if (_shouldMuteHostAudio && AudioBridge.GetCurrentMuteTarget() == MuteTarget.Host)
            {
                Array.Clear(buffer, offset, floatsRead);
            }
            
            // Log periodically
            _writeCounter++;
            if (_writeCounter % 1000 == 0 && AudioBridge.IsDebugLogging())
            {
                UniLog.Log($"[AudioBridge] Processed {_writeCounter} audio chunks");
            }
        }
        catch (Exception ex) { AudioBridge.Err($"Audio float processing error: {ex.Message}"); }
    }
    
    // Postfix for Read(byte[], int, int)
    private static void Read_Byte_Postfix(CSCoreAudioOutputDriver __instance, byte[] buffer, int offset, int count, ref int __result)
    {
        try
        {
            // Check if audio sharing is enabled
            if (!AudioBridge.IsEnabled()) return;
            if (__result <= 0) return;
            
            var outp = _fOut(__instance);
            var fmt = outp?.ActualOutputFormat;
            
            if (fmt == null) return;
            
            // Log format info once
            if (!_loggedByte)
            {
                if (AudioBridge.IsDebugLogging())
                    UniLog.Log($"[AudioBridge] Audio output detected (byte mode): {fmt.SampleRate}Hz, {fmt.Channels}ch, {fmt.BitsPerSample}bit");
                _loggedByte = true;
            }
            
            // Convert byte data to float based on format
            int bytesRead = __result;
            int sampleRate = fmt.SampleRate;
            int channels = fmt.Channels;
            int bitsPerSample = fmt.BitsPerSample;
            
            // Initialize shared memory only once
            if (!_busInitialized)
            {
                if (ShadowBus.EnsureInit(writer: true, sampleRate: sampleRate, channels: channels, sessionId: _capturedSessionId))
                {
                    _busInitialized = true;
                    UniLog.Log("[AudioBridge] Shared memory initialized for audio streaming");
                }
                else
                {
                    return;
                }
            }
            
            // Convert bytes to float based on bit depth
            if (bitsPerSample == 32)
            {
                // It's already float data in byte form
                var floatBuffer = new float[bytesRead / sizeof(float)];
                Buffer.BlockCopy(buffer, offset, floatBuffer, 0, bytesRead);
                
                // Write to shared memory BEFORE muting
                ShadowBus.WriteFloats(floatBuffer.AsSpan());
                
                // If host should be muted, zero out the original buffer AFTER sharing
                if (_shouldMuteHostAudio && AudioBridge.GetCurrentMuteTarget() == MuteTarget.Host)
                {
                    Array.Clear(buffer, offset, bytesRead);
                }
                
                _writeCounter++;
                if (_writeCounter % 1000 == 0 && AudioBridge.IsDebugLogging())
                {
                    UniLog.Log($"[AudioBridge] Processed {_writeCounter} audio chunks (32-bit float)");
                }
            }
            else if (bitsPerSample == 16)
            {
                // Convert 16-bit PCM to float
                int sampleCount = bytesRead / 2; // 2 bytes per sample
                var floatBuffer = new float[sampleCount];
                
                for (int i = 0; i < sampleCount; i++)
                {
                    short sample = BitConverter.ToInt16(buffer, offset + i * 2);
                    floatBuffer[i] = sample / 32768.0f; // Convert to -1.0 to 1.0 range
                }
                
                // Write to shared memory BEFORE muting
                ShadowBus.WriteFloats(floatBuffer.AsSpan());
                
                // If host should be muted, zero out the original buffer AFTER sharing
                if (_shouldMuteHostAudio && AudioBridge.GetCurrentMuteTarget() == MuteTarget.Host)
                {
                    Array.Clear(buffer, offset, bytesRead);
                }
                
                _writeCounter++;
                if (_writeCounter % 1000 == 0 && AudioBridge.IsDebugLogging())
                {
                    UniLog.Log($"[AudioBridge] Processed {_writeCounter} audio chunks (16-bit PCM)");
                }
            }
            else if (bitsPerSample == 24)
            {
                // Convert 24-bit PCM to float
                int sampleCount = bytesRead / 3; // 3 bytes per sample
                var floatBuffer = new float[sampleCount];
                
                for (int i = 0; i < sampleCount; i++)
                {
                    int sample = (buffer[offset + i * 3] << 8) | 
                                 (buffer[offset + i * 3 + 1] << 16) | 
                                 (buffer[offset + i * 3 + 2] << 24);
                    sample >>= 8; // Sign extend
                    floatBuffer[i] = sample / 8388608.0f; // Convert to -1.0 to 1.0 range
                }
                
                // Write to shared memory BEFORE muting
                ShadowBus.WriteFloats(floatBuffer.AsSpan());
                
                // If host should be muted, zero out the original buffer AFTER sharing
                if (_shouldMuteHostAudio && AudioBridge.GetCurrentMuteTarget() == MuteTarget.Host)
                {
                    Array.Clear(buffer, offset, bytesRead);
                }
                
                _writeCounter++;
                if (_writeCounter % 1000 == 0 && AudioBridge.IsDebugLogging())
                {
                    UniLog.Log($"[AudioBridge] Processed {_writeCounter} audio chunks (24-bit PCM)");
                }
            }
            else
            {
                // Unsupported format
                if (!_loggedUnsupported && AudioBridge.IsDebugLogging())
                {
                    UniLog.Log($"[AudioBridge] Unsupported audio format: {bitsPerSample}-bit");
                    _loggedUnsupported = true;
                }
            }
        }
        catch (Exception ex) { AudioBridge.Err($"Audio byte processing error: {ex.Message}"); }
    }
    
    private static bool _loggedUnsupported = false;
    
    // Postfix for ReadAuto(Span<byte>, WaveFormat)
    private static void ReadAuto_Span_Postfix(CSCoreAudioOutputDriver __instance, ref int __result)
    {
        try
        {
            
            // Log that ReadAuto was called
            if (!_loggedAuto && AudioBridge.IsDebugLogging())
            {
                UniLog.Log("[AudioBridge] Auto-read method detected");
                _loggedAuto = true;
            }
        }
        catch (Exception ex) { AudioBridge.Err($"Auto-read processing error: {ex.Message}"); }
    }
    
    private static bool _loggedByte = false;
    private static bool _loggedAuto = false;
    
    // Postfix for base class Start method
    private static void Start_Base_Postfix(AudioOutputDriver __instance, string context)
    {
        try
        {
            
            if (AudioBridge.IsDebugLogging())
                UniLog.Log($"[AudioBridge] Audio driver started with context: {context}");
            
            // Try to capture the Engine's SessionID through reflection
            // AudioOutputDriver should have a reference to AudioSystem which has Engine
            try
            {
                var audioSystemField = AccessTools.Field(typeof(AudioOutputDriver), "System") 
                    ?? AccessTools.Field(typeof(AudioOutputDriver), "system")
                    ?? AccessTools.Field(typeof(AudioOutputDriver), "_system");
                
                if (audioSystemField != null)
                {
                    var audioSystem = audioSystemField.GetValue(__instance);
                    if (audioSystem != null)
                    {
                        var engineField = AccessTools.Field(audioSystem.GetType(), "Engine")
                            ?? AccessTools.Field(audioSystem.GetType(), "engine")
                            ?? AccessTools.Field(audioSystem.GetType(), "_engine");
                        
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
                                        if (AudioBridge.IsDebugLogging())
                                            UniLog.Log($"[AudioBridge] Captured Engine SessionID: {_capturedSessionId}");

                                        // If bus is already initialized, update the SessionID
                                        if (_busInitialized && _capturedSessionId != null)
                                        {
                                            ShadowBus.Messenger?.SendString("sessionId", _capturedSessionId);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception sessionEx)
            {
                if (AudioBridge.IsDebugLogging())
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
                        if (AudioBridge.IsDebugLogging())
                            UniLog.Log($"[AudioBridge] Audio device format: {fmt.SampleRate}Hz, {fmt.Channels}ch, {fmt.BitsPerSample}bit");
                    }
                    
                    var device = outp.Device;
                    if (device != null)
                    {
                        if (AudioBridge.IsDebugLogging())
                            UniLog.Log($"[AudioBridge] Audio device: {device.FriendlyName}");
                        
                        // Store reference and apply muting if needed
                        _currentAudioOutput = outp;
                        if (AudioBridge.IsEnabled() && AudioBridge.GetCurrentMuteTarget() == MuteTarget.Host)
                        {
                            UniLog.Log("[AudioBridge] Applying Host mute configuration on audio start");
                            ApplyMuteConfiguration(true);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AudioBridge.Err($"Audio start error: {ex.Message}");
        }
    }

    private static bool _loggedFormat = false;
    private static int _writeCounter = 0;
    
    internal static bool TryApplyMuteConfiguration(bool shouldMute)
    {
        if (_currentAudioOutput == null || _currentAudioOutput.Device == null)
        {
            UniLog.Log("[AudioBridge] No audio device available to mute/unmute yet");
            return false;
        }
        
        ApplyMuteConfiguration(shouldMute);
        return true;
    }
    
    internal static void ApplyMuteConfiguration(bool shouldMute)
    {
        // Set the flag for buffer-level muting as primary approach
        _shouldMuteHostAudio = shouldMute;
        UniLog.Log($"[AudioBridge] Host audio buffer muting {(shouldMute ? "enabled" : "disabled")}");
        
        // Also try session-level muting as secondary approach
        if (_currentAudioOutput == null || _currentAudioOutput.Device == null)
        {
            UniLog.Log("[AudioBridge] No audio device available for session muting (buffer muting will still work)");
            return;
        }
        
        try
        {
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
                    _isMuted = shouldMute;
                    if (AudioBridge.IsDebugLogging())
                        UniLog.Log($"[AudioBridge] Host audio session found for PID {currentProcessId}, {(shouldMute ? "muted" : "unmuted")} at session level");
                    
                    // Also log session details for debugging
                    if (AudioBridge.IsDebugLogging())
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
                if (AudioBridge.IsDebugLogging())
                    UniLog.Log($"[AudioBridge] No audio session found for PID {currentProcessId}, using buffer-level muting only");
            }
        }
        catch (Exception ex)
        {
            AudioBridge.Err($"Session muting failed (buffer muting still active): {ex.Message}");
        }
    }
    
    internal static void ResetState()
    {
        UniLog.Log("[AudioBridge] Resetting audio writer state");
        _busInitialized = false;
        // Don't reset _currentAudioOutput - keep the reference to the audio device
        // Don't reset _capturedSessionId - keep it for re-initialization
        _isMuted = false;
        _loggedFormat = false;
        _loggedByte = false;
        _loggedAuto = false;
        _writeCounter = 0;
    }
}
