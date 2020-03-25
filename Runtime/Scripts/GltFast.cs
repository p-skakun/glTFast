﻿// Copyright 2020 Andreas Atteneder
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//

#if NET_LEGACY || NET_2_0 || NET_2_0_SUBSET
#warning Consider using .NET 4.x equivalent scripting runtime version or upgrading Unity 2019.1 or newer for better performance
#define COPY_LEGACY
#endif

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using UnityEngine.Events;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
#if BURST
using Unity.Mathematics;
#endif
using System.Runtime.InteropServices;
#if KTX_UNITY
using KtxUnity;
#endif // KTX_UNITY

namespace GLTFast {

    using Schema;
    using Loading;

    public class GLTFast {

        public const int DefaultBatchCount = 50000;
        const uint GLB_MAGIC = 0x46546c67;
        const string GLB_EXT = ".glb";

        public const string ErrorUnsupportedType = "Unsupported {0} type {1}";
        public const string ErrorUnsupportedColorFormat = "Unsupported Color format {0}";
        const string ErrorUnsupportedPrimitiveMode = "Primitive mode {0} is untested!";
        const string ErrorMissingImageURL = "Image URL missing";
#if !KTX_UNITY
        const string ErrorKtxUnsupported = "KTX textures are not supported!";
#endif
        const string ErrorPackageMissing = "{0} package needs to be installed in order to support glTF extension {1}!\nSee https://github.com/atteneder/glTFast#installing for instructions";

        const string ExtDracoMeshCompression = "KHR_draco_mesh_compression";
        const string ExtTextureBasisu = "KHR_texture_basisu";

        public static readonly HashSet<string> supportedExtensions = new HashSet<string> {
#if DRACO_UNITY
            ExtDracoMeshCompression,
#endif
#if KTX_UNITY
            ExtTextureBasisu,
#endif // KTX_UNITY
            "KHR_materials_pbrSpecularGlossiness",
            "KHR_materials_unlit",
            "KHR_texture_transform",
            "KHR_mesh_quantization"
        };

        enum ChunkFormat : uint
        {
            JSON = 0x4e4f534a,
            BIN = 0x004e4942
        }

        enum ImageFormat {
            Unknown,
            PNG,
            Jpeg,
            KTX
        }

        /// <summary>
        /// MonoBehaviour instance that is used for scheduling loading Coroutines.
        /// Can be an arbitrary one, but cannot be destroyed before the loading
        /// process finished.
        /// </summary>
        MonoBehaviour monoBehaviour;

        IDownloadProvider downloadProvider;
        IMaterialGenerator materialGenerator;
        IDeferAgent deferAgent;

        public UnityAction<bool> onLoadComplete;

#region VolatileData

        /// <summary>
        /// These members are only used during loading phase.
        /// </summary>
        byte[][] buffers;
        NativeArray<byte>[] nativeBuffers;

        GlbBinChunk[] binChunks;

        AccessorDataBase[] accessorData;
        AccessorUsage[] accessorUsage;
        JobHandle accessorJobsHandle;
        PrimitiveCreateContextBase[] primitiveContexts;
        Dictionary<Attributes,VertexBufferConfigBase> vertexAttributes;
        /// <summary>
        /// Array of dictionaries, indexed by mesh ID
        /// The dictionary contains all the mesh's primitives, clustered
        /// by Vertex Attribute usage (Primitives with identical vertex
        /// data will be clustered).
        /// </summary>
        Dictionary<Attributes,List<MeshPrimitive>>[] meshPrimitiveCluster;
        List<ImageCreateContext> imageCreateContexts;
#if KTX_UNITY
        List<KtxLoadContextBase> ktxLoadContexts;
        List<KtxLoadContextBase> ktxLoadContextsBuffer;
#endif // KTX_UNITY

        Texture2D[] images = null;
        ImageFormat[] imageFormats;
        bool[] imageGamma;

        /// optional glTF-binary buffer
        /// https://github.com/KhronosGroup/glTF/tree/master/specification/2.0#binary-buffer
        GlbBinChunk? glbBinChunk;

#endregion VolatileData

        /// Main glTF data structure
        Root gltfRoot;
        UnityEngine.Material[] materials;
        List<UnityEngine.Object> resources;

        Primitive[] primitives;
        int[] meshPrimitiveIndex;

        /// TODO: Some of these class members maybe could be passed
        /// between loading routines. Turn them into parameters or at
        /// least dispose them once all ingredients are ready.

        bool loadingError = false;
        public bool LoadingError { get { return loadingError; } private set { this.loadingError = value; } }

        static string GetUriBase( string url ) {
            var uri = new Uri(url);
            return new Uri( uri, ".").AbsoluteUri;
        }

        public GLTFast(
            MonoBehaviour monoBehaviour,
            IDownloadProvider downloadProvider=null,
            IDeferAgent deferAgent=null
            )
        {
            this.monoBehaviour = monoBehaviour;
            this.downloadProvider = downloadProvider ?? new DefaultDownloadProvider();
            this.deferAgent = deferAgent ?? monoBehaviour.gameObject.AddComponent<TimeBudgetPerFrameDeferAgent>();
            materialGenerator = new DefaultMaterialGenerator();
        }

        public void Load( string url ) {
            bool gltfBinary = false;
            // quick glTF-binary check
            gltfBinary = url.EndsWith(GLB_EXT,StringComparison.OrdinalIgnoreCase);
            if(!gltfBinary) {
                // thourough glTF-binary extension check that strips HTTP GET parameters
                int getIndex = url.LastIndexOf('?');
                gltfBinary = getIndex>=0 && url.Substring(getIndex-GLB_EXT.Length,GLB_EXT.Length).Equals(GLB_EXT,StringComparison.OrdinalIgnoreCase);
            }
            monoBehaviour.StartCoroutine(LoadRoutine(url,gltfBinary));
        }

        IEnumerator LoadRoutine( string url, bool gltfBinary ) {

            var download = downloadProvider.Request(url);
            yield return download;

            if(download.success) {
                if(gltfBinary) {
                    LoadGlb(download.data,url);
                } else {
                    LoadGltf(download.text,url);
                }
                yield return LoadContent();
            } else {
                Debug.LogErrorFormat("{0} {1}",download.error,url);
                loadingError=true;
            }

            DisposeVolatileData();
            OnLoadComplete(!loadingError);
        }

        IEnumerator LoadContent() {

            if(loadingError) {
                OnLoadComplete(!loadingError);
                yield break;
            }

            if( deferAgent.ShouldDefer() ) {
                yield return null;
            }

            var routineBuffers = monoBehaviour.StartCoroutine( WaitForBufferDownloads() );
            var routineTextures = monoBehaviour.StartCoroutine( WaitForTextureDownloads() );

            yield return routineBuffers;
            yield return routineTextures;
            
#if KTX_UNITY
            yield return WaitForKtxTextures();
#endif // KTX_UNITY

            if(loadingError) {
                OnLoadComplete(!loadingError);
                yield break;
            }

            var prepareRoutine = Prepare();
            while(prepareRoutine.MoveNext()) {
                if(loadingError) {
                    break;
                }
                if( deferAgent.ShouldDefer() ) {
                    yield return null;
                }
            }
        }

        void OnLoadComplete(bool success) {
            if(onLoadComplete!=null) {
                onLoadComplete(success);
            }
        }

        void ParseJsonAndLoadBuffers( string json, string baseUri ) {
            gltfRoot = ParseJson(json);

            if(!CheckExtensionSupport(gltfRoot)) {
                loadingError = true;
                return;
            }

            var bufferCount = gltfRoot.buffers.Length;
            if(bufferCount>0) {
                buffers = new byte[bufferCount][];
                nativeBuffers = new NativeArray<byte>[bufferCount];
                binChunks = new GlbBinChunk[bufferCount];
            }

            for( int i=0; i<bufferCount;i++) {
                var buffer = gltfRoot.buffers[i];
                if( !string.IsNullOrEmpty(buffer.uri) ) {
                    if(buffer.uri.StartsWith("data:")) {
                        buffers[i] = DecodeEmbedBuffer(buffer.uri);
                        if(buffers[i]==null) {
                            Debug.LogError("Error loading embed buffer!");
                            loadingError = true;
                        }
                    } else {
                        LoadBuffer( i, baseUri+buffer.uri );
                    }
                }
            }
        }

        Root ParseJson(string json) {
            // JsonUtility sometimes creates non-null default instances of objects-type members
            // even though there are none in the original JSON.
            // This work-around makes sure not existent JSON nodes will be null in the result.

            // Step one: main JSON parsing
            Profiler.BeginSample("JSON main");
            var root = JsonUtility.FromJson<Root>(json);
            Profiler.EndSample();

            /// Step two:
            /// detect, if a secondary null-check is necessary.
            Profiler.BeginSample("JSON extension check");
            bool check = false;
            if(root.materials!=null) {
                for (int i = 0; i < root.materials.Length; i++) {
                    var mat = root.materials[i];
                    check = mat.extensions!=null &&
                    (
                        mat.extensions.KHR_materials_pbrSpecularGlossiness!=null
                        || mat.extensions.KHR_materials_unlit!=null
                    );
                    if(check) break;
                }
            }
            Profiler.EndSample();

            /// Step three:
            /// If we have to make an explicit check, parse the JSON again with a
            /// different, minimal Root class, where class members are serialized to
            /// the type string. In case the string is null, there's no JSON node.
            /// Otherwise the string would be empty ("").
            if(check) {
                Profiler.BeginSample("JSON secondary");
                var fakeRoot = JsonUtility.FromJson<FakeSchema.Root>(json);

                for (int i = 0; i < root.materials.Length; i++)
                {
                    var mat = root.materials[i];
                    if(mat.extensions == null) continue;
                    Assert.AreEqual(mat.name,fakeRoot.materials[i].name);
                    var fake = fakeRoot.materials[i].extensions;
                    if(fake.KHR_materials_unlit==null) {
                        mat.extensions.KHR_materials_unlit = null;
                    }
                    if(fake.KHR_materials_pbrSpecularGlossiness==null) {
                        mat.extensions.KHR_materials_pbrSpecularGlossiness = null;
                    }
                }
                Profiler.EndSample();
            }
            return root;
        }

        /// <summary>
        /// Validates required and used glTF extensions and reports unsupported ones.
        /// </summary>
        /// <param name="gltfRoot"></param>
        /// <returns>False if a required extension is not supported. True otherwise.</returns>
        bool CheckExtensionSupport (Root gltfRoot) {
            if(gltfRoot.extensionsRequired!=null) {
                foreach(var ext in gltfRoot.extensionsRequired) {
                    var supported = supportedExtensions.Contains(ext);
                    if(!supported) {
#if !DRACO_UNITY
                        if(ext==ExtDracoMeshCompression) {
                            Debug.LogErrorFormat(ErrorPackageMissing,"DracoUnity",ext);
                        } else
#endif
#if !KTX_UNITY
                        if(ext==ExtTextureBasisu) {
                            Debug.LogErrorFormat(ErrorPackageMissing,"KtxUnity",ext);
                        } else
#endif
                        {
                            Debug.LogErrorFormat("Required glTF extension {0} is not supported!",ext);
                        }
                        return false;
                    }
                }
            }
            if(gltfRoot.extensionsUsed!=null) {
                foreach(var ext in gltfRoot.extensionsUsed) {
                    var supported = supportedExtensions.Contains(ext);
                    if(!supported) {
                        Debug.LogWarningFormat("glTF extension {0} is not supported!",ext);
                    }
                }
            }
            return true;
        }

        void LoadGltf( string json, string url ) {
            Profiler.BeginSample("LoadGltf");
            var baseUri = GetUriBase(url);
            ParseJsonAndLoadBuffers(json,baseUri);
            if(!loadingError) {
                LoadImages(baseUri);
            }
            Profiler.EndSample();
        }

        void LoadImages( string baseUri ) {

            Profiler.BeginSample("LoadImages");

            if (gltfRoot.textures != null && gltfRoot.images != null && gltfRoot.materials!=null) {
                images = new Texture2D[gltfRoot.images.Length];
                imageFormats = new ImageFormat[gltfRoot.images.Length];

                if(QualitySettings.activeColorSpace==ColorSpace.Linear) {

                    imageGamma = new bool[gltfRoot.images.Length];

                    for(int i=0;i<gltfRoot.materials.Length;i++) {
                        var mat = gltfRoot.materials[i];
                        if( mat.pbrMetallicRoughness != null ) {
                            if(
                                mat.pbrMetallicRoughness.baseColorTexture != null &&
                                mat.pbrMetallicRoughness.baseColorTexture.index >= 0 &&
                                mat.pbrMetallicRoughness.baseColorTexture.index < imageGamma.Length
                            ) {
                                imageGamma[mat.pbrMetallicRoughness.baseColorTexture.index] = true;
                            }
                        }
                        if(
                            mat.emissiveTexture != null &&
                            mat.emissiveTexture.index >= 0 &&
                            mat.emissiveTexture.index < imageGamma.Length
                        ) {
                            imageGamma[mat.emissiveTexture.index] = true;
                        }
                        if( mat.extensions != null &&
                            mat.extensions.KHR_materials_pbrSpecularGlossiness != null )
                        {
                            if(
                                mat.extensions.KHR_materials_pbrSpecularGlossiness.diffuseTexture != null &&
                                mat.extensions.KHR_materials_pbrSpecularGlossiness.diffuseTexture.index >= 0 &&
                                mat.extensions.KHR_materials_pbrSpecularGlossiness.diffuseTexture.index < imageGamma.Length
                            ) {
                                imageGamma[mat.extensions.KHR_materials_pbrSpecularGlossiness.diffuseTexture.index] = true;
                            }
                            if(
                                mat.extensions.KHR_materials_pbrSpecularGlossiness.specularGlossinessTexture != null &&
                                mat.extensions.KHR_materials_pbrSpecularGlossiness.specularGlossinessTexture.index >= 0 &&
                                mat.extensions.KHR_materials_pbrSpecularGlossiness.specularGlossinessTexture.index < imageGamma.Length
                            ) {
                                imageGamma[mat.extensions.KHR_materials_pbrSpecularGlossiness.specularGlossinessTexture.index] = true;
                            }
                        }
                    }
                }

#if KTX_UNITY
                // Derive image type from texture extension
                for (int i = 0; i < gltfRoot.textures.Length; i++) {
                    var texture = gltfRoot.textures[i];
                    if(texture.isKtx) {
                        var imgIndex = texture.GetImageIndex();
                        imageFormats[imgIndex] = ImageFormat.KTX;
                    }
                }
#endif // KTX_UNITY

                for (int i = 0; i < gltfRoot.images.Length; i++) {
                    var img = gltfRoot.images[i];

                    if(!string.IsNullOrEmpty(img.uri) && img.uri.StartsWith("data:")) {
                        string mimeType;
                        var data = DecodeEmbedBuffer(img.uri,out mimeType);
                        var imgFormat = GetImageFormatFromMimeType(mimeType);
                        if(data==null || imgFormat==ImageFormat.Unknown) {
                            Debug.LogError("Loading embedded image failed");
                            continue;
                        }
                        if(imageFormats[i]!=ImageFormat.Unknown && imageFormats[i]!=imgFormat) {
                            Debug.LogErrorFormat("Inconsistent embed image type {0}!={1}",imageFormats[i],imgFormat);
                        }
                        imageFormats[i] = imgFormat;
                        if(imageFormats[i]!=ImageFormat.Jpeg && imageFormats[i]!=ImageFormat.PNG) {
                            // TODO: support embed KTX textures
                            Debug.LogErrorFormat("Unsupported embed image format {0}",imageFormats[i]);
                        }
                        // TODO: jobify (if Unity allows LoadImage to be off the main thread)
                        bool forceSampleLinear = imageGamma!=null && !imageGamma[i];
                        var txt = CreateEmptyTexture(img,i,forceSampleLinear);
                        txt.LoadImage(data);
                        images[i] = txt;
                    } else {
                        ImageFormat imgFormat;
                        if(imageFormats[i]==ImageFormat.Unknown) {
                            if(string.IsNullOrEmpty(img.mimeType)) {
                                imgFormat = GetImageFormatFromPath(img.uri);
                            } else {
                                imgFormat = GetImageFormatFromMimeType(img.mimeType);
                            }
                            imageFormats[i] = imgFormat;
                        } else {
                            imgFormat=imageFormats[i];
                        }

                        if (imgFormat!=ImageFormat.Unknown) {
                            if (img.bufferView < 0) {
                                // Not Inside buffer
                                if(!string.IsNullOrEmpty(img.uri)) {
                                    LoadTexture(i,baseUri+img.uri,imgFormat==ImageFormat.KTX);
                                } else {
                                    Debug.LogError(ErrorMissingImageURL);
                                }
                            } 
                        } else {
                            Debug.LogErrorFormat("Unknown image format (image {0};uri:{1})",i,img.uri);
                        }
                    }
                }
            }

            Profiler.EndSample();
        }

        IEnumerator WaitForBufferDownloads() {
            if(downloads!=null) {
                foreach( var downloadPair in downloads ) {
                    var download = downloadPair.Value;
                    yield return download;
                    if (download.success) {
                        Profiler.BeginSample("GetData");
                        buffers[downloadPair.Key] = download.data;
                        Profiler.EndSample();
                    } else {
                        Debug.LogError(download.error);
                    }
                }
            }

            Profiler.BeginSample("CreateGlbBinChungs");
            for( int i=0; i<buffers.Length; i++ ) {
                if(i==0 && glbBinChunk.HasValue) {
                    // Already assigned in LoadGlb
                    continue;
                }
                var b = buffers[i];
                binChunks[i] = new GlbBinChunk(0,(uint) b.Length);
            }
            Profiler.EndSample();
        }

        IEnumerator WaitForTextureDownloads() {
            if(textureDownloads!=null) {
                foreach( var dl in textureDownloads ) {
                    yield return dl.Value;
                    var www = dl.Value;
                    if(www.success) {
                        if(imageFormats[dl.Key]==ImageFormat.KTX) {
#if KTX_UNITY
                            if(ktxLoadContexts==null) {
                                ktxLoadContexts = new List<KtxLoadContextBase>();
                            }
                            var ktxContext = new KtxLoadContext(dl.Key,www.data);
                            ktxLoadContexts.Add(ktxContext);
#else
                            Debug.LogError(ErrorKtxUnsupported);
#endif // KTX_UNITY
                        } else {
                            bool forceSampleLinear = imageGamma!=null && !imageGamma[dl.Key];
                            Texture2D txt;
                            if(forceSampleLinear) {
                                txt = CreateEmptyTexture(gltfRoot.images[dl.Key], dl.Key, forceSampleLinear);
                                txt.LoadImage(www.data);
                            } else {
                                txt = (www as ITextureDownload).texture;
                            }
                            images[dl.Key] = txt;
                        }
                    } else {
                        Debug.LogError(www.error);
                    }
                }
            }
        }


#if KTX_UNITY
        public IEnumerator WaitForKtxTextures() {
            if(ktxLoadContexts==null) yield break;
            foreach (var ktx in ktxLoadContexts)
            {
                bool forceSampleLinear = imageGamma!=null && !imageGamma[ktx.imageIndex];
                yield return ktx.LoadKtx(forceSampleLinear);
                images[ktx.imageIndex] = ktx.texture;
            }
            ktxLoadContexts.Clear();
        }
#endif // KTX_UNITY

        public bool InstantiateGltf( Transform parent ) {
            CreateGameObjects( gltfRoot, parent );
            return !loadingError;
        }

        Dictionary<int,IDownload> downloads;
        Dictionary<int,IDownload> textureDownloads;

        void LoadBuffer( int index, string url ) {
            if(downloads==null) {
                downloads = new Dictionary<int,IDownload>();
            }
            downloads[index] = downloadProvider.Request(url);
        }

        byte[] DecodeEmbedBuffer(string encodedBytes) {
            string tmp;
            return DecodeEmbedBuffer(encodedBytes,out tmp);
        }

        byte[] DecodeEmbedBuffer(string encodedBytes, out string mimeType) {
            Profiler.BeginSample("DecodeEmbedBuffer");
            mimeType = null;
            Debug.LogWarning("JSON embed buffers are slow! consider using glTF binary");
            var mediaTypeEnd = encodedBytes.IndexOf(';',5,Math.Min(encodedBytes.Length-5,1000) );
            if(mediaTypeEnd<0) {
                Profiler.EndSample();
                return null;
            }
            mimeType = encodedBytes.Substring(5,mediaTypeEnd-5);
            var tmp = encodedBytes.Substring(mediaTypeEnd+1,7);
            if(tmp!="base64,") {
                Profiler.EndSample();
                return null;
            }
            var data = System.Convert.FromBase64String(encodedBytes.Substring(mediaTypeEnd+8));
            Profiler.EndSample();
            return data;
        }

        void LoadTexture( int index, string url, bool isKtx ) {

            Profiler.BeginSample("LoadTexture");

            if(textureDownloads==null) {
                textureDownloads = new Dictionary<int,IDownload>();
            }
            IDownload download;
            if(isKtx) {
#if KTX_UNITY
                download = downloadProvider.Request(url);
#else
                Debug.LogError(ErrorKtxUnsupported);
                Profiler.EndSample();
                return;
#endif // KTX_UNITY
            } else {
                download = downloadProvider.RequestTexture(url);
            }
            textureDownloads[index] = download;
            Profiler.EndSample();
        }

        bool LoadGlb( byte[] bytes, string url ) {
            Profiler.BeginSample("LoadGlb");
            uint magic = BitConverter.ToUInt32( bytes, 0 );

            if (magic != GLB_MAGIC) {
                loadingError = true;
                Profiler.EndSample();
                return false;
            }

            uint version = BitConverter.ToUInt32( bytes, 4 );
            //uint length = BitConverter.ToUInt32( bytes, 8 );

            //Debug.Log( string.Format("version: {0:X}; length: {1}", version, length ) );

            if (version != 2) {
                loadingError = true;
                Profiler.EndSample();
                return false;
            }

            int index = 12; // first chunk header

            var baseUri = GetUriBase(url);

            while( index < bytes.Length ) {
                uint chLength = BitConverter.ToUInt32( bytes, index );
                index += 4;
                uint chType = BitConverter.ToUInt32( bytes, index );
                index += 4;

                //Debug.Log( string.Format("chunk: {0:X}; length: {1}", chType, chLength) );

                if (chType == (uint)ChunkFormat.BIN) {
                    //Debug.Log( string.Format("chunk: BIN; length: {0}", chLength) );
                    Assert.IsFalse(glbBinChunk.HasValue); // There can only be one binary chunk
                    glbBinChunk = new GlbBinChunk( index, chLength);
                }
                else if (chType == (uint)ChunkFormat.JSON) {
                    Assert.IsNull(gltfRoot);

                    Profiler.BeginSample("GetJSON");
                    string json = System.Text.Encoding.UTF8.GetString(bytes, index, (int)chLength );
                    //Debug.Log( string.Format("chunk: JSON; length: {0}", json ) );
                    Profiler.EndSample();

                    Profiler.BeginSample("ParseJSON");
                    ParseJsonAndLoadBuffers(json,baseUri);
                    Profiler.EndSample();

                    if(loadingError) {
                        Profiler.EndSample();
                        return false;
                    }
                }
 
                index += (int) chLength;
            }

            //Debug.Log(index);
            if(gltfRoot!=null) {
                //Debug.Log(gltf);
                if(glbBinChunk.HasValue) {
                    binChunks[0] = glbBinChunk.Value;
                    buffers[0] = bytes;
                }
                LoadImages(baseUri);
                Profiler.EndSample();
                return !loadingError;
            } else {
                Debug.LogError("Invalid JSON chunk");
                loadingError = true;
            }
            Profiler.EndSample();
            return false;
        }

        byte[] GetBuffer(int index) {
            return buffers[index];
        }

        NativeSlice<byte> GetBufferView(BufferView bufferView) {
            int bufferIndex = bufferView.buffer;
            if(!nativeBuffers[bufferIndex].IsCreated) {
                nativeBuffers[bufferIndex] = new NativeArray<byte>(GetBuffer(bufferIndex),Allocator.Persistent);
            }
            var chunk = binChunks[bufferIndex];
            return new NativeSlice<byte>(nativeBuffers[bufferIndex],chunk.start+bufferView.byteOffset,bufferView.byteLength);
        }

        IEnumerator Prepare() {
            meshPrimitiveIndex = new int[gltfRoot.meshes.Length+1];

            resources = new List<UnityEngine.Object>();

            Profiler.BeginSample("CreateTexturesFromBuffers");
            if(gltfRoot.images!=null) {
                if(images==null) {
                    images = new Texture2D[gltfRoot.images.Length];
                } else {
                    Assert.AreEqual(images.Length,gltfRoot.images.Length);
                }
                imageCreateContexts = new List<ImageCreateContext>();
                CreateTexturesFromBuffers(gltfRoot.images,gltfRoot.bufferViews,imageCreateContexts);
            }
            Profiler.EndSample();
            yield return null;

            LoadAccessorData(gltfRoot);
            yield return null;

            while(!accessorJobsHandle.IsCompleted) {
                yield return null;
            }
            accessorJobsHandle.Complete();
            foreach(var ad in accessorData) {
                if(ad!=null) {
                    ad.Unpin();
                }
            }

            CreatePrimitiveContexts(gltfRoot);
            yield return null;

#if KTX_UNITY
            if(ktxLoadContextsBuffer!=null) {

                for (int i = 0; i < ktxLoadContextsBuffer.Count; i++)
                {
                    var ktx = ktxLoadContextsBuffer[i];
                    bool forceSampleLinear = imageGamma!=null && !imageGamma[ktx.imageIndex];
                    var ktxRoutine = ktx.LoadKtx(forceSampleLinear);
                    while(ktxRoutine.MoveNext()) {
                        yield return null;
                    }
                    images[ktx.imageIndex] = ktx.texture;
                }
                ktxLoadContextsBuffer.Clear();
            }
#endif // KTX_UNITY

            if(imageCreateContexts!=null) {
                foreach(var jh in imageCreateContexts) {
                    while(!jh.jobHandle.IsCompleted) {
                        yield return null;
                    }
                    jh.jobHandle.Complete();
                    images[jh.imageIndex].LoadImage(jh.buffer);
                    jh.gcHandle.Free();
                }
                imageCreateContexts = null;
            }

            Dictionary<int,Texture2D>[] imageVariants = null;
            if(images!=null && gltfRoot.textures!=null) {
                imageVariants = new Dictionary<int,Texture2D>[images.Length];
                for (int textureIndex = 0; textureIndex < gltfRoot.textures.Length; textureIndex++)
                {
                    var txt = gltfRoot.textures[textureIndex];
                    var imageIndex = txt.GetImageIndex();
                    var img = images[imageIndex];
                    if(imageVariants[imageIndex]==null) {
                        if(txt.sampler>=0) {
                            gltfRoot.samplers[txt.sampler].Apply(img);
                        }
                        imageVariants[imageIndex] = new Dictionary<int, Texture2D>();
                        imageVariants[imageIndex][txt.sampler] = img;
                    } else 
                    if(!imageVariants[imageIndex].ContainsKey(txt.sampler)) {
                        var newImg = Texture2D.Instantiate(img);
                        resources.Add(newImg);
#if DEBUG
                        newImg.name = string.Format("{0}_sampler{1}",img.name,txt.sampler);
                        Debug.LogWarningFormat("Have to create copy of image {0} due to different samplers. This is harmless, but requires more memory.", imageIndex);
#endif
                        if(txt.sampler>=0) {
                            gltfRoot.samplers[txt.sampler].Apply(newImg);
                        }
                        imageVariants[imageIndex][txt.sampler] = newImg;
                    }
                }
            }

            Profiler.BeginSample("GenerateMaterial");
            if(gltfRoot.materials!=null) {
                materials = new UnityEngine.Material[gltfRoot.materials.Length];
                for(int i=0;i<materials.Length;i++) {
                    materials[i] = materialGenerator.GenerateMaterial(
                        gltfRoot.materials[i],
                        ref gltfRoot.textures,
                        ref gltfRoot.images,
                        ref imageVariants
                        );
                }
            }
            Profiler.EndSample();
            yield return null;

            for(int i=0;i<primitiveContexts.Length;i++) {
                var primitiveContext = primitiveContexts[i];
                if(primitiveContext==null) continue;
                while(!primitiveContext.IsCompleted) {
                    yield return null;
                }
                yield return null;
            }
            AssignAllAccessorData(gltfRoot);

            for(int i=0;i<primitiveContexts.Length;i++) {
                var primitiveContext = primitiveContexts[i];
                while(!primitiveContext.IsCompleted) {
                    yield return null;
                }
                var primitive = primitiveContext.CreatePrimitive();
                if(primitive.HasValue) {
                    primitives[primitiveContext.primtiveIndex] = primitive.Value;
                    resources.Add(primitive.Value.mesh);
                } else {
                    loadingError = true;
                }

                yield return null;
            }

            foreach(var ad in accessorData) {
                if(ad!=null) {
                    ad.Dispose();
                }
            }
            accessorData = null;
        }

        /// <summary>
        /// Free up volatile loading resources
        /// </summary>
        void DisposeVolatileData() {

            if (vertexAttributes != null) {
                foreach (var vac in vertexAttributes.Values) {
                    vac.Dispose();
                }
            }
            vertexAttributes = null;
            
            primitiveContexts = null;

            if(nativeBuffers!=null) {
                foreach (var nativeBuffer in nativeBuffers)
                {
                    if(nativeBuffer.IsCreated) {
                        nativeBuffer.Dispose();
                    }
                }
            }
            nativeBuffers = null;
            buffers = null;
            binChunks = null;

            accessorData = null;
            accessorUsage = null;
            primitiveContexts = null;
            meshPrimitiveCluster = null;
            imageCreateContexts = null;
            images = null;
            imageFormats = null;
            imageGamma = null;
            glbBinChunk = null;
        }

        void CreateGameObjects( Root gltf, Transform parent ) {

            Profiler.BeginSample("CreateGameObjects");
            var nodes = new Transform[gltf.nodes.Length];
            var relations = new Dictionary<uint,uint>();

            for( uint nodeIndex = 0; nodeIndex < gltf.nodes.Length; nodeIndex++ ) {
                var node = gltf.nodes[nodeIndex];

                if( node.children==null && node.mesh<0 ) {
                    continue;
                }

                var goName = node.name;
                var go = new GameObject();
                nodes[nodeIndex] = go.transform;

                if(node.children!=null) {
                    foreach( var child in node.children ) {
                        relations[child] = nodeIndex;
                    }
                }

                if(node.matrix!=null) {
                    Matrix4x4 m = new Matrix4x4();
                    m.m00 = node.matrix[0];
                    m.m10 = node.matrix[1];
                    m.m20 = -node.matrix[2];
                    m.m30 = node.matrix[3];
                    m.m01 = node.matrix[4];
                    m.m11 = node.matrix[5];
                    m.m21 = -node.matrix[6];
                    m.m31 = node.matrix[7];
                    m.m02 = -node.matrix[8];
                    m.m12 = -node.matrix[9];
                    m.m22 = node.matrix[10];
                    m.m32 = node.matrix[11];
                    m.m03 = node.matrix[12];
                    m.m13 = node.matrix[13];
                    m.m23 = -node.matrix[14];
                    m.m33 = node.matrix[15];

                    if(m.ValidTRS()) {
                        go.transform.localPosition = new Vector3( m.m03, m.m13, m.m23 );
                        go.transform.localRotation = m.rotation;
                        go.transform.localScale = m.lossyScale;
                    } else {
                        Debug.LogErrorFormat("Invalid matrix on node {0}",nodeIndex);
                        Profiler.EndSample();
                        loadingError = true;
                        return;
                    }
                } else {
                    if(node.translation!=null) {
                        Assert.AreEqual( node.translation.Length, 3 );
                        go.transform.localPosition = new Vector3(
                            node.translation[0],
                            node.translation[1],
                            -node.translation[2]
                        );
                    }
                    if(node.rotation!=null) {
                        Assert.AreEqual( node.rotation.Length, 4 );
                        go.transform.localRotation = new Quaternion(
                            -node.rotation[0],
                            -node.rotation[1],
                            node.rotation[2],
                            node.rotation[3]
                        );
                    }
                    if(node.scale!=null) {
                        Assert.AreEqual( node.scale.Length, 3 );
                        go.transform.localScale = new Vector3(
                            node.scale[0],
                            node.scale[1],
                            node.scale[2]
                        );
                    }
                }

                if(node.mesh>=0) {
                    int end = meshPrimitiveIndex[node.mesh+1];
                    GameObject meshGo = null;
                    for( int i=meshPrimitiveIndex[node.mesh]; i<end; i++ ) {
                        var mesh = primitives[i].mesh;
                        var meshName = string.IsNullOrEmpty(mesh.name) ? null : mesh.name;
                        if(meshGo==null) {
                            meshGo = go;
                            goName = goName ?? meshName;
                        } else {
                            meshGo = new GameObject( meshName ?? "Primitive" );
                            meshGo.transform.SetParent(go.transform,false);
                        }
                        var mf = meshGo.AddComponent<MeshFilter>();
                        mf.mesh = mesh;
                        var mr = meshGo.AddComponent<MeshRenderer>();
                        
                        var primMaterials = new UnityEngine.Material[primitives[i].materialIndices.Length];
                        for (int m = 0; m < primitives[i].materialIndices.Length; m++)
                        {
                            var materialIndex = primitives[i].materialIndices[m];
                            if( materials!=null && materialIndex>=0 && materialIndex<materials.Length ) {
                                primMaterials[m] = materials[materialIndex];
                            } else {
                                primMaterials[m] = materialGenerator.GetPbrMetallicRoughnessMaterial();
                            }
                        }
                        mr.sharedMaterials = primMaterials;
                    }
                }

                go.name = goName ?? "Node";
            }

            foreach( var rel in relations ) {
                if (nodes[rel.Key] != null) {
                    nodes[rel.Key].SetParent( nodes[rel.Value], false );
                }
            }

            foreach(var scene in gltf.scenes) {
                var go = new GameObject(scene.name ?? "Scene");
                go.transform.SetParent( parent, false);

                foreach(var nodeIndex in scene.nodes) {
                    if (nodes[nodeIndex] != null) {
                        nodes[nodeIndex].SetParent( go.transform, false );
                    }
                }
            }

            Profiler.EndSample();
        }

        unsafe void CreateTexturesFromBuffers( Schema.Image[] src_images, Schema.BufferView[] bufferViews, List<ImageCreateContext> contexts ) {
            for (int i = 0; i < images.Length; i++) {
                if(images[i]!=null) {
                    resources.Add(images[i]);
                }
                var img = src_images[i];
                ImageFormat imgFormat = imageFormats[i];
                if(imgFormat==ImageFormat.Unknown) {
                    if(string.IsNullOrEmpty(img.mimeType)) {
                        // Image is missing mime type
                        // try to determine type by file extension
                        imgFormat = GetImageFormatFromPath(img.uri);
                    } else {
                        imgFormat = GetImageFormatFromMimeType(img.mimeType);
                    }
                }

                if (imgFormat!=ImageFormat.Unknown) {
                    if (img.bufferView >= 0) {
                        var bufferView = bufferViews[img.bufferView];
                        
                        if(imgFormat == ImageFormat.KTX) {
#if KTX_UNITY
                            if(ktxLoadContextsBuffer==null) {
                                ktxLoadContextsBuffer = new List<KtxLoadContextBase>();
                            }
                            var ktxContext = new KtxLoadNativeContext(i,GetBufferView(bufferView));
                            ktxLoadContextsBuffer.Add(ktxContext);
#else
                            Debug.LogError(ErrorKtxUnsupported);
#endif // KTX_UNITY
                        } else {
                            var buffer = GetBuffer(bufferView.buffer);
                            var chunk = binChunks[bufferView.buffer];

                            bool forceSampleLinear = imageGamma!=null && !imageGamma[i];
                            var txt = CreateEmptyTexture(img,i,forceSampleLinear);
                            var icc = new ImageCreateContext();
                            icc.imageIndex = i;
                            icc.buffer = new byte[bufferView.byteLength];
                            icc.gcHandle = GCHandle.Alloc(icc.buffer,GCHandleType.Pinned);
#if !COPY_LEGACY
                            var job = new Jobs.MemCopyJob();
                            job.bufferSize = bufferView.byteLength;
                            fixed( void* src = &(buffer[bufferView.byteOffset + chunk.start]), dst = &(icc.buffer[0]) ) {
                                job.input = src;
                                job.result = dst;
                            }
                            icc.jobHandle = job.Schedule();
#else
                            var job = new Jobs.MemCopyLegacyJob();
                            fixed( void* src = &(buffer[bufferView.byteOffset + chunk.start]), dst = &(icc.buffer[0]) ) {
                                job.input = (byte*)src;
                                job.result = (byte*)dst;
                            }
                            icc.jobHandle = job.Schedule(bufferView.byteLength,DefaultBatchCount);
#endif
                            contexts.Add(icc);
                            
                            images[i] = txt;
                            resources.Add(txt);
                        }
                    }
                }
            }
        }

        Texture2D CreateEmptyTexture(Schema.Image img, int index, bool forceSampleLinear) {
            Texture2D txt;
            if(forceSampleLinear) {
                txt = new Texture2D(4,4,GraphicsFormat.R8G8B8A8_UNorm,TextureCreationFlags.MipChain);
            } else {
                txt = new UnityEngine.Texture2D(4, 4);
            }
            txt.name = string.IsNullOrEmpty(img.name) ? string.Format("image_{0}",index) : img.name;
            return txt;
        }

        public void Destroy() {
            if(materials!=null) {
                foreach( var material in materials ) {
                    UnityEngine.Object.Destroy(material);
                }
                materials = null;
            }

            if(resources!=null) {
                foreach( var resource in resources ) {
                    UnityEngine.Object.Destroy(resource);
                }
                resources = null;
            }
        }

        void LoadAccessorData( Root gltf ) {

            Profiler.BeginSample("LoadAccessorData");

            vertexAttributes = new Dictionary<Attributes,VertexBufferConfigBase>();
            meshPrimitiveCluster = new Dictionary<Attributes,List<MeshPrimitive>>[gltf.meshes.Length];

            /// Iterate over all primitive vertex attributes and remember the accessors usage.
            accessorUsage = new AccessorUsage[gltf.accessors.Length];
            int totalPrimitives = 0;
            for (int meshIndex = 0; meshIndex < gltf.meshes.Length; meshIndex++)
            {
                var mesh = gltf.meshes[meshIndex];
                meshPrimitiveIndex[meshIndex] = totalPrimitives;
                var cluster = new Dictionary<Attributes, List<MeshPrimitive>>();
                
                foreach(var primitive in mesh.primitives) {
                    
                    if(!cluster.ContainsKey(primitive.attributes)) {
                        cluster[primitive.attributes] = new List<MeshPrimitive>();
                    }
                    cluster[primitive.attributes].Add(primitive);
#if DRACO_UNITY
                    var isDraco = primitive.isDracoCompressed;
                    if(isDraco) continue;
#else
                    var isDraco = false;
#endif
                    var att = primitive.attributes;
                    if(primitive.indices>=0) {
                        var usage = (
                            primitive.mode == DrawMode.Triangles
                            || primitive.mode == DrawMode.TriangleStrip
                            || primitive.mode == DrawMode.TriangleFan
                            )
                        ? AccessorUsage.IndexFlipped
                        : AccessorUsage.Index;
                        SetAccessorUsage(primitive.indices, isDraco ? AccessorUsage.Ignore : usage );
                    }

                    VertexBufferConfigBase config;
                    if(!vertexAttributes.TryGetValue(att,out config)) {
                        if(att.TANGENT>=0) {
                            config = new VertexBufferConfig<Vertex.VPosNormTan>();
                        } else
                        if(att.NORMAL>=0) {
                            config = new VertexBufferConfig<Vertex.VPosNorm>();
                        } else {
                            config = new VertexBufferConfig<Vertex.VPos>();
                        }
                        vertexAttributes[primitive.attributes] = config;
                    }
#if DEBUG
                    config.meshIndices.Add(meshIndex);
#endif
                }
                meshPrimitiveCluster[meshIndex] = cluster;
                totalPrimitives += cluster.Count;
            }

            meshPrimitiveIndex[gltf.meshes.Length] = totalPrimitives;
            primitives = new Primitive[totalPrimitives];
            primitiveContexts = new PrimitiveCreateContextBase[totalPrimitives];
            var tmpList = new List<JobHandle>(vertexAttributes.Count);
            
            foreach(var attributeConfig in vertexAttributes) {
#if DEBUG
                if(attributeConfig.Value.meshIndices.Count>1) {
                    Debug.LogWarning(@"glTF file uses certain vertex attributes/accessors across multiple meshes!
                    This may result in low performance and high memory usage. Try optimizing the glTF file.
                    See details in corresponding issue at https://github.com/atteneder/glTFast/issues/52");
                    break;
                }
                attributeConfig.Value.meshIndices = null;
#endif

                var att = attributeConfig.Key;

                var posInput = GetAccessorParams(gltf,att.POSITION);
                VertexInputData? nrmInput = null;
                VertexInputData? tanInput = null;
                if (att.NORMAL >= 0) {
                    nrmInput = GetAccessorParams(gltf,att.NORMAL);
                }
                if (att.TANGENT >= 0) {
                    tanInput = GetAccessorParams(gltf,att.TANGENT);
                }

                VertexInputData[] uvInputs = null;
                if (att.TEXCOORD_0 >= 0) {
                    int uvCount = 1;
                    if (att.TEXCOORD_1 >= 0) uvCount++;
                    uvInputs = new VertexInputData[uvCount];
                    uvInputs[0] = GetAccessorParams(gltf, att.TEXCOORD_0);
                    if (att.TEXCOORD_1 >= 0) {
                        uvInputs[1] = GetAccessorParams(gltf, att.TEXCOORD_1);
                    }
                }
                VertexInputData? colorInput = null;
                if (att.COLOR_0 >= 0) {
                    colorInput = GetAccessorParams(gltf, att.COLOR_0);
                }

                var jh = attributeConfig.Value.ScheduleVertexJobs(posInput,nrmInput,tanInput,uvInputs,colorInput);
                if (jh.HasValue) {
                    tmpList.Add(jh.Value);
                } else {
                    loadingError = true;
                }
            }

            /// Retrieve indices data jobified
            accessorData = new AccessorDataBase[gltf.accessors.Length];

            for(int i=0; i<accessorData.Length; i++) {
                var acc = gltf.accessors[i];
                if(acc.bufferView<0) {
                    // Not actual accessor to data
                    // Common for draco meshes
                    // the accessor only holds meta information
                    continue;
                }
                if (acc.typeEnum==GLTFAccessorAttributeType.SCALAR
                    &&( accessorUsage[i]==AccessorUsage.IndexFlipped ||
                        accessorUsage[i]==AccessorUsage.Index )
                    )
                {
                    JobHandle? jh;
                    var ads = new  AccessorData<int>();
                    GetIndicesJob(gltf,i,out ads.data, out jh, out ads.gcHandle, accessorUsage[i]==AccessorUsage.IndexFlipped);
                    tmpList.Add(jh.Value);
                    accessorData[i] = ads;
                }
            }

            int primitiveIndes=0;
            for( int meshIndex = 0; meshIndex<gltf.meshes.Length; meshIndex++ ) {
                var mesh = gltf.meshes[meshIndex];
                foreach( var cluster in meshPrimitiveCluster[meshIndex].Values) {

                    PrimitiveCreateContextBase context = null;

                    for (int primIndex = 0; primIndex < cluster.Count; primIndex++) {
                        var primitive = cluster[primIndex];
#if DRACO_UNITY
                        if (primitive.isDracoCompressed) {
                            context = new PrimitiveDracoCreateContext();
                            context.materials = new int[1];
                        }
                        else
#endif
                        {
                            PrimitiveCreateContext c;
                            if(context==null) {
                                c = new PrimitiveCreateContext();
                                c.indices = new int[cluster.Count][];
                                c.materials = new int[cluster.Count];
                            } else {
                                c = (context as PrimitiveCreateContext);
                            }
                            // PreparePrimitiveIndices(gltf,mesh,primitive,ref c,primIndex);
                            context = c;
                        }
                        context.primtiveIndex = primitiveIndes;
                        context.materials[primIndex] = primitive.material;

                        context.needsNormals |= primitive.material<0 || gltf.materials[primitive.material].requiresNormals;
                        context.needsTangents |= primitive.material>=0 && gltf.materials[primitive.material].requiresTangents;
                    }

                    primitiveContexts[primitiveIndes] = context;
                    primitiveIndes++;
                }
            }
            
            NativeArray<JobHandle> jobHandles = new NativeArray<JobHandle>(tmpList.ToArray(), Allocator.Temp);
            accessorJobsHandle = JobHandle.CombineDependencies(jobHandles);
            jobHandles.Dispose();
            JobHandle.ScheduleBatchedJobs();

            Profiler.EndSample();
        }

        void SetAccessorUsage(int index, AccessorUsage newUsage) {
#if UNITY_EDITOR
            if(accessorUsage[index]!=AccessorUsage.Unknown && newUsage!=accessorUsage[index]) {
                Debug.LogErrorFormat("Inconsistent accessor usage {0} != {1}", accessorUsage[index], newUsage);
            }
#endif
            accessorUsage[index] = newUsage;
        }

        void CreatePrimitiveContexts( Root gltf ) {
            Profiler.BeginSample("CreatePrimitiveContexts");

            int i=0;
            for( int meshIndex = 0; meshIndex<gltf.meshes.Length; meshIndex++ ) {
                var mesh = gltf.meshes[meshIndex];
                foreach( var kvp in meshPrimitiveCluster[meshIndex]) {
                    var cluster = kvp.Value;
                    PrimitiveCreateContextBase context = primitiveContexts[i];

                    for (int primIndex = 0; primIndex < cluster.Count; primIndex++) {
                        var primitive = cluster[primIndex];
#if DRACO_UNITY
                        if( primitive.isDracoCompressed ) {
                            var c = (PrimitiveDracoCreateContext) context;
                            PreparePrimitiveDraco(gltf,mesh,primitive,ref c);
                        } else
#endif
                        {
                            PrimitiveCreateContext c = (PrimitiveCreateContext) context;
                            c.vertexData = vertexAttributes[kvp.Key];
                            PreparePrimitiveIndices(gltf,mesh,primitive,ref c,primIndex);
                        }
                    }
                    i++;
                }
            }
            Profiler.EndSample();
        }

        void AssignAllAccessorData( Root gltf ) {
            Profiler.BeginSample("AssignAllAccessorData");
            int i=0;
            for( int meshIndex = 0; meshIndex<gltf.meshes.Length; meshIndex++ ) {
                var mesh = gltf.meshes[meshIndex];

                foreach( var cluster in meshPrimitiveCluster[meshIndex]) {
#if DRACO_UNITY
                    if( !cluster.Value[0].isDracoCompressed )
#endif
                    {
                        // Create one PrimitiveCreateContext per Primitive cluster
                        PrimitiveCreateContext c = (PrimitiveCreateContext) primitiveContexts[i];
                        AssignAccessorData(gltf,mesh,cluster.Key,ref c);
                    }
                    i++;
                }
            }
            Profiler.EndSample();
        }

        void PreparePrimitiveIndices( Root gltf, Mesh mesh, MeshPrimitive primitive, ref PrimitiveCreateContext c, int submeshIndex = 0 ) {
            Profiler.BeginSample("PreparePrimitiveIndices");
            switch(primitive.mode) {
            case DrawMode.Triangles:
                c.topology = MeshTopology.Triangles;
                break;
            case DrawMode.Points:
                Debug.LogErrorFormat(ErrorUnsupportedPrimitiveMode,primitive.mode);
                c.topology = MeshTopology.Points;
                break;
            case DrawMode.Lines:
                Debug.LogErrorFormat(ErrorUnsupportedPrimitiveMode,primitive.mode);
                c.topology = MeshTopology.Lines;
                break;
            case DrawMode.LineLoop:
                Debug.LogErrorFormat(ErrorUnsupportedPrimitiveMode,primitive.mode);
                c.topology = MeshTopology.LineStrip;
                break;
            case DrawMode.LineStrip:
                c.topology = MeshTopology.LineStrip;
                break;
            case DrawMode.TriangleStrip:
            case DrawMode.TriangleFan:
            default:
                Debug.LogErrorFormat(ErrorUnsupportedPrimitiveMode,primitive.mode);
                c.topology = MeshTopology.Triangles;
                break;
            }

            if(primitive.indices >= 0) {
                c.indices[submeshIndex] = (accessorData[primitive.indices] as AccessorData<int>).data;
            } else {
                int vertexCount = gltf.accessors[primitive.attributes.POSITION].count;
                JobHandle? jh;
                CalculateIndicesJob(gltf,primitive, vertexCount, c.topology, out c.indices[submeshIndex], out jh, out c.calculatedIndicesHandle );
                c.jobHandle = jh.Value;
            }
            Profiler.EndSample();
        }

        unsafe void AssignAccessorData( Root gltf, Mesh mesh, Attributes attributes, ref PrimitiveCreateContext c ) {

            Profiler.BeginSample("AssignAccessorData");
            c.mesh = mesh;

            // int vertexCount;
            {
                // c.positions = (accessorData[attributes.POSITION] as AccessorNativeData<Vector3>).data;
                // vertexCount = c.positions.Length;
            }

            if(attributes.NORMAL>=0) {
                // c.normals = (accessorData[attributes.NORMAL] as AccessorNativeData<Vector3>).data;
            }
            
            if(attributes.TEXCOORD_0>=0) {
                // c.uvs0 = (accessorData[attributes.TEXCOORD_0] as AccessorNativeData<Vector2>).data;
            }

            if(attributes.TANGENT>=0) {
                // c.tangents = (accessorData[attributes.TANGENT] as AccessorNativeData<Vector4>).data;
            }

            if(attributes.COLOR_0>=0) {
                if(IsColorAccessorByte(gltf.accessors[attributes.COLOR_0])) {
                    // c.colors32 = (accessorData[attributes.COLOR_0] as AccessorNativeData<Color32>).data;
                } else {
                    // c.colors = (accessorData[attributes.COLOR_0] as AccessorNativeData<Color>).data;
                }
            }

            Profiler.EndSample();
        }

#if DRACO_UNITY
        void PreparePrimitiveDraco( Root gltf, Mesh mesh, MeshPrimitive primitive, ref PrimitiveDracoCreateContext c ) {
            var draco_ext = primitive.extensions.KHR_draco_mesh_compression;
            
            var bufferView = gltf.bufferViews[draco_ext.bufferView];
            var buffer = GetBufferView(bufferView);

            var job = new DracoMeshLoader.DracoJob();

            c.dracoResult = new NativeArray<int>(1,DracoMeshLoader.defaultAllocator);
            c.dracoPtr = new NativeArray<IntPtr>(1,DracoMeshLoader.defaultAllocator);

            job.data = buffer;
            job.result = c.dracoResult;
            job.outMesh = c.dracoPtr;

            c.jobHandle = job.Schedule();
        }
#endif

        void OnMeshesLoaded( Mesh mesh ) {
            Debug.Log("draco is ready");
        }

        unsafe void CalculateIndicesJob(Root gltf, MeshPrimitive primitive, int vertexCount, MeshTopology topology, out int[] indices, out JobHandle? jobHandle, out GCHandle resultHandle ) {
            Profiler.BeginSample("CalculateIndicesJob");
            // No indices: calculate them
            bool lineLoop = primitive.mode == DrawMode.LineLoop;
            // extra index (first vertex again) for closing line loop
            indices = new int[vertexCount+(lineLoop?1:0)];
            resultHandle = GCHandle.Alloc(indices, GCHandleType.Pinned);
            if(topology == MeshTopology.Triangles) {
                var job8 = new Jobs.CreateIndicesFlippedJob();
                fixed( void* dst = &(indices[0]) ) {
                    job8.result = (int*)dst;
                }
                jobHandle = job8.Schedule(indices.Length,DefaultBatchCount);
            } else {
                var job8 = new Jobs.CreateIndicesJob();
                if(lineLoop) {
                    // Set the last index to the first vertex
                    indices[vertexCount] = 0;
                }
                fixed( void* dst = &(indices[0]) ) {
                    job8.result = (int*)dst;
                }
                jobHandle = job8.Schedule(vertexCount,DefaultBatchCount);
            }
            Profiler.EndSample();
        }

        unsafe void GetIndicesJob(Root gltf, int accessorIndex, out int[] indices, out JobHandle? jobHandle, out GCHandle resultHandle, bool flip) {
            Profiler.BeginSample("PrepareGetIndicesJob");
            // index
            var accessor = gltf.accessors[accessorIndex];
            var bufferView = gltf.bufferViews[accessor.bufferView];
            int bufferIndex = bufferView.buffer;
            var buffer = GetBuffer(bufferIndex);

            Profiler.BeginSample("Alloc");
            indices = new int[accessor.count];
            Profiler.EndSample();
            Profiler.BeginSample("Pin");
            resultHandle = GCHandle.Alloc(indices, GCHandleType.Pinned);
            Profiler.EndSample();

            var chunk = binChunks[bufferIndex];
            Assert.AreEqual(accessor.typeEnum, GLTFAccessorAttributeType.SCALAR);
            //Assert.AreEqual(accessor.count * GetLength(accessor.typeEnum) * 4 , (int) chunk.length);
            var start = accessor.byteOffset + bufferView.byteOffset + chunk.start;

            Profiler.BeginSample("CreateJob");
            switch( accessor.componentType ) {
            case GLTFComponentType.UnsignedByte:
                if(flip) {
                    var job8 = new Jobs.GetIndicesUInt8FlippedJob();
                    fixed( void* src = &(buffer[start]), dst = &(indices[0]) ) {
                        job8.input = (byte*)src;
                        job8.result = (int*)dst;
                    }
                    jobHandle = job8.Schedule(accessor.count/3,DefaultBatchCount);
                } else {
                    var job8 = new Jobs.GetIndicesUInt8Job();
                    fixed( void* src = &(buffer[start]), dst = &(indices[0]) ) {
                        job8.input = (byte*)src;
                        job8.result = (int*)dst;
                    }
                    jobHandle = job8.Schedule(accessor.count,DefaultBatchCount);
                }
                break;
            case GLTFComponentType.UnsignedShort:
                if(flip) {
                    var job16 = new Jobs.GetIndicesUInt16FlippedJob();
                    fixed( void* src = &(buffer[start]), dst = &(indices[0]) ) {
                        job16.input = (ushort*) src;
                        job16.result = (int*) dst;
                    }
                    jobHandle = job16.Schedule(accessor.count/3,DefaultBatchCount);
                } else {
                    var job16 = new Jobs.GetIndicesUInt16Job();
                    fixed( void* src = &(buffer[start]), dst = &(indices[0]) ) {
                        job16.input = (ushort*) src;
                        job16.result = (int*) dst;
                    }
                    jobHandle = job16.Schedule(accessor.count,DefaultBatchCount);
                }
                break;
            case GLTFComponentType.UnsignedInt:
                if(flip) {
                    var job32 = new Jobs.GetIndicesUInt32FlippedJob();
                    fixed( void* src = &(buffer[start]), dst = &(indices[0]) ) {
                        job32.input = (uint*) src;
                        job32.result = (int*) dst;
                    }
                    jobHandle = job32.Schedule(accessor.count/3,DefaultBatchCount);
                } else {
                    var job32 = new Jobs.GetIndicesUInt32Job();
                    fixed( void* src = &(buffer[start]), dst = &(indices[0]) ) {
                        job32.input = (uint*) src;
                        job32.result = (int*) dst;
                    }
                    jobHandle = job32.Schedule(accessor.count,DefaultBatchCount);
                }
                break;
            default:
                Debug.LogErrorFormat( "Invalid index format {0}", accessor.componentType );
                jobHandle = null;
                break;
            }
            Profiler.EndSample();
            Profiler.EndSample();
        }

        VertexInputData GetAccessorParams(Root gltf, int accessorIndex) {
            var accessor = gltf.accessors[accessorIndex];
            var bufferView = gltf.bufferViews[accessor.bufferView];
            var bufferIndex = bufferView.buffer;
            var result = new VertexInputData {
                accessor = accessor,
                buffer = GetBuffer(bufferIndex),
                bufferView = bufferView,
                chunkStart = binChunks[bufferIndex].start
            };
            return result;
        }
 /*
        unsafe int GetVector3sJob(Root gltf, int accessorIndex, out JobHandle? jobHandle, out NativeArray<Vector3> result) {
            Profiler.BeginSample("PrepareGetVector3sJob");
            Assert.IsTrue(accessorIndex>=0);
            #if DEBUG
            Assert.AreEqual( GetAccessorTye(gltf.accessors[accessorIndex].typeEnum), typeof(Vector3) );
            #endif

            var accessor = gltf.accessors[accessorIndex];
            var bufferView = gltf.bufferViews[accessor.bufferView];
            var buffer = GetBuffer(bufferView.buffer);
            var chunk = binChunks[bufferView.buffer];
            int count = accessor.count;
            Profiler.BeginSample("Alloc");
            result = new NativeArray<Vector3>(count,Allocator.TempJob);
            Profiler.EndSample();
            var start = accessor.byteOffset + bufferView.byteOffset + chunk.start;
            if (gltf.IsAccessorInterleaved(accessorIndex)) {
                if(accessor.componentType == GLTFComponentType.Float) {
                    var job = new Jobs.GetVector3sInterleavedJob();
                    job.byteStride = bufferView.byteStride;
                    fixed( void* src = &(buffer[start]) ) {
                        job.input = (byte*)src;
                        job.result = (Vector3*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(result);
                    }
                    jobHandle = job.Schedule(count,DefaultBatchCount);
                } else
                if(accessor.componentType == GLTFComponentType.UnsignedShort) {
                    if (accessor.normalized) {
                        var job = new Jobs.GetUInt16PositionsInterleavedNormalizedJob();
                        job.byteStride = bufferView.byteStride;
                        fixed( void* src = &(buffer[start])) {
                            job.input = (byte*)src;
                            job.result = (Vector3*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(result);
                        }
                        jobHandle = job.Schedule(count,DefaultBatchCount);
                    } else {
                        var job = new Jobs.GetUInt16PositionsInterleavedJob();
                        job.inputByteStride = bufferView.byteStride;
                        fixed( void* src = &(buffer[start])) {
                            job.input = (byte*)src;
                            job.result = (Vector3*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(result);
                        }
                        jobHandle = job.Schedule(count,DefaultBatchCount);
                    }
                } else
                if(accessor.componentType == GLTFComponentType.Short) {
                    // TODO: test. did not have test files
                    if (accessor.normalized) {
                        var job = new Jobs.GetVector3FromInt16InterleavedNormalizedJob();
                        job.byteStride = bufferView.byteStride;
                        fixed( void* src = &(buffer[start]) ) {
                            job.input = (byte*)src;
                            job.result = (Vector3*) NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(result);
                        }
                        jobHandle = job.Schedule(count,DefaultBatchCount);
                    } else {
                        var job = new Jobs.GetVector3FromInt16InterleavedJob();
                        job.inputByteStride = bufferView.byteStride;
                        fixed( void* src = &(buffer[start]) ) {
                            job.input = (byte*)src;
                            job.result = (Vector3*) NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(result);
                        }
                        jobHandle = job.Schedule(count,DefaultBatchCount);
                    }
                } else
                if(accessor.componentType == GLTFComponentType.Byte) {
                    // TODO: test positions. did not have test files
                    if (accessor.normalized) {
                        var job = new Jobs.GetVector3FromSByteInterleavedNormalizedJob();
                        fixed( void* src = &(buffer[start]) ) {
                            job.Setup(bufferView.byteStride,(sbyte*)src,(Vector3*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(result));
                        }
                        jobHandle = job.Schedule(count,DefaultBatchCount);
                    } else {
                        var job = new Jobs.GetVector3FromSByteInterleavedJob();
                        fixed( void* src = &(buffer[start])) {
                            job.Setup(bufferView.byteStride,(sbyte*)src,(Vector3*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(result));
                        }
                        jobHandle = job.Schedule(count,DefaultBatchCount);
                    }
                } else
                if(accessor.componentType == GLTFComponentType.UnsignedByte) {
                    // TODO: test. did not have test files
                    if (accessor.normalized) {
                        var job = new Jobs.GetVector3FromByteInterleavedNormalizedJob();
                        fixed( void* src = &(buffer[start]) ) {
                            job.Setup(bufferView.byteStride,(byte*)src,(Vector3*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(result));
                        }
                        jobHandle = job.Schedule(count,DefaultBatchCount);
                    } else {
                        var job = new Jobs.GetVector3FromByteInterleavedJob();
                        fixed( void* src = &(buffer[start]) ) {
                            job.Setup(bufferView.byteStride,(byte*)src,(Vector3*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(result));
                        }
                        jobHandle = job.Schedule(count,DefaultBatchCount);
                    }
                } else {
                    Debug.LogError("Unknown componentType");
                    jobHandle = null;
                }
            } else {
                if(accessor.componentType == GLTFComponentType.Float) {
                    var job = new Jobs.GetVector3sJob();
                    fixed( void* src = &(buffer[start])) {
                        job.input = (float*)src;
                        job.result = (float*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(result);
                    }
                    jobHandle = job.Schedule(accessor.count,DefaultBatchCount);
                } else
                if(accessor.componentType == GLTFComponentType.UnsignedShort) {
                    if (accessor.normalized) {
                        var job = new Jobs.GetUInt16PositionsNormalizedJob();
                        fixed( void* src = &(buffer[start])) {
                            job.input = (ushort*)src;
                            job.result = (Vector3*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(result);
                        }
                        jobHandle = job.Schedule(accessor.count,DefaultBatchCount);
                    } else {
                        var job = new Jobs.GetUInt16PositionsJob();
                        fixed( void* src = &(buffer[start]) ) {
                            job.input = (ushort*)src;
                            job.result = (Vector3*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(result);
                        }
                        jobHandle = job.Schedule(accessor.count,DefaultBatchCount);
                    }
                } else
                if(accessor.componentType == GLTFComponentType.Short) {
                    // TODO: test. did not have test files
                    // TODO: is a non-interleaved variant faster?
                    if (accessor.normalized) {
                        var job = new Jobs.GetVector3FromInt16InterleavedNormalizedJob();
                        job.byteStride = 6; // 2 bytes * 3
                        fixed( void* src = &(buffer[start]) ) {
                            job.input = (byte*)src;
                            job.result = (Vector3*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(result);
                        }
                        jobHandle = job.Schedule(count,DefaultBatchCount);
                    } else {
                        var job = new Jobs.GetVector3FromInt16InterleavedJob();
                        job.inputByteStride = 6; // 2 bytes * 3
                        fixed( void* src = &(buffer[start]) ) {
                            job.input = (byte*)src;
                            job.result = (Vector3*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(result);
                        }
                        jobHandle = job.Schedule(count,DefaultBatchCount);
                    }
                } else
                if(accessor.componentType == GLTFComponentType.Byte) {
                    // TODO: test. did not have test files
                    // TODO: is a non-interleaved variant faster?
                    if(accessor.normalized) {
                        var job = new Jobs.GetVector3FromSByteInterleavedNormalizedJob();
                        fixed( void* src = &(buffer[start]) ) {
                            job.Setup(3,(sbyte*)src,(Vector3*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(result));
                        }
                        jobHandle = job.Schedule(count,DefaultBatchCount);
                    } else {
                        var job = new Jobs.GetVector3FromSByteInterleavedJob();
                        fixed( void* src = &(buffer[start]) ) {
                            job.Setup(3,(sbyte*)src,(Vector3*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(result));
                        }
                        jobHandle = job.Schedule(count,DefaultBatchCount);
                    }
                } else
                if(accessor.componentType == GLTFComponentType.UnsignedByte) {
                    // TODO: test. did not have test files
                    // TODO: is a non-interleaved variant faster?
                    if (accessor.normalized) {
                        var job = new Jobs.GetVector3FromByteInterleavedNormalizedJob();
                        fixed( void* src = &(buffer[start]) ) {
                            job.Setup(3,(byte*)src,(Vector3*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(result));
                        }
                        jobHandle = job.Schedule(count,DefaultBatchCount);
                    } else {
                        var job = new Jobs.GetVector3FromByteInterleavedJob();
                        fixed( void* src = &(buffer[start]) ) {
                            job.Setup(3,(byte*)src,(Vector3*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(result));
                        }
                        jobHandle = job.Schedule(count,DefaultBatchCount);
                    }
                } else {
                    Debug.LogError("Unknown componentType");
                    jobHandle = null;
                }
            }
            Profiler.EndSample();
            return count;
        }
*/
 
        /// <summary>
        /// Determines whether color accessor data can be retrieved as Color[] (floats) or Color32[] (unsigned bytes)
        /// </summary>
        /// <param name="gltf"></param>
        /// <param name="accessorIndex"></param>
        /// <returns>True if unsinged byte based colors are sufficient, false otherwise.</returns>
        bool IsColorAccessorByte( Accessor colorAccessor ) {
            return colorAccessor.componentType == GLTFComponentType.UnsignedByte;
        }

        bool IsKnownImageMimeType(string mimeType) {
            return GetImageFormatFromMimeType(mimeType) != ImageFormat.Unknown;
        }
        
        bool IsKnownImageFileExtension(string path) {
            return GetImageFormatFromPath(path) != ImageFormat.Unknown;
        }

        ImageFormat GetImageFormatFromMimeType(string mimeType) {
            if(!mimeType.StartsWith("image/")) return ImageFormat.Unknown;
            var sub = mimeType.Substring(6);
            switch(sub) {
                case "jpeg":
                    return ImageFormat.Jpeg;
                case "png":
                    return ImageFormat.PNG;
                case "ktx":
                case "ktx2":
                    return ImageFormat.KTX;
                default:
                    return ImageFormat.Unknown;
            }
        }

        ImageFormat GetImageFormatFromPath(string path) {
            if(path.EndsWith(".png",StringComparison.OrdinalIgnoreCase)) return ImageFormat.PNG;
            if(path.EndsWith(".jpg",StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".jpeg",StringComparison.OrdinalIgnoreCase)) return ImageFormat.Jpeg;
            if(path.EndsWith(".ktx",StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".ktx2",StringComparison.OrdinalIgnoreCase)) return ImageFormat.KTX;
            return ImageFormat.Unknown;
        }

#if DEBUG
        static Type GetAccessorTye( GLTFAccessorAttributeType accessorAttributeType ) {
            switch (accessorAttributeType)
            {
                case GLTFAccessorAttributeType.SCALAR:
                    return typeof(float);
                case GLTFAccessorAttributeType.VEC2:
                    return typeof(Vector2);
                case GLTFAccessorAttributeType.VEC3:
                    return typeof(Vector3);
                case GLTFAccessorAttributeType.VEC4:
                case GLTFAccessorAttributeType.MAT2:
                    return typeof(Vector4);
                case GLTFAccessorAttributeType.MAT3:
                    return typeof(Matrix4x4);
                case GLTFAccessorAttributeType.MAT4:
                default:
                    Debug.LogError("Unknown/Unsupported GLTFAccessorAttributeType");
                    return typeof(float);
            }
        }

        static Type GetAccessorComponentType( GLTFComponentType componentType ) {
            switch (componentType)
            {
                case GLTFComponentType.Byte:
                    return typeof(byte);
                case GLTFComponentType.Float:
                    return typeof(float);
                case GLTFComponentType.Short:
                    return typeof(short);
                case GLTFComponentType.UnsignedByte:
                    return typeof(byte);
                case GLTFComponentType.UnsignedInt:
                    return typeof(int);
                case GLTFComponentType.UnsignedShort:
                    return typeof(ushort);
                default:
                    Debug.LogError("Unknown GLTFComponentType");
                    return null;
            }
        }
#endif // DEBUG
    }
}
