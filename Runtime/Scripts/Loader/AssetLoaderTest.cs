using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Sirenix.OdinInspector;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace Sakumon.Loader
{
    /// <summary>
    /// Test MonoBehaviour covering every Loader method.
    /// Assign references in the Inspector and wire methods to UI buttons.
    /// </summary>
    public class AssetLoaderTest : MonoBehaviour
    {
        #region Inspector Fields

        [Header("Single Asset")] [SerializeField]
        private AssetReferenceGameObject _prefabReference;

        [SerializeField] private AssetReference _assetReference;

        [Header("Batch Assets")] [SerializeField]
        private List<AssetReferenceGameObject> _multiplePrefabReferences;

        [SerializeField] private List<AssetReference> _multipleAssetReferences;

        [Header("Label")] [SerializeField] private string _label = "monsters";

        [Header("Scene")] [SerializeField] private AssetReference _sceneReference;

        [Header("Test UI")]
        [SerializeField] private bool _createButtonsOnStart;
        [SerializeField] private GameObject _buttonPrefab;
        [SerializeField] private Transform _buttonContainer;

        #endregion

        #region State

        private GameObject _prefabInstance;
        private List<GameObject> _batchInstances = new();
        private SceneInstance _loadedScene;

        #endregion

        #region Life Cycle

        private void Awake()
        {
            Initialize();
        }

        private void Start()
        {
            if (_createButtonsOnStart)
                CreateButtons();
        }

        private void OnDestroy()
        {
            if (_prefabInstance != null)
                Loader.ReleaseInstance(_prefabInstance);

            if (_batchInstances.Count > 0)
                Loader.ReleaseInstances(_batchInstances);

            Loader.UnloadAllScenes();
            Loader.ReleaseAll();
        }

        #endregion

        #region Initialize

        [Button]
        public async void Initialize()
        {
            try
            {
                await Loader.InitializeAsync()
                    .OnComplete(() => Debug.Log("[Test] Addressables initialized."));
            }
            catch (Exception e)
            {
                Debug.LogError($"[Test] Initialize failed: {e.Message}");
            }
        }

        #endregion

        #region Load

        [Button]
        public async void LoadAsset()
        {
            try
            {
                await Loader.LoadAssetAsync<Object>(_assetReference)
                    .Progress(p => Debug.Log($"[Test] LoadAsset: {p * 100f:F0}%"))
                    .OnComplete(asset => Debug.Log($"[Test] LoadAsset: {asset.name}"));
            }
            catch (Exception e)
            {
                Debug.LogError($"[Test] LoadAsset failed: {e.Message}");
            }
        }

        [Button]
        public async void PreloadAll()
        {
            try
            {
                await Loader.PreloadAsync(_multipleAssetReferences)
                    .Progress(p => Debug.Log($"[Test] PreloadAll: {p * 100f:F0}%"))
                    .OnComplete(() => Debug.Log("[Test] PreloadAll complete."));
            }
            catch (Exception e)
            {
                Debug.LogError($"[Test] PreloadAll failed: {e.Message}");
            }
        }

        [Button]
        public async void LoadByLabel()
        {
            try
            {
                await Loader.LoadByLabelAsync<Object>(_label)
                    .Progress(p => Debug.Log($"[Test] LoadByLabel '{_label}': {p * 100f:F0}%"))
                    .OnComplete(results =>
                    {
                        Debug.Log($"[Test] LoadByLabel '{_label}': {results.Count} assets loaded.");
                        foreach (var (key, asset) in results)
                            Debug.Log($"  {key} → {asset.name}");
                    });
            }
            catch (Exception e)
            {
                Debug.LogError($"[Test] LoadByLabel failed: {e.Message}");
            }
        }

        [Button]
        public void CheckCacheState()
        {
            bool isLoaded = Loader.IsLoaded(_assetReference);
            Debug.Log($"[Test] IsLoaded: {isLoaded}");

            if (isLoaded)
            {
                var cached = Loader.TryGetCached<Object>(_assetReference);
                Debug.Log($"[Test] TryGetCached: {cached?.name ?? "null"}");
            }
        }

        #endregion

        #region Instantiate

        [Button]
        public async void InstantiatePrefab()
        {
            try
            {
                if (_prefabInstance != null)
                {
                    Loader.ReleaseInstance(_prefabInstance);
                    _prefabInstance = null;
                }

                Vector3 position = new Vector3(Random.Range(-5, 5), 0, 0);

                await Loader.InstantiateAsync(_prefabReference, transform, position, Quaternion.identity)
                    .Progress(p => Debug.Log($"[Test] InstantiatePrefab: {p * 100f:F0}%"))
                    .OnComplete(instance =>
                    {
                        _prefabInstance = instance;
                        Debug.Log($"[Test] InstantiatePrefab: {instance.name}");
                    });
            }
            catch (Exception e)
            {
                Debug.LogError($"[Test] InstantiatePrefab failed: {e.Message}");
            }
        }

        [Button]
        public async void InstantiateAll()
        {
            try
            {
                if (_batchInstances.Count > 0)
                {
                    Loader.ReleaseInstances(_batchInstances);
                    _batchInstances.Clear();
                }

                await Loader.InstantiateAllAsync(_multiplePrefabReferences, transform)
                    .Progress(p => Debug.Log($"[Test] InstantiateAll: {p * 100f:F0}%"))
                    .OnComplete(instances =>
                    {
                        _batchInstances = instances;
                        Debug.Log($"[Test] InstantiateAll: {instances.Count} instances.");
                    });
            }
            catch (AggregateException ae)
            {
                Debug.LogError($"[Test] InstantiateAll partially failed: {ae.Message}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Test] InstantiateAll failed: {e.Message}");
            }
        }

        #endregion

        #region Scene

        [Button]
        public async void LoadScene()
        {
            try
            {
                await Loader.LoadSceneAsync(_sceneReference, LoadSceneMode.Additive)
                    .Progress(p => Debug.Log($"[Test] LoadScene: {p * 100f:F0}%"))
                    .OnComplete(scene =>
                    {
                        _loadedScene = scene;
                        Debug.Log("[Test] LoadScene complete.");
                    });
            }
            catch (Exception e)
            {
                Debug.LogError($"[Test] LoadScene failed: {e.Message}");
            }
        }

        [Button]
        public async void UnloadScene()
        {
            try
            {
                await Loader.UnloadSceneAsync(_sceneReference)
                    .OnComplete(() =>
                    {
                        _loadedScene = default;
                        Debug.Log("[Test] UnloadScene complete.");
                    });
            }
            catch (Exception e)
            {
                Debug.LogError($"[Test] UnloadScene failed: {e.Message}");
            }
        }

        [Button]
        public async void UnloadAllScenes()
        {
            try
            {
                await Loader.UnloadAllScenesAsync()
                    .OnComplete(() =>
                    {
                        _loadedScene = default;
                        Debug.Log("[Test] UnloadAllScenes complete.");
                    });
            }
            catch (Exception e)
            {
                Debug.LogError($"[Test] UnloadAllScenes failed: {e.Message}");
            }
        }

        #endregion

        #region Release

        [Button]
        public void ReleaseInstance()
        {
            Loader.ReleaseInstance(_prefabInstance);
            _prefabInstance = null;
            Debug.Log("[Test] ReleaseInstance complete.");
        }

        [Button]
        public void ReleaseBatchInstances()
        {
            Loader.ReleaseInstances(_batchInstances);
            _batchInstances.Clear();
            Debug.Log("[Test] ReleaseBatchInstances complete.");
        }

        [Button]
        public void ReleaseAsset()
        {
            Loader.Release(_assetReference);
            Debug.Log("[Test] ReleaseAsset complete.");
        }

        [Button]
        public void ReleaseLabel()
        {
            Loader.ReleaseLabel(_label);
            Debug.Log($"[Test] Label '{_label}' released.");
        }

        [Button]
        public void ReleaseAll()
        {
            Loader.ReleaseAll();
            Debug.Log("[Test] ReleaseAll complete.");
        }

        #endregion

        #region Test UI

        private void CreateButtons()
        {
            if (_buttonPrefab == null || _buttonContainer == null)
            {
                Debug.LogWarning("[Test] CreateButtons: _buttonPrefab or _buttonContainer is not assigned.");
                return;
            }

            var methods = GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            foreach (var method in methods)
            {
                if (method.IsSpecialName || method.GetParameters().Length > 0) continue;

                var buttonObj = Instantiate(_buttonPrefab, _buttonContainer);

                var btn = buttonObj.GetComponentInChildren<Button>();
                if (btn == null) continue;

                var label = buttonObj.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null) label.text = NicifyMethodName(method.Name);

                var captured = method;
                btn.onClick.AddListener(() => captured.Invoke(this, null));
            }
        }

        private static string NicifyMethodName(string name)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                    sb.Append(' ');
                sb.Append(name[i]);
            }
            return sb.ToString();
        }

        #endregion
    }
}