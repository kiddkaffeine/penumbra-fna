﻿using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Penumbra.Utilities;
using Polygon = Penumbra.Utilities.FastList<Microsoft.Xna.Framework.Vector2>;

namespace Penumbra.Graphics.Renderers
{
    internal class LightVaos
    {
        public DynamicVao ShadowVao { get; set; }
        public DynamicVao HullVao { get; set; }
        public bool Enabled { get; set; }
    }

    internal class ShadowRenderer : IDisposable
    {
        private static readonly Vector4 DebugColor = Color.Red.ToVector4();
        private static readonly Vector4 WhiteColor = Color.White.ToVector4();
        private static readonly Vector4 TransparentColor = Color.Transparent.ToVector4();

        private readonly FastList<VertexShadow> _shadowVertices = new FastList<VertexShadow>();
        private readonly FastList<Vector2> _hullVertices = new FastList<Vector2>();
        private readonly FastList<int> _shadowIndices = new FastList<int>();
        private readonly FastList<int> _hullIndices = new FastList<int>();
        private readonly Dictionary<Light, LightVaos> _lightsVaos = new Dictionary<Light, LightVaos>();

        private PenumbraEngine _engine;

        private Effect _fxShadow;
        private EffectTechnique _fxShadowTech;
        private EffectTechnique _fxShadowTechDebug;
        private EffectParameter _fxShadowParamLightPosition;
        private EffectParameter _fxShadowParamLightRadius;
        private EffectParameter _fxShadowParamVp;
        private Effect _fxHull;
        private EffectTechnique _fxHullTech;
        private EffectParameter _fxHullParamVp;
        private EffectParameter _fxHullParamColor;
        private BlendState _bsShadow;
        private BlendState _bsHull;
        private DepthStencilState _dsOccludedShadow;
        private DepthStencilState _dsOccludedHull;

        public void Load(PenumbraEngine engine, Effect fxShadow, Effect fxHull)
        {
            _engine = engine;

            _fxShadow = fxShadow;
            _fxShadowTech = _fxShadow.Techniques["Main"];
            _fxShadowTechDebug = _fxShadow.Techniques["Debug"];
            _fxShadowParamLightPosition = _fxShadow.Parameters["LightPosition"];
            _fxShadowParamLightRadius = _fxShadow.Parameters["LightRadius"];
            _fxShadowParamVp = _fxShadow.Parameters["ViewProjection"];

            _fxShadow.Parameters["Color"].SetValue(DebugColor);

            _fxHull = fxHull;
            _fxHullTech = _fxHull.Techniques["Main"];
            _fxHullParamVp = _fxHull.Parameters["ViewProjection"];
            _fxHullParamColor = _fxHull.Parameters["Color"];

            BuildGraphicsResources();
        }

        public void PreRender()
        {
            _fxShadowParamVp.SetValue(_engine.Camera.ViewProjection);
        }

        public void Render(Light light)
        {
            LightVaos vaos = GetVaosForLight(light);
            if (!vaos.Enabled)
                return;

            _engine.Device.RasterizerState = _engine.Rs;
            _engine.Device.DepthStencilState = DepthStencilState.None;
            _engine.Device.DepthStencilState = light.ShadowType == ShadowType.Occluded
                ? _dsOccludedShadow
                : DepthStencilState.None;

            if (light.CastsShadows)
            {
                _fxShadowParamLightPosition.SetValue(light.Position);
                _fxShadowParamLightRadius.SetValue(light.Radius);

                // Draw shadows.
                DynamicVao shadowVao = vaos.ShadowVao;
                _engine.Device.BlendState = _bsShadow;
                _engine.Device.SetVertexArrayObject(shadowVao);
                _fxShadowTech.Passes[0].Apply();
                _engine.Device.DrawIndexedPrimitives(shadowVao.PrimitiveTopology, 0, 0, shadowVao.VertexCount, 0, shadowVao.PrimitiveCount);

                // Draw shadows borders if debugging.
                if (_engine.Debug)
                {
                    _engine.Device.RasterizerState = _engine.RsDebug;
                    _engine.Device.BlendState = BlendState.Opaque;
                    _fxShadowTechDebug.Passes[0].Apply();
                    _engine.Device.DrawIndexedPrimitives(shadowVao.PrimitiveTopology, 0, 0, shadowVao.VertexCount, 0, shadowVao.PrimitiveCount);
                }
            }

            // Draw hulls.
            bool isShadowTypeSolid = light.ShadowType == ShadowType.Solid;
            if (light.CastsShadows || isShadowTypeSolid)
            {
                if (light.ShadowType == ShadowType.Occluded)
                    _engine.Device.DepthStencilState = _dsOccludedHull;

                DynamicVao hullVao = vaos.HullVao;
                _engine.Device.RasterizerState = _engine.Rs;
                _engine.Device.BlendState = _bsHull;
                _fxHullParamVp.SetValue(_engine.Camera.ViewProjection);
                _fxHullParamColor.SetValue(isShadowTypeSolid ? TransparentColor : WhiteColor);
                _engine.Device.SetVertexArrayObject(hullVao);
                _fxHullTech.Passes[0].Apply();
                _engine.Device.DrawIndexedPrimitives(hullVao.PrimitiveTopology, 0, 0, hullVao.VertexCount, 0, hullVao.PrimitiveCount);
            }
        }

        public void Dispose()
        {
            _fxShadow?.Dispose();
            _fxHull?.Dispose();
            _bsShadow?.Dispose();
            _bsHull?.Dispose();
            foreach (LightVaos lightVaos in _lightsVaos.Values)
            {
                lightVaos.ShadowVao?.Dispose();
                lightVaos.HullVao?.Dispose();
            }
        }

        private LightVaos GetVaosForLight(Light light)
        {
            if (!_lightsVaos.TryGetValue(light, out LightVaos lightVaos))
            {
                lightVaos = new LightVaos();
                _lightsVaos[light] = lightVaos;
            }

            if (light.Dirty || _engine.Hulls.Dirty)
                BuildVaosForLight(light, lightVaos);

            return lightVaos;
        }

        private void BuildVaosForLight(Light light, LightVaos lightVaos)
        {
            _hullVertices.Clear();
            _shadowVertices.Clear();
            _shadowIndices.Clear();
            _hullIndices.Clear();

            int numSegments = 0;
            int shadowIndexOffset = 0;
            int hullIndexOffset = 0;
            int hullCount = _engine.Hulls.Count;
            for (int i = 0; i < hullCount; i++)
            {
                Hull hull = _engine.Hulls[i];
                if (!hull.Enabled ||
                    !hull.Valid ||
                    light.IgnoredHulls.Contains(hull) ||
                    !light.Intersects(hull))
                {
                    continue;
                }

                Polygon points = hull.WorldPoints;

                Vector2 prevPoint = points[points.Count - 1];

                int pointCount = points.Count;
                numSegments += pointCount;
                for (int j = 0; j < pointCount; j++)
                {
                    Vector2 currentPoint = points[j];

                    _shadowVertices.Add(new VertexShadow(prevPoint, currentPoint, new Vector2(0.0f, 0.0f)));
                    _shadowVertices.Add(new VertexShadow(prevPoint, currentPoint, new Vector2(1.0f, 0.0f)));
                    _shadowVertices.Add(new VertexShadow(prevPoint, currentPoint, new Vector2(0.0f, 1.0f)));
                    _shadowVertices.Add(new VertexShadow(prevPoint, currentPoint, new Vector2(1.0f, 1.0f)));

                    _shadowIndices.Add(shadowIndexOffset * 4 + 0);
                    _shadowIndices.Add(shadowIndexOffset * 4 + 1);
                    _shadowIndices.Add(shadowIndexOffset * 4 + 2);

                    _shadowIndices.Add(shadowIndexOffset * 4 + 1);
                    _shadowIndices.Add(shadowIndexOffset * 4 + 3);
                    _shadowIndices.Add(shadowIndexOffset * 4 + 2);

                    prevPoint = currentPoint;
                    shadowIndexOffset++;
                }

                _hullVertices.AddRange(hull.WorldPoints);

                int indexCount = hull.Indices.Count;
                for (int j = 0; j < indexCount; j++)
                    _hullIndices.Add(hull.Indices[j] + hullIndexOffset);
                hullIndexOffset += pointCount;
            }

            lightVaos.Enabled = numSegments > 0;
            if (!lightVaos.Enabled)
                return;

            if (lightVaos.ShadowVao == null)
            {
                lightVaos.ShadowVao = DynamicVao.New(
                    _engine.Device,
                    VertexShadow.Layout,
                    PrimitiveType.TriangleList,
                    _shadowVertices.Count,
                    _shadowIndices.Count,
                    useIndices: true);
                lightVaos.HullVao = DynamicVao.New(
                    _engine.Device,
                    VertexPosition2.Layout,
                    PrimitiveType.TriangleList,
                    _hullVertices.Count,
                    _hullIndices.Count,
                    useIndices: true);
            }

            lightVaos.ShadowVao.SetVertices(_shadowVertices);
            lightVaos.ShadowVao.SetIndices(_shadowIndices);
            lightVaos.HullVao.SetVertices(_hullVertices);
            lightVaos.HullVao.SetIndices(_hullIndices);
        }

        private void BuildGraphicsResources()
        {
            _bsShadow = new BlendState
            {
                ColorWriteChannels = ColorWriteChannels.Alpha,
                AlphaBlendFunction = BlendFunction.ReverseSubtract,
                AlphaSourceBlend = Blend.One,
                AlphaDestinationBlend = Blend.One
            };
            _bsHull = new BlendState
            {
                ColorWriteChannels = ColorWriteChannels.Alpha,
                AlphaBlendFunction = BlendFunction.Add,
                AlphaSourceBlend = Blend.One,
                AlphaDestinationBlend = Blend.Zero
            };
            _dsOccludedShadow = new DepthStencilState
            {
                DepthBufferEnable = false,

                StencilEnable = true,
                StencilWriteMask = 0xff,
                StencilMask = 0x00,
                StencilFunction = CompareFunction.Always,
                StencilPass = StencilOperation.Replace,
                ReferenceStencil = 1
            };
            _dsOccludedHull = new DepthStencilState
            {
                DepthBufferEnable = false,

                StencilEnable = true,
                StencilWriteMask = 0x00,
                StencilMask = 0xff,
                StencilFunction = CompareFunction.Less,
                StencilPass = StencilOperation.Keep,
                StencilFail = StencilOperation.Keep,
                ReferenceStencil = 1
            };
        }
    }
}