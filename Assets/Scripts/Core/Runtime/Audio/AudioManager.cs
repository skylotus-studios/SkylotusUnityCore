using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace Skylotus
{
    /// <summary>Audio channel categories for independent volume control.</summary>
    /// <remarks>
    /// Every member maps to a mixer group of the same name inside
    /// <c>Assets/Resources/Audio/SkylotusAudioMixer.mixer</c> and to an exposed mixer
    /// parameter named <c>&lt;Channel&gt;Volume</c>. Adding a member here means adding both,
    /// which <c>SkylotusCI.GenerateAudioMixer</c> does.
    /// </remarks>
    public enum AudioChannel { Master, Music, SFX, UI, Ambience, Voice }

    /// <summary>
    /// Centralized audio manager providing channel-based volume control, a pre-allocated
    /// SFX source pool, smooth music crossfading, and spatial audio helpers.
    ///
    /// Register via <see cref="ServiceLocator"/> and access anywhere:
    /// <code>ServiceLocator.Get&lt;AudioManager&gt;().PlaySFX(clip);</code>
    ///
    /// <b>Mixing.</b> Every source routes into an <see cref="AudioMixer"/> group matching its
    /// <see cref="AudioChannel"/>, and <see cref="SetVolume"/> writes an exposed mixer
    /// parameter on a decibel curve rather than multiplying onto
    /// <see cref="AudioSource.volume"/>. That is what makes ducking, snapshots and per-channel
    /// DSP possible without touching a single call site. When no mixer is assigned the manager
    /// falls back to the old direct-volume behaviour so the game still makes noise.
    /// </summary>
    public class AudioManager : MonoBehaviour
    {
        /// <summary>Resources-relative path of the mixer used when none is assigned.</summary>
        public const string MixerResourcePath = "Audio/SkylotusAudioMixer";

        /// <summary>Decibel value written for a fully muted channel.</summary>
        /// <remarks>-80 dB is Unity's mixer floor; anything below it is inaudible.</remarks>
        public const float MinimumVolumeDecibels = -80f;

        /// <summary>Linear volume at or below which a channel is treated as silent.</summary>
        private const float SilenceThreshold = 0.0001f;

        [Header("Configuration")]
        [Tooltip("Number of pre-allocated AudioSources for simultaneous SFX playback.")]
        [SerializeField] private int _sfxPoolSize = 16;

        [Tooltip("Default duration in seconds for music crossfades.")]
        [SerializeField] private float _crossfadeDuration = 1.5f;

        [Header("Mixing")]
        [Tooltip("AudioMixer with one group per AudioChannel and an exposed '<Channel>Volume' " +
                 "parameter per group. Leave empty to load the generated mixer from Resources.")]
        [SerializeField] private AudioMixer _mixer;

        /// <summary>Per-channel volume values (0–1) as the game sees them.</summary>
        private readonly Dictionary<AudioChannel, float> _volumes = new();

        /// <summary>Mixer group per channel, resolved once at Awake.</summary>
        private readonly Dictionary<AudioChannel, AudioMixerGroup> _groups = new();

        /// <summary>Pool of reusable AudioSources for one-shot SFX, UI and voice playback.</summary>
        private readonly List<AudioSource> _sfxPool = new();

        /// <summary>Primary music AudioSource (ping-pong pair A).</summary>
        private AudioSource _musicSourceA;

        /// <summary>Secondary music AudioSource (ping-pong pair B) used during crossfade.</summary>
        private AudioSource _musicSourceB;

        /// <summary>Whichever music source is currently playing.</summary>
        private AudioSource _activeMusic;

        /// <summary>Dedicated looping source for background ambience.</summary>
        private AudioSource _ambienceSource;

        /// <summary>Dedicated source for voice lines, so dialogue never loses a pool slot.</summary>
        private AudioSource _voiceSource;

        /// <summary>Reference to the running crossfade coroutine so it can be interrupted.</summary>
        private Coroutine _crossfadeRoutine;

        /// <summary>Reference to the running ambience fade so it can be interrupted.</summary>
        private Coroutine _ambienceRoutine;

        /// <summary>
        /// The mixer every source routes through, or null when running on the direct-volume
        /// fallback. Exposed so settings code can drive snapshots and extra parameters.
        /// </summary>
        public AudioMixer Mixer => _mixer;

        /// <summary>Unity Awake — resolve the mixer, create sources, apply starting volumes.</summary>
        private void Awake()
        {
            // Default all channel volumes to 1 (full)
            foreach (AudioChannel ch in Enum.GetValues(typeof(AudioChannel)))
                _volumes[ch] = 1f;

            ResolveMixer();

            // Two music sources enable crossfading between tracks
            _musicSourceA = CreateSource("Music_A", true, AudioChannel.Music);
            _musicSourceB = CreateSource("Music_B", true, AudioChannel.Music);
            _activeMusic = _musicSourceA;

            _ambienceSource = CreateSource("Ambience", true, AudioChannel.Ambience);
            _voiceSource = CreateSource("Voice", false, AudioChannel.Voice);

            // Pre-allocate SFX sources to avoid runtime Instantiate calls
            for (int i = 0; i < _sfxPoolSize; i++)
                _sfxPool.Add(CreateSource($"SFX_{i}", false, AudioChannel.SFX));

            // Push the starting volumes into the mixer so it never sits at a stale value
            foreach (AudioChannel ch in Enum.GetValues(typeof(AudioChannel)))
                ApplyVolume(ch);
        }

        // ─── Music ──────────────────────────────────────────────────

        /// <summary>
        /// Start playing a music track. If music is already playing, the old track
        /// crossfades out while the new one fades in over <paramref name="fadeDuration"/> seconds.
        /// </summary>
        /// <param name="clip">The music AudioClip to play.</param>
        /// <param name="fadeDuration">Crossfade time in seconds. Pass -1 to use the default.</param>
        public void PlayMusic(AudioClip clip, float fadeDuration = -1f)
        {
            if (clip == null) return;
            if (fadeDuration < 0) fadeDuration = _crossfadeDuration;

            // Alternate between sources A and B
            var incoming = _activeMusic == _musicSourceA ? _musicSourceB : _musicSourceA;
            incoming.clip = clip;
            incoming.volume = 0f;
            incoming.Play();

            // Cancel any in-progress crossfade
            if (_crossfadeRoutine != null)
                StopCoroutine(_crossfadeRoutine);

            _crossfadeRoutine = StartCoroutine(CrossfadeRoutine(_activeMusic, incoming, fadeDuration));
            _activeMusic = incoming;
        }

        /// <summary>
        /// Fade out and stop the currently playing music track.
        /// </summary>
        /// <param name="fadeDuration">Time in seconds to fade to silence.</param>
        public void StopMusic(float fadeDuration = 1f)
        {
            if (_activeMusic.isPlaying)
                StartCoroutine(FadeTo(_activeMusic, 0f, fadeDuration, true));
        }

        // ─── Ambience ───────────────────────────────────────────────

        /// <summary>
        /// Start a looping ambience bed on the <see cref="AudioChannel.Ambience"/> channel,
        /// fading in from silence. Replaces whatever ambience was playing.
        /// </summary>
        /// <param name="clip">The looping ambience clip.</param>
        /// <param name="fadeDuration">Fade-in time in seconds. Pass -1 to use the default.</param>
        public void PlayAmbience(AudioClip clip, float fadeDuration = -1f)
        {
            if (clip == null) return;
            if (fadeDuration < 0) fadeDuration = _crossfadeDuration;

            if (_ambienceRoutine != null)
                StopCoroutine(_ambienceRoutine);

            _ambienceSource.clip = clip;
            _ambienceSource.volume = 0f;
            _ambienceSource.Play();

            float target = SourceVolume(AudioChannel.Ambience, 1f);
            _ambienceRoutine = StartCoroutine(FadeTo(_ambienceSource, target, fadeDuration, false));
        }

        /// <summary>
        /// Fade out and stop the ambience bed.
        /// </summary>
        /// <param name="fadeDuration">Fade-out time in seconds.</param>
        public void StopAmbience(float fadeDuration = 1f)
        {
            if (!_ambienceSource.isPlaying) return;

            if (_ambienceRoutine != null)
                StopCoroutine(_ambienceRoutine);

            _ambienceRoutine = StartCoroutine(FadeTo(_ambienceSource, 0f, fadeDuration, true));
        }

        // ─── Voice ──────────────────────────────────────────────────

        /// <summary>
        /// Play a voice line on the <see cref="AudioChannel.Voice"/> channel. Uses a dedicated
        /// source rather than the SFX pool, so a busy soundscape can never drop dialogue and a
        /// new line always interrupts the previous one.
        /// </summary>
        /// <param name="clip">The voice clip.</param>
        /// <param name="volumeScale">Optional multiplier on top of channel volume (0–1).</param>
        /// <returns>The AudioSource playing the clip, or null if the clip was null.</returns>
        public AudioSource PlayVoice(AudioClip clip, float volumeScale = 1f)
        {
            if (clip == null) return null;

            _voiceSource.Stop();
            _voiceSource.pitch = 1f;
            _voiceSource.clip = clip;
            _voiceSource.volume = SourceVolume(AudioChannel.Voice, volumeScale);
            _voiceSource.Play();
            return _voiceSource;
        }

        /// <summary>Stop the current voice line immediately.</summary>
        public void StopVoice() => _voiceSource.Stop();

        // ─── SFX ────────────────────────────────────────────────────

        /// <summary>
        /// Play a non-positional (2D) sound effect through the SFX pool.
        /// </summary>
        /// <param name="clip">The sound effect clip.</param>
        /// <param name="volumeScale">Optional multiplier on top of channel volume (0–1).</param>
        /// <param name="pitchVariance">Random pitch variation range (e.g. 0.1 = pitch between 0.9–1.1).</param>
        /// <returns>The AudioSource playing the clip, or null if the pool is exhausted.</returns>
        public AudioSource PlaySFX(AudioClip clip, float volumeScale = 1f, float pitchVariance = 0f)
        {
            if (clip == null) return null;
            var source = GetAvailableSource(AudioChannel.SFX);
            if (source == null) return null;

            source.clip = clip;
            source.volume = SourceVolume(AudioChannel.SFX, volumeScale);
            if (pitchVariance > 0f)
                source.pitch = UnityEngine.Random.Range(1f - pitchVariance, 1f + pitchVariance);
            source.Play();
            return source;
        }

        /// <summary>
        /// Play a spatialized (3D) sound effect at a world position.
        /// </summary>
        /// <param name="clip">The sound effect clip.</param>
        /// <param name="position">The world-space position to play the sound at.</param>
        /// <param name="volumeScale">Optional multiplier on top of channel volume (0–1).</param>
        /// <param name="pitchVariance">Random pitch variation range (e.g. 0.1 = pitch between 0.9–1.1).</param>
        /// <returns>The AudioSource playing the clip, or null if the pool is exhausted.</returns>
        public AudioSource PlaySFXAtPosition(AudioClip clip, Vector3 position, float volumeScale = 1f, float pitchVariance = 0f)
        {
            if (clip == null) return null;
            var source = GetAvailableSource(AudioChannel.SFX);
            if (source == null) return null;

            source.transform.position = position;
            source.spatialBlend = 1f;
            source.clip = clip;
            source.volume = SourceVolume(AudioChannel.SFX, volumeScale);
            if (pitchVariance > 0f)
                source.pitch = UnityEngine.Random.Range(1f - pitchVariance, 1f + pitchVariance);
            source.Play();
            return source;
        }

        /// <summary>
        /// Play a UI sound effect (non-positional, uses the UI channel).
        /// </summary>
        /// <param name="clip">The UI sound clip.</param>
        /// <param name="volumeScale">Optional multiplier (0–1).</param>
        public void PlayUI(AudioClip clip, float volumeScale = 1f)
        {
            if (clip == null) return;
            var source = GetAvailableSource(AudioChannel.UI);
            if (source == null) return;

            source.clip = clip;
            source.volume = SourceVolume(AudioChannel.UI, volumeScale);
            source.Play();
        }

        // ─── Volume Control ─────────────────────────────────────────

        /// <summary>
        /// Set the volume for a channel (0–1). The linear value is converted to decibels and
        /// written to the channel's exposed mixer parameter, so the slider tracks perceived
        /// loudness instead of raw amplitude. Publishes <see cref="OnAudioVolumeChangedEvent"/>
        /// so UI sliders and other listeners can react.
        /// </summary>
        /// <param name="channel">The audio channel to adjust.</param>
        /// <param name="volume">The new volume (clamped to 0–1).</param>
        public void SetVolume(AudioChannel channel, float volume)
        {
            volume = Mathf.Clamp01(volume);
            _volumes[channel] = volume;

            ApplyVolume(channel);

            EventBus.Publish(new OnAudioVolumeChangedEvent { Channel = channel, Volume = volume });
        }

        /// <summary>
        /// Get the current volume of a channel.
        /// </summary>
        /// <param name="channel">The channel to query.</param>
        /// <returns>The volume value (0–1).</returns>
        public float GetVolume(AudioChannel channel) =>
            _volumes.TryGetValue(channel, out var v) ? v : 1f;

        /// <summary>
        /// Name of the exposed mixer parameter that carries a channel's volume, in decibels.
        /// </summary>
        /// <param name="channel">The channel to name.</param>
        /// <returns>For example <c>"MusicVolume"</c>.</returns>
        public static string GetVolumeParameter(AudioChannel channel) => channel + "Volume";

        /// <summary>
        /// Convert a linear 0–1 volume into the decibel value a mixer parameter expects.
        /// Perceived loudness is logarithmic, so a linear slider written straight onto
        /// amplitude sounds near-full at its midpoint; this is the curve that fixes it.
        /// </summary>
        /// <param name="linear">Linear volume (0–1).</param>
        /// <returns>Decibels, floored at <see cref="MinimumVolumeDecibels"/> for silence.</returns>
        public static float LinearToDecibels(float linear) =>
            linear <= SilenceThreshold ? MinimumVolumeDecibels : Mathf.Log10(linear) * 20f;

        /// <summary>
        /// Inverse of <see cref="LinearToDecibels"/>, for reading a mixer parameter back into
        /// a 0–1 slider position.
        /// </summary>
        /// <param name="decibels">A decibel value read from the mixer.</param>
        /// <returns>Linear volume (0–1).</returns>
        public static float DecibelsToLinear(float decibels) =>
            decibels <= MinimumVolumeDecibels ? 0f : Mathf.Clamp01(Mathf.Pow(10f, decibels / 20f));

        // ─── Internals ──────────────────────────────────────────────

        /// <summary>
        /// Resolve the mixer and cache one group per channel. Falls back to the generated
        /// mixer in Resources when the inspector reference is empty, so a freshly cloned
        /// project mixes correctly with no wiring; falls back to direct source volumes when
        /// even that is missing.
        /// </summary>
        private void ResolveMixer()
        {
            if (_mixer == null)
                _mixer = Resources.Load<AudioMixer>(MixerResourcePath);

            if (_mixer == null)
            {
                GameLogger.LogWarning("Audio",
                    $"No AudioMixer assigned and none found at Resources/{MixerResourcePath} — " +
                    "falling back to direct AudioSource volumes (no ducking, snapshots or DSP).");
                return;
            }

            foreach (AudioChannel ch in Enum.GetValues(typeof(AudioChannel)))
            {
                string groupName = ch.ToString();
                AudioMixerGroup match = null;

                // FindMatchingGroups matches on sub-paths, so "Master" returns every group.
                foreach (var group in _mixer.FindMatchingGroups(groupName))
                {
                    if (group.name != groupName) continue;
                    match = group;
                    break;
                }

                if (match == null)
                    GameLogger.LogWarning("Audio", $"Mixer '{_mixer.name}' has no group named '{groupName}'");
                else
                    _groups[ch] = match;
            }
        }

        /// <summary>Look up the mixer group a channel routes into.</summary>
        /// <param name="channel">The channel to route.</param>
        /// <returns>The group, or null when no mixer is in use.</returns>
        private AudioMixerGroup GroupFor(AudioChannel channel) =>
            _groups.TryGetValue(channel, out var group) ? group : null;

        /// <summary>
        /// Push a channel's stored volume wherever it belongs: the mixer parameter when a
        /// mixer is in use, otherwise straight onto the long-lived sources.
        /// </summary>
        /// <param name="channel">The channel whose volume changed.</param>
        private void ApplyVolume(AudioChannel channel)
        {
            if (_mixer != null)
            {
                string parameter = GetVolumeParameter(channel);

                if (!_mixer.SetFloat(parameter, LinearToDecibels(GetVolume(channel))))
                    GameLogger.LogWarning("Audio",
                        $"Mixer '{_mixer.name}' exposes no parameter '{parameter}' — " +
                        "re-run SkylotusCI.GenerateAudioMixer");

                return;
            }

            // Fallback path only: without a mixer, Master has to be folded in by hand, and
            // the looping sources have to be corrected in place because they are already playing.
            if (channel is AudioChannel.Master or AudioChannel.Music && _activeMusic != null)
                _activeMusic.volume = SourceVolume(AudioChannel.Music, 1f);

            if (channel is AudioChannel.Master or AudioChannel.Ambience && _ambienceSource != null)
                _ambienceSource.volume = SourceVolume(AudioChannel.Ambience, 1f);
        }

        /// <summary>
        /// Volume to write onto an <see cref="AudioSource"/> for a channel. With a mixer the
        /// channel and master attenuation both live in the mixer, so only the caller's scale
        /// remains; without one, they are multiplied in here as they always were.
        /// </summary>
        /// <param name="channel">The channel the source plays on.</param>
        /// <param name="volumeScale">The caller's per-sound multiplier.</param>
        /// <returns>The value for <see cref="AudioSource.volume"/>.</returns>
        private float SourceVolume(AudioChannel channel, float volumeScale)
        {
            if (_mixer != null) return volumeScale;

            return GetVolume(channel) * GetVolume(AudioChannel.Master) * volumeScale;
        }

        /// <summary>
        /// Find the first idle AudioSource in the SFX pool and reset it to a known state.
        ///
        /// Resetting on acquisition rather than per play method is the point: a source last
        /// used by <see cref="PlaySFX"/> with a pitch variance would otherwise carry that pitch
        /// into the next UI sound, and every new play method would have to remember to clear
        /// every property the others set.
        /// </summary>
        /// <param name="channel">The channel the caller is about to play on.</param>
        /// <returns>A reset AudioSource, or null when the pool is exhausted.</returns>
        private AudioSource GetAvailableSource(AudioChannel channel)
        {
            foreach (var src in _sfxPool)
            {
                if (src.isPlaying) continue;

                src.clip = null;
                src.pitch = 1f;
                src.volume = 1f;
                src.spatialBlend = 0f;
                src.panStereo = 0f;
                src.loop = false;
                src.transform.localPosition = Vector3.zero;
                src.outputAudioMixerGroup = GroupFor(channel);
                return src;
            }

            GameLogger.LogWarning("Audio", "SFX pool exhausted — consider increasing pool size");
            return null;
        }

        /// <summary>Create a child AudioSource GameObject routed to a channel's mixer group.</summary>
        /// <param name="name">Name of the child GameObject.</param>
        /// <param name="loop">Whether the source loops.</param>
        /// <param name="channel">The channel whose mixer group the source routes into.</param>
        /// <returns>The configured AudioSource.</returns>
        private AudioSource CreateSource(string name, bool loop, AudioChannel channel)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = loop;
            src.outputAudioMixerGroup = GroupFor(channel);
            return src;
        }

        /// <summary>Smoothly crossfade between two music sources over a duration.</summary>
        private IEnumerator CrossfadeRoutine(AudioSource outgoing, AudioSource incoming, float duration)
        {
            float t = 0f;
            float target = SourceVolume(AudioChannel.Music, 1f);
            float startVol = outgoing.volume;

            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float lerp = t / duration;
                outgoing.volume = Mathf.Lerp(startVol, 0f, lerp);
                incoming.volume = Mathf.Lerp(0f, target, lerp);
                yield return null;
            }

            incoming.volume = target;
            outgoing.Stop();
            outgoing.clip = null;
        }

        /// <summary>Fade an AudioSource to a target volume, optionally stopping it afterwards.</summary>
        /// <param name="source">The source to fade.</param>
        /// <param name="target">The volume to arrive at.</param>
        /// <param name="duration">Fade time in seconds.</param>
        /// <param name="stopWhenDone">Whether to stop the source once the fade completes.</param>
        private IEnumerator FadeTo(AudioSource source, float target, float duration, bool stopWhenDone)
        {
            float startVol = source.volume;
            float t = 0f;

            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                source.volume = Mathf.Lerp(startVol, target, t / duration);
                yield return null;
            }

            source.volume = target;
            if (stopWhenDone) source.Stop();
        }
    }
}
