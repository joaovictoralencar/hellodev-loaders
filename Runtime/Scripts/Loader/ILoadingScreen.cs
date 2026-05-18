using UnityEngine;
using UnityEngine.AddressableAssets;

namespace Sakumon.Loader
{
    public interface ILoadingScreen
    {
        public void OnStart(string sceneName);
        public void OnProgress(float progress);
        public void OnComplete(string sceneName);
    }

    [System.Serializable]
    public struct SceneReference
    {
        public string SceneName;
        public AssetReference Reference;

        public bool IsValid() => Reference != null && Reference.RuntimeKeyIsValid();
    }
}