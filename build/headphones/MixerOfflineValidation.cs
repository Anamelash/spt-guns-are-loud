using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Experimental.Audio;
using GunsAreLoud.Client.Audio;

[InitializeOnLoad]
public static class MixerOfflineValidation
{
    private const int Rate = 48000;
    private static Task NextFrame()
    {
        var ready = new TaskCompletionSource<bool>();
        int before = Time.frameCount;
        double started = EditorApplication.timeSinceStartup;
        EditorApplication.CallbackFunction tick = null;
        tick = () =>
        {
            if (Time.frameCount == before && EditorApplication.timeSinceStartup - started < 10) return;
            EditorApplication.update -= tick;
            if (Time.frameCount == before) ready.SetException(new Exception("Player loop did not advance"));
            else ready.SetResult(true);
        };
        EditorApplication.update += tick;
        return ready.Task;
    }

    [Serializable]
    private sealed class Report
    {
        public string baselineMixer, candidateMixer, vanillaGroup, passiveGroup, electronicsGroup;
        public float vanillaMaxError, vanillaRmsError, passive125Db, passive1000Db, passive4000Db, passiveMaximumAnchorErrorDb;
        public float electronicsQuietRightRms, electronicsLoudEventRightRms;
        public bool vanillaIdentity, passiveSpectral, electronicsLinked, vanillaRestored;
        public float restoreMaxError, restoreRmsError;
        public bool electronicsCoverage;
        public float[] electronicsCategoryRms;
        public int contrastRoutesChecked;
        public float contrastNeutralMaxError, contrastNeutralRmsError;
        public float contrastScaledMaxError, contrastScaledRmsError;
        public bool contrastEquivalent;
    }
    [Serializable] private sealed class FitSettings { public string profileId; public float baseVolumeDb; public FitBand[] bands; }
    [Serializable] private sealed class FitBand { public float frequencyHz, linearGain, octaveRange, targetAttenuationDb; }
    [Serializable] private sealed class ParameterSettings { public MixerParameter[] parameters; }
    [Serializable] private sealed class MixerParameter { public string name; public float value; }

    static MixerOfflineValidation()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    public static void Run()
    {
        SessionState.SetBool("GAL_MIXER_VALIDATION_PENDING", true);
        EditorApplication.EnterPlaymode();
    }

    private static async void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode ||
            !SessionState.GetBool("GAL_MIXER_VALIDATION_PENDING", false)) return;
        SessionState.SetBool("GAL_MIXER_VALIDATION_PENDING", false);
        try { await NextFrame(); await Validate(); EditorApplication.Exit(0); }
        catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
    }

    private static async Task Validate()
    {
        if (AudioSettings.outputSampleRate != Rate || AudioSettings.speakerMode != AudioSpeakerMode.Stereo)
            throw new Exception("Harness requires existing 48 kHz stereo output");
        Debug.Log("GAL_RENDER rate=" + AudioSettings.outputSampleRate + " channels=" + AudioSettings.speakerMode);
        await NextFrame();
        string baselinePath = Required("GAL_BASELINE_MIXER");
        string candidatePath = Required("GAL_CANDIDATE_MIXER");
        string[] vanillaGroups = Required("GAL_VANILLA_GROUPS").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        string vanillaGroup = vanillaGroups[0].Trim();
        string passiveGroup = Required("GAL_PASSIVE_GROUP");
        string electronicsGroup = Required("GAL_ELECTRONICS_GROUP");
        string output = Environment.GetEnvironmentVariable("GAL_VALIDATION_OUTPUT") ?? "mixer-offline-validation.json";

        AudioMixer baseline = Load(baselinePath), candidate = Load(candidatePath);
        ApplyCommonState(baseline); ApplyCommonState(candidate);
        await NextFrame(); // Native snapshot transitions are committed by the player loop.
        var restoration = new System.Collections.Generic.Dictionary<string, float>();
        foreach (string name in File.ReadAllLines("headphone-mixer-parameters.txt"))
        {
            if (!candidate.GetFloat(name, out float value)) throw new Exception("Cannot snapshot " + name);
            restoration[name] = value;
        }
        if (!candidate.GetFloat("HeadphonesMixerVolume", out float stockHeadphoneVolume)) throw new Exception("Missing stock headphones output");
        restoration["HeadphonesMixerVolume"] = stockHeadphoneVolume;
        Debug.Log("GAL_RESTORE_SNAPSHOT HeadphonesMixerVolume=" + stockHeadphoneVolume);
        var report = new Report {
            baselineMixer = baselinePath, candidateMixer = candidatePath, vanillaGroup = vanillaGroup,
            passiveGroup = passiveGroup, electronicsGroup = electronicsGroup
        };
        for (int i = 0; i < vanillaGroups.Length; i++)
        {
            string path = vanillaGroups[i].Trim();
            float[] reference = await Render(baseline, path, TestSignal());
            float[] vanilla = await Render(candidate, path, TestSignal());
            EnsureAudible(reference, "baseline vanilla " + path); EnsureAudible(vanilla, "candidate vanilla " + path);
            Compare(reference, vanilla, out float max, out float rms);
            Debug.Log("GAL_VANILLA route=" + path + " max=" + max + " rms=" + rms +
                " onsetBefore=" + FindOnset(reference) + " onsetAfter=" + FindOnset(vanilla));
            report.vanillaMaxError = Math.Max(report.vanillaMaxError, max);
            report.vanillaRmsError = Math.Max(report.vanillaRmsError, rms);
        }
        report.vanillaIdentity = report.vanillaMaxError <= 0.00001f && report.vanillaRmsError <= 0.000001f;

        // Compiled-graph validation covers all 52 nodes. Render representative
        // parent chains here to prove the neutral child is transparent and its
        // fader is equivalent to the former pre-mixer per-source multiplier.
        foreach (int routeIndex in new[] { 0, 2, 17, 18, 27, 48, 51 })
        {
            ContrastRouteSpec route = GunshotContrastMixerRouteTable.Routes[routeIndex];
            float[] signal = TestSignal();
            float[] direct = await Render(candidate, route.ParentPath, signal);
            float[] neutral = await Render(candidate, route.InputPath, signal);
            Compare(direct, neutral, out float neutralMax, out float neutralErrorRms);
            report.contrastNeutralMaxError = Math.Max(report.contrastNeutralMaxError, neutralMax);
            report.contrastNeutralRmsError = Math.Max(report.contrastNeutralRmsError, neutralErrorRms);

            const float attenuationDb = 6f;
            float gain = (float)Math.Pow(10, -attenuationDb / 20f);
            float[] scaledSignal = (float[])signal.Clone();
            for (int sample = 0; sample < scaledSignal.Length; sample++) scaledSignal[sample] *= gain;
            float[] scaledReference = await Render(candidate, route.ParentPath, scaledSignal);
            if (!candidate.SetFloat(route.Parameter, -attenuationDb))
                throw new Exception("Missing contrast parameter " + route.Parameter);
            float[] scaledInput = await Render(candidate, route.InputPath, signal);
            if (!candidate.SetFloat(route.Parameter, 0f))
                throw new Exception("Could not restore contrast parameter " + route.Parameter);
            Compare(scaledReference, scaledInput, out float scaledMax, out float scaledRms);
            Debug.Log("GAL_CONTRAST route=" + route.ParentPath +
                " neutralMax=" + neutralMax + " neutralRms=" + neutralErrorRms +
                " scaledMax=" + scaledMax + " scaledRms=" + scaledRms);
            report.contrastScaledMaxError = Math.Max(report.contrastScaledMaxError, scaledMax);
            report.contrastScaledRmsError = Math.Max(report.contrastScaledRmsError, scaledRms);
            report.contrastRoutesChecked++;
        }
        report.contrastEquivalent = report.contrastRoutesChecked == 7 &&
            report.contrastNeutralMaxError <= 0.00001f && report.contrastNeutralRmsError <= 0.000001f &&
            // Unity's compiled dB fader is not bit-identical to multiplying the
            // source floats with System.Math. Bound the residual below -80 dBFS.
            report.contrastScaledMaxError <= 0.0001f && report.contrastScaledRmsError <= 0.00001f;

        FitSettings fit = JsonUtility.FromJson<FitSettings>(File.ReadAllText(Required("GAL_FIT_JSON")));
        if (Environment.GetEnvironmentVariable("GAL_EQ_PROBE") == "1")
        {
        double volumeReference = await RouteRms(candidate, passiveGroup, 1000);
        candidate.SetFloat("GAL_PassiveVolume", -20);
        double volumeMuted = await RouteRms(candidate, passiveGroup, 1000);
        candidate.GetFloat("GAL_PassiveVolume", out float volumeReadback);
        Debug.Log("GAL_VOLUME_TEST db=" + (20*Math.Log10(volumeMuted/volumeReference)) + " readback=" + volumeReadback);
        candidate.SetFloat("GAL_PassiveVolume", 0);
        float[] probeFrequencies = { 250, 500, 707.1068f, 1000, 1414.2136f, 2000, 4000 };
        double[] probeReferences = new double[probeFrequencies.Length];
        for (int probe = 0; probe < probeFrequencies.Length; probe++)
            probeReferences[probe] = await RouteRms(candidate, passiveGroup, probeFrequencies[probe]);
        candidate.SetFloat("GAL_PassiveBand1Frequency", 1000);
        candidate.SetFloat("GAL_PassiveBand1Q", 1);
        candidate.SetFloat("GAL_PassiveBand1Gain", 2);
        double bandBoost = await RouteRms(candidate, passiveGroup, 1000);
        Debug.Log("GAL_EQ_TEST db=" + (20*Math.Log10(bandBoost/volumeReference)));
        for (int probe = 0; probe < probeFrequencies.Length; probe++)
            Debug.Log("GAL_EQ_SHAPE f=" + probeFrequencies[probe] + " db=" +
                (20*Math.Log10((await RouteRms(candidate, passiveGroup, probeFrequencies[probe]))/probeReferences[probe])));
        candidate.SetFloat("GAL_PassiveBand1Q", 2);
        Debug.Log("GAL_EQ_WIDTH2_500 db=" + (20*Math.Log10((await RouteRms(candidate, passiveGroup, 500))/probeReferences[1])));
        candidate.SetFloat("GAL_PassiveBand1Gain", 1);
        }
        var neutralRms = new double[fit.bands.Length];
        for (int i = 0; i < fit.bands.Length; i++)
            neutralRms[i] = await RouteRms(candidate, passiveGroup, fit.bands[i].frequencyHz);
        ApplyFit(candidate, fit);
        report.passiveMaximumAnchorErrorDb = 0;
        for (int i = 0; i < fit.bands.Length; i++)
        {
            float actual = await PassiveDb(candidate, passiveGroup, fit.bands[i].frequencyHz, neutralRms[i]);
            float error = Math.Abs(actual + fit.bands[i].targetAttenuationDb);
            Debug.Log("GAL_ANCHOR f=" + fit.bands[i].frequencyHz + " actual=" + actual + " target=" + fit.bands[i].targetAttenuationDb);
            report.passiveMaximumAnchorErrorDb = Math.Max(report.passiveMaximumAnchorErrorDb, error);
            if (Math.Abs(fit.bands[i].frequencyHz - 125f) < 1) report.passive125Db = actual;
            if (Math.Abs(fit.bands[i].frequencyHz - 1000f) < 1) report.passive1000Db = actual;
            if (Math.Abs(fit.bands[i].frequencyHz - 4000f) < 1) report.passive4000Db = actual;
        }
        report.passiveSpectral = fit.bands.Length > 0 && report.passiveMaximumAnchorErrorDb <= 1.5f;

        ApplyParameterFile(candidate, Environment.GetEnvironmentVariable("GAL_ELECTRONICS_JSON"));
        float[] dynamics = await Render(candidate, "Guns/Gunshots", DynamicsSignal(),
            "Main/Environment/CommonSounds", Tone(1000, 0, 0.015f, 0.75f));
        EnsureAudible(dynamics, "electronics branch");
        int latency = Rate; // One second of known pre-roll, not residual reverb onset.
        report.electronicsQuietRightRms = (float)ChannelRms(dynamics, latency + Rate / 20, latency + Rate / 5, 1);
        report.electronicsLoudEventRightRms = (float)ChannelRms(dynamics, latency + Rate * 7 / 20, latency + Rate / 2, 1);
        report.electronicsLinked = report.electronicsLoudEventRightRms < report.electronicsQuietRightRms * 0.8f;
        report.electronicsCategoryRms = new float[12];
        report.electronicsCoverage = true;
        for (int category = 0; category < 12; category++)
        {
            double rms = await RouteRms(candidate, vanillaGroups[category].Trim(), 1000);
            report.electronicsCategoryRms[category] = (float)rms;
            report.electronicsCoverage &= rms > 0.00005;
        }

        foreach (var parameter in restoration)
            if (!candidate.SetFloat(parameter.Key, parameter.Value)) throw new Exception("Restore failed: " + parameter.Key);
        float[] restoredReference = await Render(baseline, vanillaGroup, TestSignal());
        float[] restoredCandidate = await Render(candidate, vanillaGroup, TestSignal());
        Compare(restoredReference, restoredCandidate, out report.restoreMaxError, out report.restoreRmsError);
        report.vanillaRestored = report.restoreMaxError <= 0.00001f && report.restoreRmsError <= 0.000001f;

        File.WriteAllText(output, JsonUtility.ToJson(report, true));
        if (!report.vanillaIdentity) throw new Exception("Candidate Vanilla route differs from cloned baseline; see " + output);
        if (!report.contrastEquivalent) throw new Exception("Contrast input differs from equivalent direct-source gain; see " + output);
        if (!report.passiveSpectral) throw new Exception("Passive route lacks the required frequency-dependent attenuation; see " + output);
        if (!report.electronicsLinked) throw new Exception("Electronics branch did not show shared stereo gain reduction; see " + output);
        if (!report.vanillaRestored) throw new Exception("Returning to Vanilla changed the stock route; see " + output);
        if (!report.electronicsCoverage) throw new Exception("A world category does not reach the shared electronics bus; see " + output);
        Debug.Log("GAL_MIXER_OFFLINE_VALIDATION_COMPLETE " + output);
    }

    private static void ApplyFit(AudioMixer mixer, FitSettings fit)
    {
        if (fit == null || fit.bands == null || fit.bands.Length == 0 || fit.bands.Length > 9)
            throw new Exception("GAL_FIT_JSON has no valid 1..9 band fit");
        if (!mixer.SetFloat("GAL_PassiveVolume", fit.baseVolumeDb)) throw new Exception("Missing GAL_PassiveVolume");
        for (int i = 0; i < 9; i++)
        {
            FitBand band = i < fit.bands.Length ? fit.bands[i] : null;
            float frequency = band == null ? 1000f : band.frequencyHz;
            float gain = band == null ? 1f : band.linearGain;
            float range = band == null ? 1f : band.octaveRange;
            int number = i + 1;
            if (!mixer.SetFloat("GAL_PassiveBand" + number + "Frequency", frequency) ||
                !mixer.SetFloat("GAL_PassiveBand" + number + "Gain", gain) ||
                !mixer.SetFloat("GAL_PassiveBand" + number + "Q", range))
                throw new Exception("Missing exposed parameters for passive band " + number);
        }
    }

    private static async Task<float> PassiveDb(AudioMixer mixer, string passiveGroup, float frequency, double neutralRms)
    {
        double rendered = await RouteRms(mixer, passiveGroup, frequency);
        return (float)(20 * Math.Log10(Math.Max(1e-9, rendered) / Math.Max(1e-9, neutralRms)));
    }

    private static async Task<double> RouteRms(AudioMixer mixer, string group, float frequency)
    {
        float[] output = await Render(mixer, group, Tone(frequency, 0.001f, 0.001f, 0.6f));
        EnsureAudible(output, "route " + group + " " + frequency.ToString(CultureInfo.InvariantCulture) + " Hz");
        int onset = Rate; // Ignore the preceding route's decaying reverb during pre-roll.
        return ChannelRms(output, onset + Rate / 5, onset + Rate / 2, 0);
    }

    private static void ApplyCommonState(AudioMixer mixer)
    {
        string snapshotName = Environment.GetEnvironmentVariable("GAL_SNAPSHOT");
        if (!string.IsNullOrWhiteSpace(snapshotName))
        {
            AudioMixerSnapshot snapshot = mixer.FindSnapshot(snapshotName);
            if (snapshot == null) throw new Exception("Missing snapshot " + snapshotName);
            snapshot.TransitionTo(0f);
        }
        ApplyParameterFile(mixer, Environment.GetEnvironmentVariable("GAL_COMMON_PARAMETERS_JSON"));
    }

    private static void ApplyParameterFile(AudioMixer mixer, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        ParameterSettings settings = JsonUtility.FromJson<ParameterSettings>(File.ReadAllText(path));
        if (settings == null || settings.parameters == null) throw new Exception("Invalid parameter JSON " + path);
        foreach (MixerParameter parameter in settings.parameters)
            if (!mixer.SetFloat(parameter.name, parameter.value)) throw new Exception("Missing exposed parameter " + parameter.name);
    }

    private static async Task<float[]> Render(AudioMixer mixer, string groupName, float[] sourceData,
        string secondGroup = null, float[] secondData = null)
    {
        AudioMixerGroup group = FindGroup(mixer, groupName);
        var listenerObject = new GameObject("OfflineValidation.Listener");
        listenerObject.AddComponent<AudioListener>();
        var sourceObject = new GameObject("OfflineValidation.Source");
        var source = sourceObject.AddComponent<AudioSource>();
        source.outputAudioMixerGroup = group; source.spatialBlend = 0; source.playOnAwake = false;
        var padded = new float[sourceData.Length + Rate * 2];
        Array.Copy(sourceData, 0, padded, Rate * 2, sourceData.Length);
        sourceData = padded;
        int frames = sourceData.Length / 2;
        AudioClip clip = AudioClip.Create("OfflineValidation", frames, 2, Rate, false);
        clip.SetData(sourceData, 0); source.clip = clip;
        GameObject secondObject = null;
        AudioClip secondClip = null;
        AudioSource secondSource = null;
        if (secondGroup != null)
        {
            secondObject = new GameObject("OfflineValidation.SecondSource");
            secondSource = secondObject.AddComponent<AudioSource>();
            secondSource.outputAudioMixerGroup = FindGroup(mixer, secondGroup);
            secondSource.spatialBlend = 0; secondSource.playOnAwake = false;
            var secondPadded = new float[frames * 2];
            Array.Copy(secondData, 0, secondPadded, Rate * 2, secondData.Length);
            secondClip = AudioClip.Create("OfflineValidation.Second", frames, 2, Rate, false);
            secondClip.SetData(secondPadded, 0); secondSource.clip = secondClip;
        }
        var rendered = new float[(frames + Rate / 4) * 2];
        NativeArray<float> block = new NativeArray<float>(2048, Allocator.Persistent);
        int written = 0;
        try
        {
            if (!AudioRenderer.Start()) throw new Exception("Unity AudioRenderer.Start returned false");
            source.Play();
            if (secondSource != null) secondSource.Play();
            await NextFrame();
            while (written < rendered.Length)
            {
                if (!AudioRenderer.Render(block)) throw new Exception("Unity AudioRenderer.Render returned false");
                int count = Math.Min(block.Length, rendered.Length - written);
                for (int i = 0; i < count; i++) rendered[written + i] = block[i];
                written += count;
                await NextFrame();
            }
            // Both bundles feed the same listener. Drain the previous source's
            // authored reverb before measuring the next bundle; otherwise its
            // tail is incorrectly counted as a candidate/baseline difference.
            source.Stop();
            if (secondSource != null) secondSource.Stop();
            int quietBlocks = 0;
            for (int drain = 0; drain < Rate * 20 / 1024 && quietBlocks < 8; drain++)
            {
                await NextFrame();
                if (!AudioRenderer.Render(block)) throw new Exception("Tail drain render failed");
                float peak = 0;
                for (int sample = 0; sample < block.Length; sample++)
                    peak = Math.Max(peak, Math.Abs(block[sample]));
                quietBlocks = peak < 0.00000001f ? quietBlocks + 1 : 0;
            }
            if (quietBlocks < 8) throw new Exception("Previous mixer state did not drain before next measurement");
        }
        finally
        {
            AudioRenderer.Stop();
            if (block.IsCreated) block.Dispose();
            UnityEngine.Object.DestroyImmediate(sourceObject);
            UnityEngine.Object.DestroyImmediate(listenerObject);
            UnityEngine.Object.DestroyImmediate(clip);
            if (secondObject != null) UnityEngine.Object.DestroyImmediate(secondObject);
            if (secondClip != null) UnityEngine.Object.DestroyImmediate(secondClip);
        }
        return rendered;
    }

    private static float[] TestSignal()
    {
        var data = Tone(997f, 0.08f, 0.06f, 0.75f);
        data[0] += 0.4f; data[1] -= 0.3f;
        return data;
    }

    private static float[] DynamicsSignal()
    {
        int frames = Rate * 3 / 4; var data = new float[frames * 2];
        for (int frame = 0; frame < frames; frame++)
        {
            float wave = (float)Math.Sin(2 * Math.PI * 1000 * frame / Rate);
            bool loud = frame >= Rate / 4 && frame < Rate / 2;
            data[frame * 2] = (loud ? 0.9f : 0f) * wave;
            data[frame * 2 + 1] = 0;
        }
        return data;
    }

    private static float[] Tone(float frequency, float left, float right, float seconds)
    {
        int frames = (int)(Rate * seconds); var data = new float[frames * 2];
        for (int frame = 0; frame < frames; frame++)
        {
            float wave = (float)Math.Sin(2 * Math.PI * frequency * frame / Rate);
            data[frame * 2] = left * wave; data[frame * 2 + 1] = right * wave;
        }
        return data;
    }

    private static readonly System.Collections.Generic.List<AssetBundle> Bundles =
        new System.Collections.Generic.List<AssetBundle>();
    private static AudioMixer Load(string path)
    {
        string[] parts = path.Split('|');
        if (parts.Length == 2)
        {
            AssetBundle bundle = AssetBundle.LoadFromFile(parts[0]);
            if (bundle == null) throw new Exception("Mixer bundle failed to load: " + parts[0]);
            Bundles.Add(bundle);
            return bundle.LoadAsset<AudioMixer>(parts[1]) ??
                throw new Exception("Compiled mixer failed to load: " + parts[1]);
        }
        return AssetDatabase.LoadAssetAtPath<AudioMixer>(path) ??
            throw new Exception("Mixer asset failed to load: " + path);
    }
    private static AudioMixerGroup FindGroup(AudioMixer mixer, string name)
    {
        AudioMixerGroup[] matches = mixer.FindMatchingGroups(name);
        string leaf = name.Substring(name.LastIndexOf('/') + 1);
        AudioMixerGroup selected = null;
        foreach (AudioMixerGroup match in matches)
            if (match.name == leaf)
            {
                if (selected != null) throw new Exception("Ambiguous exact group: " + name);
                selected = match;
            }
        return selected ?? throw new Exception("Missing exact mixer group: " + name);
    }
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ??
        throw new Exception("Required environment variable is missing: " + name);
    private static void EnsureAudible(float[] data, string label)
    {
        float peak = 0; for (int i = 0; i < data.Length; i++) peak = Math.Max(peak, Math.Abs(data[i]));
        if (float.IsNaN(peak) || float.IsInfinity(peak) || peak < 0.000001f)
            throw new Exception(label + " rendered silence/nonfinite; offline validation is invalid");
    }
    private static int FindOnset(float[] data)
    { for (int frame = 0; frame < data.Length / 2; frame++) if (Math.Max(Math.Abs(data[frame * 2]), Math.Abs(data[frame * 2 + 1])) > 0.000001f) return frame; throw new Exception("No rendered onset"); }
    private static double ChannelRms(float[] data, int startFrame, int endFrame, int channel)
    {
        int frames = data.Length / 2; startFrame = Math.Max(0, Math.Min(frames, startFrame)); endFrame = Math.Max(startFrame + 1, Math.Min(frames, endFrame));
        double energy = 0; for (int frame = startFrame; frame < endFrame; frame++) { double v = data[frame * 2 + channel]; energy += v * v; }
        return Math.Sqrt(energy / (endFrame - startFrame));
    }
    private static void Compare(float[] a, float[] b, out float max, out float rms)
    {
        int count = Math.Min(a.Length, b.Length); double energy = 0; max = 0;
        // Compare the known stimulus and its tail. The preceding second is
        // explicitly a settling interval after control changes, not the F12
        // transition-under-sound test. Do not claim click-free switching here.
        int start = Rate * 2;
        for (int i = start; i < count; i++) { float d = a[i] - b[i]; max = Math.Max(max, Math.Abs(d)); energy += d * (double)d; }
        rms = (float)Math.Sqrt(energy / Math.Max(1, count - start));
    }
}
