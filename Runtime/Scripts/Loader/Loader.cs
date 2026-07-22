using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using Logger = HelloDev.Logging.Logger;
using Object = UnityEngine.Object;

namespace HelloDev.Loader
{
    /// <summary>
    /// Static Addressables facade
    /// All async methods return a LoaderOperation that supports fluent chaining:
    ///   await Loader.LoadAssetAsync&lt;T&gt;(ref)
    ///       .Progress(p => slider.value = p)
    ///       .OnComplete(asset => Use(asset));
    /// </summary>
    public static class Loader
    {
        #region Fields

        // Non-instantiated assets, keyed by AssetReference.RuntimeKey
        private static readonly Dictionary<string, AsyncOperationHandle> _loadedHandles = new();

        // Label-loaded assets, keyed by label string. Each entry holds all handles for that label.
        private static readonly Dictionary<string, List<AsyncOperationHandle>> _labelHandles = new();

        // Scene handles, keyed by AssetReference.RuntimeKey
        private static readonly Dictionary<string, AsyncOperationHandle<SceneInstance>> _sceneHandles = new();

        // Maps Unity scene name → AssetReference.RuntimeKey, populated after each successful scene load.
        // Allows callers to unload a scene by its Unity name without holding an AssetReference themselves.
        private static readonly Dictionary<string, string> _sceneNameToKey = new();

        #endregion

        #region Logging

        private static void LogVerbose(string msg) => Logger.LogVerbose("Loader", msg);
        private static void Log(string msg) => Logger.Log("Loader", msg);
        private static void LogWarning(string msg) => Logger.LogWarning("Loader", msg);
        private static void LogError(string msg) => Logger.LogError("Loader", msg);

        #endregion

        #region Initialize

        /// <summary>
        /// Explicitly initializes the Addressables system. Safe to call multiple times.
        /// Call on startup (e.g. splash screen) to avoid a hitch on the first asset load.
        /// Fire-and-forget overload — use when you don't need to await completion.
        /// </summary>
        public static void Initialize(Action onComplete = null)
        {
            var op = InitializeAsync();
            if (onComplete != null) op.OnComplete(onComplete);
            op.Forget();
        }

        /// <summary>
        /// Explicitly initializes the Addressables system. Safe to call multiple times.
        /// Call on startup (e.g. splash screen) to avoid a hitch on the first asset load.
        /// </summary>
        public static LoaderOperation InitializeAsync() => new((_, _) => InitializeInternalAsync());

        #endregion

        #region Load
        
        /// <summary>
        /// Loads and caches an asset. Cancellation via token aborts the await and throws OperationCanceledException.
        /// </summary>
        public static UniTask<T> LoadAssetAsync<T>(AssetReference assetReference, CancellationToken token) where T : Object
            => LoadAssetInternalAsync<T>(assetReference, null, token);

        /// <summary>
        /// Loads and caches an asset by address. Cancellation via token aborts the await.
        /// </summary>
        public static UniTask<T> LoadAssetAsync<T>(string address, CancellationToken token) where T : Object
            => LoadAssetInternalAsync<T>(address, null, token);

// In the #region Instantiate block

        /// <summary>
        /// Instantiates a prefab. Cancellation via token aborts the await.
        /// </summary>
        public static UniTask<GameObject> InstantiateAsync(
            AssetReferenceGameObject prefabReference,
            CancellationToken token,
            Transform parent = null,
            Vector3 position = default,
            Quaternion rotation = default,
            bool releaseOnDestroy = true)
            => InstantiateInternalAsync(prefabReference, parent, position, rotation, releaseOnDestroy, null, token);

        /// <summary>
        /// Loads and caches an asset. Concurrent calls on the same reference share one operation.
        /// </summary>
        public static LoaderOperation<T> LoadAssetAsync<T>(AssetReference assetReference) where T : Object
            => new((progress, token) => LoadAssetInternalAsync<T>(assetReference, progress, token));

        /// <summary>
        /// Loads and caches an asset by its Addressable address string.
        /// Concurrent calls on the same address share one operation.
        /// </summary>
        public static LoaderOperation<T> LoadAssetAsync<T>(string address) where T : Object
            => new((progress, token) => LoadAssetInternalAsync<T>(address, progress, token));

        /// <summary>
        /// Loads all assets with the given label. Returns a dictionary keyed by each asset's address (PrimaryKey).
        /// Stores handles internally — release with ReleaseLabel(label) when done.
        /// </summary>
        public static LoaderOperation<Dictionary<string, T>> LoadByLabelAsync<T>(string label) where T : Object
            => new((progress, token) => LoadByLabelInternalAsync<T>(label, progress));

        /// <summary>
        /// Preloads a list of assets into the cache in parallel. Fire-and-forget overload.
        /// </summary>
        public static void Preload(IEnumerable<AssetReference> references, Action<float> onProgress = null, Action onComplete = null)
        {
            var op = PreloadAsync(references);
            if (onProgress != null) op.Progress(onProgress);
            if (onComplete != null) op.OnComplete(onComplete);
            op.Forget();
        }

        /// <summary>
        /// Preloads a list of assets into the cache in parallel. Use before entering a scene or battle.
        /// </summary>
        public static LoaderOperation PreloadAsync(IEnumerable<AssetReference> references)
            => new((progress, token) => PreloadInternalAsync(references, progress));

        /// <summary>
        /// Returns true if the asset is already fully loaded in the cache. Does not trigger a load.
        /// </summary>
        public static bool IsLoaded(AssetReference assetReference)
        {
            if (assetReference == null || !assetReference.RuntimeKeyIsValid()) return false;
            string key = assetReference.RuntimeKey.ToString();
            return _loadedHandles.TryGetValue(key, out var handle)
                   && handle.IsDone
                   && handle.Status == AsyncOperationStatus.Succeeded;
        }

        /// <summary>
        /// Returns a cached asset without triggering a load. Returns null if not yet loaded.
        /// </summary>
        public static T TryGetCached<T>(AssetReference assetReference) where T : Object
        {
            if (!IsLoaded(assetReference)) return null;
            return (T)_loadedHandles[assetReference.RuntimeKey.ToString()].Result;
        }

        #endregion

        #region Instantiate

        /// <summary>
        /// Instantiates a prefab. Each call creates one independently tracked instance.
        /// If releaseOnDestroy is true (default), the instance self-releases when destroyed.
        /// Fire-and-forget overload — use from void callers. Optional onComplete callback receives the spawned GameObject.
        /// </summary>
        public static void Instantiate(
            AssetReferenceGameObject prefabReference,
            Transform parent = null,
            Vector3 position = default,
            Quaternion rotation = default,
            bool releaseOnDestroy = true,
            Action<GameObject> onComplete = null)
        {
            var op = InstantiateAsync(prefabReference, parent, position, rotation, releaseOnDestroy);
            if (onComplete != null) op.OnComplete(onComplete);
            op.Forget();
        }

        /// <summary>
        /// Instantiates a prefab. Each call creates one independently tracked instance.
        /// If releaseOnDestroy is true (default), the instance self-releases when destroyed.
        /// </summary>
        public static LoaderOperation<GameObject> InstantiateAsync(
            AssetReferenceGameObject prefabReference,
            Transform parent = null,
            Vector3 position = default,
            Quaternion rotation = default,
            bool releaseOnDestroy = true)
            => new((progress, token) => InstantiateInternalAsync(prefabReference, parent, position, rotation, releaseOnDestroy, progress, token));


        /// <summary>
        /// Instantiates multiple prefabs in parallel. Partial failures are collected in an AggregateException.
        /// Progress reports completed instances out of total (0–1).
        /// </summary>
        public static LoaderOperation<List<GameObject>> InstantiateAllAsync(
            IEnumerable<AssetReferenceGameObject> prefabReferences,
            Transform parent = null,
            bool releaseOnDestroy = true)
            => new((progress, token) => InstantiateAllInternalAsync(prefabReferences, parent, releaseOnDestroy, progress));

        #endregion

        #region Scene

        /// <summary>
        /// Loads an Addressable scene. Fire-and-forget overload — use from void callers (e.g. UI buttons).
        /// </summary>
        public static void LoadScene(
            AssetReference sceneReference,
            LoadSceneMode loadMode = LoadSceneMode.Additive,
            bool activateOnLoad = true,
            Action onStart = null,
            Action<float> onProgress = null,
            Action<SceneInstance> onComplete = null)
        {
            if (onStart != null) onStart();
            var op = LoadSceneAsync(sceneReference, loadMode, activateOnLoad);
            if (onProgress != null) op.Progress(onProgress);
            if (onComplete != null) op.OnComplete(onComplete);
            op.Forget();
        }

        /// <summary>
        /// Loads an Addressable scene. Fire-and-forget overload — use from void callers (e.g. UI buttons).
        /// </summary>
        public static void LoadScene(
            SceneReference sceneReference,
            LoadSceneMode loadMode = LoadSceneMode.Additive,
            bool activateOnLoad = true,
            ILoadingScreen iLoadingScreen = null)
        {
            if (iLoadingScreen != null) iLoadingScreen.OnStart(sceneReference.SceneName);
            LoaderOperation<SceneInstance> op = LoadSceneAsync(sceneReference.Reference, loadMode, activateOnLoad);
            if (iLoadingScreen != null) op.Progress(iLoadingScreen.OnProgress);
            if (iLoadingScreen != null) op.OnComplete((scene) => iLoadingScreen.OnComplete(scene.Scene.name));
            op.Forget();
        }

        /// <summary>
        /// Loads an Addressable scene and caches its handle for unloading.
        /// Use LoadSceneMode.Additive to layer scenes. LoadSceneMode.Single triggers UnloadUnusedAssets automatically.
        /// WARNING: activateOnLoad = false blocks the entire Addressables queue until ActivateAsync() is called.
        /// </summary>
        public static LoaderOperation<SceneInstance> LoadSceneAsync(
            AssetReference sceneReference,
            LoadSceneMode loadMode = LoadSceneMode.Additive,
            bool activateOnLoad = true)
            => new((progress, token) => LoadSceneInternalAsync(sceneReference, loadMode, activateOnLoad, progress));


        /// <summary>
        /// Unloads a scene loaded with LoadScene/LoadSceneAsync. Fire-and-forget overload.
        /// </summary>
        public static void UnloadScene(AssetReference sceneReference, Action onComplete = null)
        {
            var op = UnloadSceneAsync(sceneReference);
            if (onComplete != null) op.OnComplete(onComplete);
            op.Forget();
        }

        /// <summary>
        /// Unloads a scene loaded with LoadSceneAsync and releases its handle.
        /// Scene unload is always asynchronous — WaitForCompletion has no effect on it.
        /// </summary>
        public static LoaderOperation UnloadSceneAsync(AssetReference sceneReference)
            => new((_, _) => UnloadSceneInternalAsync(sceneReference));

        /// <summary>
        /// Unloads an addressable scene by its Unity scene name. Safe to call on non-addressable
        /// scenes — silently skips if the name isn't tracked. Fire-and-forget overload.
        /// </summary>
        public static void UnloadSceneByName(string sceneName, Action onComplete = null)
        {
            var op = UnloadSceneByNameAsync(sceneName);
            if (onComplete != null) op.OnComplete(onComplete);
            op.Forget();
        }

        /// <summary>
        /// Unloads an addressable scene by its Unity scene name and releases its handle.
        /// Safe to call on non-addressable scenes — silently skips if the name isn't tracked.
        /// </summary>
        public static LoaderOperation UnloadSceneByNameAsync(string sceneName)
            => new((_, _) => UnloadSceneByNameInternalAsync(sceneName));

        /// <summary>
        /// Unloads all loaded scenes. Fire-and-forget overload.
        /// </summary>
        public static void UnloadAllScenes(Action onComplete = null)
        {
            var op = UnloadAllScenesAsync();
            if (onComplete != null) op.OnComplete(onComplete);
            op.Forget();
        }

        /// <summary>
        /// Unloads all loaded scenes. Does not affect loaded assets or instances.
        /// </summary>
        public static LoaderOperation UnloadAllScenesAsync()
            => new((_, _) => UnloadAllScenesInternalAsync());

        #endregion

        #region Release

        /// <summary>
        /// Releases a cached loaded asset. Call once when the asset is no longer needed, not per-instance.
        /// </summary>
        public static void Release(AssetReference assetReference)
        {
            if (assetReference == null || !assetReference.IsValid() || !assetReference.RuntimeKeyIsValid())
            {
                //LogVerbose("Release skipped: null or invalid reference.");
                return;
            }

            string key = assetReference.RuntimeKey.ToString();

            if (!_loadedHandles.TryGetValue(key, out var handle))
            {
                LogWarning($"Release skipped: no cached handle for key: {key}");
                return;
            }

            handle.Release();
            _loadedHandles.Remove(key);
            LogVerbose($"Released: {key}");
        }

        /// <summary>
        /// Releases all assets loaded with LoadByLabelAsync for the given label.
        /// </summary>
        public static void ReleaseLabel(string label)
        {
            if (!_labelHandles.TryGetValue(label, out var handles))
            {
                LogWarning($"ReleaseLabel skipped: label '{label}' not loaded.");
                return;
            }

            foreach (var handle in handles)
                handle.Release();

            _labelHandles.Remove(label);
            LogVerbose($"Label released: '{label}'.");
        }

        /// <summary>
        /// Destroys an instance created with InstantiateAsync and releases its handle.
        /// </summary>
        public static void ReleaseInstance(GameObject instance)
        {
            if (instance == null)
            {
                LogWarning("ReleaseInstance skipped: instance is null.");
                return;
            }

            Addressables.ReleaseInstance(instance);
        }

        /// <summary>
        /// Releases a list of instances. Null entries are safely skipped.
        /// </summary>
        public static void ReleaseInstances(IEnumerable<GameObject> instances)
        {
            foreach (var instance in instances)
                ReleaseInstance(instance);
            Resources.UnloadUnusedAssets();
        }

        /// <summary>
        /// Releases all cached loaded assets and all label groups.
        /// Does not affect instances or scenes — release those with ReleaseInstance and UnloadSceneAsync.
        /// </summary>
        public static void ReleaseAll()
        {
            foreach (var pair in _loadedHandles)
                pair.Value.Release();
            _loadedHandles.Clear();

            foreach (var pair in _labelHandles)
            foreach (var handle in pair.Value)
                handle.Release();
            _labelHandles.Clear();

            Resources.UnloadUnusedAssets();
            LogVerbose("All cached assets released.");
        }

        #endregion

        #region Private — Internal Async Implementations

        private static async UniTask InitializeInternalAsync()
        {
            await Addressables.InitializeAsync().ToUniTask();
            Log("Addressables initialized.");
        }

        private static async UniTask<T> LoadAssetInternalAsync<T>(
            AssetReference assetReference,
            IProgress<float> progress,
            CancellationToken token = default) where T : Object
        {
            ValidateReference(assetReference);
            string key = assetReference.RuntimeKey.ToString();

            if (_loadedHandles.TryGetValue(key, out var cachedHandle))
                return await AwaitCachedHandleAsync<T>(cachedHandle, key, progress, token);

            return await ExecuteLoadAsync<T>(assetReference, key, progress, token);
        }

        private static async UniTask<T> LoadAssetInternalAsync<T>(
            string address,
            IProgress<float> progress,
            CancellationToken token = default) where T : Object
        {
            if (string.IsNullOrEmpty(address))
                throw new ArgumentException("[Loader] Address is null or empty.", nameof(address));

            if (_loadedHandles.TryGetValue(address, out var cachedHandle))
                return await AwaitCachedHandleAsync<T>(cachedHandle, address, progress, token);

            var handle = Addressables.LoadAssetAsync<T>(address);
            _loadedHandles[address] = handle;

            await handle.ToUniTask(progress: progress, cancellationToken: token);

            if (handle.Status != AsyncOperationStatus.Succeeded)
            {
                LogError($"Load failed: {address}");
                _loadedHandles.Remove(address);
                handle.Release();
                throw new Exception($"[Loader] Load failed. Key: {address}");
            }

            LogVerbose($"Loaded: {address}");
            return handle.Result;
        }

        private static async UniTask<Dictionary<string, T>> LoadByLabelInternalAsync<T>(
            string label,
            IProgress<float> progress) where T : Object
        {
            if (string.IsNullOrEmpty(label))
                throw new ArgumentException("[Loader] Label is null or empty.", nameof(label));

            var (locations, locHandle) = await FetchLocationsAsync<T>(label);

            if (locations == null || locations.Count == 0)
            {
                locHandle.Release();
                LogWarning($"LoadByLabelAsync: no locations found for label '{label}'.");
                return new Dictionary<string, T>();
            }

            var loadPairs = StartLabelLoads<T>(locations, locHandle, label);
            var result = await CollectLabelResultsAsync<T>(loadPairs, progress);

            LogVerbose($"LoadByLabelAsync '{label}': {result.Count}/{locations.Count} loaded.");
            return result;
        }

        private static async UniTask PreloadInternalAsync(
            IEnumerable<AssetReference> references,
            IProgress<float> progress)
        {
            var tasks = BuildPreloadTasks(references);
            await AwaitBatchAsync(tasks, progress);
            LogVerbose($"Preload complete. {tasks.Count} assets cached.");
        }

        private static async UniTask<GameObject> InstantiateInternalAsync(
            AssetReferenceGameObject prefabReference,
            Transform parent,
            Vector3 position,
            Quaternion rotation,
            bool releaseOnDestroy,
            IProgress<float> progress,
            CancellationToken token = default)
        {
            ValidateReference(prefabReference);

            var handle = prefabReference.InstantiateAsync(parent, true);
            await handle.ToUniTask(progress: progress, cancellationToken: token);

            if (handle.Status != AsyncOperationStatus.Succeeded)
            {
                LogError($"Instantiate failed: {prefabReference.RuntimeKey}");
                handle.Release();
                throw new Exception($"[Loader] Instantiate failed. Key: {prefabReference.RuntimeKey}");
            }

            var instance = handle.Result;
            LogVerbose($"Instantiated: {instance.name} [{prefabReference.RuntimeKey}] at position: {position}");
            if (releaseOnDestroy) instance.AddComponent<AddressableAutoRelease>();
            instance.transform.position = position;
            instance.transform.rotation = rotation;
            return instance;
        }

        private static async UniTask<List<GameObject>> InstantiateAllInternalAsync(
            IEnumerable<AssetReferenceGameObject> prefabReferences,
            Transform parent,
            bool releaseOnDestroy,
            IProgress<float> progress)
        {
            var tasks = BuildInstantiateTasks(prefabReferences, parent, releaseOnDestroy);
            return await CollectInstantiateResultsAsync(tasks, progress);
        }

        private static async UniTask<SceneInstance> LoadSceneInternalAsync(
            AssetReference sceneReference,
            LoadSceneMode loadMode,
            bool activateOnLoad,
            IProgress<float> progress)
        {
            ValidateReference(sceneReference);
            string key = sceneReference.RuntimeKey.ToString();

            if (_sceneHandles.ContainsKey(key))
            {
                var existingHandle = _sceneHandles[key];

                try
                {
                    if (existingHandle.IsValid() && existingHandle.IsDone && existingHandle.Result.Scene.isLoaded)
                    {
                        LogWarning($"Scene already loaded: {key}");
                        progress?.Report(1f);
                        return existingHandle.Result;
                    }
                }
                catch (Exception e)
                {
                    LogWarning($"Exception checking existing handle for key {key}: {e.Message}");
                }

                LogWarning($"Stale or invalid handle detected, removing: {key}");
                _sceneHandles.Remove(key);
            }
            else
            {
                LogVerbose($"No existing handle for key: {key}");
            }

            LogVerbose($"Starting LoadSceneAsync: {key}");
            AsyncOperationHandle<SceneInstance> handle = Addressables.LoadSceneAsync(sceneReference, loadMode, activateOnLoad);
            _sceneHandles[key] = handle;

            await handle.ToUniTask(progress: progress);

            if (handle.Status != AsyncOperationStatus.Succeeded)
            {
                LogError($"Scene load failed: {key}");
                _sceneHandles.Remove(key);
                handle.Release();
                throw new Exception($"[Loader] Scene load failed. Key: {key}");
            }

            // Map the Unity scene name → RuntimeKey so callers can unload by name
            // without needing to hold on to the original AssetReference.
            _sceneNameToKey[handle.Result.Scene.name] = key;

            Log($"Scene loaded: {handle.Result.Scene.name}");
            return handle.Result;
        }

        private static async UniTask UnloadSceneInternalAsync(AssetReference sceneReference)
        {
            if (sceneReference == null || !sceneReference.RuntimeKeyIsValid())
            {
                LogWarning("UnloadSceneAsync skipped: null or invalid reference.");
                return;
            }

            string key = sceneReference.RuntimeKey.ToString();
            await UnloadSceneByKeyInternalAsync(key);
        }

        private static async UniTask UnloadSceneByNameInternalAsync(string sceneName)
        {
            if (!_sceneNameToKey.TryGetValue(sceneName, out var key))
            {
                LogVerbose($"UnloadSceneByName skipped: '{sceneName}' is not an addressable scene.");
                return;
            }

            _sceneNameToKey.Remove(sceneName);
            await UnloadSceneByKeyInternalAsync(key);
        }

        // Shared unload path used by both UnloadSceneInternalAsync and UnloadSceneByNameInternalAsync.
        private static async UniTask UnloadSceneByKeyInternalAsync(string key)
        {
            if (!_sceneHandles.TryGetValue(key, out var handle))
            {
                LogWarning($"UnloadScene skipped: no handle for key '{key}'.");
                return;
            }

            _sceneHandles.Remove(key);
            var unloadHandle = Addressables.UnloadSceneAsync(handle);
            await unloadHandle.ToUniTask();

            if (unloadHandle.Status != AsyncOperationStatus.Succeeded)
                LogError($"Scene unload failed: {key}");
            else
                LogVerbose($"Scene unloaded: {key}");
        }

        private static async UniTask UnloadAllScenesInternalAsync()
        {
            var keys = new List<string>(_sceneHandles.Keys);
            var tasks = new List<UniTask>(keys.Count);

            foreach (var key in keys)
            {
                var handle = _sceneHandles[key];
                _sceneHandles.Remove(key);
                tasks.Add(Addressables.UnloadSceneAsync(handle).ToUniTask());
            }

            _sceneNameToKey.Clear();
            await UniTask.WhenAll(tasks);
            LogVerbose("All scenes unloaded.");
        }

        #endregion

        #region Private — Load Helpers

        private static async UniTask<T> AwaitCachedHandleAsync<T>(
            AsyncOperationHandle cachedHandle,
            string key,
            IProgress<float> progress,
            CancellationToken token = default) where T : Object
        {
            if (!cachedHandle.IsDone)
                await cachedHandle.ToUniTask(cancellationToken: token);

            if (cachedHandle.Status != AsyncOperationStatus.Succeeded)
            {
                _loadedHandles.Remove(key);
                throw new Exception($"[Loader] Cached load failed. Key: {key}");
            }

            progress?.Report(1f);
            return (T)cachedHandle.Result;
        }

        private static async UniTask<T> ExecuteLoadAsync<T>(
            AssetReference assetReference,
            string key,
            IProgress<float> progress,
            CancellationToken token = default) where T : Object
        {
            var handle = assetReference.LoadAssetAsync<T>();
            _loadedHandles[key] = handle;

            await handle.ToUniTask(progress: progress, cancellationToken: token);

            if (handle.Status != AsyncOperationStatus.Succeeded)
            {
                LogError($"Load failed: {key}");
                _loadedHandles.Remove(key);
                handle.Release();
                throw new Exception($"[Loader] Load failed. Key: {key}");
            }

            return handle.Result;
        }

        #endregion

        #region Private — Label Helpers

        private static async UniTask<(IList<IResourceLocation> locations, AsyncOperationHandle locHandle)> FetchLocationsAsync<T>(string label)
        {
            var locHandle = Addressables.LoadResourceLocationsAsync(label, typeof(T));
            await locHandle.ToUniTask();
            return (locHandle.Result, locHandle);
        }

        private static List<(IResourceLocation location, AsyncOperationHandle<T> handle)> StartLabelLoads<T>(
            IList<IResourceLocation> locations,
            AsyncOperationHandle locHandle,
            string label) where T : Object
        {
            var loadPairs = new List<(IResourceLocation, AsyncOperationHandle<T>)>(locations.Count);
            foreach (var location in locations)
                loadPairs.Add((location, Addressables.LoadAssetAsync<T>(location)));

            if (_labelHandles.ContainsKey(label))
                ReleaseLabel(label);

            var allHandles = new List<AsyncOperationHandle>(locations.Count + 1);
            foreach (var (_, h) in loadPairs) allHandles.Add(h);
            allHandles.Add(locHandle);
            _labelHandles[label] = allHandles;

            return loadPairs;
        }

        private static async UniTask<Dictionary<string, T>> CollectLabelResultsAsync<T>(
            List<(IResourceLocation location, AsyncOperationHandle<T> handle)> loadPairs,
            IProgress<float> progress) where T : Object
        {
            int total = loadPairs.Count;
            int done = 0;
            var result = new Dictionary<string, T>(total);

            foreach (var (location, handle) in loadPairs)
            {
                await handle.ToUniTask();
                done++;
                progress?.Report((float)done / total);

                if (handle.Status == AsyncOperationStatus.Succeeded)
                    result[location.PrimaryKey] = handle.Result;
                else
                    LogError($"CollectLabelResults: failed to load '{location.PrimaryKey}'.");
            }

            progress?.Report(1f);
            return result;
        }

        #endregion

        #region Private — Instantiate Helpers

        private static List<UniTask<GameObject>> BuildInstantiateTasks(
            IEnumerable<AssetReferenceGameObject> references,
            Transform parent,
            bool releaseOnDestroy)
        {
            var tasks = new List<UniTask<GameObject>>();
            foreach (var reference in references)
            {
                if (reference == null || !reference.RuntimeKeyIsValid())
                {
                    LogWarning("InstantiateAllAsync: skipping null or invalid reference.");
                    continue;
                }

                tasks.Add(InstantiateInternalAsync(reference, parent, default, Quaternion.identity, releaseOnDestroy, null));
            }

            return tasks;
        }

        private static async UniTask<List<GameObject>> CollectInstantiateResultsAsync(
            List<UniTask<GameObject>> tasks,
            IProgress<float> progress)
        {
            int total = tasks.Count;
            int done = 0;
            var results = new List<GameObject>(total);
            var exceptions = new List<Exception>();

            foreach (var task in tasks)
            {
                try
                {
                    results.Add(await task);
                }
                catch (Exception e)
                {
                    LogError($"InstantiateAllAsync: one failed. {e.Message}");
                    exceptions.Add(e);
                }

                done++;
                progress?.Report((float)done / total);
            }

            progress?.Report(1f);

            if (exceptions.Count > 0)
                throw new AggregateException($"[Loader] {exceptions.Count} instantiation(s) failed.", exceptions);

            LogVerbose($"InstantiateAllAsync complete. {results.Count} instances created.");
            return results;
        }

        #endregion

        #region Private — Batch / Progress Helpers

        private static List<UniTask> BuildPreloadTasks(IEnumerable<AssetReference> references)
        {
            var tasks = new List<UniTask>();
            foreach (var reference in references)
            {
                if (reference == null || !reference.RuntimeKeyIsValid())
                {
                    LogWarning("PreloadAsync: skipping null or invalid reference.");
                    continue;
                }

                tasks.Add(LoadAssetInternalAsync<Object>(reference, null));
            }

            return tasks;
        }

        private static async UniTask AwaitBatchAsync(List<UniTask> tasks, IProgress<float> progress)
        {
            int total = tasks.Count;
            int done = 0;

            foreach (var task in tasks)
            {
                await task;
                done++;
                progress?.Report((float)done / total);
            }

            progress?.Report(1f);
        }

        private static void ValidateReference(AssetReference assetReference)
        {
            if (assetReference == null)
                throw new ArgumentNullException(nameof(assetReference), "[Loader] AssetReference is null.");

            if (!assetReference.RuntimeKeyIsValid())
                throw new ArgumentException("[Loader] AssetReference has no asset assigned.", nameof(assetReference));
        }

        #endregion
    }

    /// <summary>
    /// Automatically releases an Addressables instance handle on destroy.
    /// Guards against double-release if ReleaseInstance is also called manually.
    /// </summary>
    internal class AddressableAutoRelease : MonoBehaviour
    {
        private bool _released;

        private void OnDestroy()
        {
            if (_released) return;
            _released = true;
            Loader.ReleaseInstance(gameObject);
            Resources.UnloadUnusedAssets();
        }
    }
}