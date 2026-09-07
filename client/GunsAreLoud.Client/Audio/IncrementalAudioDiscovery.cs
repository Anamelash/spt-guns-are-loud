using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GunsAreLoud.Client.Audio
{
    internal sealed class IncrementalAudioDiscovery
    {
        // Limits include hierarchy traversal, not just the number of audio sources found.
        internal const int MaxStepsPerFrame = 256;
        internal const double BudgetMilliseconds = 0.2;
        private static readonly Func<long> Timestamp = Stopwatch.GetTimestamp;
        private readonly List<GameObject> _roots = new List<GameObject>();
        private readonly List<AudioSource> _sources = new List<AudioSource>();
        private readonly BudgetedHierarchyWalker<GameObject> _walker;
        private readonly Action<AudioSource> _track;
        private readonly Scene _persistentScene;
        private int _scene;
        private Scene _currentScene;
        private bool _hasScene;

        internal IncrementalAudioDiscovery(Action<AudioSource> track, Scene persistentScene)
        {
            _track = track;
            _persistentScene = persistentScene;
            _walker = new BudgetedHierarchyWalker<GameObject>(node => node != null,
                node => node.transform.childCount,
                (node, index) => node.transform.GetChild(index).gameObject, Visit);
        }

        internal void Tick()
        {
            long deadline = Stopwatch.GetTimestamp() +
                (long)(Stopwatch.Frequency * BudgetMilliseconds / 1000);
            if (_hasScene && (!_currentScene.IsValid() || !_currentScene.isLoaded)) Reset();
            if (_walker.Complete)
            {
                int loadedCount = SceneManager.sceneCount;
                // BepInEx's DontDestroyOnLoad scene is not in SceneManager.sceneCount.
                // Its unmanaged persistent ambient sources still need discovery.
                int count = loadedCount + (_persistentScene.IsValid() && _persistentScene.isLoaded ? 1 : 0);
                if (count == 0) return;
                _scene %= count;
                _currentScene = _scene < loadedCount ? SceneManager.GetSceneAt(_scene) : _persistentScene;
                _scene++;
                _hasScene = true;
                if (!_currentScene.IsValid() || !_currentScene.isLoaded) return;
                // One native root-list call per scene pass, never a global object scan.
                // Unity requires spare list capacity to avoid its internal temporary array.
                int capacity = _currentScene.rootCount + 1;
                if (_roots.Capacity < capacity) _roots.Capacity = capacity;
                _currentScene.GetRootGameObjects(_roots);
                _walker.Reset(_roots);
            }
            _walker.Run(MaxStepsPerFrame, deadline, Timestamp);
        }

        private void Visit(GameObject node)
        {
            node.GetComponents(_sources);
            foreach (AudioSource source in _sources) _track(source);
        }

        internal void Reset()
        {
            _walker.Reset(null);
            _roots.Clear(); _sources.Clear();
            _hasScene = false;
            _scene = 0;
        }
    }
}
