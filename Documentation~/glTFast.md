# glTFast Documentation

*glTFast* enables loading [glTF™ (GL Transmission Format)][gltf] asset files in [Unity][unity].

It focuses on speed, memory efficiency and a small build footprint.

Two workflows are supported

- Load glTF assets fast and efficient at runtime
- Import glTF assets as prefabs into the asset database at design-time in the Unity Editor

Try the [WebGL Demo][gltfast-web-demo] and check out the [demo project](https://github.com/atteneder/glTFastDemo).

## Features

*glTFast* supports large parts of the glTF 2.0 specification plus many extensions, works with URP, HDRP, the Built-In render pipe and runs on following platforms:

- WebGL
- iOS
- Android
- Windows
- macOS
- Linux
- Universal Windows Platform


Get more details from the [list of features/extensions](./features.md).

## Usage

You can load a glTF asset from an URL or a file path.

> Note: glTFs are loaded via [UnityWebRequests](https://docs.unity3d.com/ScriptReference/Networking.UnityWebRequest.html). File paths have to be prefixed with `file://` in the Unity Editor and on certain platforms (e.g. iOS).

### Runtime Loading via Component

Add a `GltfAsset` component to a GameObject.

![GltfAsset component][gltfasset_component]

### Runtime Loading via Script

```C#
var gltf = gameObject.AddComponent<GLTFast.GltfAsset>();
gltf.url = "https://raw.githubusercontent.com/KhronosGroup/glTF-Sample-Models/master/2.0/Duck/glTF/Duck.gltf";
```

#### Load from byte array

In case you want to handle download/file loading yourself, you can load glTF binary files directly from C# byte[] like so:

```csharp
async void LoadGltfBinaryFromMemory() {
    byte[] data = File.ReadAllBytes("/path/to/file.glb");
    var gltf = new GltfImport();
    bool success = await gltf.LoadGltfBinary(data, new Uri(m_Path));
    if (success) {
        success = gltf.InstantiateMainScene(transform);
    }
}
```

> Note: Most users want to load self-contained glTF binary files this way, but `LoadGltfBinary` also takes the original URI of glTF file as second parameter, so it can resolve relative URIs.

### Customize loading behavior

Loading via script allows you to:

- Custom download or file loading behaviour (see [`IDownloadProvider`](../Runtime/Scripts/IDownload.cs))
- Custom material generation (see [`IMaterialGenerator`](../Runtime/Scripts/IMaterialGenerator.cs))
- Customize [instantiation](#Instantiation)
- Load glTF once and instantiate its scenes many times (see example [below](#custom-post-loading-behaviour))
- Access data of glTF scene (for example get material; see example [below](#custom-post-loading-behaviour))
- Load [reports](#report) allow reacting and communicating incidents during loading and instantiation
- Tweak and optimize loading performance

#### Custom Post-Loading Behaviour

The async `Load` method can be awaited and followed up by custom behaviour.

```C#
async void Start() {
    // First step: load glTF
    var gltf = new GLTFast.GltfImport();
    var success = await gltf.Load("file:///path/to/file.gltf");

    if (success) {
        // Here you can customize the post-loading behavior
        
        // Get the first material
        var material = gltf.GetMaterial();
        Debug.LogFormat("The first material is called {0}", material.name);

        // Instantiate the glTF's main scene
        gltf.InstantiateMainScene( new GameObject("Instance 1").transform );
        // Instantiate the glTF's main scene
        gltf.InstantiateMainScene( new GameObject("Instance 2").transform );

        // Instantiate each of the glTF's scenes
        for (int sceneId = 0; sceneId < gltf.sceneCount; sceneId++) {
            gltf.InstantiateScene(transform, sceneId);
        }
    } else {
        Debug.LogError("Loading glTF failed!");
    }
}
```

#### Instantiation

Creating actual GameObjects (or Entities) from the imported data (Meshes, Materials) is called instantiation.

You can customize it by providing an implementation of `IInstantiator` ( see [source](./Runtime/Scripts/IInstatiator.cs) and the reference implementation [`GameObjectInstantiator`](./Runtime/Scripts/GameObjectInstantiator.cs) for details).

Inject your custom instantiation like so

```csharp
public class YourCustomInstantiator : GLTFast.IInstantiator {
  // Your code here
}
…

  // In your custom post-loading script, use it like this
  gltfAsset.InstantiateMainScene( new YourCustomInstantiator() );
```

#### Report

When loading a glTF file, glTFast creates a report containing messages of varying severity (errors, warnigns or infos). Developers can choose what to make of those report messages. Examples:

- Log to console in readable form
- Feed the information into an analytics framework
- Display details to the users

The provided component `GltfAsset` logs all of those messages to the console by default.  

Look into [`Report`](./Runtime/Scripts/ReportItem.cs) for details.

#### Tune loading performance

When loading glTFs, *glTFast* let's you optimize for two diametrical extremes

- A stable frame rate
- Fastest loading time

By default each `GltfAsset` instance tries not to block the main thread for longer than a certain time budget and defer the remaining loading process to the next frame / game loop iteration.

If you load many glTF files at once, by default they won't be aware of each other and collectively might block the main game loop for too long.

You can solve this by using a common "defer agent". It decides if work should continue right now or at the next game loop iteration. *glTFast* comes with two defer agents

- `TimeBudgetPerFrameDeferAgent` for stable frame rate
- `UninterruptedDeferAgent` for fastest, uninterrupted loading

Usage example

```C#
async Task CustomDeferAgent() {
    // Recommended: Use a common defer agent across multiple GltfImport instances!
    // For a stable frame rate:
    IDeferAgent deferAgent = gameObject.AddComponent<TimeBudgetPerFrameDeferAgent>();
    // Or for faster loading:
    deferAgent = new UninterruptedDeferAgent();

    var tasks = new List<Task>();
    
    foreach( var url in manyUrls) {
        var gltf = new GLTFast.GltfImport(null,deferAgent);
        var task = gltf.Load(url).ContinueWith(
            t => {
                if (t.Result) {
                    gltf.InstantiateMainScene(transform);
                }
            },
            TaskScheduler.FromCurrentSynchronizationContext()
            );
        tasks.Add(task);
    }

    await Task.WhenAll(tasks);
}
```

> Note 1: Depending on your glTF scene, using the `UninterruptedDeferAgent` may block the main thread for up to multiple seconds. Be sure to not do this during critical game play action.

> Note2 : Using the `TimeBudgetPerFrameDeferAgent` does **not** guarantee a stutter free frame rate. This is because some sub tasks of the loading routine (like uploading a texture to the GPU) may take too long, cannot be interrupted and **have** to be done on the main thread.

### Editor Import

To convert your glTF asset into a native Unity prefab, just move/copy it and all its companioning buffer and texture files into the *Assets* folder of your Unity project. It'll get imported into the Asset Database automatically. Select it in the Project view to see detailed settings and import reports in the Inspector. Expand it in the Project View to see the components (Scenes, Meshes, Materials, AnimationClips and Textures) that were imported.

## Project Setup

### Materials and Shader Variants

❗ IMPORTANT ❗

glTF materials might require many shader/features combinations. You **have** to make sure all shader variants your project will ever use are included, or the materials will not work in builds (even if they work in the Editor).

*glTFast* uses custom shaders that are derived from the Unity Standard shaders (and have a similar big number of variants). Including all those variants can make your build big. There's an easy way to find the right subset, if you already know what files you'll expect:

- Run your scene that loads all glTFs you expect in the editor.
- Go to Edit->Project Settings->Graphics
- At the bottom end you'll see the "Shader Preloading" section
- Save the currently tracked shaders/variants to an asset
- Take this ShaderVariantCollection asset and add it to the "Preloaded Shaders" list

An alternative way is to create placeholder materials for all feature combinations you expect and put them in a "Resource" folder in your project.

Read the documentation about [`Shader.Find`](https://docs.unity3d.com/ScriptReference/Shader.Find.html) for details how to include shaders in builds.

### Readable Mesh Data

By default glTFast discards mesh data after it was uploaded to the GPU to free up main memory (see [`markNoLongerReadable`](https://docs.unity3d.com/ScriptReference/Mesh.UploadMeshData.html)). You can disable this globally by using the scripting define `GLTFAST_KEEP_MESH_DATA`.

Motivations for this might be using meshes as physics colliders amongst [other cases](https://docs.unity3d.com/ScriptReference/Mesh-isReadable.html).

## Upgrade Guides

### Upgrade to 4.x

When upgrading from an older version to 4.x or newer the most notable difference is the imported models' orentation. They will appear 180° rotated around the up-axis (Y).

![GltfAsset component][gltfast3to4]

To counter-act this in applications that used older versions of *glTFast* before, make sure you rotate the parent `Transform` by 180° around the Y-axis, which brings the model back to where it should be.

This change was implemented to conform more closely to the [glTF specification](https://github.com/KhronosGroup/glTF/blob/master/specification/2.0/README.md#coordinate-system-and-units), which says:

> The front of a glTF asset faces +Z.

In Unity, the positive Z axis is also defined as forward, so it makes sense to align those and so the coordinate space conversion from glTF's right-handed to Unity's left-handed system is performed by inverting the X-axis (before the Z-axis was inverted).

## Implementation details

*glTFast* uses [Unity's JsonUtility](https://docs.unity3d.com/ScriptReference/JsonUtility.html) for parsing, which has little overhead, is fast and memory-efficient (See <https://docs.unity3d.com/Manual/JSONSerialization.html>).

It also uses fast low-level memory copy methods, [Unity's Job system](https://docs.unity3d.com/Manual/JobSystem.html) and the [Advanced Mesh API](https://docs.unity3d.com/ScriptReference/Mesh.html).

[unity]: https://unity.com
[gltf]: https://www.khronos.org/gltf
[gltfast-web-demo]: https://gltf.pixel.engineer
[gltfasset_component]: ./img/gltfasset_component.png  "Inspector showing a GltfAsset component added to a GameObject"
[gltfast3to4]: ./img/gltfast3to4.png  "3D scene view showing BoomBoxWithAxes model twice. One with the legacy axis conversion and one with the new orientation"
