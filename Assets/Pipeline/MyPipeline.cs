using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using Conditional = System.Diagnostics.ConditionalAttribute;
using System;

public class MyPipeline : RenderPipeline
{
    const int maxVisibleLights = 16;

    const string shadowsSoftKeyword = "_SHADOWS_SOFT";
    const string shadowsHardKeyword = "_SHADOWS_HARD";

    static int shadowMapSizeId = Shader.PropertyToID("_ShadowMapSize");
    //static int shadowStrengthId = Shader.PropertyToID("_ShadowStrength");
    static int shadowDataId = Shader.PropertyToID("_ShadowData");
    static int shadowBiasId = Shader.PropertyToID("_ShadowBias");
    static int shadowMapId = Shader.PropertyToID("_ShadowMap");
    //static int worldToShadowMatrixId = Shader.PropertyToID("_WorldToShadowMatrix");
    static int worldToShadowMatricesId = Shader.PropertyToID("_WorldToShadowMatrices");
    static int visibleLightColorsId = Shader.PropertyToID("_VisibleLightColors");
    static int visibleLightDirectionsOrPositionsId = Shader.PropertyToID("_VisibleLightDirectionsOrPositions");
    static int visibleLightAttenuationsId = Shader.PropertyToID("_VisibleLightAttenuations");
    static int visibleLightSpotDirectionsId = Shader.PropertyToID("_VisibleLightSpotDirections");
    static int lightIndicesOffsetAndCountID = Shader.PropertyToID("unity_LightIndicesOffsetAndCount");

    Vector4[] visibleLightColors = new Vector4[maxVisibleLights];
    Vector4[] visibleLightDirectionsOrPositions = new Vector4[maxVisibleLights];
    Vector4[] visibleLightAttenuations = new Vector4[maxVisibleLights];
    Vector4[] visibleLightSpotDirections = new Vector4[maxVisibleLights];

    Vector4[] shadowData = new Vector4[maxVisibleLights];
    Matrix4x4[] worldToShadowMatrices = new Matrix4x4[maxVisibleLights];

    CullResults cull;

    RenderTexture shadowMap;

    Material errorMaterial;

    CommandBuffer buffer = new CommandBuffer { name = "Render Camera" };
    CommandBuffer shadowBuffer = new CommandBuffer { name = "Render Shadows" };


    DrawRendererFlags drawFlags;

    int shadowMapSize;
    int shadowTileCount;


    public MyPipeline(bool dynamicBatching, bool instancing, int shadowMapSize)
    {
        GraphicsSettings.lightsUseLinearIntensity = true;
        if (dynamicBatching)
        {
            drawFlags = DrawRendererFlags.EnableDynamicBatching;
        }
        if (instancing)
        {
            drawFlags |= DrawRendererFlags.EnableInstancing;
        }
        this.shadowMapSize = shadowMapSize;
    }

    public override void Render(ScriptableRenderContext renderContext, Camera[] cameras)
    {
        base.Render(renderContext, cameras);
        foreach (var camera in cameras)
            Render(renderContext, camera);
    }

    public void Render(ScriptableRenderContext renderContext, Camera camera)
    {
        ScriptableCullingParameters cullingParameters;//cull信息
        if (!CullResults.GetCullingParameters(camera, out cullingParameters))
        {
            return;//获取camera 的cull信息，无效的camera就退出
        }
        // CullResults cull = CullResults.Cull(ref cullingParameters, renderContext);//拿到信息后，调用静态方法CullResults.Cull 并且传入裁剪参数和上下文环境作为参数来调用它
#if UNITY_EDITOR
        if (camera.cameraType == CameraType.SceneView)
        {
            ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);//自带ui界面
        }
#endif

        CullResults.Cull(ref cullingParameters, renderContext, ref cull);
        if (cull.visibleLights.Count > 0)
        {
            ConfigureLights();
            if (shadowTileCount > 0)
            {
                RenderShadows(renderContext);
            }
            else
            {
                buffer.DisableShaderKeyword(shadowsHardKeyword);
                buffer.DisableShaderKeyword(shadowsSoftKeyword);
            }
        }
        else
        {
            buffer.SetGlobalVector(lightIndicesOffsetAndCountID, Vector4.zero);
            buffer.DisableShaderKeyword(shadowsHardKeyword);
            buffer.DisableShaderKeyword(shadowsSoftKeyword);
        }

        renderContext.SetupCameraProperties(camera);//连接上下文

        //var buffer = new CommandBuffer { name=camera.name};

        CameraClearFlags clearFlags = camera.clearFlags;
        buffer.ClearRenderTarget(
            (clearFlags & CameraClearFlags.Depth) != 0,
            (clearFlags & CameraClearFlags.Color) != 0,
            camera.backgroundColor);

        buffer.BeginSample("Render Camera");

        buffer.SetGlobalVectorArray(visibleLightColorsId, visibleLightColors);
        buffer.SetGlobalVectorArray(visibleLightDirectionsOrPositionsId, visibleLightDirectionsOrPositions);
        buffer.SetGlobalVectorArray(visibleLightAttenuationsId, visibleLightAttenuations);
        buffer.SetGlobalVectorArray(visibleLightSpotDirectionsId, visibleLightSpotDirections);

        renderContext.ExecuteCommandBuffer(buffer);
        //buffer.Release();
        buffer.Clear();

        var drawSettings = new DrawRendererSettings(camera, new ShaderPassName("SRPDefaultUnlit"))
        {
            flags = drawFlags,
            //rendererConfiguration = RendererConfiguration.PerObjectLightIndices8//光源索引
        };
        if (cull.visibleLights.Count > 0)
        {
            drawSettings.rendererConfiguration = RendererConfiguration.PerObjectLightIndices8;
        }
        drawSettings.sorting.flags = SortFlags.CommonOpaque;
        var filterSettings = new FilterRenderersSettings(true) { renderQueueRange = RenderQueueRange.opaque };
        renderContext.DrawRenderers(cull.visibleRenderers, ref drawSettings, filterSettings);

        renderContext.DrawSkybox(camera);
        drawSettings.sorting.flags = SortFlags.CommonTransparent;
        filterSettings.renderQueueRange = RenderQueueRange.transparent;
        renderContext.DrawRenderers(cull.visibleRenderers, ref drawSettings, filterSettings);

        DrawDefaultPipeline(renderContext, camera);

        buffer.EndSample("Render Camera");
        renderContext.ExecuteCommandBuffer(buffer);
        buffer.Clear();

        renderContext.Submit();

        if (shadowMap)
        {
            RenderTexture.ReleaseTemporary(shadowMap);
            shadowMap = null;
        }
    }

    private void ConfigureLights()
    {
        shadowTileCount = 0;
        for (int i = 0; i < cull.visibleLights.Count; i++)
        {
            if (i == maxVisibleLights)
            {
                break;
            }
            VisibleLight light = cull.visibleLights[i];
            visibleLightColors[i] = light.finalColor;
            Vector4 attenuation = Vector4.zero;
            attenuation.w = 1f;
            Vector4 shadow = Vector4.zero;

            if (light.lightType == LightType.Directional)
            {
                Vector4 v = light.localToWorld.GetColumn(2);
                v.x = -v.x;
                v.y = -v.y;
                v.z = -v.z;
                visibleLightDirectionsOrPositions[i] = v;
            }

            else
            {
                visibleLightDirectionsOrPositions[i] = light.localToWorld.GetColumn(3);
                attenuation.x = 1f / Mathf.Max(light.range * light.range, 0.00001f);
                if (light.lightType == LightType.Spot)
                {
                    Vector4 v = light.localToWorld.GetColumn(2);
                    v.x = -v.x;
                    v.y = -v.y;
                    v.z = -v.z;
                    visibleLightSpotDirections[i] = v;
                    float outerRad = Mathf.Deg2Rad * 0.5f * light.spotAngle;
                    float outerCos = Mathf.Cos(outerRad);
                    float outerTan = Mathf.Tan(outerRad);
                    float innerCos = Mathf.Cos(Mathf.Atan((46f / 64f) * outerTan));
                    float angleRange = Mathf.Max(innerCos - outerCos, 0.001f);
                    attenuation.z = 1f / angleRange;
                    attenuation.w = -outerCos * attenuation.z;

                    Light shadowLight = light.light;
                    Bounds shadowBounds;

                    if (shadowLight.shadows != LightShadows.None && cull.GetShadowCasterBounds(i, out shadowBounds))
                    {
                        shadowTileCount += 1;
                        shadow.x = shadowLight.shadowStrength;
                        shadow.y = shadowLight.shadows == LightShadows.Soft ? 1f : 0f;
                    }

                }
            }
            visibleLightAttenuations[i] = attenuation;
            shadowData[i] = shadow;
        }

        if (cull.visibleLights.Count > maxVisibleLights)
        {
            int[] lightIndices = cull.GetLightIndexMap();
            for (int i = maxVisibleLights; i < cull.visibleLights.Count; i++)
            {
                lightIndices[i] = -1;
            }
            cull.SetLightIndexMap(lightIndices);
        }
        //for (; i < cull.visibleLights.Count; i++)
        //{
        //    visibleLightColors[i] = Color.clear;
        //}
    }

    [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]//开发及editor模式下，发布时不可见
    void DrawDefaultPipeline(ScriptableRenderContext context, Camera camera)
    {
        if (errorMaterial == null)
        {
            Shader errorShader = Shader.Find("Hidden/InternalErrorShader");
            errorMaterial = new Material(errorShader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
        }
        var drawSettings = new DrawRendererSettings(camera, new ShaderPassName("ForwardBase"));
        drawSettings.SetShaderPassName(1, new ShaderPassName("PrepassBase"));
        drawSettings.SetShaderPassName(2, new ShaderPassName("Always"));
        drawSettings.SetShaderPassName(3, new ShaderPassName("Vertex"));
        drawSettings.SetShaderPassName(4, new ShaderPassName("VertexLMRGBM"));
        drawSettings.SetShaderPassName(5, new ShaderPassName("VertexLM"));
        drawSettings.SetOverrideMaterial(errorMaterial, 0);

        var filterSettings = new FilterRenderersSettings(true);
        context.DrawRenderers(cull.visibleRenderers, ref drawSettings, filterSettings);
    }

    void RenderShadows(ScriptableRenderContext context)
    {
        int split;
        if (shadowTileCount <= 1)
        {
            split = 1;
        }
        else if (shadowTileCount <= 4)
        {
            split = 2;
        }
        else if (shadowTileCount <= 9)
        {
            split = 3;
        }
        else
        {
            split = 4;
        }

        float tileSize = shadowMapSize / split;
        float tileScale = 1f / split;
        Rect tileViewport = new Rect(0f, 0f, tileSize, tileSize);

        shadowMap = RenderTexture.GetTemporary(shadowMapSize, shadowMapSize, 16, RenderTextureFormat.Shadowmap);//获取纹理
        shadowMap.filterMode = FilterMode.Bilinear;//过滤模式
        shadowMap.wrapMode = TextureWrapMode.Clamp;//截断模式

        CoreUtils.SetRenderTarget(shadowBuffer, shadowMap, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, ClearFlag.Depth);
        shadowBuffer.BeginSample("Render Shadows");
        context.ExecuteCommandBuffer(shadowBuffer);
        shadowBuffer.Clear();

        int tileIndex = 0;
        bool hardShadows = false;
        bool softShadows = false;

        for (int i = 0; i < cull.visibleLights.Count; i++)
        {
            if (i == maxVisibleLights)
            {
                break;
            }
            if (shadowData[i].x <= 0f) { continue; }
            Matrix4x4 viewMatrix, projectionMatrix;
            ShadowSplitData splitData;
            if (!cull.ComputeSpotShadowMatricesAndCullingPrimitives(i, out viewMatrix, out projectionMatrix, out splitData))
            {
                shadowData[i].x = 0f;
                continue;
            }
            float tileOffsetX = i % split;
            float tileOffsetY = i / split;
            tileViewport.x = tileOffsetX * tileSize;
            tileViewport.y = tileOffsetY * tileSize;
            if (split > 1)
            {
                shadowBuffer.SetViewport(tileViewport);
                shadowBuffer.EnableScissorRect(new Rect(
                    tileViewport.x + 4f, tileViewport.y + 4f,
                    tileSize - 8f, tileSize - 8f
                ));
            }

            shadowBuffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);
            shadowBuffer.SetGlobalFloat(shadowBiasId, cull.visibleLights[i].light.shadowBias);
            context.ExecuteCommandBuffer(shadowBuffer);
            shadowBuffer.Clear();

            var shadowSettings = new DrawShadowsSettings(cull, i);
            context.DrawShadows(ref shadowSettings);

            if (SystemInfo.usesReversedZBuffer)
            {
                projectionMatrix.m20 = -projectionMatrix.m20;
                projectionMatrix.m21 = -projectionMatrix.m21;
                projectionMatrix.m22 = -projectionMatrix.m22;
                projectionMatrix.m23 = -projectionMatrix.m23;
            }
            var scaleOffset = Matrix4x4.identity;
            scaleOffset.m00 = scaleOffset.m11 = scaleOffset.m22 = 0.5f;
            scaleOffset.m03 = scaleOffset.m13 = scaleOffset.m23 = 0.5f;

            //Matrix4x4 worldToShadowMatrix = scaleOffset * (projectionMatrix * viewMatrix);
            //shadowBuffer.SetGlobalMatrix(worldToShadowMatrixId, worldToShadowMatrix);
            worldToShadowMatrices[i] = scaleOffset * (projectionMatrix * viewMatrix);

            if (split > 1)
            {
                var tileMatrix = Matrix4x4.identity;
                tileMatrix.m00 = tileMatrix.m11 = tileScale;
                tileMatrix.m03 = tileOffsetX * tileScale;
                tileMatrix.m13 = tileOffsetY * tileScale;
                worldToShadowMatrices[i] = tileMatrix * worldToShadowMatrices[i];
            }
            tileIndex += 1;
            if (shadowData[i].y <= 0f)
            {
                hardShadows = true;
            }
            else
            {
                softShadows = true;
            }

            //shadowBuffer.SetViewport(tileViewport);
            //shadowBuffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);
        }

        if (split > 1)
        {
            shadowBuffer.DisableScissorRect();
        }
        shadowBuffer.SetGlobalTexture(shadowMapId, shadowMap);
        shadowBuffer.SetGlobalMatrixArray( worldToShadowMatricesId, worldToShadowMatrices);
        shadowBuffer.SetGlobalVectorArray(shadowDataId, shadowData);
        float invShadowMapSize = 1f / shadowMapSize;
        shadowBuffer.SetGlobalVector(shadowMapSizeId, new Vector4(invShadowMapSize, invShadowMapSize, shadowMapSize, shadowMapSize));

        //if (cull.visibleLights[0].light.shadows == LightShadows.Soft)
        //{
        //    shadowBuffer.EnableShaderKeyword(shadowsSoftKeyword);
        //}
        //else
        //{
        //    shadowBuffer.DisableShaderKeyword(shadowsSoftKeyword)
        //}

        CoreUtils.SetKeyword(shadowBuffer, shadowsHardKeyword, hardShadows);
        CoreUtils.SetKeyword(shadowBuffer, shadowsSoftKeyword, softShadows);
        shadowBuffer.EndSample("Render Shadows");
        context.ExecuteCommandBuffer(shadowBuffer);
        shadowBuffer.Clear();

    }

}
