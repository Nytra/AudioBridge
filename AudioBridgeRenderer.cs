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
        //private ShadowBusReader _busReader;
        private const string MMF_NAME = "AudioBridge_SharedMemory";
        //private Thread _audioThread;
        //private bool _running;
        private bool _stopped;
        private Messenger _messenger;
        private MuteTarget _lastMuteTarget;
        private Task keepAlive;
        private ShadowAudioSource _audioSource;
    
        public void Start()
        {
            //_audioThread = new Thread(AudioThreadMain) { IsBackground = true };
            //_audioThread.Start();
            //AudioThreadMain();

            Debug.Log("[AudioBridge.Renderer] Shadow audio player starting - will monitor for audio sharing");

            // Initialize
            _messenger = new(MMF_NAME, new List<Type>() { typeof(ShadowBusData) });

            Debug.Log($"[AudioBridge.Renderer] Messenger created: {MMF_NAME}");

            _messenger.ReceiveObject<ShadowBusData>("initData", (obj) =>
            {
                if (_stopped) return;

                var sampleRate = obj.sampleRate;
                var channels = obj.channels;
                string sessionId = obj.sessionId;
                Debug.Log($"[AudioBridge.Renderer] Audio format: {sampleRate}Hz, {channels} channels");
                Debug.Log($"[AudioBridge.Renderer] Using SessionID: {obj.sessionId ?? "none"}");

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

            _messenger.ReceiveValueList<float>("floats", (list) => 
            {
                _audioSource?.SetStoredBuffer(list);

            });
        }
        
        private void AudioThreadMain()
        {
            

            //// Check if we should exit or retry
            //if (!_running)
            //{
            //	Debug.Log("[AudioBridge.Renderer] Audio thread exiting (Stop() was called)");
            //	break;  // Exit only if Stop() was called
            //}

            //// If audio was disabled, log that we're waiting for re-enable
            //if (wasDisabled)
            //{
            //	Debug.Log("[AudioBridge.Renderer] Audio sharing was disabled, waiting for re-enable...");
            //	wasDisabled = false;
            //}
            //else
            //{
            //	Debug.Log("[AudioBridge.Renderer] Will retry connection in 2 seconds...");
            //}
            //Thread.Sleep(2000);
        }
        
        public void Stop()
        {
            _stopped = true;
            _audioOut?.Stop();
            _audioOut?.Dispose();
            _audioOut = null; // needed?
            _messenger = null;
            //_busReader?.Dispose();
            //_audioThread?.Join(1000);
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

    internal class ShadowBusData : IMemoryPackable
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

    //public class ShadowBusReader : IDisposable
 //   {
 //       private const string MMF_NAME = "AudioBridge_SharedMemory";
 //       private const string MUTEX_NAME = "AudioBridge_SharedMemory_Mutex";
 //       private const int HEADER_BYTES = 64;
 //       private const int RING_BYTES = 2 * 1024 * 1024; // 2MB ring buffer for stable audio

 //       //private MemoryMappedFile _mmf;
 //       //private MemoryMappedViewAccessor _view;
 //       //private Mutex _mutex;
 //       //private MessageProcessingHandler
 //       internal Messenger _messenger;
 //       private long _totalSamplesRead;
        
 //       public bool TryConnect()
 //       {
 //           try
 //           {
 //               //_mmf = MemoryMappedFile.OpenExisting(MMF_NAME, MemoryMappedFileRights.ReadWrite);
 //               //_mutex = Mutex.OpenExisting(MUTEX_NAME);
 //               //_view = _mmf.CreateViewAccessor(0, HEADER_BYTES + RING_BYTES, MemoryMappedFileAccess.ReadWrite);
 //               _messenger = new(MMF_NAME, new List<Type>() { typeof(ShadowBusData) });
 //               return true;
 //           }
 //           catch
 //           {
 //               Dispose();
 //               return false;
 //           }
 //       }
        
 //       //public MuteTarget GetMuteTarget()
 //       //{
 //       //    if (_mutex == null || _view == null) return MuteTarget.Renderer;
            
 //       //    _mutex.WaitOne();
 //       //    try
 //       //    {
 //       //        int muteValue = _view.ReadInt32(16);
 //       //        if (muteValue < 0 || muteValue > 2) return MuteTarget.Renderer;
 //       //        return (MuteTarget)muteValue;
 //       //    }
 //       //    finally { _mutex.ReleaseMutex(); }
 //       //}
        
 //       //public bool IsEnabled()
 //       //{
 //       //    if (_mutex == null || _view == null) return false;
            
 //       //    _mutex.WaitOne();
 //       //    try
 //       //    {
 //       //        return _view.ReadInt32(20) == 1;
 //       //    }
 //       //    finally { _mutex.ReleaseMutex(); }
 //       //}
        
 //       public int ReadFloats(float[] buffer, int offset, int count)
 //       {
 //           if (_view == null) return 0;
            
 //           int bytesWanted = count * sizeof(float);
 //           byte[] tempBuffer = new byte[bytesWanted];
 //           int gotBytes = 0;
            
 //           _mutex.WaitOne();
 //           try
 //           {
 //               uint w = _view.ReadUInt32(0);
 //               uint r = _view.ReadUInt32(4);
                
 //               int avail = (int)((RING_BYTES + w - r) % RING_BYTES);
 //               if (avail <= 0) return 0;
                
 //               int want = Math.Min(avail, bytesWanted);
 //               int headOffset = HEADER_BYTES + (int)r;
 //               int tail = Math.Min(want, RING_BYTES - (int)r);
                
 //               // Read first segment
 //               _view.ReadArray(headOffset, tempBuffer, 0, tail);
                
 //               // Read wrapped segment if needed
 //               if (want > tail)
 //               {
 //                   int rest = want - tail;
 //                   _view.ReadArray(HEADER_BYTES, tempBuffer, tail, rest);
 //               }
                
 //               // Update read index
 //               r = (uint)((r + want) % RING_BYTES);
 //               _view.Write(4, r);
 //               gotBytes = want;
 //           }
 //           finally { _mutex.ReleaseMutex(); }
            
 //           // Convert bytes to floats
 //           int floatsRead = gotBytes / sizeof(float);
 //           Buffer.BlockCopy(tempBuffer, 0, buffer, offset, gotBytes);
            
 //           _totalSamplesRead += floatsRead;
 //           return floatsRead;
 //       }
        
 //       public void Dispose()
 //       {
 //           _messenger = null;
 //       }
 //   }
    
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

        private float[] storedBuffer;
        private object _lockObj = new();

        public void SetStoredBuffer(List<float> newData)
        {
            lock (_lockObj)
                storedBuffer = newData.ToArray();
        }
        
        public int Read(float[] buffer, int offset, int count)
        {
            try
            {
                while (storedBuffer is null)
                {
                    Thread.Sleep(1);
                }
                if (storedBuffer == null)
                {
                    Array.Clear(buffer, offset, count);
                }
                else
                {
                    lock (_lockObj)
                    {
                        Buffer.BlockCopy(storedBuffer, 0, buffer, offset, storedBuffer.Length);

                        // Fill silence if needed
                        if (storedBuffer.Length < count)
                        {
                            for (int i = offset + storedBuffer.Length; i < offset + count; i++)
                            {
                                buffer[i] = 0f;
                            }
                        }

                        storedBuffer = null;
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