using BepInEx;
using CSCore;
using CSCore.CoreAudioAPI;
using CSCore.SoundOut;
using InterprocessLib;
using Renderite.Shared;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Process = System.Diagnostics.Process;

namespace AudioBridge.Renderer
{
    // Mirror of the MuteTarget enum from main mod
    public enum MuteTarget
    {
        None = 0,
        Host = 1,
        Renderer = 2
    }
    
    [BepInPlugin("com.knackrack615.AudioBridgeRenderer", "AudioBridge Renderer", "3.0.0")]
    public class AudioBridgeRendererPlugin : BaseUnityPlugin
    {
        private ShadowAudioPlayer _audioPlayer;
        
        void Awake()
        {
            Logger.LogInfo("[AudioBridge.Renderer] Initializing audio renderer plugin");

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
        private Messenger _messenger;
        private ShadowAudioSource _audioSource;

        private int _sampleRate;
        private int _channels;
        private MuteTarget _muteTarget;
        private string _sessionId;

        private bool _startedInitTask;
        private bool _stopped = true;

        private Task _keepAlive;

        private void InitAudio()
        {
            Debug.Log($"[AudioBridge.Renderer] AUDIO INIT");
            Debug.Log($"[AudioBridge.Renderer] Audio format: {_sampleRate}Hz, {_channels} channels");
            Debug.Log($"[AudioBridge.Renderer] Using SessionID: {_sessionId ?? "none"}");
            Debug.Log($"[AudioBridge.Renderer] Mute target from host: {_muteTarget}");

            if (_audioOut is not null || _audioSource is not null)
            {
                throw new InvalidOperationException("Audio output exists!");
            }

            // Create audio source
            _audioSource = new ShadowAudioSource(_sampleRate, _channels);

            // Initialize audio output with SessionID if available
            if (!string.IsNullOrEmpty(_sessionId) && Guid.TryParse(_sessionId, out Guid sessionGuid))
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

            _stopped = false;

            Debug.Log("[AudioBridge.Renderer] Audio playback started");

            UpdateSessionMuting();

            //Keep alive and monitor
            _keepAlive ??= Task.Run(async () =>
            {
                while (_audioOut is not null)
                {
                    await Task.Delay(5000);

                    if (_audioOut is null) break;

                    var playbackState = _audioOut.PlaybackState;
                    if (playbackState == PlaybackState.Stopped)
                    {
                        Debug.LogWarning("[AudioBridge.Renderer] Audio playback stopped, attempting restart");
                        try
                        {
                            _audioOut.Play();
                            Debug.Log("[AudioBridge.Renderer] Audio playback restarted");
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[AudioBridge.Renderer] Failed to restart playback: {ex.Message}");
                        }
                    }
                }
                _keepAlive = null;
            });
        }
    
        public void Start()
        {
            Debug.Log("[AudioBridge.Renderer] Shadow audio player starting - will monitor for audio sharing");

            _messenger = new(MESSENGER_NAME, new List<Type>() { typeof(ShadowBusInitData), typeof(ShadowBusFloatsData) });

            Debug.Log($"[AudioBridge.Renderer] Messenger created: {MESSENGER_NAME}");

            _messenger.ReceiveObject<ShadowBusInitData>("initData", (obj) =>
            {
                _sampleRate = obj.sampleRate;
                _channels = obj.channels;
                _sessionId = obj.sessionId;
                _muteTarget = (MuteTarget)obj.muteTarget;

                if (!_stopped)
                {
                    if (!_startedInitTask)
                    {
                        Task.Run(async () =>
                        {
                            while (!_stopped)
                            {
                                await Task.Delay(1);
                            }
                            InitAudio();
                            _startedInitTask = false;
                        });
                        _startedInitTask = true;
                    }
                }
                else
                {
                    InitAudio();
                }
            });

            _messenger.ReceiveEmptyCommand("stop", () =>
            {
                Debug.Log("[AudioBridge.Renderer] Audio sharing disabled by host, stopping playback");
                Stop();
            });

            _messenger.ReceiveValue<int>("muteTarget", (val) =>
            {
                var muteTarget = (MuteTarget)val;
                Debug.Log($"[AudioBridge.Renderer] Mute target changed to: {muteTarget}");
                _muteTarget = muteTarget;
                UpdateSessionMuting();
            });

            _messenger.ReceiveObject<ShadowBusFloatsData>("floats", (obj) => 
            {
                _audioSource.EnqueueFloats(obj.data);
            });
        }
        
        public void Stop()
        {
            _audioOut?.Stop();
            _audioOut?.Dispose();
            _audioOut = null;
            _audioSource?.Dispose();
            _audioSource = null;
            _stopped = true;
        }

        private void UpdateSessionMuting()
        {
            if (_audioOut?.Device is not null)
            {
                if (_muteTarget == MuteTarget.Renderer)
                {
                    MuteCurrentProcessAudio(_audioOut.Device);
                }
                else
                {
                    UnmuteCurrentProcessAudio(_audioOut.Device);
                }
            }
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
        public string sessionId;
        public void Pack(ref MemoryPacker packer)
        {
            packer.Write(sampleRate);
            packer.Write(channels);
            packer.Write(muteTarget);
            packer.Write(sessionId);
        }

        public void Unpack(ref MemoryUnpacker unpacker)
        {
            unpacker.Read(ref sampleRate);
            unpacker.Read(ref channels);
            unpacker.Read(ref muteTarget);
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
        private bool _disposed = false;

        public void EnqueueFloats(float[] newData)
        {
            lock (_lockObj)
            {
                if (_disposed) return;
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
                    if (_disposed)
                    {
                        Array.Clear(buffer, offset, count);
                        return count;
                    }

                    int minSize = Math.Min(count, audioQueue.Count);

                    for (int i = offset; i < offset + minSize; i++)
                    {
                        buffer[i] = audioQueue.Dequeue();
                    }

                    // Fill silence if needed
                    if (minSize < count)
                    {
                        Array.Clear(buffer, offset + minSize, count - minSize);
                    }

                    return count;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in ShadowAudioSource Read\n{ex}");
                Array.Clear(buffer, offset, count);
                return count;
            }
        }
        
        public void Dispose()
        {
            Debug.Log("[AudioBridge.Renderer] Disposing ShadowAudioSource...");
            lock (_lockObj)
            {
                audioQueue.Clear();
                audioQueue = null;
                _disposed = true;
            }
        }
    }
}