# HelloDev Loaders

Asset and scene loading utilities using Unity Addressables. Provides loading screens, progress tracking, and async operations.

## Features

### SceneLoader
- Async scene loading via Addressables
- Loading screen management with automatic show/hide
- Smooth progress tracking with PrimeTween animations
- Multiple scene loading support
- Minimum loading time option (for smooth UX)
- Network scene loading (with Netcode for GameObjects)

### AssetLoader
- `InstantiateAllAsync` - Load and instantiate asynchronously (non-blocking)
- `InstantiateSync` - Load async, then instantiate immediately
- Multiple instance spawning support
- Automatic cleanup with SelfCleanup component

## Installation

### Via Package Manager (Local)
1. Open Unity Package Manager
2. Click "+" > "Add package from disk"
3. Navigate to this folder and select `package.json`

## Usage

### Loading a Scene

```csharp
using HelloDev.Loaders;
using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;

public class GameLoader : MonoBehaviour
{
    [SerializeField] private SceneLoader sceneLoader;
    [SerializeField] private AssetReference gameSceneReference;

    public void LoadGameScene()
    {
        sceneLoader.LoadSceneAsync(gameSceneReference, LoadSceneMode.Additive);
    }

    // Load with options
    public void LoadWithOptions()
    {
        sceneLoader.LoadSceneAsync(
            gameSceneReference,
            LoadSceneMode.Additive,
            showLoadingScreen: true,
            unloadAll: true,
            useMinLoadingTime: true
        );
    }
}
```

### Loading Multiple Scenes

```csharp
[SerializeField] private AssetReference[] levelScenes;

public void LoadAllLevels()
{
    sceneLoader.LoadSceneAsync(levelScenes, LoadSceneMode.Additive);
}
```

### Instantiating Assets

```csharp
using HelloDev.Loaders;
using UnityEngine.AddressableAssets;

public class Spawner : MonoBehaviour
{
    [SerializeField] private AssetLoader assetLoader;
    [SerializeField] private AssetReference enemyPrefab;

    public void SpawnEnemy()
    {
        assetLoader.InstantiateAllAsync(
            enemyPrefab,
            go => Debug.Log($"Spawned: {go.name}"),
            transformParent: transform
        );
    }

    // Spawn multiple instances
    public void SpawnWave(int count)
    {
        assetLoader.InstantiateAllAsync(
            enemyPrefab,
            go => Debug.Log($"Spawned: {go.name}"),
            onCompleteAll: allSpawned => Debug.Log($"Wave complete: {allSpawned.Length} enemies"),
            numInstances: count
        );
    }
}
```

### Tracking Loading Progress

```csharp
void Update()
{
    if (sceneLoader.IsLoading)
    {
        progressBar.value = sceneLoader.GlobalProgress;
    }
}
```

## Dependencies

### Required
- com.unity.addressables
- PrimeTween (for progress animations)

### Optional
- Netcode for GameObjects (for network scene loading)

## License

MIT License
