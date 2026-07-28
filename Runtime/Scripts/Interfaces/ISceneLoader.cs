using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;

namespace HelloDev.Loader
{
    public interface ISceneLoader
    {
        float GlobalProgress { get; }
        bool IsLoading { get; }
        bool IsUnloading { get; }
        bool IsLoadingScreenActive { get; }

        UniTask LoadSceneAsync(
            AssetReference sceneReference,
            LoadSceneMode loadMode,
            bool showLoadingScreen = true,
            bool unloadAll = false,
            bool useMinLoadingTime = false,
            CancellationToken token = default);

        UniTask LoadSceneAsync(
            AssetReference[] sceneReferences,
            LoadSceneMode loadMode,
            bool showLoadingScreen = true,
            bool unloadAll = false,
            bool useMinLoadingTime = false,
            CancellationToken token = default);

        UniTask UnloadSceneAsync(
            Scene scene,
            bool showLoadingScreen = true,
            CancellationToken token = default);

        UniTask UnloadAllScenesAsync(
            bool showLoadingScreen,
            bool hideOnFinish = true,
            CancellationToken token = default);

        UniTask HandleLoadingScreenAsync(
            bool state,
            CancellationToken token = default);

#if UNITY_NETCODE_GAMEOBJECTS
        string GameSceneName { get; }

        UniTask LoadNetworkSceneAsync(
            string sceneName,
            LoadSceneMode loadMode,
            CancellationToken token = default);
#endif

        void LoadSceneAsyncForget(
            AssetReference sceneReference,
            LoadSceneMode loadMode,
            bool showLoadingScreen = true,
            bool unloadAll = false,
            bool useMinLoadingTime = false);

        void LoadSceneAsyncForget(
            AssetReference[] sceneReferences,
            LoadSceneMode loadMode,
            bool showLoadingScreen = true,
            bool unloadAll = false,
            bool useMinLoadingTime = false);

#if UNITY_NETCODE_GAMEOBJECTS
void LoadNetworkSceneForget(
    string sceneName,
    LoadSceneMode loadMode);
#endif
    }
}