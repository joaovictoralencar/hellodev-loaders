using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using HelloDev.Loader;
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
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

namespace HelloDev.Loaders
{
    public class SceneLoader : MonoBehaviour, ISceneLoader
    {
        #region Fields

        [SerializeField] private float _minLoadingTime = 5;
        [SerializeField] private AssetReferenceGameObject _loadingScreenReference;

        private enum LoadingScreenState
        {
            Hidden,
            Loading,
            Showing,
            Hiding
        }

        private LoadingScreenState _loadingScreenState = LoadingScreenState.Hidden;
        private GameObject _loadingScreenObject;
        private LoaderOperation<GameObject>? _loadingScreenLoadOp;
        private bool _waitingForMinLoadingTime;
        private CancellationTokenSource _loadingScreenCts;

        private readonly List<SceneOperationWrapper> _activeHandles = new();
        private readonly object _handleLock = new();

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
                    return _activeHandles.Exists(w => !w.IsDone && w.IsLoading);
            }
        }

        public bool IsUnloading
        {
            get
            {
                lock (_handleLock)
                    return _activeHandles.Exists(w => !w.IsDone && !w.IsLoading);
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

            // Delegate to Loader — handles caching, deduplication, and progress
            var op = Loader.Loader.LoadSceneAsync(sceneReference, loadMode, activateOnLoad: true);
            var wrapper = new SceneOperationWrapper(op, sceneReference.RuntimeKey.ToString(), true);
            AddHandle(wrapper);

            op.Progress(p => wrapper.Progress = p);
            op.OnComplete(_ =>
            {
                wrapper.IsDone = true;
                OnSceneOperationComplete(wrapper);
            });

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

                var op = Loader.Loader.LoadSceneAsync(sceneReference, loadMode, activateOnLoad: true);
                var wrapper = new SceneOperationWrapper(op, sceneReference.RuntimeKey.ToString(), true);
                AddHandle(wrapper);

                op.Progress(p => wrapper.Progress = p);
                op.OnComplete(_ =>
                {
                    wrapper.IsDone = true;
                    OnSceneOperationComplete(wrapper);
                });
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

            // Use Loader if this scene is tracked, else fallback to Unity SceneManager
            if (Loader.Loader.IsSceneTracked(scene.name))
            {
                var op = Loader.Loader.UnloadSceneByNameAsync(scene.name);
                wrapper = new SceneOperationWrapper(op, scene.name, false);
                AddHandle(wrapper);
                op.OnComplete(() =>
                {
                    wrapper.IsDone = true;
                    OnSceneOperationComplete(wrapper);
                });
                await op.ToUniTask(token: token);
            }
            else
            {
                AsyncOperation unloadOp = SceneManager.UnloadSceneAsync(scene);
                wrapper = new SceneOperationWrapper(unloadOp, scene.name, false);
                AddHandle(wrapper);
                await UniTask.WaitUntil(() => wrapper.IsDone, cancellationToken: token);
                OnSceneOperationComplete(wrapper);
            }

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
            CancellationTokenSource cts = new CancellationTokenSource();
            ShowLoadingScreenAsync(cts.Token).Forget();

            var wrapper = new SceneOperationWrapper(asyncOperation, sceneName, true);
            AddHandle(wrapper);

            asyncOperation.completed += _ =>
            {
                wrapper.IsDone = true;
                OnSceneOperationComplete(wrapper);
            };
            UpdateGlobalProgressAsync().Forget();
            // TODO: Hide loading screen when all clients have loaded
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

        #region Public — Loading Screen Control (manual)

        public async UniTask HandleLoadingScreenAsync(bool state, CancellationToken token = default)
        {
            if (state)
                await ShowLoadingScreenAsync(token);
            else
                await HideLoadingScreenAsync(token);
        }

        #endregion

        #region Private — Loading Screen State Machine (prefab via Loader)

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
                // Release previous instance if any
                if (_loadingScreenObject != null)
                {
                    Loader.Loader.ReleaseInstance(_loadingScreenObject);
                    _loadingScreenObject = null;
                    _loadingScreenLoadOp = null;
                }

                // Instantiate via Loader
                _loadingScreenLoadOp = Loader.Loader.InstantiateAsync(_loadingScreenReference);
                if (_loadingScreenLoadOp == null)
                {
                    LogError("Failed to instantiate loading screen via Loader");
                    return;
                }

                _loadingScreenObject = await _loadingScreenLoadOp;

                _loadingScreenObject.SetActive(true);
                _loadingScreenState = LoadingScreenState.Showing;
            }
            catch (OperationCanceledException)
            {
                if (_loadingScreenLoadOp != null)
                {
                    if (_loadingScreenObject != null)
                        Loader.Loader.ReleaseInstance(_loadingScreenObject);
                    _loadingScreenObject = null;
                    _loadingScreenLoadOp = null;
                }

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

                // Smooth progress bar completion
                if (_progressTween.isAlive) _progressTween.Stop();
                await Tween.Custom(this, _globalProgress, 1f, 0.5f,
                        (loader, x) => loader._globalProgress = x, Ease.OutQuad)
                    .ToUniTask(cancellationToken: _loadingScreenCts.Token);

                // Release the instantiated loading screen
                if (_loadingScreenObject != null)
                {
                    Loader.Loader.ReleaseInstance(_loadingScreenObject);
                    _loadingScreenObject = null;
                    _loadingScreenLoadOp = null;
                }

                _globalProgress = 0f;
                _lastGlobalProgress = 0f;
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
                _loadingScreenCts?.Dispose();
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

    // Bridge extensions: convert LoaderOperations into awaitable UniTasks
    public static class LoaderOperationExtensions
    {
        public static async UniTask<T> ToUniTask<T>(this LoaderOperation<T> operation, CancellationToken token = default)
        {
            var tcs = new UniTaskCompletionSource<T>();
            operation.OnComplete(result => tcs.TrySetResult(result));
            token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }

        public static async UniTask ToUniTask(this LoaderOperation operation, CancellationToken token = default)
        {
            var tcs = new UniTaskCompletionSource();
            operation.OnComplete(() => tcs.TrySetResult());
            token.Register(() => tcs.TrySetCanceled());
            await tcs.Task;
        }
    }

    // Unified scene operation wrapper (handles Loader operations and Unity fallback)
    public class SceneOperationWrapper : IEquatable<SceneOperationWrapper>
    {
        public AsyncOperation AsyncOperation { get; } // Unity fallback
        public LoaderOperation LoaderOperation { get; } // Non‑generic (e.g., unload)
        public LoaderOperation<SceneInstance> LoaderSceneOperation { get; } // Scene load

        public string SceneName { get; }
        public bool IsLoading { get; }

        public float Progress { get; set; }
        public bool IsDone { get; set; }

        // Unity AsyncOperation constructor
        public SceneOperationWrapper(AsyncOperation asyncOperation, string sceneName, bool isLoading)
        {
            AsyncOperation = asyncOperation;
            SceneName = sceneName;
            IsLoading = isLoading;
        }

        // LoaderOperation (non‑generic) constructor
        public SceneOperationWrapper(LoaderOperation loaderOperation, string sceneName, bool isLoading)
        {
            LoaderOperation = loaderOperation;
            SceneName = sceneName;
            IsLoading = isLoading;
        }

        // LoaderOperation<SceneInstance> constructor
        public SceneOperationWrapper(LoaderOperation<SceneInstance> loaderSceneOperation, string sceneName, bool isLoading)
        {
            LoaderSceneOperation = loaderSceneOperation;
            SceneName = sceneName;
            IsLoading = isLoading;
        }

        public bool Equals(SceneOperationWrapper other) => ReferenceEquals(this, other);
        public override bool Equals(object obj) => ReferenceEquals(this, obj);
        public override int GetHashCode() => base.GetHashCode();
    }
}