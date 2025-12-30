# HelloDev Loaders

Asset and scene loading utilities using Unity Addressables. Provides loading screens, progress tracking, and async operations for smooth scene transitions.

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

## Getting Started

### 1. Install the Package

**Via Package Manager (Local):**
1. Open Unity Package Manager (Window > Package Manager)
2. Click "+" > "Add package from disk"
3. Navigate to this folder and select `package.json`

**Dependencies:**
- `com.unity.addressables`
- PrimeTween (for progress animations)

### 2. Set Up Scene Loading

1. Add a **SceneLoader** component to a persistent GameObject
2. Create your loading screen UI
3. Configure Addressable scene references

### 3. Load a Scene

```csharp
using HelloDev.Loaders;
using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;
using UnityEngine;

public class GameLoader : MonoBehaviour
{
    [SerializeField] private SceneLoader sceneLoader;
    [SerializeField] private AssetReference gameSceneRef;

    public void LoadGameScene()
    {
        sceneLoader.LoadSceneAsync(gameSceneRef, LoadSceneMode.Single);
    }
}
```

### 4. Show Progress

```csharp
using HelloDev.Loaders;
using UnityEngine;
using UnityEngine.UI;

public class LoadingScreen : MonoBehaviour
{
    [SerializeField] private SceneLoader sceneLoader;
    [SerializeField] private Slider progressBar;

    void Update()
    {
        if (sceneLoader.IsLoading)
        {
            progressBar.value = sceneLoader.GlobalProgress;
        }
    }
}
```

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

### Synchronous Instantiation (Load Async, Instantiate Sync)

```csharp
public void SpawnEnemySync()
{
    assetLoader.InstantiateSync(
        enemyPrefab,
        go => {
            // Called immediately after instantiation
            go.GetComponent<Enemy>().Initialize();
        },
        transformParent: transform
    );
}
```

### Tracking Loading Progress

```csharp
public class LoadingUI : MonoBehaviour
{
    [SerializeField] private SceneLoader sceneLoader;
    [SerializeField] private Slider progressBar;
    [SerializeField] private Text progressText;

    void Update()
    {
        if (sceneLoader.IsLoading)
        {
            float progress = sceneLoader.GlobalProgress;
            progressBar.value = progress;
            progressText.text = $"{progress * 100:F0}%";
        }
    }
}
```

### With Loading Screen

```csharp
public class GameManager : MonoBehaviour
{
    [SerializeField] private SceneLoader sceneLoader;
    [SerializeField] private GameObject loadingScreen;

    public void LoadLevel(AssetReference levelRef)
    {
        // SceneLoader can auto-show/hide loading screen
        sceneLoader.LoadSceneAsync(
            levelRef,
            LoadSceneMode.Single,
            showLoadingScreen: true,  // Shows loading screen
            unloadAll: true,          // Unloads current scenes
            useMinLoadingTime: true   // Ensures minimum display time
        );
    }
}
```

### Network Scene Loading (Netcode for GameObjects)

```csharp
// When using Netcode for GameObjects
// Note: Uses scene name string (not AssetReference)
public void LoadNetworkScene(string sceneName)
{
    sceneLoader.LoadNetworkSceneSceneAsync(sceneName, LoadSceneMode.Additive);
}
```

## API Reference

### SceneLoader
| Member | Description |
|--------|-------------|
| `IsLoading` | True while loading is in progress |
| `GlobalProgress` | Combined progress of all loading operations (0-1) |
| `LoadSceneAsync(AssetReference, LoadSceneMode)` | Load a single scene |
| `LoadSceneAsync(AssetReference[], LoadSceneMode)` | Load multiple scenes |
| `LoadSceneAsync(..., showLoadingScreen, unloadAll, useMinLoadingTime)` | Full options |
| `LoadNetworkSceneSceneAsync(string sceneName, LoadSceneMode)` | Load scene by name for network sync |

### AssetLoader
| Member | Description |
|--------|-------------|
| `InstantiateAllAsync(AssetReference, onComplete, ...)` | Async load and instantiate |
| `InstantiateAllAsync(..., onCompleteAll, numInstances)` | Spawn multiple instances |
| `InstantiateSync(AssetReference, onComplete, ...)` | Load async, instantiate sync |

### LoadSceneAsync Parameters
| Parameter | Type | Description |
|-----------|------|-------------|
| `sceneReference` | AssetReference | Addressable scene reference |
| `loadSceneMode` | LoadSceneMode | Single or Additive |
| `showLoadingScreen` | bool | Auto-show loading UI |
| `unloadAll` | bool | Unload existing scenes first |
| `useMinLoadingTime` | bool | Ensure minimum loading duration |

### InstantiateAllAsync Parameters
| Parameter | Type | Description |
|-----------|------|-------------|
| `assetReference` | AssetReference | Addressable prefab reference |
| `onComplete` | Action\<GameObject\> | Called per instance |
| `onCompleteAll` | Action\<GameObject[]\> | Called when all complete |
| `transformParent` | Transform | Parent for instantiated objects |
| `numInstances` | int | Number of instances to spawn |

## Dependencies

### Required
- com.unity.addressables (2.3.1+)

### Recommended
- PrimeTween (for smooth progress animations)

### Optional
- Netcode for GameObjects (for network scene loading)

## Changelog

### v1.0.0
- Initial release
- SceneLoader for async scene loading via Addressables
- AssetLoader for async asset instantiation
- Loading screen management
- Progress tracking with animations
- Multiple scene loading support
- Network scene loading support

## License

MIT License
