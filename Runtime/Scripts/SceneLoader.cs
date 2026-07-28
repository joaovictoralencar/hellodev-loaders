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
    public class SceneLoader : MonoBehaviour
    {
        #region Fields

        [SerializeField] private float _minLoadingTime = 5;
        [SerializeField] private AssetReference _loadingScreenReference;

        // Loading screen state machine
        private enum LoadingScreenState { Hidden, Loading, Showing, Hiding }
        private LoadingScreenState _loadingScreenState = LoadingScreenState.Hidden;
        private SceneInstance _loadingScreenInstance;
        private AsyncOperationHandle<SceneInstance> _loadingScreenLoadHandle;
        private bool _waitingForMinLoadingTime;
        private CancellationTokenSource _loadingScreenCts;

        // Scene tracking
        private readonly List<SceneOperationWrapper> _activeHandles = new List<SceneOperationWrapper>();
        private readonly object _handleLock = new object();

        // Progress
        private float _globalProgress;
        private float _lastGlobalProgress;
        private Tween _progressTween;

        #endregion

        #region Properties

        public float GlobalProgress => _globalProgress;

        public bool IsLoading
        {
            get
            {
                lock (_handleLock)
                    return _activeHandles.Exists(h => !h.IsDone && h.IsLoading);
            }
        }

        public bool IsUnloading
        {
            get
            {
                lock (_handleLock)
                    return _activeHandles.Exists(h => !h.IsDone && !h.IsLoading);
            }
        }

        public bool IsLoadingScreenActive => _loadingScreenState == LoadingScreenState.Showing;

#if UNITY_NETCODE_GAMEOBJECTS
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

        public async UniTask LoadSceneAsync(AssetReference sceneReference, LoadSceneMode loadMode,
            bool showLoadingScreen = true, bool unloadAll = false, bool useMinLoadingTime = false,
            CancellationToken token = default)
        {
            if (sceneReference == null)
            {
                LogError("Scene reference is not assigned.");
                return;
            }

            if (token.IsCancellationRequested) return;

            if (unloadAll)
                await UnloadAllScenesAsync(showLoadingScreen, token: token);

            if (showLoadingScreen)
                await ShowLoadingScreenAsync(token);

            float startTime = Time.time;
            if (useMinLoadingTime) _waitingForMinLoadingTime = true;

            // Optional safety: if the scene is already loaded, release the handle first (implicit unload)
            if (sceneReference.OperationHandle.IsValid() && sceneReference.OperationHandle.Status == AsyncOperationStatus.Succeeded)
            {
                LogWarning($"Scene '{sceneReference.RuntimeKey}' is already loaded. Releasing previous handle before re‑load.");
                Addressables.Release(sceneReference.OperationHandle);
            }

            AsyncOperationHandle<SceneInstance> handle = sceneReference.LoadSceneAsync(loadMode);
            var wrapper = new SceneOperationWrapper(handle, sceneReference.RuntimeKey.ToString(), true);
            AddHandle(wrapper);

            // Token cancellation: release if still loading, but dispose registration on completion
            CancellationTokenRegistration? reg = null;
            if (token.CanBeCanceled)
            {
                reg = token.Register(() =>
                {
                    if (handle.IsValid() && !handle.IsDone)
                        Addressables.Release(handle);
                });
            }

            handle.Completed += _ =>
            {
                reg?.Dispose(); // prevent late callback
                OnSceneOperationComplete(wrapper);
            };

            UpdateGlobalProgressAsync().Forget();

            if (useMinLoadingTime)
                await LoadingWaitingTimeAsync(Mathf.Max(0, _minLoadingTime - (Time.time - startTime)), token);
        }

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

            if (token.IsCancellationRequested) return;

            if (useMinLoadingTime) _waitingForMinLoadingTime = true;

            if (unloadAll)
                await UnloadAllScenesAsync(showLoadingScreen, false, token);

            if (showLoadingScreen)
                await ShowLoadingScreenAsync(token);

            foreach (var sceneReference in scenesReferences)
            {
                if (string.IsNullOrEmpty(sceneReference.RuntimeKey.ToString())) continue;

                // Optional safety for already loaded scenes
                if (sceneReference.OperationHandle.IsValid() && sceneReference.OperationHandle.Status == AsyncOperationStatus.Succeeded)
                {
                    LogWarning($"Scene '{sceneReference.RuntimeKey}' already loaded – releasing handle before re‑load.");
                    Addressables.Release(sceneReference.OperationHandle);
                }

                AsyncOperationHandle<SceneInstance> handle = sceneReference.LoadSceneAsync(loadMode);
                var wrapper = new SceneOperationWrapper(handle, sceneReference.RuntimeKey.ToString(), true);
                AddHandle(wrapper);

                CancellationTokenRegistration? reg = null;
                if (token.CanBeCanceled)
                {
                    reg = token.Register(() =>
                    {
                        if (handle.IsValid() && !handle.IsDone)
                            Addressables.Release(handle);
                    });
                }

                handle.Completed += _ =>
                {
                    reg?.Dispose();
                    OnSceneOperationComplete(wrapper);
                };
            }

            UpdateGlobalProgressAsync().Forget();
            float remaining = Mathf.Max(0, _minLoadingTime - (Time.time - timeStart));
            await LoadingWaitingTimeAsync(remaining, token);
        }

        public void LoadSceneAsyncForget(AssetReference sceneReference, LoadSceneMode loadMode,
            bool showLoadingScreen = true, bool unloadAll = false, bool useMinLoadingTime = false)
        {
            if (string.IsNullOrEmpty(sceneReference.RuntimeKey.ToString())) return;
            LoadSceneAsync(sceneReference, loadMode, showLoadingScreen, unloadAll, useMinLoadingTime).Forget();
        }

        public void LoadSceneAsyncForget(AssetReference[] scenesReferences, LoadSceneMode loadMode,
            bool showLoadingScreen = true, bool unloadAll = false, bool useMinLoadingTime = false)
        {
            LoadSceneAsync(scenesReferences, loadMode, showLoadingScreen, unloadAll, useMinLoadingTime).Forget();
        }

        #endregion

        #region Public — Unload (Async)
        
        public async UniTask HandleLoadingScreenAsync(bool state, CancellationToken token = default)
        {
            if (state)
                await ShowLoadingScreenAsync(token);
            else
                await HideLoadingScreenAsync(token);
        }

        public async UniTask UnloadSceneAsync(Scene scene, bool showLoadingScreen = true,
            CancellationToken token = default)
        {
            if (!scene.isLoaded)
            {
                LogError($"Scene '{scene.name}' is not loaded or already unloaded.");
                return;
            }

            if (showLoadingScreen)
                await ShowLoadingScreenAsync(token);

            SceneOperationWrapper wrapper;
            AsyncOperationHandle<SceneInstance>? addressableHandle = null;

            lock (_handleLock)
            {
                var existing = _activeHandles.Find(h => h.AsyncOperationHandle.HasValue && h.SceneName == scene.name && h.IsLoading);
                if (existing != null)
                    addressableHandle = existing.AsyncOperationHandle;
            }

            if (addressableHandle.HasValue)
            {
                var unloadOp = Addressables.UnloadSceneAsync(addressableHandle.Value, true);
                wrapper = new SceneOperationWrapper(unloadOp, scene.name, false);
                AddHandle(wrapper);

                CancellationTokenRegistration? reg = null;
                if (token.CanBeCanceled)
                {
                    reg = token.Register(() =>
                    {
                        if (unloadOp.IsValid() && !unloadOp.IsDone)
                            Addressables.Release(unloadOp);
                    });
                }

                unloadOp.Completed += _ =>
                {
                    reg?.Dispose();
                };
            }
            else
            {
                AsyncOperation unloadOp = SceneManager.UnloadSceneAsync(scene);
                wrapper = new SceneOperationWrapper(unloadOp, scene.name, false);
                AddHandle(wrapper);
            }

            await UniTask.WaitUntil(() => wrapper.IsDone, cancellationToken: token);
            OnSceneOperationComplete(wrapper);

            // Release the original load handle after successful unload
            if (addressableHandle.HasValue && addressableHandle.Value.IsValid())
                Addressables.Release(addressableHandle.Value);

            if (showLoadingScreen && _activeHandles.Count == 0)
                await HideLoadingScreenAsync(token);
        }

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
                await ShowLoadingScreenAsync(token);

            foreach (var scene in scenesToUnload)
                await UnloadSceneAsync(scene, false, token);

            if (hideOnFinish)
                await HideLoadingScreenAsync(token);
        }

        #endregion

        #region Public — Network (Netcode for GameObjects)

#if UNITY_NETCODE_GAMEOBJECTS
        public void LoadNetworkSceneForget(string sceneName, LoadSceneMode additive)
        {
            LoadNetworkSceneAsync(sceneName, additive).Forget();
        }

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
            // Show loading screen using the safe state machine
            CancellationTokenSource cts = new CancellationTokenSource();
            ShowLoadingScreenAsync(cts.Token).Forget();

            var wrapper = new SceneOperationWrapper(asyncOperation, sceneName, true);
            AddHandle(wrapper);

            asyncOperation.completed += _ => OnSceneOperationComplete(wrapper);
            UpdateGlobalProgressAsync().Forget();

            // TODO: Hide the loading screen when all clients have loaded (e.g., in OnLoadCompleted).
            // You may need to keep track of pending client loads and call HideLoadingScreenAsync() when done.
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

        #region Private — Loading Screen State Machine

        private async UniTask ShowLoadingScreenAsync(CancellationToken token)
        {
            CancelLoadingScreenTransition();
            _loadingScreenCts = CancellationTokenSource.CreateLinkedTokenSource(token);

            while (_loadingScreenState == LoadingScreenState.Hiding ||
                   _loadingScreenState == LoadingScreenState.Loading)
            {
                await UniTask.Yield(PlayerLoopTiming.Update, _loadingScreenCts.Token);
            }

            if (_loadingScreenState == LoadingScreenState.Showing)
                return;

            _loadingScreenState = LoadingScreenState.Loading;
            try
            {
                if (_loadingScreenReference.OperationHandle.IsValid())
                    _loadingScreenReference.ReleaseAsset();

                var handle = _loadingScreenReference.LoadSceneAsync(LoadSceneMode.Additive);
                _loadingScreenLoadHandle = handle;
                await handle.ToUniTask(cancellationToken: _loadingScreenCts.Token);

                if (handle.Status == AsyncOperationStatus.Succeeded)
                {
                    _loadingScreenInstance = handle.Result;
                    _loadingScreenState = LoadingScreenState.Showing;
                }
                else
                {
                    LogError("Failed to load the loading screen.");
                    _loadingScreenState = LoadingScreenState.Hidden;
                    if (handle.IsValid()) Addressables.Release(handle);
                }
            }
            catch (OperationCanceledException)
            {
                if (_loadingScreenLoadHandle.IsValid())
                    Addressables.Release(_loadingScreenLoadHandle);
                _loadingScreenState = LoadingScreenState.Hidden;
                throw;
            }
            finally
            {
                _loadingScreenCts?.Dispose();
                _loadingScreenCts = null;
            }
        }

        private async UniTask HideLoadingScreenAsync(CancellationToken token)
        {
            CancelLoadingScreenTransition();
            _loadingScreenCts = CancellationTokenSource.CreateLinkedTokenSource(token);

            while (_loadingScreenState == LoadingScreenState.Loading ||
                   _loadingScreenState == LoadingScreenState.Hiding)
            {
                await UniTask.Yield(PlayerLoopTiming.Update, _loadingScreenCts.Token);
            }

            if (_loadingScreenState == LoadingScreenState.Hidden)
                return;

            _loadingScreenState = LoadingScreenState.Hiding;

            try
            {
                await UniTask.WaitUntil(() => !_waitingForMinLoadingTime, cancellationToken: _loadingScreenCts.Token);

                if (_progressTween.isAlive) _progressTween.Stop();
                await Tween.Custom(this, _globalProgress, 1f, 0.5f,
                    (loader, x) => loader._globalProgress = x, Ease.OutQuad)
                    .ToUniTask(cancellationToken: _loadingScreenCts.Token);

                if (_loadingScreenInstance.Scene.IsValid() && _loadingScreenInstance.Scene.isLoaded)
                {
                    var unloadHandle = Addressables.UnloadSceneAsync(_loadingScreenInstance);
                    await unloadHandle.ToUniTask(cancellationToken: _loadingScreenCts.Token);
                }

                if (_loadingScreenLoadHandle.IsValid())
                    Addressables.Release(_loadingScreenLoadHandle);
                _loadingScreenReference.ReleaseAsset();

                _globalProgress = 0f;
                _lastGlobalProgress = 0f;
                _loadingScreenInstance = default;
                _loadingScreenState = LoadingScreenState.Hidden;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            finally
            {
                _loadingScreenCts?.Dispose();
                _loadingScreenCts = null;
            }
        }

        private void CancelLoadingScreenTransition()
        {
            if (_loadingScreenCts != null)
            {
                _loadingScreenCts.Cancel();
                _loadingScreenCts.Dispose();
                _loadingScreenCts = null;
            }
        }

        #endregion

        #region Private — Internal Operations

        private void AddHandle(SceneOperationWrapper wrapper)
        {
            lock (_handleLock)
                _activeHandles.Add(wrapper);
        }

        private void RemoveHandle(SceneOperationWrapper wrapper)
        {
            lock (_handleLock)
                _activeHandles.Remove(wrapper);
        }

        private void OnSceneOperationComplete(SceneOperationWrapper wrapper)
        {
            Log($"Scene operation completed: {wrapper.SceneName} ({(wrapper.IsLoading ? "Loading" : "Unloading")})");
            RemoveHandle(wrapper);

            if (!IsLoading && !IsUnloading)
                HideLoadingScreenAsync(CancellationToken.None).Forget();
        }

        private async UniTask UpdateGlobalProgressAsync()
        {
            while (true)
            {
                await UniTask.Yield();

                List<SceneOperationWrapper> handles;
                lock (_handleLock)
                    handles = new List<SceneOperationWrapper>(_activeHandles);

                if (handles.Count == 0)
                    break;

                float totalProgress = 0f;
                foreach (var wrapper in handles)
                    totalProgress += wrapper.Progress;

                float nextGlobalProgress = Mathf.Min(totalProgress / handles.Count, 0.9f);

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

    public class SceneOperationWrapper : IEquatable<SceneOperationWrapper>
    {
        public AsyncOperation AsyncOperation { get; }
        public AsyncOperationHandle<SceneInstance>? AsyncOperationHandle { get; }
        public string SceneName { get; }
        public bool IsLoading { get; }

        public bool IsDone => AsyncOperation?.isDone ?? (AsyncOperationHandle?.IsDone ?? false);
        public float Progress => AsyncOperation?.progress ?? (AsyncOperationHandle?.PercentComplete ?? 0f);

        public SceneOperationWrapper(AsyncOperation asyncOperation, string sceneName, bool isLoading)
        {
            AsyncOperation = asyncOperation;
            SceneName = sceneName;
            IsLoading = isLoading;
        }

        public SceneOperationWrapper(AsyncOperationHandle<SceneInstance> asyncOperationHandle, string sceneName, bool isLoading)
        {
            AsyncOperationHandle = asyncOperationHandle;
            SceneName = sceneName;
            IsLoading = isLoading;
        }

        public bool Equals(SceneOperationWrapper other) => ReferenceEquals(this, other);
        public override bool Equals(object obj) => ReferenceEquals(this, obj);
        public override int GetHashCode() => base.GetHashCode();
    }
}