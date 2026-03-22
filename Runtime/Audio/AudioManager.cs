using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Skylotus
{
    /// <summary>Audio channel categories for independent volume control.</summary>
    public enum AudioChannel { Master, Music, SFX, UI, Ambience, Voice }

    /// <summary>
    /// Centralized audio manager providing channel-based volume control, a pre-allocated
    /// SFX source pool, smooth music crossfading, and spatial audio helpers.
    ///
    /// Register via <see cref="ServiceLocator"/> and access anywhere:
    /// <code>ServiceLocator.Get&lt;AudioManager&gt;().PlaySFX(clip);</code>
    /// </summary>
    public class AudioManager : MonoBehaviour
    {
        [Header("Configuration")]
        [Tooltip("Number of pre-allocated AudioSources for simultaneous SFX playback.")]
        [SerializeField] private int _sfxPoolSize = 16;

        [Tooltip("Default duration in seconds for music crossfades.")]
        [SerializeField] private float _crossfadeDuration = 1.5f;

        /// <summary>Per-channel volume values (0–1). Master multiplies into all channels.</summary>
        private readonly Dictionary<AudioChannel, float> _volumes = new();

        /// <summary>Pool of reusable AudioSources for one-shot SFX.</summary>
        private readonly List<AudioSource> _sfxPool = new();

        /// <summary>Primary music AudioSource (ping-pong pair A).</summary>
        private AudioSource _musicSourceA;

        /// <summary>Secondary music AudioSource (ping-pong pair B) used during crossfade.</summary>
        private AudioSource _musicSourceB;

        /// <summary>Whichever music source is currently playing.</summary>
        private AudioSource _activeMusic;

        /// <summary>Reference to the running crossfade coroutine so it can be interrupted.</summary>
        private Coroutine _crossfadeRoutine;

        /// <summary>Unity Awake — create music source pair and SFX pool, initialize volumes.</summary>
        private void Awake()
        {
            // Default all channel volumes to 1 (full)
            foreach (AudioChannel ch in Enum.GetValues(typeof(AudioChannel)))
                _volumes[ch] = 1f;

            // Two music sources enable crossfading between tracks
            _musicSourceA = CreateSource("Music_A", true);
            _musicSourceB = CreateSource("Music_B", true);
            _activeMusic = _musicSourceA;

            // Pre-allocate SFX sources to avoid runtime Instantiate calls
            for (int i = 0; i < _sfxPoolSize; i++)
                _sfxPool.Add(CreateSource($"SFX_{i}", false));
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
                StartCoroutine(FadeOut(_activeMusic, fadeDuration));
        }

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
            var source = GetAvailableSource();
            if (source == null) return null;

            source.spatialBlend = 0f;
            source.clip = clip;
            source.volume = GetVolume(AudioChannel.SFX) * GetVolume(AudioChannel.Master) * volumeScale;
            source.pitch = pitchVariance > 0f ? UnityEngine.Random.Range(1f - pitchVariance, 1f + pitchVariance) : 1f;
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
            var source = GetAvailableSource();
            if (source == null) return null;

            source.transform.position = position;
            source.spatialBlend = 1f;
            source.clip = clip;
            source.volume = GetVolume(AudioChannel.SFX) * GetVolume(AudioChannel.Master) * volumeScale;
            source.pitch = pitchVariance > 0f ? UnityEngine.Random.Range(1f - pitchVariance, 1f + pitchVariance) : 1f;
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
            var source = GetAvailableSource();
            if (source == null) return;

            source.spatialBlend = 0f;
            source.clip = clip;
            source.volume = GetVolume(AudioChannel.UI) * GetVolume(AudioChannel.Master) * volumeScale;
            source.Play();
        }

        // ─── Volume Control ─────────────────────────────────────────

        /// <summary>
        /// Set the volume for a channel (0–1). Publishes <see cref="OnAudioVolumeChangedEvent"/>
        /// so UI sliders and other listeners can react.
        /// </summary>
        /// <param name="channel">The audio channel to adjust.</param>
        /// <param name="volume">The new volume (clamped to 0–1).</param>
        public void SetVolume(AudioChannel channel, float volume)
        {
            volume = Mathf.Clamp01(volume);
            _volumes[channel] = volume;

            // Immediately apply to the active music source so changes are audible
            if (channel == AudioChannel.Music || channel == AudioChannel.Master)
            {
                float musicVol = GetVolume(AudioChannel.Music) * GetVolume(AudioChannel.Master);
                if (_activeMusic != null) _activeMusic.volume = musicVol;
            }

            EventBus.Publish(new OnAudioVolumeChangedEvent { Channel = channel, Volume = volume });
        }

        /// <summary>
        /// Get the current volume of a channel.
        /// </summary>
        /// <param name="channel">The channel to query.</param>
        /// <returns>The volume value (0–1).</returns>
        public float GetVolume(AudioChannel channel) =>
            _volumes.TryGetValue(channel, out var v) ? v : 1f;

        // ─── Internals ──────────────────────────────────────────────

        /// <summary>Find the first idle AudioSource in the SFX pool.</summary>
        private AudioSource GetAvailableSource()
        {
            foreach (var src in _sfxPool)
                if (!src.isPlaying) return src;

            GameLogger.LogWarning("Audio", "SFX pool exhausted — consider increasing pool size");
            return null;
        }

        /// <summary>Create a child AudioSource GameObject.</summary>
        private AudioSource CreateSource(string name, bool loop)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = loop;
            return src;
        }

        /// <summary>Smoothly crossfade between two music sources over a duration.</summary>
        private IEnumerator CrossfadeRoutine(AudioSource outgoing, AudioSource incoming, float duration)
        {
            float t = 0f;
            float masterVol = GetVolume(AudioChannel.Master);
            float musicVol = GetVolume(AudioChannel.Music);
            float startVol = outgoing.volume;

            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float lerp = t / duration;
                float vol = masterVol * musicVol;
                outgoing.volume = Mathf.Lerp(startVol, 0f, lerp);
                incoming.volume = Mathf.Lerp(0f, vol, lerp);
                yield return null;
            }

            outgoing.Stop();
            outgoing.clip = null;
        }

        /// <summary>Fade an AudioSource to silence and stop it.</summary>
        private IEnumerator FadeOut(AudioSource source, float duration)
        {
            float startVol = source.volume;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                source.volume = Mathf.Lerp(startVol, 0f, t / duration);
                yield return null;
            }
            source.Stop();
        }
    }
}