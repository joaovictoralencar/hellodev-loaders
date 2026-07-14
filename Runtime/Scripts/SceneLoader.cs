using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using PrimeTween;
using Logger = HelloDev.Logging.Logger;
#if UNITY_NETCODE_GAMEOBJECTS
using Unity.Netcode;
#endif
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

namespace HelloDev.Loader
{
    /// <summary>
    /// Manages loading, unloading, and tracking of scenes via Addressables.
    /// Provides progress reporting, optional loading screen, and network scene support.
    /// All async operations return UniTask and accept an optional CancellationToken.
    /// </summary>
    public class SceneLoader : MonoBehaviour
    {
        #region Fields

        [SerializeField] private float _minLoadingTime = 5;
        [SerializeField] private AssetReference _loadingScreenReference; // Reference to the loading screen

        private SceneInstance _loadingScreenInstance;
        private bool _isLoadingScreenActive;
        private bool _isUnloadingLoadingScreen;
        private bool _waitingForMinLoadingTime;

        private readonly List<SceneOperationWrapper> _activeHandles = new();
        private float _globalProgress;
        private float _lastGlobalProgress;
        private Tween _progressTween;

        #endregion

        #region Properties

        /// <summary>
        /// Global loading progress, smoothly interpolated via tweening.
        /// </summary>
        public float GlobalProgress => _globalProgress;

        /// <summary>
        /// True if any scene is currently being loaded.
        /// </summary>
        public bool IsLoading { get; private set; }

        /// <summary>
        /// True if any scene is currently being unloaded.
        /// </summary>
        public bool IsUnloading { get; private set; }

        /// <summary>
        /// True while the optional loading screen scene is active.
        /// </summary>
        public bool IsLoadingScreenActive => _isLoadingScreenActive;

#if UNITY_NETCODE_GAMEOBJECTS
        /// <summary>
        /// The name of the game scene used for network loading (editor-only assignment).
        /// </summary>
        public string GameSceneName => _gameSceneName;

#if ODIN_INSPECTOR
        [SerializeField, ReadOnly] private string _gameSceneName;
#else
        [SerializeField] private string _gameSceneName;
#endif
#endif

        #endregion

        #region Logging

        private static void LogVerbose(string msg) => Logger.LogVerbose("SceneLoader", msg);
        private static void Log(string msg) => Logger.Log("SceneLoader", msg);
        private static void LogWarning(string msg) => Logger.LogWarning("SceneLoader", msg);
        private static void LogError(string msg) => Logger.LogError("SceneLoader", msg);

        #endregion

        #region Public — Load (Async)

        /// <summary>
        /// Loads a single scene via Addressables.
        /// </summary>
        /// <param name="sceneReference">Addressable scene reference.</param>
        /// <param name="loadMode">Additive or Single.</param>
        /// <param name="showLoadingScreen">Whether to display the loading screen.</param>
        /// <param name="unloadAll">If true, unloads all currently loaded scenes before loading.</param>
        /// <param name="useMinLoadingTime">If true, enforces at least <see cref="_minLoadingTime"/> seconds of loading display.</param>
        /// <param name="token">Optional cancellation token.</param>
        public async UniTask LoadSceneAsync(AssetReference sceneReference, LoadSceneMode loadMode,
            bool showLoadingScreen = true, bool unloadAll = false, bool useMinLoadingTime = false,
            CancellationToken token = default)
        {
            if (sceneReference == null)
            {
                LogError("Scene reference is not assigned.");
                return;
            }

            if (unloadAll)
                await UnloadAllScenesAsync(showLoadingScreen, token: token);

            await HandleLoadingScreenAsync(true, token);

            AsyncOperationHandle<SceneInstance> handle = sceneReference.LoadSceneAsync(loadMode);
            var wrapper = new SceneOperationWrapper(handle, sceneReference.RuntimeKey.ToString(), true);
            AddHandle(wrapper);

            handle.Completed += _ => OnSceneOperationComplete(wrapper);
            IsLoading = true;
            UpdateGlobalProgressAsync().Forget();
        }

        /// <summary>
        /// Loads multiple scenes sequentially. In Single mode only one scene is allowed.
        /// </summary>
        /// <param name="scenesReferences">Array of Addressable scene references.</param>
        /// <param name="loadMode">Additive or Single.</param>
        /// <param name="showLoadingScreen">Whether to display the loading screen.</param>
        /// <param name="unloadAll">If true, unloads all currently loaded scenes before loading.</param>
        /// <param name="useMinLoadingTime">If true, enforces at least <see cref="_minLoadingTime"/> seconds of loading display.</param>
        /// <param name="token">Optional cancellation token.</param>
        public async UniTask LoadSceneAsync(AssetReference[] scenesReferences, LoadSceneMode loadMode,
            bool showLoadingScreen = true, bool unloadAll = false, bool useMinLoadingTime = false,
            CancellationToken token = default)
        {
            float timeStart = Time.time;

            foreach (var reference in scenesReferences)
            {
                if (reference == null || string.IsNullOrEmpty(reference.RuntimeKey.ToString()))
                    LogWarning("Scene reference is not assigned.");
            }

            if (scenesReferences.Length > 1 && loadMode == LoadSceneMode.Single)
            {
                LogError("LoadSceneMode.Single is not supported when loading multiple scenes at once.");
                return;
            }

            if (useMinLoadingTime) _waitingForMinLoadingTime = true;
            if (unloadAll)
                await UnloadAllScenesAsync(showLoadingScreen, false, token);

            await HandleLoadingScreenAsync(true, token);

            IsLoading = true;
            foreach (var sceneReference in scenesReferences)
            {
                if (string.IsNullOrEmpty(sceneReference.RuntimeKey.ToString())) continue;
                AsyncOperationHandle<SceneInstance> handle = sceneReference.LoadSceneAsync(loadMode);
                var wrapper = new SceneOperationWrapper(handle, sceneReference.RuntimeKey.ToString(), true);
                AddHandle(wrapper);
                handle.Completed += _ => OnSceneOperationComplete(wrapper);
            }

            UpdateGlobalProgressAsync().Forget();
            float remaining = Mathf.Max(0, _minLoadingTime - (Time.time - timeStart));
            await LoadingWaitingTimeAsync(remaining, token);
        }

        /// <summary>
        /// Fire-and-forget overload for loading a single scene. Calls the async version and forgets it.
        /// </summary>
        public void LoadSceneAsyncForget(AssetReference sceneReference, LoadSceneMode loadMode,
            bool showLoadingScreen = true, bool unloadAll = false, bool useMinLoadingTime = false)
        {
            if (string.IsNullOrEmpty(sceneReference.RuntimeKey.ToString())) return;
            LoadSceneAsync(sceneReference, loadMode, showLoadingScreen, unloadAll, useMinLoadingTime).Forget();
        }

        /// <summary>
        /// Fire-and-forget overload for loading multiple scenes.
        /// </summary>
        public void LoadSceneAsyncForget(AssetReference[] scenesReferences, LoadSceneMode loadMode,
            bool showLoadingScreen = true, bool unloadAll = false, bool useMinLoadingTime = false)
        {
            LoadSceneAsync(scenesReferences, loadMode, showLoadingScreen, unloadAll, useMinLoadingTime).Forget();
        }

        #endregion

        #region Public — Unload (Async)

        /// <summary>
        /// Unloads a scene. Handles both Addressables‑loaded and Unity‑loaded scenes.
        /// </summary>
        /// <param name="scene">Scene to unload.</param>
        /// <param name="showLoadingScreen">If true, shows the loading screen during the operation.</param>
        /// <param name="token">Optional cancellation token.</param>
        public async UniTask UnloadSceneAsync(Scene scene, bool showLoadingScreen = true,
            CancellationToken token = default)
        {
            if (!scene.isLoaded)
            {
                LogError($"Scene '{scene.name}' is not loaded or already unloaded.");
                return;
            }

            if (showLoadingScreen)
                await HandleLoadingScreenAsync(true, token);

            SceneOperationWrapper wrapper;

            var addressableWrapper = _activeHandles.Find(h => h.AsyncOperationHandle.HasValue && h.SceneName == scene.name);
            if (addressableWrapper != null)
            {
                var unloadHandle = Addressables.UnloadSceneAsync(addressableWrapper.AsyncOperationHandle.Value, true);
                wrapper = new SceneOperationWrapper(unloadHandle, scene.name, false);
                AddHandle(wrapper);
            }
            else
            {
                AsyncOperation unloadOp = SceneManager.UnloadSceneAsync(scene);
                wrapper = new SceneOperationWrapper(unloadOp, scene.name, false);
                AddHandle(wrapper);
            }

            await UniTask.WaitUntil(() => wrapper.IsDone, cancellationToken: token);
            OnSceneOperationComplete(wrapper, !showLoadingScreen);

            IsUnloading = false;
            if (showLoadingScreen && _activeHandles.Count == 0)
                await HandleLoadingScreenAsync(false, token);
        }

        /// <summary>
        /// Unloads all loaded scenes, optionally showing the loading screen.
        /// </summary>
        /// <param name="showLoadingScreen">If true, shows the loading screen during the operation.</param>
        /// <param name="hideOnFinish">If true, hides the loading screen after all scenes are unloaded.</param>
        /// <param name="token">Optional cancellation token.</param>
        public async UniTask UnloadAllScenesAsync(bool showLoadingScreen, bool hideOnFinish = true,
            CancellationToken token = default)
        {
            var scenesToUnload = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded)
                    scenesToUnload.Add(scene);
            }

            if (showLoadingScreen)
                await HandleLoadingScreenAsync(true, token);

            foreach (var scene in scenesToUnload)
                await UnloadSceneAsync(scene, false, token);

            if (hideOnFinish)
                await HandleLoadingScreenAsync(false, token);
        }

        #endregion

        #region Public — Network (Netcode for GameObjects)

#if UNITY_NETCODE_GAMEOBJECTS
        /// <summary>
        /// Starts the network scene load on the server side. Fire‑and‑forget.
        /// </summary>
        public void LoadNetworkSceneForget(string sceneName, LoadSceneMode additive)
        {
            LoadNetworkSceneAsync(sceneName, additive).Forget();
        }

        /// <summary>
        /// Async network scene load. Only the server may call this.
        /// </summary>
        public async UniTask LoadNetworkSceneAsync(string sceneName, LoadSceneMode additive,
            CancellationToken token = default)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(1), cancellationToken: token);
            await UniTask.WaitUntil(() => NetworkManager.Singleton.IsListening, cancellationToken: token);

            if (!NetworkManager.Singleton.IsServer) return;

            NetworkManager.Singleton.SceneManager.OnLoadComplete += OnLoadCompleted;
            NetworkManager.Singleton.SceneManager.OnLoad += OnLoad;

            SceneEventProgressStatus status = NetworkManager.Singleton.SceneManager.LoadScene(sceneName, additive);
            if (status != SceneEventProgressStatus.Started)
                LogError($"Failed to load {sceneName} with a {nameof(SceneEventProgressStatus)}: {status}");
        }

        private void OnLoad(ulong clientId, string sceneName, LoadSceneMode loadSceneMode, AsyncOperation asyncOperation)
        {
            HandleLoadingScreenAsync(true).Forget();
            var wrapper = new SceneOperationWrapper(asyncOperation, sceneName, true);
            AddHandle(wrapper);

            asyncOperation.completed += _ => OnSceneOperationComplete(wrapper);

            IsLoading = true;
            UpdateGlobalProgressAsync().Forget();
        }

        private void OnLoadCompleted(ulong clientId, string sceneName, LoadSceneMode loadSceneMode) { }

#if UNITY_EDITOR
        public UnityEditor.SceneAsset SceneAsset;

        private void OnValidate()
        {
            if (SceneAsset != null)
                _gameSceneName = SceneAsset.name;
        }
#endif
#endif

        #endregion

        #region Private — Internal Operations

        private void AddHandle(SceneOperationWrapper wrapper)
        {
            _activeHandles.Add(wrapper);
            IsLoading = _activeHandles.Exists(h => !h.IsDone && h.IsLoading);
            IsUnloading = _activeHandles.Exists(h => !h.IsDone && !h.IsLoading);
        }

        private void OnSceneOperationComplete(SceneOperationWrapper wrapper, bool dontHideLoadingScreen = false)
        {
            Log($"Scene operation completed: {wrapper.SceneName} ({(wrapper.IsLoading ? "Loading" : "Unloading")})");
            int index = _activeHandles.FindIndex(w => w.SceneName == wrapper.SceneName);
            if (index >= 0) _activeHandles.RemoveAt(index);

            IsLoading = _activeHandles.Exists(h => !h.IsDone && h.IsLoading);
            IsUnloading = _activeHandles.Exists(h => !h.IsDone && !h.IsLoading);

            if (!IsLoading && !IsUnloading && !dontHideLoadingScreen)
                HandleLoadingScreenAsync(false).Forget();
        }

        private async UniTask UpdateGlobalProgressAsync()
        {
            while (_activeHandles.Count > 0)
            {
                if (_isUnloadingLoadingScreen)
                {
                    await UniTask.Yield();
                    continue;
                }

                float totalProgress = 0f;
                foreach (var wrapper in _activeHandles)
                    totalProgress += wrapper.Progress;

                float nextGlobalProgress = Mathf.Min(totalProgress / _activeHandles.Count, 0.9f);

                if (nextGlobalProgress >= _lastGlobalProgress)
                {
                    if (_progressTween.isAlive) _progressTween.Stop();
                    float target = Mathf.Min(nextGlobalProgress, 0.9f);
                    _progressTween = Tween.Custom(this, _globalProgress, target, 1f,
                        (loader, x) =>
                        {
                            loader._globalProgress = x;
                            loader._lastGlobalProgress = loader._globalProgress;
                        }, Ease.OutQuad);
                }

                await UniTask.Yield();
            }
        }

        /// <summary>
        /// Shows or hides the loading screen asynchronously.
        /// </summary>
        public async UniTask HandleLoadingScreenAsync(bool state, CancellationToken token = default)
        {
            if (state)
            {
                if (_isLoadingScreenActive)
                {
                    if (_loadingScreenInstance.Scene.IsValid() && _loadingScreenInstance.Scene.isLoaded)
                    {
                        LogWarning("Loading screen is already loaded. Skipping load.");
                        return;
                    }
                }

                _isLoadingScreenActive = true;
                var handle = _loadingScreenReference.LoadSceneAsync(LoadSceneMode.Additive);
                await handle.ToUniTask(cancellationToken: token);

                if (handle.IsValid() && handle.Status == AsyncOperationStatus.Succeeded)
                    _loadingScreenInstance = handle.Result;
                else
                    LogError("Failed to load the loading screen or handle is invalid.");
            }
            else
            {
                if (!_isLoadingScreenActive) return;

                await UniTask.WaitUntil(() => !_waitingForMinLoadingTime, cancellationToken: token);

                if (_loadingScreenInstance.Scene.IsValid() && _loadingScreenInstance.Scene.isLoaded)
                {
                    _isUnloadingLoadingScreen = true;
                    Tween.Custom(this, _globalProgress, 1f, 0.5f,
                        (loader, x) => loader._globalProgress = x, Ease.OutQuad);
                    await UniTask.Delay(TimeSpan.FromSeconds(0.5f), cancellationToken: token);

                    var unloadHandle = Addressables.UnloadSceneAsync(_loadingScreenInstance);
                    await unloadHandle.ToUniTask(cancellationToken: token);

                    _globalProgress = 0f;
                    _lastGlobalProgress = 0f;
                    _isLoadingScreenActive = false;
                    _isUnloadingLoadingScreen = false;
                }
            }
        }

        private async UniTask LoadingWaitingTimeAsync(float remainingTime, CancellationToken token = default)
        {
            if (remainingTime > 0)
                await UniTask.Delay(TimeSpan.FromSeconds(remainingTime), cancellationToken: token);
            _waitingForMinLoadingTime = false;
        }

        #endregion
    }

    /// <summary>
    /// Wraps either a Unity AsyncOperation or an Addressables AsyncOperationHandle for unified progress tracking.
    /// </summary>
    public class SceneOperationWrapper
    {
        /// <summary>
        /// Unity's built-in AsyncOperation (used for non-Addressables unloads).
        /// </summary>
        public AsyncOperation AsyncOperation { get; }

        /// <summary>
        /// Addressables scene handle (used for loads and Addressables unloads).
        /// </summary>
        public AsyncOperationHandle<SceneInstance>? AsyncOperationHandle { get; }

        /// <summary>
        /// Name of the scene being operated on.
        /// </summary>
        public string SceneName { get; }

        /// <summary>
        /// True if this is a load operation; false if unload.
        /// </summary>
        public bool IsLoading { get; }

        /// <summary>
        /// True when the underlying operation has finished.
        /// </summary>
        public bool IsDone => AsyncOperation?.isDone ?? (AsyncOperationHandle?.IsDone ?? false);

        /// <summary>
        /// Progress of the operation (0-1). For Addressables handles this uses PercentComplete.
        /// </summary>
        public float Progress => AsyncOperation?.progress ?? (AsyncOperationHandle?.PercentComplete ?? 0f);

        /// <summary>
        /// Creates a wrapper for a Unity AsyncOperation.
        /// </summary>
        public SceneOperationWrapper(AsyncOperation asyncOperation, string sceneName, bool isLoading)
        {
            AsyncOperation = asyncOperation;
            SceneName = sceneName;
            IsLoading = isLoading;
        }

        /// <summary>
        /// Creates a wrapper for an Addressables scene handle.
        /// </summary>
        public SceneOperationWrapper(AsyncOperationHandle<SceneInstance> asyncOperationHandle, string sceneName, bool isLoading)
        {
            AsyncOperationHandle = asyncOperationHandle;
            SceneName = sceneName;
            IsLoading = isLoading;
        }
    }
}