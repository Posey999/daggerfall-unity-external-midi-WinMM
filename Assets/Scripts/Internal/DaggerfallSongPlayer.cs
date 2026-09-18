// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2023 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: Gavin Clayton (interkarma@dfworkshop.net)
// Contributors:
//
// Notes:           Added optional external MIDI output via winmm.dll (Windows only).
//                  The Exterior/Interior/Dungeon song players share a single open
//                  device (Windows MIDI outputs are single-client) and silence
//                  themselves when their GameObject is deactivated.
//                  External MIDI is the default output; use launch option
//                  "-midiout off" to force the internal synthesizer, or
//                  "-midiout <id>" to select a different output device.
//

using UnityEngine;
using System;
using System.IO;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;
using DaggerfallWorkshop.AudioSynthesis.Bank;
using DaggerfallWorkshop.AudioSynthesis.Sequencer;
using DaggerfallWorkshop.AudioSynthesis.Synthesis;
using DaggerfallWorkshop.AudioSynthesis.Midi;
using DaggerfallWorkshop.Utility.AssetInjection;
using DaggerfallWorkshop.Game.UserInterfaceWindows;

namespace DaggerfallWorkshop
{
    [RequireComponent(typeof(AudioSource))]
    public class DaggerfallSongPlayer : MonoBehaviour
    {
        const string sourceFolderName = "SoundFonts";
        const string defaultSoundFontFilename = "TimGM6mb.sf2";
        const int sampleRate = 48000;
        const int polyphony = 100;

        [NonSerialized, HideInInspector]
        public bool IsPlaying = false;
        [NonSerialized, HideInInspector]
        public int CurrentTime = 0;
        [NonSerialized, HideInInspector]
        public int EndTime = 0;

        public bool ShowDebugString = false;

        [Range(0.0f, 10.0f)]
        public float Gain = 5.0f;
        private bool IsMuted = false;
        public string SongFolder = "Songs/";
        public SongFiles Song = SongFiles.song_none;

        [Header("External MIDI (Windows winmm.dll)")]
        [Tooltip("Route MIDI playback to an external MIDI output device instead of the internal soundfont synthesizer.")]
        public bool UseExternalMidi = true;
        [Tooltip("External MIDI output device index. -1 = Windows MIDI Mapper / default device. Use DaggerfallSongPlayer.GetExternalMidiDevices() to list devices. Can be overridden at launch with '-midiout <id>'.")]
        public int MidiOutDeviceId = 2;

        AudioSource audioSource;
        Synthesizer midiSynthesizer = null;
        MidiFileSequencer midiSequencer = null;
        string currentMidiName;
        float[] sampleBuffer = new float[0];
        int channels = 0;
        int bufferLength = 0;
        int numBuffers = 0;
        bool playEnabled = false;
        bool awakeComplete = false;
        float oldGain;

        bool isImported;
        bool isLoading;

        ExternalMidiPlayer externalMidiPlayer = null;

        /// <summary>
        /// Gets peer AudioSource component.
        /// </summary>
        public AudioSource AudioSource
        {
            get { return (audioSource) ? audioSource : GetComponent<AudioSource>(); }
        }

        /// <summary>
        /// Gets names of all available external MIDI output devices. Index in array = device ID for MidiOutDeviceId.
        /// </summary>
        public static string[] GetExternalMidiDevices()
        {
            return WinmmMidiOut.GetDeviceNames();
        }

        void Start()
        {
            audioSource = GetComponent<AudioSource>();

            // Allow launch options to override MIDI routing in a built game:
            //   -midiout <id>   force external MIDI on, using device id
            //   -midiout off    force internal synthesizer
            ParseMidiCommandLine();

            if (UseExternalMidi)
            {
                // List available devices to help with configuration
                string[] devices = WinmmMidiOut.GetDeviceNames();
                for (int i = 0; i < devices.Length; i++)
                    Debug.LogFormat("DaggerfallSongPlayer: MIDI Out device {0}: {1}", i, devices[i]);

                externalMidiPlayer = new ExternalMidiPlayer();
                if (externalMidiPlayer.Open(MidiOutDeviceId))
                    Debug.LogFormat("DaggerfallSongPlayer: Using external MIDI device '{0}'.", externalMidiPlayer.DeviceName);
                else
                    DaggerfallUnity.LogMessage("DaggerfallSongPlayer: Could not open external MIDI device. Will retry on Play and fall back to internal synthesizer.");
            }
            else
            {
                InitSynth();
            }

            DaggerfallVidPlayerWindow.OnVideoStart += DaggerfallVidPlayerWindow_OnVideoStart;
            DaggerfallVidPlayerWindow.OnVideoEnd += DaggerfallVidPlayerWindow_OnVideoEnd;
        }

        void OnEnable()
        {
            // Waking up (e.g. player moved Exterior -> Interior).
            // Re-arm playOnAwake so this player's song starts again, and make
            // sure we're attached to the shared MIDI device.
            awakeComplete = false;
            if (UseExternalMidi && externalMidiPlayer != null && !externalMidiPlayer.IsOpen)
                externalMidiPlayer.Open(MidiOutDeviceId);
        }

        void OnDisable()
        {
            // Going to sleep (another SongPlayer branch is taking over).
            // An external synthesizer keeps sounding notes until explicitly
            // silenced - Unity cannot mute it - and LateUpdate never runs while
            // inactive, so playback must be stopped here.
            if (UseExternalMidi && externalMidiPlayer != null)
                externalMidiPlayer.Stop();
        }

        void OnDestroy()
        {
            DaggerfallVidPlayerWindow.OnVideoStart -= DaggerfallVidPlayerWindow_OnVideoStart;
            DaggerfallVidPlayerWindow.OnVideoEnd -= DaggerfallVidPlayerWindow_OnVideoEnd;

            if (externalMidiPlayer != null)
            {
                externalMidiPlayer.Dispose();
                externalMidiPlayer = null;
            }
        }

        void Update()
        {
            if (!isImported)
            {
                if (UseExternalMidi)
                {
                    // Update status from external MIDI player
                    if (externalMidiPlayer != null)
                    {
                        IsPlaying = externalMidiPlayer.IsPlaying;
                        CurrentTime = externalMidiPlayer.CurrentTimeSec;
                        EndTime = externalMidiPlayer.EndTimeSec;
                        externalMidiPlayer.SetVolume(IsMuted ? 0f : DaggerfallUnity.Settings.MusicVolume);
                    }
                }
                else if (midiSequencer != null)
                {
                    // Update status
                    IsPlaying = midiSequencer.IsPlaying;
                    CurrentTime = midiSequencer.CurrentTime;
                    EndTime = midiSequencer.EndTime;
                }
            }
            else
            {
                // Start playing
                if (isLoading && audioSource.clip.loadState == AudioDataLoadState.Loaded)
                {
                    isLoading = false;
                    audioSource.Play();
                }

                // Update status
                IsPlaying = audioSource.isPlaying || isLoading;
                CurrentTime = audioSource.timeSamples;
                EndTime = audioSource.clip.samples;
            }

            if (audioSource != null)
                audioSource.volume = IsMuted ? 0f : DaggerfallUnity.Settings.MusicVolume;
        }

        void LateUpdate()
        {
            if (!isImported)
            {
                bool isPlayingNow;
                if (UseExternalMidi)
                    isPlayingNow = externalMidiPlayer != null && externalMidiPlayer.IsPlaying;
                else
                    isPlayingNow = midiSequencer != null && midiSequencer.IsPlaying;

                if (audioSource != null && audioSource.playOnAwake && !isPlayingNow && !awakeComplete)
                {
                    Play();
                    awakeComplete = true;
                }
                if (audioSource != null && audioSource.loop && !isPlayingNow)
                {
                    Play();
                }
            }
        }

        void OnGUI()
        {
            if (Event.current.type.Equals(EventType.Repaint) && ShowDebugString)
            {
                GUIStyle style = new GUIStyle();
                style.normal.textColor = Color.black;
                string text = GetDebugString();
                GUI.Label(new Rect(10, 50, 800, 24), text, style);
                GUI.Label(new Rect(8, 48, 800, 24), text);
            }
        }

        public void Play()
        {
            if (Song == SongFiles.song_none)
                return;

            Play(Song);
        }

        /// <summary>
        /// Play current song.
        /// </summary>
        public void Play(SongFiles song)
        {
            // Route playback to external MIDI device when enabled
            if (UseExternalMidi && PlayExternal(song))
                return;

            if (!InitSynth())
                return;

            // Stop if playing another song
            Stop();

            // Import custom song
            AudioClip clip;
            if (isImported = SoundReplacement.TryImportSong(song, out clip))
            {
                Song = song;
                audioSource.clip = clip;
                isLoading = true;
                return;
            }

            // Load song data
            string filename = EnumToFilename(song);
            byte[] songData = LoadSong(filename);
            if (songData == null)
                return;

            // Create song
            MidiFile midiFile = new MidiFile(new MyMemoryFile(songData, filename));
            if (midiSequencer.LoadMidi(midiFile))
            {
                midiSequencer.Play();
                currentMidiName = filename;
                playEnabled = true;
                IsPlaying = true;
            }
        }

        /// <summary>
        /// Stop playing song.
        /// </summary>
        public void Stop()
        {
            // Stop external MIDI playback (silences channels on the hardware)
            if (externalMidiPlayer != null)
                externalMidiPlayer.Stop();

            // Reset audiosource clip
            if (isImported)
            {
                isImported = false;
                if (audioSource == null)
                    audioSource = GetComponent<AudioSource>();
                if (audioSource != null)
                {
                    audioSource.Stop();
                    audioSource.clip = null;
                    audioSource.Play();
                }
            }

            // Stop internal synth if it is in use
            if (midiSequencer != null && midiSequencer.IsPlaying)
            {
                midiSequencer.Stop();
                midiSynthesizer.NoteOffAll(true);
                midiSynthesizer.ResetSynthControls();
                midiSynthesizer.ResetPrograms();
                playEnabled = false;
            }
        }

        #region Private Methods

        /// <summary>
        /// Command-line overrides for external MIDI in built game.
        /// </summary>
        void ParseMidiCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!args[i].Equals("-midiout", StringComparison.OrdinalIgnoreCase))
                    continue;

                string val = args[i + 1];
                int id;
                if (int.TryParse(val, out id))
                {
                    MidiOutDeviceId = id;
                    UseExternalMidi = true;
                    Debug.LogFormat("DaggerfallSongPlayer: Command line forces external MIDI, device id {0}.", id);
                }
                else if (val.Equals("off", StringComparison.OrdinalIgnoreCase) || val.Equals("internal", StringComparison.OrdinalIgnoreCase))
                {
                    UseExternalMidi = false;
                    Debug.Log("DaggerfallSongPlayer: Command line forces internal synthesizer.");
                }
            }
        }

        /// <summary>
        /// Attempts to play song on the external MIDI device.
        /// Returns true if playback was handled (external MIDI or imported audio), false to fall back to internal synth.
        /// </summary>
        private bool PlayExternal(SongFiles song)
        {
            if (externalMidiPlayer == null)
                externalMidiPlayer = new ExternalMidiPlayer();

            // (Re)open/attach device if needed
            if (!externalMidiPlayer.IsOpen && !externalMidiPlayer.Open(MidiOutDeviceId))
            {
                DaggerfallUnity.LogMessage(string.Format("DaggerfallSongPlayer: Unable to open external MIDI output device ({0}). Falling back to internal synthesizer.", WinmmMidiOut.GetErrorText(externalMidiPlayer.LastError)));
                return false;
            }

            // Stop current playback (external, internal or imported)
            Stop();

            // Imported audio replacements still take priority over MIDI output
            AudioClip clip;
            if (isImported = SoundReplacement.TryImportSong(song, out clip))
            {
                Song = song;
                audioSource.clip = clip;
                isLoading = true;
                return true;
            }

            // Load song data
            string filename = EnumToFilename(song);
            byte[] songData = LoadSong(filename);
            if (songData == null)
                return true;

            // Parse MIDI for external playback
            if (!externalMidiPlayer.Load(songData, filename))
                return false; // Parse failed - try internal synthesizer instead

            Song = song;
            currentMidiName = filename;
            externalMidiPlayer.SetVolume(IsMuted ? 0f : DaggerfallUnity.Settings.MusicVolume);
            externalMidiPlayer.Play();
            IsPlaying = true;
            return true;
        }

        private bool InitSynth()
        {
            // Get peer AudioSource
            audioSource = GetComponent<AudioSource>();
            if (audioSource == null)
            {
                DaggerfallUnity.LogMessage("DaggerfallSongPlayer: Could not find AudioSource component.");
                return false;
            }

            // Create synthesizer and load bank
            if (midiSynthesizer == null)
            {
                // Get number of channels
                if (AudioSettings.driverCapabilities.ToString() == "Mono")
                    channels = 1;
                else
                    channels = 2;

                // Create synth
                AudioSettings.GetDSPBufferSize(out bufferLength, out numBuffers);
                midiSynthesizer = new Synthesizer(sampleRate, channels, bufferLength / numBuffers, numBuffers, polyphony);

                // Load bank data
                string filename = DaggerfallUnity.Settings.SoundFont;
                byte[] bankData = LoadBank(filename);
                if (bankData == null)
                {
                    // Attempt to fallback to default internal soundfont
                    bankData = LoadDefaultSoundFont();
                    filename = defaultSoundFontFilename;
                    Debug.LogFormat("Using default SoundFont {0}", defaultSoundFontFilename);
                }
                else
                {
                    Debug.LogFormat("Trying custom SoundFont {0}", filename);
                }

                // Assign to synth
                if (bankData == null)
                    return false;
                else
                {
                    midiSynthesizer.LoadBank(new MyMemoryFile(bankData, filename));
                    midiSynthesizer.ResetSynthControls(); // Need to do this for bank to load properly, don't know why
                }
            }

            // Create sequencer
            if (midiSequencer == null)
                midiSequencer = new MidiFileSequencer(midiSynthesizer);

            // Check init
            if (midiSynthesizer == null || midiSequencer == null)
            {
                DaggerfallUnity.LogMessage("DaggerfallSongPlayer: Failed to init synth.");
                return false;
            }

            return true;
        }

        private string EnumToFilename(SongFiles song)
        {
            string enumName = song.ToString();
            return enumName.Remove(0, "song_".Length) + ".mid";
        }

        private byte[] LoadBank(string filename)
        {
            // Do nothing if no filename set
            if (string.IsNullOrEmpty(filename))
                return null;

            // Check file exists
            string path = Path.Combine(Application.streamingAssetsPath, sourceFolderName);
            string filePath = Path.Combine(path, filename);
            if (!File.Exists(filePath))
            {
                // Fallback to default sound font
                Debug.LogFormat("Could not find file '{0}', falling back to default soundfont {1}.", filePath, defaultSoundFontFilename);
                return null;
            }

            // Load data
            return File.ReadAllBytes(filePath);
        }

        private byte[] LoadDefaultSoundFont()
        {
            TextAsset asset = Resources.Load<TextAsset>(defaultSoundFontFilename);
            if (asset != null)
            {
                return asset.bytes;
            }

            DaggerfallUnity.LogMessage(string.Format("DaggerfallSongPlayer: Bank file '{0}' not found.", defaultSoundFontFilename));

            return null;
        }

        private byte[] LoadSong(string filename)
        {
            // Get custom midi song
            byte[] songBytes;
            if (SoundReplacement.TryImportMidiSong(filename, out songBytes))
                return songBytes;

            // Get Daggerfal song
            TextAsset asset = Resources.Load<TextAsset>(Path.Combine(SongFolder, filename));
            if (asset != null)
            {
                return asset.bytes;
            }

            DaggerfallUnity.LogMessage(string.Format("DaggerfallSongPlayer: Song file '{0}' not found.", filename));

            return null;
        }

        private string GetDebugString()
        {
            if (UseExternalMidi)
            {
                if (isImported)
                {
                    if (isLoading)
                        return string.Format("Loading song '{0}'", Song);
                    else
                        return string.Format("Playing song '{0}' at position {1}/{2}", Song, audioSource.timeSamples, audioSource.clip.samples);
                }
                if (externalMidiPlayer == null || !externalMidiPlayer.IsOpen)
                    return "External MIDI device not available.";
                if (externalMidiPlayer.IsPlaying)
                    return string.Format("Playing song '{0}' on '{1}' at position {2}/{3}", currentMidiName, externalMidiPlayer.DeviceName, externalMidiPlayer.CurrentTimeSec, externalMidiPlayer.EndTimeSec);
                return string.Format("Song '{0}' ready on '{1}'. Not playing.", currentMidiName, externalMidiPlayer.DeviceName);
            }

            if (midiSequencer == null)
                return "Sequencer not ready.";
            if (midiSynthesizer == null)
                return "Synthesizer not ready.";

            string final;
            if (isImported)
            {
                if (isLoading)
                    final = string.Format("Loading song '{0}'", Song);
                else
                    final = string.Format("Playing song '{0}' at position {1}/{2}", Song, audioSource.timeSamples, audioSource.clip.samples);
            }
            else if (midiSequencer.IsPlaying)
                final = string.Format("Playing song '{0}' at position {1}/{2}", currentMidiName, midiSequencer.CurrentTime, midiSequencer.EndTime);
            else
                final = string.Format("Song '{0}' ready. Not playing.", currentMidiName);

            return final;
        }

        private void DaggerfallVidPlayerWindow_OnVideoStart()
        {
            // Mute music while video is playing
            oldGain = Gain;
            Gain = 0;
            IsMuted = true;
            if (externalMidiPlayer != null)
                externalMidiPlayer.SetVolume(0f);
        }

        private void DaggerfallVidPlayerWindow_OnVideoEnd()
        {
            // Restore music to previous level
            Gain = oldGain;
            IsMuted = false;
            if (externalMidiPlayer != null)
                externalMidiPlayer.SetVolume(DaggerfallUnity.Settings.MusicVolume);
        }

        #endregion

        #region Audio Filter

        // Called when audio filter needs more sound data
        void OnAudioFilterRead(float[] data, int channels)
        {
            // External MIDI bypasses the audio filter entirely
            if (UseExternalMidi)
                return;

            // Do nothing if play not enabled
            // This flag is raised/lowered when user starts/stops play
            // Helps avoids thread finding synth in state of shutting down
            if (!playEnabled)
                return;

            // Must have synth and seq
            if (midiSynthesizer == null || midiSequencer == null)
                return;

            // Sample buffer size must match working buffer size
            if (sampleBuffer.Length != midiSynthesizer.WorkingBufferSize)
                sampleBuffer = new float[midiSynthesizer.WorkingBufferSize];

            try
            {
                // Update sequencing - must be playing a song
                if (midiSequencer.IsMidiLoaded && midiSequencer.IsPlaying)
                {
                    midiSequencer.FillMidiEventQueue();
                    midiSynthesizer.GetNext(sampleBuffer);
                    for (int i = 0; i < data.Length; i++)
                    {
                        data[i] = sampleBuffer[i] * Gain;
                    }
                }
            }
            catch (Exception)
            {
                // Will sometimes drop here if Unity tries to feed audio filter
                // from another thread while synth is starting up or shutting down
                // Just nom the exception
            }
        }

        #endregion

        #region External MIDI (winmm.dll)

        /// <summary>
        /// Wrapper for Windows Multimedia API (winmm.dll) MIDI output device.
        /// </summary>
        public class WinmmMidiOut : IDisposable
        {
            const int MMSYSERR_NOERROR = 0;
            const int MMSYSERR_ALLOCATED = 4;
            const uint MIDI_MAPPER = 0xFFFFFFFF;
            const uint CALLBACK_NULL = 0;
            const int MAXPNAMELEN = 32;
            const int MHDR_DONE = 0x0001;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
            struct MIDIOUTCAPS
            {
                public ushort wMid;
                public ushort wPid;
                public uint vDriverVersion;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAXPNAMELEN)]
                public string szPname;
                public ushort wTechnology;
                public ushort wVoices;
                public ushort wNotes;
                public ushort wChannelMask;
                public uint dwSupport;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct MIDIHDR
            {
                public IntPtr lpData;
                public uint dwBufferLength;
                public uint dwBytesRecorded;
                public IntPtr dwUser;
                public uint dwFlags;
                public IntPtr lpNext;
                public IntPtr reserved;
                public uint dwOffset;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
                public IntPtr[] dwReserved;
            }

            [DllImport("winmm.dll")]
            static extern uint midiOutGetNumDevs();

            [DllImport("winmm.dll")]
            static extern int midiOutGetDevCaps(UIntPtr uDeviceID, out MIDIOUTCAPS lpMidiOutCaps, uint cbMidiOutCaps);

            [DllImport("winmm.dll")]
            static extern int midiOutOpen(out IntPtr lphMidiOut, uint uDeviceID, IntPtr dwCallback, IntPtr dwInstance, uint dwFlags);

            [DllImport("winmm.dll")]
            static extern int midiOutClose(IntPtr hMidiOut);

            [DllImport("winmm.dll")]
            static extern int midiOutShortMsg(IntPtr hMidiOut, uint dwMsg);

            [DllImport("winmm.dll")]
            static extern int midiOutReset(IntPtr hMidiOut);

            [DllImport("winmm.dll")]
            static extern int midiOutSetVolume(IntPtr hMidiOut, uint dwVolume);

            [DllImport("winmm.dll", EntryPoint = "midiOutPrepareHeader")]
            static extern int midiOutPrepareHeader(IntPtr hMidiOut, IntPtr lpMidiOutHdr, uint cbMidiOutHdr);

            [DllImport("winmm.dll", EntryPoint = "midiOutUnprepareHeader")]
            static extern int midiOutUnprepareHeader(IntPtr hMidiOut, IntPtr lpMidiOutHdr, uint cbMidiOutHdr);

            [DllImport("winmm.dll", EntryPoint = "midiOutLongMsg")]
            static extern int midiOutLongMsg(IntPtr hMidiOut, IntPtr lpMidiOutHdr, uint cbMidiOutHdr);

            [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
            static extern int midiOutGetErrorText(int mmrError, StringBuilder lpText, uint cchText);

            [DllImport("winmm.dll")]
            static extern uint timeBeginPeriod(uint uPeriod);

            [DllImport("winmm.dll")]
            static extern uint timeEndPeriod(uint uPeriod);

            IntPtr handle = IntPtr.Zero;
            string deviceName = string.Empty;

            public bool IsOpen { get { return handle != IntPtr.Zero; } }
            public string DeviceName { get { return deviceName; } }
            public int LastError { get; private set; }

            public static string[] GetDeviceNames()
            {
                uint count = midiOutGetNumDevs();
                string[] names = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    MIDIOUTCAPS caps;
                    if (midiOutGetDevCaps((UIntPtr)i, out caps, (uint)Marshal.SizeOf(typeof(MIDIOUTCAPS))) == MMSYSERR_NOERROR)
                        names[i] = caps.szPname;
                    else
                        names[i] = "Unknown MIDI device " + i;
                }
                return names;
            }

            public static string GetErrorText(int errorCode)
            {
                StringBuilder sb = new StringBuilder(256);
                if (midiOutGetErrorText(errorCode, sb, (uint)sb.Capacity) == MMSYSERR_NOERROR)
                    return sb.ToString();
                return "MIDI error " + errorCode;
            }

            public bool Open(int deviceId)
            {
                Close();

                // Retry briefly - another app may be in the process of releasing the device
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (TryOpen(deviceId))
                        return true;
                    if (LastError != MMSYSERR_ALLOCATED)
                        break;
                    Thread.Sleep(200);
                }
                return false;
            }

            bool TryOpen(int deviceId)
            {
                uint id = deviceId < 0 ? MIDI_MAPPER : (uint)deviceId;
                IntPtr newHandle;
                LastError = midiOutOpen(out newHandle, id, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
                if (LastError != MMSYSERR_NOERROR)
                {
                    handle = IntPtr.Zero;
                    deviceName = string.Empty;
                    return false;
                }

                handle = newHandle;

                // Get friendly device name
                MIDIOUTCAPS caps;
                if (midiOutGetDevCaps((UIntPtr)id, out caps, (uint)Marshal.SizeOf(typeof(MIDIOUTCAPS))) == MMSYSERR_NOERROR)
                    deviceName = caps.szPname;
                else
                    deviceName = deviceId < 0 ? "MIDI Mapper" : "MIDI device " + deviceId;

                // Request 1ms timer resolution for playback thread sleeps
                timeBeginPeriod(1);
                return true;
            }

            public void Close()
            {
                if (handle != IntPtr.Zero)
                {
                    midiOutReset(handle);
                    midiOutClose(handle);
                    handle = IntPtr.Zero;
                    deviceName = string.Empty;
                    timeEndPeriod(1);
                }
            }

            public void SendShortMessage(byte status, byte data1, byte data2)
            {
                if (handle == IntPtr.Zero)
                    return;
                uint msg = (uint)(status | (data1 << 8) | (data2 << 16));
                midiOutShortMsg(handle, msg);
            }

            public void SendSysEx(byte[] data)
            {
                if (handle == IntPtr.Zero || data == null || data.Length == 0)
                    return;

                int headerSize = Marshal.SizeOf(typeof(MIDIHDR));
                IntPtr headerPtr = Marshal.AllocHGlobal(headerSize);
                IntPtr dataPtr = Marshal.AllocHGlobal(data.Length);
                try
                {
                    Marshal.Copy(data, 0, dataPtr, data.Length);

                    MIDIHDR header = new MIDIHDR();
                    header.lpData = dataPtr;
                    header.dwBufferLength = (uint)data.Length;
                    header.dwBytesRecorded = (uint)data.Length;
                    header.dwReserved = new IntPtr[4];
                    Marshal.StructureToPtr(header, headerPtr, false);

                    if (midiOutPrepareHeader(handle, headerPtr, (uint)headerSize) != MMSYSERR_NOERROR)
                        return;

                    midiOutLongMsg(handle, headerPtr, (uint)headerSize);

                    // Wait until driver is finished with the buffer (no callback; poll MHDR_DONE)
                    for (int i = 0; i < 1000; i++)
                    {
                        MIDIHDR current = (MIDIHDR)Marshal.PtrToStructure(headerPtr, typeof(MIDIHDR));
                        if ((current.dwFlags & MHDR_DONE) != 0)
                            break;
                        Thread.Sleep(1);
                    }

                    midiOutUnprepareHeader(handle, headerPtr, (uint)headerSize);
                }
                finally
                {
                    Marshal.FreeHGlobal(dataPtr);
                    Marshal.FreeHGlobal(headerPtr);
                }
            }

            public void Reset()
            {
                if (handle != IntPtr.Zero)
                    midiOutReset(handle);
            }

            public void SetDeviceVolume(uint volumeLR)
            {
                // Not supported by all devices (e.g. many hardware ports) - ignore errors
                if (handle != IntPtr.Zero)
                    midiOutSetVolume(handle, volumeLR);
            }

            public void Dispose()
            {
                Close();
            }
        }

        /// <summary>
        /// Parses standard MIDI files and plays them on an external MIDI output device
        /// using a dedicated high-resolution timing thread.
        /// All DaggerfallSongPlayer instances (Exterior/Interior/Dungeon) share a single
        /// open MIDI device, because Windows MIDI outputs are single-client.
        /// </summary>
        public class ExternalMidiPlayer : IDisposable
        {
            static readonly byte[] GmSystemOn = new byte[] { 0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7 };

            // One shared device for all song players in the scene
            static readonly object deviceLock = new object();
            static WinmmMidiOut sharedDevice = null;
            static int sharedUsers = 0;
            static int sharedDeviceId = -1;

            class TimedEvent
            {
                public double TimeSec;
                public byte Status;
                public byte Data1;
                public byte Data2;
                public byte[] SysEx;
                public bool IsTempo;
                public int TempoUSec;
            }

            class RawEvent
            {
                public long Tick;
                public int Order;
                public byte Status;
                public byte Data1;
                public byte Data2;
                public byte[] SysEx;
                public bool IsTempo;
                public int TempoUSec;
            }

            WinmmMidiOut midiOut = null; // attached shared device
            readonly int[] channelVolumeRaw = new int[16]; // last CC7 value per channel from song
            readonly bool[] usedChannels = new bool[16];   // channels this player has sent on
            int lastError = 0;

            List<TimedEvent> events = new List<TimedEvent>();
            Thread playThread = null;
            volatile bool stopRequested = false;
            volatile bool isPlaying = false;
            volatile int currentTimeSec = 0;
            double endTimeSec = 0;
            float volume = 1f;
            float lastAppliedVolume = -1f;

            byte[] smfData;
            int readPos;

            public bool IsOpen { get { return midiOut != null && midiOut.IsOpen; } }
            public bool IsPlaying { get { return isPlaying; } }
            public string DeviceName { get { return midiOut != null ? midiOut.DeviceName : string.Empty; } }
            public int LastError { get { return midiOut != null ? midiOut.LastError : lastError; } }
            public int CurrentTimeSec { get { return currentTimeSec; } }
            public int EndTimeSec { get { return (int)Math.Round(endTimeSec); } }

            public bool Open(int deviceId)
            {
                lock (deviceLock)
                {
                    // Already attached to the shared device
                    if (midiOut != null && midiOut.IsOpen)
                        return true;

                    // Attach to device already opened by another song player
                    if (sharedDevice != null && sharedDevice.IsOpen)
                    {
                        midiOut = sharedDevice;
                        sharedUsers++;
                        if (deviceId != sharedDeviceId)
                            Debug.LogFormat("DaggerfallSongPlayer: Sharing external MIDI device '{0}' (id={1}) opened by another song player.", sharedDevice.DeviceName, sharedDeviceId);
                        return true;
                    }

                    // First song player - open the device for everyone
                    WinmmMidiOut candidate = new WinmmMidiOut();
                    if (!candidate.Open(deviceId))
                    {
                        lastError = candidate.LastError;
                        return false;
                    }

                    sharedDevice = candidate;
                    sharedDeviceId = deviceId;
                    sharedUsers = 1;
                    midiOut = sharedDevice;
                    return true;
                }
            }

            /// <summary>
            /// Parse a standard MIDI file into an absolute-timed event list.
            /// </summary>
            public bool Load(byte[] data, string songName)
            {
                events = new List<TimedEvent>();
                endTimeSec = 0;
                currentTimeSec = 0;
                for (int i = 0; i < 16; i++)
                {
                    channelVolumeRaw[i] = 127;
                    usedChannels[i] = false;
                }

                try
                {
                    smfData = data;
                    readPos = 0;
                    ParseSmf();
                }
                catch (Exception ex)
                {
                    DaggerfallUnity.LogMessage(string.Format("DaggerfallSongPlayer: Failed to parse MIDI '{0}' for external playback: {1}", songName, ex.Message));
                    events = new List<TimedEvent>();
                    return false;
                }
                finally
                {
                    smfData = null;
                }
                return true;
            }

            public void Play()
            {
                if (!IsOpen || events.Count == 0)
                    return;

                // Previous playback thread still running? Stop and silence it first
                if (playThread != null && playThread.IsAlive)
                    Stop();

                stopRequested = false;
                isPlaying = true;
                playThread = new Thread(ThreadProc);
                playThread.IsBackground = true;
                playThread.Name = "DFU External MIDI";
                playThread.Priority = System.Threading.ThreadPriority.AboveNormal;
                playThread.Start();
            }

            /// <summary>
            /// Stops playback and silences every channel this player used.
            /// The shared device stays attached (released in Dispose) so song
            /// changes don't churn close/open cycles on the hardware.
            /// </summary>
            public void Stop()
            {
                stopRequested = true;
                Thread t = playThread;
                if (t != null && t.IsAlive)
                    t.Join(500);
                playThread = null;

                WinmmMidiOut device = midiOut;
                if (device != null && device.IsOpen)
                {
                    lock (deviceLock)
                    {
                        for (int ch = 0; ch < 16; ch++)
                        {
                            if (usedChannels[ch])
                            {
                                device.SendShortMessage((byte)(0xB0 | ch), 120, 0); // All Sound Off
                                device.SendShortMessage((byte)(0xB0 | ch), 123, 0); // All Notes Off
                            }
                        }
                    }
                }

                Array.Clear(usedChannels, 0, usedChannels.Length);
                isPlaying = false;
                currentTimeSec = 0;
            }

            /// <summary>
            /// Sets master volume (0-1). Scales channel volume (CC7) so the music
            /// volume slider keeps working even for songs using CC7 themselves.
            /// </summary>
            public void SetVolume(float value)
            {
                value = Mathf.Clamp01(value);
                if (Math.Abs(value - volume) < 0.0001f)
                    return;
                volume = value;
                ApplyVolume(false);
            }

            public void Dispose()
            {
                Stop();
                Detach();
            }

            /// <summary>
            /// Detach from the shared device. The device itself is closed when
            /// the last attached player detaches (scene unload / play mode exit).
            /// </summary>
            void Detach()
            {
                lock (deviceLock)
                {
                    if (midiOut != null)
                    {
                        sharedUsers--;
                        if (sharedUsers <= 0 && sharedDevice != null)
                        {
                            sharedDevice.Dispose();
                            sharedDevice = null;
                            sharedUsers = 0;
                        }
                        midiOut = null;
                    }
                }
            }

            void ApplyVolume(bool force)
            {
                WinmmMidiOut device = midiOut;
                if (device == null || !device.IsOpen)
                    return;
                if (!force && Math.Abs(volume - lastAppliedVolume) < 0.0001f)
                    return;
                lastAppliedVolume = volume;

                int master = (int)(volume * 127f + 0.5f);
                for (int ch = 0; ch < 16; ch++)
                {
                    int scaled = (channelVolumeRaw[ch] * master) / 127;
                    device.SendShortMessage((byte)(0xB0 | ch), 7, (byte)scaled);
                }

                // Also set device-level volume where supported (e.g. MS GS Wavetable Synth)
                ushort v = (ushort)(volume * 65535f);
                device.SetDeviceVolume((uint)v | ((uint)v << 16));
            }

            void ThreadProc()
            {
                WinmmMidiOut device = midiOut;
                if (device == null || !device.IsOpen)
                {
                    isPlaying = false;
                    return;
                }

                try
                {
                    // Prepare device for GM playback
                    device.SendSysEx(GmSystemOn);
                    for (int ch = 0; ch < 16; ch++)
                        device.SendShortMessage((byte)(0xB0 | ch), 121, 0); // Reset All Controllers
                    ApplyVolume(true);
                    Thread.Sleep(50); // Give device a moment after reset

                    System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                    List<TimedEvent> evs = events;
                    int index = 0;
                    int count = evs.Count;
                    while (!stopRequested && index < count)
                    {
                        TimedEvent ev = evs[index];
                        double wait = ev.TimeSec - clock.Elapsed.TotalSeconds;
                        if (wait > 0.004)
                        {
                            Thread.Sleep(1);
                            continue;
                        }
                        // Spin for the final few milliseconds for tighter timing
                        while (!stopRequested && ev.TimeSec - clock.Elapsed.TotalSeconds > 0)
                        {
                        }
                        if (stopRequested)
                            break;

                        Dispatch(ev);
                        currentTimeSec = (int)ev.TimeSec;
                        index++;
                    }
                    if (index >= count)
                        currentTimeSec = (int)Math.Round(endTimeSec);
                }
                catch (Exception)
                {
                    // Nom - device may have been closed during playback
                }
                isPlaying = false;
            }

            void Dispatch(TimedEvent ev)
            {
                WinmmMidiOut device = midiOut;
                if (device == null || !device.IsOpen)
                    return;

                if (ev.SysEx != null)
                {
                    device.SendSysEx(ev.SysEx);
                    return;
                }
                if (ev.IsTempo)
                    return; // Tempo already consumed when building the time map

                usedChannels[ev.Status & 0x0F] = true;

                // Intercept channel volume so master volume slider keeps working
                if ((ev.Status & 0xF0) == 0xB0 && ev.Data1 == 7)
                {
                    int ch = ev.Status & 0x0F;
                    channelVolumeRaw[ch] = ev.Data2;
                    int master = (int)(volume * 127f + 0.5f);
                    device.SendShortMessage(ev.Status, 7, (byte)((ev.Data2 * master) / 127));
                    return;
                }

                device.SendShortMessage(ev.Status, ev.Data1, ev.Data2);
            }

            #region SMF Parsing

            uint ReadU32()
            {
                uint v = (uint)((smfData[readPos] << 24) | (smfData[readPos + 1] << 16) | (smfData[readPos + 2] << 8) | smfData[readPos + 3]);
                readPos += 4;
                return v;
            }

            int ReadU16()
            {
                int v = (smfData[readPos] << 8) | smfData[readPos + 1];
                readPos += 2;
                return v;
            }

            int ReadVarLen()
            {
                int value = 0;
                for (int i = 0; i < 4; i++)
                {
                    byte b = smfData[readPos++];
                    value = (value << 7) | (b & 0x7F);
                    if ((b & 0x80) == 0)
                        return value;
                }
                return value;
            }

            string ReadFourCc()
            {
                string s = Encoding.ASCII.GetString(smfData, readPos, 4);
                readPos += 4;
                return s;
            }

            void ParseSmf()
            {
                if (smfData == null || smfData.Length < 14)
                    throw new Exception("File too small.");

                if (ReadFourCc() != "MThd")
                    throw new Exception("Missing MThd header.");

                int headerLen = (int)ReadU32();
                if (headerLen < 6)
                    throw new Exception("Bad header length.");

                ReadU16(); // format type - not needed, tracks are merged either way
                int trackCount = ReadU16();
                int division = ReadU16();
                if (headerLen > 6)
                    readPos += headerLen - 6;

                List<RawEvent> all = new List<RawEvent>();
                int order = 0;

                for (int t = 0; t < trackCount; t++)
                {
                    if (readPos + 8 > smfData.Length)
                        break;

                    string chunkId = ReadFourCc();
                    int chunkLen = (int)ReadU32();
                    int chunkEnd = Math.Min(readPos + chunkLen, smfData.Length);
                    if (chunkId != "MTrk")
                    {
                        readPos = chunkEnd;
                        continue;
                    }

                    long tick = 0;
                    byte runningStatus = 0;
                    while (readPos < chunkEnd)
                    {
                        tick += ReadVarLen();
                        int b = smfData[readPos];

                        if (b == 0xFF) // Meta event
                        {
                            readPos++;
                            int metaType = smfData[readPos++];
                            int metaLen = ReadVarLen();
                            if (metaType == 0x51 && metaLen == 3) // Set Tempo
                            {
                                int us = (smfData[readPos] << 16) | (smfData[readPos + 1] << 8) | smfData[readPos + 2];
                                all.Add(new RawEvent { Tick = tick, Order = order++, IsTempo = true, TempoUSec = us });
                            }
                            readPos += metaLen;
                            if (metaType == 0x2F) // End of Track
                            {
                                readPos = chunkEnd;
                                break;
                            }
                        }
                        else if (b == 0xF0 || b == 0xF7) // SysEx
                        {
                            readPos++;
                            int sysLen = ReadVarLen();
                            byte[] sx;
                            if (b == 0xF0)
                            {
                                sx = new byte[sysLen + 1];
                                sx[0] = 0xF0;
                                Array.Copy(smfData, readPos, sx, 1, sysLen);
                            }
                            else
                            {
                                sx = new byte[sysLen];
                                Array.Copy(smfData, readPos, sx, 0, sysLen);
                            }
                            readPos += sysLen;
                            all.Add(new RawEvent { Tick = tick, Order = order++, SysEx = sx });
                        }
                        else // Channel message (with running status support)
                        {
                            byte status;
                            if (b >= 0x80)
                            {
                                status = (byte)b;
                                readPos++;
                                if ((status & 0xF0) != 0xF0)
                                    runningStatus = status;
                            }
                            else
                            {
                                status = runningStatus;
                                if (status == 0)
                                    throw new Exception("Running status without previous status byte.");
                            }

                            int command = status & 0xF0;
                            if (command == 0xF0)
                                continue; // Unexpected system message - skip byte and continue

                            byte d1 = smfData[readPos++];
                            byte d2 = 0;
                            if (command != 0xC0 && command != 0xD0) // Program change & channel pressure have one data byte
                                d2 = smfData[readPos++];

                            all.Add(new RawEvent { Tick = tick, Order = order++, Status = status, Data1 = d1, Data2 = d2 });
                        }
                    }
                    readPos = chunkEnd;
                }

                // Stable sort all events by absolute tick
                all.Sort(delegate (RawEvent a, RawEvent b2)
                {
                    if (a.Tick != b2.Tick)
                        return a.Tick.CompareTo(b2.Tick);
                    return a.Order.CompareTo(b2.Order);
                });

                // Build absolute time map (seconds), applying tempo changes along the way
                bool smpte = (division & 0x8000) != 0;
                int ticksPerQuarter = division & 0x7FFF;
                double smpteSecPerTick = 0;
                if (smpte)
                {
                    int fps = 256 - ((division >> 8) & 0xFF);
                    int ticksPerFrame = division & 0xFF;
                    smpteSecPerTick = 1.0 / (fps * ticksPerFrame);
                }
                else if (ticksPerQuarter <= 0)
                {
                    throw new Exception("Invalid MIDI division.");
                }

                double sec = 0;
                long lastTick = 0;
                double usPerQuarter = 500000; // Default 120 BPM
                List<TimedEvent> timed = new List<TimedEvent>(all.Count);
                foreach (RawEvent r in all)
                {
                    long delta = r.Tick - lastTick;
                    if (delta > 0)
                    {
                        if (smpte)
                            sec += delta * smpteSecPerTick;
                        else
                            sec += (delta * usPerQuarter) / (1000000.0 * ticksPerQuarter);
                        lastTick = r.Tick;
                    }

                    timed.Add(new TimedEvent
                    {
                        TimeSec = sec,
                        Status = r.Status,
                        Data1 = r.Data1,
                        Data2 = r.Data2,
                        SysEx = r.SysEx,
                        IsTempo = r.IsTempo,
                        TempoUSec = r.TempoUSec,
                    });

                    if (r.IsTempo && !smpte)
                        usPerQuarter = r.TempoUSec;
                }

                events = timed;
                endTimeSec = sec;
            }

            #endregion
        }

        #endregion

        #region Interface Implementation

        public class MyMemoryFile : AudioSynthesis.IResource
        {
            private byte[] file;
            private string fileName;
            public MyMemoryFile(byte[] file, string fileName)
            {
                this.file = file;
                this.fileName = fileName;
            }
            public string GetName() { return fileName; }
            public bool DeleteAllowed() { return false; }
            public bool ReadAllowed() { return true; }
            public bool WriteAllowed() { return false; }
            public void DeleteResource() { return; }
            public Stream OpenResourceForRead() { return new MemoryStream(file); }
            public Stream OpenResourceForWrite() { return null; }
        }

        #endregion

        #region Static Song Arrays

        /// <summary>
        /// Just the GM songs.
        /// </summary>
        public static SongFiles[] Songs_GM = new SongFiles[]
        {
            SongFiles.song_02,
            SongFiles.song_03,
            SongFiles.song_04,
            SongFiles.song_05,
            SongFiles.song_06,
            SongFiles.song_07,
            SongFiles.song_08,
            SongFiles.song_09,
            SongFiles.song_10,
            SongFiles.song_11,
            SongFiles.song_12,
            SongFiles.song_13,
            SongFiles.song_15,
            SongFiles.song_16,
            SongFiles.song_17,
            SongFiles.song_18,
            SongFiles.song_20,
            SongFiles.song_21,
            SongFiles.song_22,
            SongFiles.song_23,
            SongFiles.song_25,
            SongFiles.song_28,
            SongFiles.song_29,
            SongFiles.song_30,
            SongFiles.song_d1,
            SongFiles.song_d10,
            SongFiles.song_d2,
            SongFiles.song_d3,
            SongFiles.song_d4,
            SongFiles.song_d5,
            SongFiles.song_d6,
            SongFiles.song_d7,
            SongFiles.song_d8,
            SongFiles.song_d9,
            SongFiles.song_dungeon,
            SongFiles.song_dungeon5,
            SongFiles.song_dungeon6,
            SongFiles.song_dungeon7,
            SongFiles.song_dungeon8,
            SongFiles.song_dungeon9,
            SongFiles.song_folk1,
            SongFiles.song_folk2,
            SongFiles.song_folk3,
            SongFiles.song_gbad,
            SongFiles.song_gcurse,
            SongFiles.song_gday___d,
            SongFiles.song_gdngn10,
            SongFiles.song_gdngn11,
            SongFiles.song_gdungn4,
            SongFiles.song_gdungn9,
            SongFiles.song_geerie,
            SongFiles.song_ggood,
            SongFiles.song_gmage_3,
            SongFiles.song_gneut,
            SongFiles.song_gpalac,
            SongFiles.song_gruins,
            SongFiles.song_gshop,
            SongFiles.song_gsneak2,
            SongFiles.song_gsnow__b,
            SongFiles.song_gsunny2,
            SongFiles.song_magic_2,
            SongFiles.song_overcast,
            SongFiles.song_overlong,
            SongFiles.song_oversnow,
            SongFiles.song_raining,
            SongFiles.song_sneaking,
            SongFiles.song_sneakng2,
            SongFiles.song_snowing,
            SongFiles.song_square_2,
            SongFiles.song_sunnyday,
            SongFiles.song_swimming,
            SongFiles.song_tavern,
        };

        #endregion
    }
}
