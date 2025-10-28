using System;
using System.IO.MemoryMappedFiles;
using System.Threading;
using BepInEx;
using UnityEngine;
using CSCore;
using CSCore.CoreAudioAPI;
using CSCore.SoundOut;
using Process = System.Diagnostics.Process;
using InterprocessLib;
using Renderite.Shared;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AudioBridge.Renderer
{
    // Mirror of the MuteTarget enum from main mod
    public enum MuteTarget
    {
        None = 0,
        Host = 1,
        Renderer = 2
    }
    
    [BepInPlugin("com.knackrack615.AudioBridgeRenderer", "AudioBridge Renderer", "1.0.0")]
    public class AudioBridgeRendererPlugin : BaseUnityPlugin
    {
        private ShadowAudioPlayer _audioPlayer;
        private bool _initialized = false;
        
        void Awake()
        {
            Logger.LogInfo("[AudioBridge.Renderer] Initializing audio renderer plugin");
            
            try
            {
                // Start immediately, no delay
                InitializeAudio();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed in Awake: {ex}");
            }
        }
        
        void InitializeAudio()
        {
            if (_initialized) return;
            _initialized = true;
            
            Logger.LogInfo("[AudioBridge.Renderer] Starting shared memory audio reader");
            
            try
            {
                _audioPlayer = new ShadowAudioPlayer();
                _audioPlayer.Start();
                Logger.LogInfo("[AudioBridge.Renderer] Audio reader started successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[AudioBridge.Renderer] Failed to start audio reader: {ex}");
            }
        }
        
        void OnDestroy()
        {
            _audioPlayer?.Stop();
        }
    }
    
    public class ShadowAudioPlayer
    {
        private WasapiOut _audioOut;
        private const string MESSENGER_NAME = "AudioBridge";
        private bool _stopped;
        private Messenger _messenger;
        private MuteTarget _lastMuteTarget;
        private Task keepAlive;
        private ShadowAudioSource _audioSource;
    
        public void Start()
        {
            Debug.Log("[AudioBridge.Renderer] Shadow audio player starting - will monitor for audio sharing");

            _messenger = new(MESSENGER_NAME, new List<Type>() { typeof(ShadowBusInitData), typeof(ShadowBusFloatsData) });

            Debug.Log($"[AudioBridge.Renderer] Messenger created: {MESSENGER_NAME}");

            _messenger.ReceiveObject<ShadowBusInitData>("initData", (obj) =>
            {
                if (_stopped) return;

                var sampleRate = obj.sampleRate;
                var channels = obj.channels;
                string sessionId = obj.sessionId;
                Debug.Log($"[AudioBridge.Renderer] Audio format: {sampleRate}Hz, {channels} channels");
                Debug.Log($"[AudioBridge.Renderer] Using SessionID: {sessionId ?? "none"}");

                // Create audio source
                _audioSource = new ShadowAudioSource(sampleRate, channels);

                // Initialize audio output with SessionID if available
                if (!string.IsNullOrEmpty(sessionId) && Guid.TryParse(sessionId, out Guid sessionGuid))
                {
                    // Use the same SessionID but with crossProcessSession: false to maintain separate control
                    // This groups them in Windows but keeps them as separate audio sessions for muting
                    _audioOut = new WasapiOut(false, AudioClientShareMode.Shared, 20, sessionGuid, false);
                    Debug.Log($"[AudioBridge.Renderer] Created WasapiOut with SessionID: {sessionGuid} (crossProcess: false for independent muting)");
                }
                else
                {
                    // Fallback to default if no SessionID
                    _audioOut = new WasapiOut(false, AudioClientShareMode.Shared, 20);
                    Debug.Log("[AudioBridge.Renderer] Created WasapiOut without SessionID (legacy mode)");
                }

                _audioOut.Initialize(_audioSource.ToWaveSource());
                _audioOut.Play();

                Debug.Log("[AudioBridge.Renderer] Audio playback started");

                MuteTarget muteTarget;
                if (obj.muteTarget < 0 || obj.muteTarget > 2) muteTarget = MuteTarget.Renderer;
                else
                    muteTarget = (MuteTarget)obj.muteTarget;

                Debug.Log($"[AudioBridge.Renderer] Mute target from host: {muteTarget}");

                if (muteTarget == MuteTarget.Renderer)
                {
                    // Mute this process's audio session so we don't hear it locally
                    // but it will still be available for recording/streaming
                    MuteCurrentProcessAudio(_audioOut.Device);
                }

                // Keep alive and monitor
                keepAlive ??= Task.Run(async () =>
                {
                    while (!_stopped)
                    {
                        await Task.Delay(5000);

                        var playbackState = _audioOut?.PlaybackState ?? PlaybackState.Stopped;
                        if (playbackState == PlaybackState.Stopped && !_stopped)
                        {
                            Debug.LogWarning("[AudioBridge.Renderer] Audio playback stopped, attempting restart");
                            try
                            {
                                _audioOut?.Play();
                            }
                            catch (Exception ex)
                            {
                                Debug.LogError($"[AudioBridge.Renderer] Failed to restart playback: {ex.Message}");
                            }
                        }
                    }
                });
                
            });

            _messenger.ReceiveValue<bool>("enabled", (val) =>
            {
                if (!val)
                {
                    Debug.Log("[AudioBridge.Renderer] Audio sharing disabled by host, stopping playback");
                    // Clean up before potentially restarting
                    if (_audioOut != null)
                    {
                        Debug.Log("[AudioBridge.Renderer] Cleaning up audio output...");
                        try { _audioOut.Stop(); } catch { }
                        try { _audioOut.Dispose(); } catch { }
                        _audioOut = null;
                    }
                }
            });

            _messenger.ReceiveValue<int>("muteTarget", (val) =>
            {
                var muteTarget = (MuteTarget)val;
                if (muteTarget != _lastMuteTarget)
                {
                    Debug.Log($"[AudioBridge.Renderer] Mute target changed to: {muteTarget}");
                    if (muteTarget == MuteTarget.Renderer)
                    {
                        MuteCurrentProcessAudio(_audioOut.Device);
                    }
                    else if (_lastMuteTarget == MuteTarget.Renderer)
                    {
                        UnmuteCurrentProcessAudio(_audioOut.Device);
                    }
                    _lastMuteTarget = muteTarget;
                }
            });

            _messenger.ReceiveObject<ShadowBusFloatsData>("floats", (obj) => 
            {
                _audioSource?.EnqueueFloats(obj.data);
            });
        }
        
        public void Stop()
        {
            _stopped = true;
            _audioOut?.Stop();
            _audioOut?.Dispose();
            _audioOut = null; // needed?
            _messenger = null;
        }
        
        private void MuteCurrentProcessAudio(MMDevice device)
        {
            try
            {
                Debug.Log("[AudioBridge.Renderer] Muting local audio playback (audio still available for recording)");
                
                using var sessionManager = AudioSessionManager2.FromMMDevice(device);
                using var sessionEnumerator = sessionManager.GetSessionEnumerator();
                var currentProcessId = (uint)Process.GetCurrentProcess().Id;
                
                foreach (var session in sessionEnumerator)
                {
                    using var sessionControl = session.QueryInterface<AudioSessionControl2>();
                    if (sessionControl.ProcessID == currentProcessId)
                    {
                        using var simpleVolume = session.QueryInterface<SimpleAudioVolume>();
                        simpleVolume.MasterVolume = 0.0f;
                        Debug.Log($"[AudioBridge.Renderer] Successfully muted audio session for process {currentProcessId}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AudioBridge.Renderer] Failed to mute audio session: {ex.Message}");
                Debug.LogWarning("[AudioBridge.Renderer] Audio will play normally (not muted)");
            }
        }
        
        private void UnmuteCurrentProcessAudio(MMDevice device)
        {
            try
            {
                Debug.Log("[AudioBridge.Renderer] Unmuting local audio playback");
                
                using var sessionManager = AudioSessionManager2.FromMMDevice(device);
                using var sessionEnumerator = sessionManager.GetSessionEnumerator();
                var currentProcessId = (uint)Process.GetCurrentProcess().Id;
                
                foreach (var session in sessionEnumerator)
                {
                    using var sessionControl = session.QueryInterface<AudioSessionControl2>();
                    if (sessionControl.ProcessID == currentProcessId)
                    {
                        using var simpleVolume = session.QueryInterface<SimpleAudioVolume>();
                        simpleVolume.MasterVolume = 1.0f;
                        Debug.Log($"[AudioBridge.Renderer] Successfully unmuted audio session for process {currentProcessId}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AudioBridge.Renderer] Failed to unmute audio session: {ex.Message}");
            }
        }
    }

    internal class ShadowBusFloatsData : IMemoryPackable
    {
        public float[] data;

        public void Pack(ref MemoryPacker packer)
        {
            packer.Write(data.Length);
            foreach (var flt in data)
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
        public string sessionId;
        public void Pack(ref MemoryPacker packer)
        {
            packer.Write(sampleRate);
            packer.Write(channels);
            packer.Write(muteTarget);
            packer.Write(enabled);
            packer.Write(sessionId);
        }

        public void Unpack(ref MemoryUnpacker unpacker)
        {
            unpacker.Read(ref sampleRate);
            unpacker.Read(ref channels);
            unpacker.Read(ref muteTarget);
            unpacker.Read(ref enabled);
            unpacker.Read(ref sessionId);
        }
    }
    
    public class ShadowAudioSource : ISampleSource
    {
        private readonly WaveFormat _format;
        
        public ShadowAudioSource(int sampleRate, int channels)
        {
            _format = new WaveFormat(sampleRate, 32, channels, AudioEncoding.IeeeFloat);
        }
        
        public WaveFormat WaveFormat => _format;
        public bool CanSeek => false;
        public long Position { get => 0; set { } }
        public long Length => 0;

        private Queue<float> audioQueue = new();
        private object _lockObj = new();

        public void EnqueueFloats(float[] newData)
        {
            lock (_lockObj)
            {
                foreach (var flt in newData)
                {
                    audioQueue.Enqueue(flt);
                }
            }

        }
        
        public int Read(float[] buffer, int offset, int count)
        {
            try
            {
                lock (_lockObj)
                {
                    int minSize = Math.Min(count, audioQueue.Count);

                    for (int i = offset; i < offset + minSize; i++)
                    {
                        buffer[i] = audioQueue.Dequeue();
                    }

                    // Fill silence if needed
                    if (minSize < count)
                    {
                        for (int i = offset + minSize; i < offset + count; i++)
                        {
                            buffer[i] = 0f;
                        }
                    }
                }

                return count;
            }
            catch
            {
                Array.Clear(buffer, offset, count);
                return count;
            }
        }
        
        public void Dispose() { }
    }
}