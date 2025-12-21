using System;
using System.Collections;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
namespace HelloDev.Loaders
{
    public class AssetLoader : MonoBehaviour
    {
        /// <summary>
        /// Loads and instantiates an asset asynchronously. This method is ideal for large prefabs
        /// because both loading and instantiating happen asynchronously to avoid blocking the main thread.
        /// After instantiation, it automatically adds a cleanup component to handle resource management.
        /// </summary>
        /// <param name="reference">The asset reference to load and instantiate.</param>
        /// <param name="onComplete">Callback invoked when the asset is successfully instantiated.</param>
        /// <param name="transformParent">Optional parent transform for the instantiated object.</param>
        /// <typeparam name="T">Type of the MonoBehaviour that represents the loaded asset.</typeparam>
        public void InstantiateAllAsync(AssetReference reference, Action<GameObject> onComplete, Action<GameObject[]> onCompleteAll = null,
            Transform transformParent = null, int numInstances = 1)
        {
            AsyncOperationHandle<GameObject> handle = reference.LoadAssetAsync<GameObject>();
            handle.Completed += asyncOperationHandle =>
            {
                if (asyncOperationHandle.Status == AsyncOperationStatus.Succeeded)
                {
                    StartCoroutine(InstantiateAsyncCoroutine(handle.Result, onComplete, onCompleteAll, transformParent, reference, numInstances));
                }
                else
                {
                    Debug.LogError("AssetReference failed to load.");
                }
            };
        }

        /// <summary>
        /// Loads an asset asynchronously and instantiates it synchronously. 
        /// While the asset loading is asynchronous, the actual instantiation happens immediately.
        /// This is useful when you want the asset to be instantiated without delay after it's loaded.
        /// </summary>
        /// <param name="reference">The asset reference to load and instantiate.</param>
        /// <param name="onComplete">Callback invoked when the asset is successfully instantiated.</param>
        /// <param name="transformParent">Optional parent transform for the instantiated object.</param>
        public void InstantiateSync(AssetReference reference, Action<GameObject> onComplete,
            Transform transformParent = null)
        {
            var handle = reference.InstantiateAsync(transformParent);
            handle.Completed += obj =>
            {
                if (obj.Status == AsyncOperationStatus.Succeeded)
                {
                    SetupInstantiatedObject(obj.Result, transformParent, reference, true);
                    onComplete?.Invoke(obj.Result);
                }
                else
                {
                    Debug.LogError("AssetReference failed to load.");
                }
            };
        }

        /// <summary>
        /// Sets up the instantiated GameObject by assigning its parent and adding a cleanup component.
        /// This method is used by both async and sync instantiation methods to reduce code duplication.
        /// </summary>
        /// <param name="go">The GameObject that was instantiated.</param>
        /// <param name="transformParent">The parent transform for the instantiated object.</param>
        /// <param name="reference">The asset reference, which is released after instantiation.</param>
        /// <param name="shouldRelease"></param>
        private void SetupInstantiatedObject(GameObject go, Transform transformParent, AssetReference reference, bool shouldRelease)
        {
            go.gameObject.AddComponent<SelfCleanup>();
            go.gameObject.transform.SetParent(transformParent);
            if (shouldRelease) reference.ReleaseAsset();
        }

        /// <summary>
        /// Coroutine to handle asynchronous instantiation of the asset after it is loaded.
        /// This is used in the asynchronous loading workflow where both loading and instantiating are non-blocking.
        /// </summary>
        /// <param name="instance">The loaded asset instance.</param>
        /// <param name="onComplete">Callback to invoke after instantiation is complete.</param>
        /// <param name="onCompleteAll">Callback to invoke after ALL instantiations are completed</param>
        /// <param name="transformParent">Optional parent transform for the instantiated object.</param>
        /// <param name="reference">The asset reference, which is released after instantiation.</param>
        /// <typeparam name="T">The type of the instantiated asset (e.g., MonoBehaviour).</typeparam>
        /// <returns>An IEnumerator to support coroutine execution.</returns>
        private IEnumerator InstantiateAsyncCoroutine(GameObject instance, Action<GameObject> onComplete, Action<GameObject[]> onCompleteAll,
            Transform transformParent, AssetReference reference, int numInstances = 1)
        {
            var asyncInstantiate = InstantiateAsync(instance, numInstances);
            yield return asyncInstantiate;
            bool shouldRelease = numInstances > 1;
            foreach (var go in asyncInstantiate.Result)
            {
                SetupInstantiatedObject(go.gameObject, transformParent, reference, !shouldRelease);
                onComplete?.Invoke(go);
            }
            if (shouldRelease) reference.ReleaseAsset();
            onCompleteAll?.Invoke(asyncInstantiate.Result);
        }
    }

    // Releases asset (trackHandle must be true in InstantiateAsync, which is the default)
    internal class SelfCleanup : MonoBehaviour
    {
        /// <summary>
        /// Automatically releases the instantiated asset when it is destroyed, 
        /// freeing up memory and other resources managed by Addressables.
        /// </summary>
        void OnDestroy()
        {
            Addressables.ReleaseInstance(gameObject);
            Debug.Log("Released instance for " + gameObject.name);
        }
    }
}