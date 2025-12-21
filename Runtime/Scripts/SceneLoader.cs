using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
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

namespace HelloDev.Loaders
{
    public class SceneLoader : MonoBehaviour
    {
        [SerializeField] private float minLoadingTime = 5;
        [SerializeField] private AssetReference loadingScreenReference; // Reference to the loading screen

        private SceneInstance loadingScreenInstance;
        private bool isLoadingScreenActive;

        private List<SceneOperationWrapper> activeHandles = new List<SceneOperationWrapper>();
        private float globalProgress = 0f;

        public float GlobalProgress => globalProgress; // Public read-only property
        public bool IsLoading { get; private set; } = false;
        public bool IsUnloading { get; private set; } = false;
        public bool IsLoadingScreenActive => isLoadingScreenActive;

#if UNITY_NETCODE_GAMEOBJECTS
        public string GameSceneName => gameSceneName;

#if ODIN_INSPECTOR
        [SerializeField, ReadOnly] private string gameSceneName;
#else
        [SerializeField] private string gameSceneName;
#endif
#endif

        public IEnumerator LoadSceneAsyncCoroutine(AssetReference sceneReference, LoadSceneMode loadMode, bool showLoadingScreen = true, bool unloadAll = false, bool useMinLoadingTime = false)
        {
            if (unloadAll) yield return UnloadAllScenesAsyncCoroutine(showLoadingScreen);

            if (sceneReference == null)
            {
                Debug.LogError("Scene reference is not assigned.");
                yield break;
            }

            // Ensure the loading screen is loaded before proceeding
            yield return HandleLoadingScreen(true);

            // Load the scene via Addressables
            AsyncOperationHandle<SceneInstance> handle = sceneReference.LoadSceneAsync(loadMode);
            var wrapper = new SceneOperationWrapper(handle, sceneReference.RuntimeKey.ToString(), true);
            AddHandle(wrapper);

            handle.Completed += op => OnSceneOperationComplete(wrapper);
            IsLoading = true;
            StartCoroutine(UpdateGlobalProgress());
        }

        public IEnumerator LoadSceneAsyncCoroutine(AssetReference[] scenesReferences, LoadSceneMode loadMode, bool showLoadingScreen = true, bool unloadAll = false, bool useMinLoadingTime = false)
        {
            float timeStart = Time.time;
            foreach (var sceneReference in scenesReferences)
            {
                if (sceneReference == null || string.IsNullOrEmpty(sceneReference.RuntimeKey.ToString()))
                {
                    Debug.LogWarning("Scene reference is not assigned.");
                }
            }

            if (scenesReferences.Length > 1 && loadMode == LoadSceneMode.Single)
            {
                Debug.LogError("LoadSceneMode.Single is not supported when loading multiple scenes at once.");
                yield break;
            }

            if (useMinLoadingTime) waitingForMinLoadingTime = true;
            if (unloadAll) yield return UnloadAllScenesAsyncCoroutine(showLoadingScreen, false);

            // Ensure the loading screen is loaded before proceeding
            yield return HandleLoadingScreen(true);

            IsLoading = true;
            foreach (AssetReference sceneReference in scenesReferences)
            {
                if (string.IsNullOrEmpty(sceneReference.RuntimeKey.ToString())) continue;
                // Load the scene via Addressables
                AsyncOperationHandle<SceneInstance> handle = sceneReference.LoadSceneAsync(loadMode);
                var wrapper = new SceneOperationWrapper(handle, sceneReference.RuntimeKey.ToString(), true);
                AddHandle(wrapper);
                handle.Completed += op => OnSceneOperationComplete(wrapper);
            }

            StartCoroutine(UpdateGlobalProgress());
            StartCoroutine(LoadingWaitingTimeCoroutine(Mathf.Max(0, minLoadingTime - (Time.time - timeStart))));
        }

        private IEnumerator LoadingWaitingTimeCoroutine(float remainingTime = 0f)
        {
            yield return new WaitForSeconds(remainingTime);
            waitingForMinLoadingTime = false;
        }

        bool waitingForMinLoadingTime = false;

        public void LoadSceneAsync(AssetReference sceneReference, LoadSceneMode loadMode, bool showLoadingScreen = true, bool unloadAll = false, bool useMinLoadingTime = false)
        {
            if (string.IsNullOrEmpty(sceneReference.RuntimeKey.ToString())) return;
            StartCoroutine(LoadSceneAsyncCoroutine(sceneReference, loadMode, showLoadingScreen, unloadAll, useMinLoadingTime));
        }

        public void LoadSceneAsync(AssetReference[] scenesReferences, LoadSceneMode loadMode, bool showLoadingScreen = true, bool unloadAll = false, bool useMinLoadingTime = false)
        {
            StartCoroutine(LoadSceneAsyncCoroutine(scenesReferences, loadMode, showLoadingScreen, unloadAll, useMinLoadingTime));
        }

        public IEnumerator UnloadSceneAsync(Scene scene, bool showLoadingScreen = true)
        {
            if (!scene.isLoaded)
            {
                Debug.LogError($"Scene '{scene.name}' is not loaded or already unloaded.");
                yield break;
            }

            // Ensure the loading screen is loaded before proceeding
            if (showLoadingScreen)
            {
                yield return HandleLoadingScreen(true);
            }

            SceneOperationWrapper wrapper = null;

            // Check if the scene was loaded via Addressables
            var addressableWrapper = activeHandles.Find(h => h.AsyncOperationHandle.HasValue && h.SceneName == scene.name);

            if (addressableWrapper != null)
            {
                var unloadHandle = Addressables.UnloadSceneAsync(addressableWrapper.AsyncOperationHandle.Value, true);
                wrapper = new SceneOperationWrapper(unloadHandle, scene.name, false);
                AddHandle(wrapper);
            }
            else
            {
                // Unload using SceneManager
                AsyncOperation unloadOp = SceneManager.UnloadSceneAsync(scene);
                Debug.Log(unloadOp);
                wrapper = new SceneOperationWrapper(unloadOp, scene.name, false);
                AddHandle(wrapper);
            }

            // Wait for completion and update state
            yield return new WaitUntil(() => wrapper.IsDone);
            OnSceneOperationComplete(wrapper, !showLoadingScreen);

            IsUnloading = false;
            if (showLoadingScreen && activeHandles.Count == 0)
            {
                StartCoroutine(HandleLoadingScreen(false));
            }
        }

        private void AddHandle(SceneOperationWrapper wrapper)
        {
            activeHandles.Add(wrapper);
            // Update loading/unloading flags
            IsLoading = activeHandles.Exists(h => !h.IsDone && h.IsLoading);
            IsUnloading = activeHandles.Exists(h => !h.IsDone && !h.IsLoading);
        }

        private void OnSceneOperationComplete(SceneOperationWrapper wrapper, bool dontHideLoadingScreen = false)
        {
            Debug.Log($"Scene operation completed successfully: {wrapper.SceneName} {(wrapper.IsLoading ? "Loading" : "Unloading")}");
            var sceneIndexToRemove = activeHandles.FindIndex(w => w.SceneName == wrapper.SceneName);
            if (sceneIndexToRemove >= 0) activeHandles.RemoveAt(sceneIndexToRemove);

            // Update loading/unloading flags
            IsLoading = activeHandles.Exists(h => !h.IsDone && h.IsLoading);
            IsUnloading = activeHandles.Exists(h => !h.IsDone && !h.IsLoading);

            if (!IsLoading && !IsUnloading && !dontHideLoadingScreen)
            {
                StartCoroutine(HandleLoadingScreen(false)); // Hide the loading screen after all operations
            }
        }

        float lastGlobalProgress = 0f;
        bool isUnloadingLoadingScreen = false;
        private Tween progressTween;
        private IEnumerator UpdateGlobalProgress()
        {
            while (activeHandles.Count > 0)
            {
                float totalProgress = 0f;
                foreach (var wrapper in activeHandles)
                {
                    totalProgress += wrapper.Progress;
                }
        
                if (isUnloadingLoadingScreen) yield return null; 
        
                float nextGlobalProgress = totalProgress / activeHandles.Count;
                nextGlobalProgress = Mathf.Min(nextGlobalProgress, .9f);

                if (nextGlobalProgress >= lastGlobalProgress)
                {
                    progressTween?.Kill();
                    float targetProgress = Mathf.Min(nextGlobalProgress, .9f);
                    progressTween = DOTween.To(() => globalProgress, (x) =>
                    {
                        globalProgress = x;
                        lastGlobalProgress = globalProgress;
                        // Debug.Log("Loading: Global progress: " + globalProgress);
                    }, targetProgress, 1f).SetEase(Ease.OutQuad);
                }

                yield return null;
            }
        }

        public IEnumerator HandleLoadingScreen(bool state)
        {
            switch (state)
            {
                // Load the loading screen if not already active
                case true when !isLoadingScreenActive:
                {
                    // Check if the loading screen scene is already loaded or if there's a valid handle
                    if (loadingScreenInstance.Scene.IsValid() && loadingScreenInstance.Scene.isLoaded)
                    {
                        Debug.LogWarning("Loading screen is already loaded. Skipping load.");
                        yield break;
                    }

                    isLoadingScreenActive = true;

                    // Load the loading screen
                    var handle = loadingScreenReference.LoadSceneAsync(LoadSceneMode.Additive);
                    yield return handle;

                    // Check if the handle is valid before accessing its status
                    if (handle.IsValid() && handle.Status == AsyncOperationStatus.Succeeded)
                    {
                        loadingScreenInstance = handle.Result;
                    }
                    else
                    {
                        Debug.LogError("Failed to load the loading screen or handle is invalid.");
                    }

                    break;
                }
                // Unload the loading screen if active
                case false when isLoadingScreenActive:
                {
                    yield return new WaitUntil(() => waitingForMinLoadingTime == false); 
                    // Check if the loading screen scene is valid before attempting to unload
                    if (loadingScreenInstance.Scene.IsValid() && loadingScreenInstance.Scene.isLoaded)
                    {
                        isUnloadingLoadingScreen = true;
                        DOVirtual.Float(globalProgress, 1, .5f, newProgress => globalProgress = newProgress).SetEase(Ease.OutQuad);
                        yield return new WaitForSeconds(0.5f); 
                        var handle = Addressables.UnloadSceneAsync(loadingScreenInstance);
                        yield return handle;
                        globalProgress = 0f;
                        lastGlobalProgress = 0f;
                        isLoadingScreenActive = false;
                        isUnloadingLoadingScreen = false;
                    }

                    break;
                }
            }
        }

        private IEnumerator UnloadAllScenesAsyncCoroutine(bool showLoadingScreen, bool hideOnFinish = true)
        {
            // Unload all currently loaded scenes except the active scene
            // Scene activeScene = SceneManager.GetActiveScene();
            List<Scene> scenesToUnload = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);

                if (scene.isLoaded)
                {
                    scenesToUnload.Add(scene);
                }
            }

            // Hide the loading screen after unloading all scenes
            if (showLoadingScreen)
            {
                yield return HandleLoadingScreen(true);
            }

            for (var index = 0; index < scenesToUnload.Count; index++)
            {
                var scene = scenesToUnload[index];
                yield return UnloadSceneAsync(scene, false);
            }

            // Hide the loading screen after unloading all scenes
            if (hideOnFinish)
            {
                yield return HandleLoadingScreen(false);
            }

        }

        #region Network

#if UNITY_NETCODE_GAMEOBJECTS
        public void LoadNetworkSceneSceneAsync(string sceneName, LoadSceneMode additive)
        {
            StartCoroutine(LoadNetworkSceneSceneAsyncCoroutine(sceneName, additive));
        }

        private IEnumerator LoadNetworkSceneSceneAsyncCoroutine(string sceneName, LoadSceneMode additive)
        {
            yield return new WaitForSeconds(1);
            yield return new WaitUntil(() => NetworkManager.Singleton.IsListening);
            if (!NetworkManager.Singleton.IsServer) yield break;
            NetworkManager.Singleton.SceneManager.OnLoadComplete += OnLoadCompleted;
            NetworkManager.Singleton.SceneManager.OnLoad += OnLoad;
            //already connected, just load scene
            SceneEventProgressStatus status = NetworkManager.Singleton.SceneManager.LoadScene(sceneName, additive);
            if (status != SceneEventProgressStatus.Started)
            {
                Debug.LogError($"Failed to load {sceneName} with a {nameof(SceneEventProgressStatus)}: {status}");
            }
        }

        private void OnLoad(ulong clientId, string sceneName, LoadSceneMode loadSceneMode, AsyncOperation asyncOperation)
        {
            // Ensure the loading screen is loaded before proceeding
            StartCoroutine(HandleLoadingScreen(true));
            var wrapper = new SceneOperationWrapper(asyncOperation, sceneName, true);
            AddHandle(wrapper);

            asyncOperation.completed += (asyncOp) => { OnSceneOperationComplete(wrapper); };

            IsLoading = true;
            StartCoroutine(UpdateGlobalProgress());
        }

        private void OnLoadCompleted(ulong clientId, string sceneName, LoadSceneMode loadSceneMode)
        {
        }
        
#if UNITY_EDITOR
        public UnityEditor.SceneAsset SceneAsset;
        private void OnValidate()
        {
            if (SceneAsset != null)
            {
                gameSceneName = SceneAsset.name;
            }
        }
#endif

#endif

        #endregion
    }

    public class SceneOperationWrapper
    {
        public AsyncOperation AsyncOperation { get; private set; }
        public AsyncOperationHandle<SceneInstance>? AsyncOperationHandle { get; private set; }
        public string SceneName { get; private set; }
        public bool IsLoading { get; private set; }

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
    }
}